#!/usr/bin/env python3
"""Avatar Wardrobe external indexer.

Scans Unity .prefab files as plain text (no Unity needed, no asset loading) and
writes the catalog consumed by the in-Unity wardrobe window:
    Library/AvatarWardrobe/catalog.json

Reads .meta files for exact GUIDs, resolves prefab variants / nested prefabs
through m_SourcePrefab links. Classification and dependency hints are advisory;
Unity remains authoritative for imported objects and safe removal decisions.

Usage:
    python3 wardrobe_index.py [--project DIR] [--full] [--threads N]
    python3 wardrobe_index.py --check OLD.json [NEW.json]

Incremental by default: unchanged sources and GUID-reference closures reuse their records.
Only stdlib is used.
"""

import argparse
import concurrent.futures
import hashlib
import json
import os
import re
import sys
import time
from collections import Counter

# Pure helpers live in wardrobe_text / wardrobe_classify / wardrobe_parse.
# This module keeps the Indexer, CLI, and a compat re-export surface.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wardrobe_text import *  # noqa: F401,F403
from wardrobe_classify import *  # noqa: F401,F403
from wardrobe_parse import *  # noqa: F401,F403
from wardrobe_inputs import InputSnapshot, atomic_json, file_digest, index_lock, read_json, walk_assets


class Indexer:
    def __init__(self, project_root, config, threads=16):
        self.root = project_root
        self.cfg = config
        self.threads = threads
        self.guid_to_path = {}
        self.script_names = dict(config.get("scripts", {}))
        self._discover_scripts()
        # DLL-embedded scripts share one guid per assembly, so the avatar
        # descriptor needs its (guid, fileID) pair, verified in Shinano.prefab
        # (doc carries lipSync/VisemeBlendShapes).
        vd = config.get(
            "vrcDescriptor",
            {"guid": "67cc4cb7839cd3741b63733d5adf0442", "fileID": "542108242"},
        )
        self.vrc_guid, self.vrc_fileid = vd.get("guid", ""), vd.get("fileID", "")
        self.local = {}  # relpath -> local analysis (or None if unreadable)
        self.memo = {}  # relpath -> resolved analysis
        self.journal = None

    # -- setup -----------------------------------------------------------
    def _discover_scripts(self):
        for base in ("Packages", "Assets"):
            top = os.path.join(self.root, base)
            for dirpath, _dirnames, filenames in walk_assets(top):
                for fn in filenames:
                    if fn.startswith("ModularAvatar") and fn.endswith(".cs.meta"):
                        try:
                            with open(os.path.join(dirpath, fn), encoding="utf-8") as f:
                                head = f.read(300)
                            m = META_GUID_RE.search(head)
                            if m:
                                self.script_names.setdefault(m.group(1), fn[:-8])
                        except OSError:
                            pass

    def build_guidmap(self):
        for base in self.cfg.get("guidDirs", ["Assets", "Packages"]):
            top = os.path.join(self.root, base)
            for dirpath, _dirnames, filenames in walk_assets(top):
                for fn in filenames:
                    if fn.startswith("."):
                        continue
                    if not fn.endswith(".meta"):
                        continue
                    if "/." in dirpath.replace("\\", "/") + "/":
                        continue
                    try:
                        with open(
                            os.path.join(dirpath, fn), encoding="utf-8", errors="replace"
                        ) as f:
                            head = f.read(200)
                        m = META_GUID_RE.search(head)
                        if m:
                            rel = os.path.relpath(
                                os.path.join(dirpath, fn[:-5]), self.root
                            ).replace("\\", "/")
                            self.guid_to_path.setdefault(m.group(1), rel)
                    except OSError:
                        pass

    def enum_prefabs(self):
        out = []
        for base in self.cfg.get("scanDirs", ["Assets"]):
            top = os.path.join(self.root, base)
            for dirpath, _dirnames, filenames in walk_assets(top):
                if "/." in dirpath.replace("\\", "/") + "/":
                    continue
                for fn in filenames:
                    if fn.startswith("."):
                        continue
                    if fn.endswith(".prefab"):
                        rel = os.path.relpath(os.path.join(dirpath, fn), self.root)
                        out.append(rel.replace("\\", "/"))
        return sorted(out)

    def own_guid(self, relpath):
        try:
            with open(
                os.path.join(self.root, relpath + ".meta"),
                encoding="utf-8",
                errors="replace",
            ) as f:
                m = META_GUID_RE.search(f.read(200))
                return m.group(1) if m else ""
        except OSError:
            return ""

    # -- phase A: threaded local parse ------------------------------------
    def parse_one(self, relpath):
        full = os.path.join(self.root, relpath)
        try:
            with open(full, "rb") as f:
                raw = f.read()
        except OSError:
            return None
        if not raw.startswith(b"%YAML"):
            return {"binary": True}
        try:
            text = raw.decode("utf-8", errors="replace")
        except OSError:
            return None
        return parse_prefab_text(text)

    # -- phase B: resolve variants / nested -------------------------------
    def resolve(self, relpath, stack=()):
        if relpath in self.memo:
            return self.memo[relpath]
        if relpath in stack or len(stack) >= 128:
            return None
        if relpath not in self.local:
            self.local[relpath] = self.parse_one(relpath)
        local = self.local.get(relpath)
        if not local or local.get("binary"):
            return None
        agg = {
            "renderers": local["renderers"],
            "skinned": local["skinned"],
            "scripts": set(local["scripts"]),
            "avatar_ref": local["avatar_ref"],
            "mesh": list(local["mesh_refs"]),
            "mat": list(local["mat_refs"]),
            "bones": set(local["bones"]),
            "sources": set(local["sources"]),
            "mod_mats": local["mod_mats"],
            "mod_bones": local["mod_bones"],
            "has_mods": local["has_mods"],
            "script_counts": Counter(local["script_counts"]),
            "renderer_names": list(local["renderer_names"]),
            "modmats": set(local["mod_mat_guids"]),
            "fbx": [],
        }
        own = self.own_guid(relpath)
        for guid in sorted(local["sources"]):
            base_path = self.guid_to_path.get(guid)
            if not base_path or base_path == relpath:
                continue
            if base_path.endswith(".prefab"):
                base = self.resolve(base_path, stack + (relpath,))
                if not base:
                    continue
                count = local.get("source_counts", {}).get(guid, 1)
                agg["renderers"] += base["renderers"] * count
                agg["skinned"] += base["skinned"] * count
                agg["scripts"] |= base["scripts"]
                agg["avatar_ref"] = agg["avatar_ref"] or base["avatar_ref"]
                agg["mesh"] += base["mesh"]
                agg["mat"] += base["mat"]
                agg["bones"] |= base["bones"]
                agg["sources"] |= base["sources"]
                agg["mod_mats"] = agg["mod_mats"] or base["mod_mats"]
                agg["mod_bones"] = agg["mod_bones"] or base["mod_bones"]
                agg["script_counts"] += Counter({key: value * count for key, value in base["script_counts"].items()})
                for name in base["renderer_names"]:
                    if name not in agg["renderer_names"]:
                        agg["renderer_names"].append(name)
                agg["modmats"] |= base["modmats"]
                agg["has_mods"] = agg["has_mods"] or base["has_mods"]
                agg["fbx"] += base["fbx"]
            elif guid not in agg["fbx"]:
                agg["fbx"].append(guid)
        ids = set()
        for (fid, guid) in agg["mesh"]:
            rid = ref_id(fid, guid, own)
            if rid:
                ids.add(rid)
        agg["meshIds"] = sorted(ids)
        agg["own_guid"] = own
        self.memo[relpath] = agg
        return agg

    def is_avatar_scripts(self, scripts):
        return any((g == self.vrc_guid and f == self.vrc_fileid) or
                   (f == "11500000" and self.script_names.get(g) == "VRCAvatarDescriptor")
                   for (g, f) in scripts)

    def material_name(self, guid):
        path = self.guid_to_path.get(guid)
        if not path or not path.startswith("Assets/"):
            return None
        return os.path.splitext(os.path.basename(path))[0]

    def is_fbx_source(self, guid):
        # Missing guids (deleted bases) resolve to nothing: only an existing
        # non-prefab target counts as a model source.
        path = self.guid_to_path.get(guid)
        return bool(path) and not path.endswith(".prefab")

    def share_fbx_content(self, prefabs, analyses, reused):
        # FBX files are binary: model instances carry no mesh/bone data in text.
        # Members of one model share its armature, so propagate resolved bones
        # and meshes within each model group (renderer counts stay local).
        groups = {}
        for rel in prefabs:
            if rel in analyses and analyses[rel] is not None:
                fbx = analyses[rel]["fbx"]
            elif rel in reused:
                fbx = [
                    g
                    for g in reused[rel].get("sourceGuids", [])
                    if not self.guid_to_path.get(g, "").endswith(".prefab")
                ]
            else:
                continue
            for g in fbx:
                groups.setdefault((g, os.path.dirname(rel)), []).append(rel)
        snapshots = {}
        for m in prefabs:
            if analyses.get(m) is not None:
                a = analyses[m]
                snapshots[m] = (set(a["bones"]), set(a["meshIds"]), list(a["renderer_names"]))
            elif m in reused:
                a = reused[m]
                snapshots[m] = (set(a.get("boneNames", [])), set(a.get("meshIds", [])), list(a.get("rendererNames", [])))
        for members in groups.values():
            # Snapshot donors before mutating anyone: members can belong to
            # several groups, so in-place updates would leak across groups
            # (and make full vs incremental runs disagree).
            snap = {m: snapshots[m] for m in members if m in snapshots}
            donor_bones, donor_mesh, donor_names = set(), set(), []
            for bones, mesh, names in snap.values():
                donor_bones |= bones
                donor_mesh |= mesh
                for name in names:
                    if name not in donor_names:
                        donor_names.append(name)
            if not donor_bones and not donor_mesh and not donor_names:
                continue
            for m in members:
                if m in analyses and analyses[m] is not None:
                    analyses[m]["bones"] |= donor_bones
                    if not analyses[m]["meshIds"]:
                        analyses[m]["meshIds"] = sorted(donor_mesh)
                    for name in donor_names:
                        if name not in analyses[m]["renderer_names"]:
                            analyses[m]["renderer_names"].append(name)
                elif m in reused and not reused[m].get("boneNames") and not reused[m].get("meshIds"):
                    reused[m]["boneNames"] = sorted(donor_bones)
                    reused[m]["meshIds"] = sorted(donor_mesh)
                    if not reused[m].get("rendererNames"):
                        reused[m]["rendererNames"] = list(donor_names)

    # -- record building ---------------------------------------------------
    def build_record(self, relpath, resolved, avatar_tokens, mtime_ns, size):
        names = self.script_names
        has = lambda key: any(names.get(g) == key for (g, _f) in resolved["scripts"])
        has_descriptor = self.is_avatar_scripts(resolved["scripts"])
        is_avatar = has_descriptor and not is_preview_utility(relpath)
        mesh_ids = list(resolved["meshIds"])
        bones = sorted(resolved["bones"])
        disp = display_name(relpath)
        flags = {
            "avatar": is_avatar,
            "skinned": resolved["skinned"],
            "renderers": resolved["renderers"],
            "humanoid": resolved["avatar_ref"],
            "merge": has("ModularAvatarMergeArmature"),
            "outfit_root": has("ModularAvatarOutfitRoot"),
            "toggle": has("ModularAvatarObjectToggle"),
            "menu": has("ModularAvatarMenuItem"),
            "mod_mats": resolved["mod_mats"],
            "mod_bones": resolved["mod_bones"],
            "instance_mods": bool(resolved["fbx"])
            and resolved["renderers"] == 0
            and resolved["has_mods"],
        }
        kind, conf = classify(flags, len(bones), relpath, avatar_tokens)
        deps = set()
        for _fid, guid in resolved["mesh"] + resolved["mat"]:
            if guid:
                p = self.guid_to_path.get(guid)
                if p and p.startswith("Assets/"):
                    deps.add(p)
        for guid in resolved["sources"]:
            p = self.guid_to_path.get(guid)
            if p and p.startswith("Assets/") and p != relpath:
                deps.add(p)
        base_noext = os.path.splitext(os.path.basename(relpath))[0]
        mat_names = []
        if kind == KIND_OUTFIT:
            mat_guids = set()
            for _fid, guid in resolved["mat"]:
                if guid:
                    mat_guids.add(guid)
            for guid in resolved["modmats"]:
                mat_guids.add(guid)
            for guid in sorted(mat_guids):
                mat_name = self.material_name(guid)
                if mat_name and mat_name not in mat_names:
                    mat_names.append(mat_name)
                if len(mat_names) >= 12:
                    break
        rec = {
            "guid": resolved["own_guid"],
            "assetPath": relpath,
            "displayName": disp,
            "kind": kind,
            "confidence": conf,
            "hasAvatarDescriptor": has_descriptor,
            "hasHumanoidAnimator": resolved["avatar_ref"],
            "hasMergeArmature": flags["merge"],
            "hasOutfitRoot": flags["outfit_root"],
            "hasObjectToggle": flags["toggle"],
            "hasMenuItem": flags["menu"],
            "rendererCount": resolved["renderers"],
            "skinnedRendererCount": resolved["skinned"],
            "meshIds": mesh_ids,
            "materialIds": [],
            "textureIds": [],
            "boneNames": bones,
            "dependencies": sorted(deps),
            "modMaterials": resolved["mod_mats"],
            "modBones": resolved["mod_bones"],
            "hasModelMods": resolved["has_mods"],
            "sourceGuids": sorted(resolved["sourceGuids"] if "sourceGuids" in resolved else resolved["sources"]),
            "familyId": "",
            "familyName": "",
            "variantName": variant_name(base_noext),
            "rendererNames": list(resolved["renderer_names"][:40]) if kind == KIND_OUTFIT else [],
            "partGroups": uv_groups(resolved["renderer_names"]) if kind == KIND_OUTFIT else [],
            "physBoneCount": resolved["script_counts"].get(PERF_SCRIPTS["phys"], 0),
            "contactCount": resolved["script_counts"].get(PERF_SCRIPTS["contact"], 0),
            "materialNames": mat_names,
            "colorway": colorway_of(mat_names) if kind == KIND_OUTFIT else "",
            "sourceModified": str(mtime_ns),
            "sourceSize": str(size),
        }
        if kind == KIND_OUTFIT:
            file_base = self.match_base(
                base_noext + " " + disp + " " + os.path.basename(os.path.dirname(relpath))
            )
            rec["familyName"] = family_name(base_noext, avatar_tokens) or disp
            rec["familyId"] = family_id(
                mesh_ids, resolved["skinned"], bones, relpath, avatar_tokens, file_base
            )
        return rec

    # -- driver -------------------------------------------------------------
    def load_base_list(self):
        # Mirrors AvatarWardrobeCatalog.MatchBaseAvatar: curated base names,
        # whole-word aliases, most-hits then highest count then alphabetical.
        entries = []
        try:
            path = os.path.join(
                os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "AvatarBaseNames.json"
            )
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            return []
        stops = set((s or "").lower() for s in data.get("stopwords") or [])
        for e in data.get("avatars") or []:
            name = (e.get("name") or "").strip()
            if not name:
                continue
            aliases = set(t for t in tokenize(name) if t not in stops)
            for extra in e.get("aliases") or []:
                extra = (extra or "").strip().lower()
                if extra:
                    aliases.add(extra)
            if not aliases:
                aliases = set(tokenize(name))
            try:
                count = int(e.get("count") or 0)
            except (ValueError, TypeError):
                count = 0
            entries.append({"name": name, "aliases": aliases, "count": count})
        return entries

    def match_base(self, text):
        if not text:
            return None
        tokens = set(tokenize(text))
        best = None
        best_score, best_count = 0, -1
        for e in self.base_list:
            score = len(e["aliases"] & tokens)
            if not score:
                continue
            if (
                score > best_score
                or (score == best_score and (e["count"] > best_count or (e["count"] == best_count and (best is None or e["name"] < best))))
            ):
                best, best_score, best_count = e["name"], score, e["count"]
        return best

    def cache_paths(self):
        d = os.path.join(self.root, "Library", "AvatarWardrobe")
        os.makedirs(d, exist_ok=True)
        return {
            "catalog": os.path.join(d, "catalog.json"),
            "progress": os.path.join(d, "progress.json"),
            "journal": os.path.join(d, "scan.log"),
        }

    def load_old(self, path):
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
            if data.get("version") != 4:
                return {}
            return {
                r["assetPath"]: r
                for r in data.get("records", [])
                if r and r.get("assetPath")
            }
        except (OSError, ValueError):
            return {}

    def write_progress(self, paths, done, total, running, phase="parsing"):
        atomic_json(paths["progress"], {"done": done, "total": total,
                    "running": running, "pid": os.getpid(), "phase": phase})

    def run(self, full=False):
        paths = self.cache_paths()
        with index_lock(os.path.join(os.path.dirname(paths["catalog"]), "index.lock")):
            try:
                # Discovery and dependency fingerprinting can take minutes on a
                # cold cache. Report activity before that preparation starts.
                self.write_progress(paths, 0, 0, True, "discovery")
                return self._run(full)
            except BaseException as error:
                if self.journal:
                    self.journal.close(); self.journal = None
                atomic_json(paths["progress"], {"done": 0, "total": 0, "running": False,
                    "pid": os.getpid(), "error": str(error)})
                raise

    def publish_preview_catalog(self, paths, completed, stats):
        # A cold scan has no prior catalog to display. Publish only resolved
        # records; final family smoothing and fingerprints replace this snapshot.
        resolved = {rel: self.resolve(rel) for rel in completed}
        tokens = set()
        for rel, data in resolved.items():
            if data and self.is_avatar_scripts(data["scripts"]):
                tokens |= avatar_tokens_for(display_name(rel), rel)
        records = []
        for rel, data in resolved.items():
            if not data:
                continue
            rec = self.build_record(rel, data, tokens, *stats.get(rel, (0, 0)))
            if rec["kind"] == KIND_OUTFIT:
                # Stable per-asset groups until all variants can be compared.
                rec["familyId"] = "asset:" + rec["guid"]
                rec["familyName"] = rec["displayName"]
                rec["variantName"] = "Default"
            records.append(rec)
        atomic_json(paths["catalog"], {"version": 4, "records": records,
            "dirtyPaths": [], "partial": True, "generator": "wardrobe_index.py"})

    def _run(self, full=False):
        paths = self.cache_paths()
        dirty_path = os.path.join(os.path.dirname(paths["catalog"]), "dirty.json")
        dirty_token = file_digest(dirty_path)
        self.guid_to_path.clear()
        self.local.clear()
        self.memo.clear()
        self.build_guidmap()
        analysis_signature = hashlib.sha256(json.dumps({"config": self.cfg,
            "scripts": self.script_names, "parserVersion": 5}, sort_keys=True).encode("utf-8")).hexdigest()
        inputs = InputSnapshot(self.root, self.guid_to_path,
                               os.path.join(os.path.dirname(paths["catalog"]), "inputs.json"), full)
        self.base_list = self.load_base_list()
        prefabs = self.enum_prefabs()
        total = len(prefabs)
        old = {} if full else self.load_old(paths["catalog"])
        stats, fingerprints, dependency_paths = {}, {}, {}
        for rel in prefabs:
            try:
                st = os.stat(os.path.join(self.root, rel))
                stats[rel] = (st.st_mtime_ns, st.st_size)
            except OSError:
                pass
        self.write_progress(paths, 0, total, True, "dependencies")
        last_report = time.monotonic()
        for position, rel in enumerate(prefabs, 1):
            fingerprints[rel], dependency_paths[rel] = inputs.fingerprint(rel)
            if time.monotonic() - last_report >= 2:
                self.write_progress(paths, position, total, True, "dependencies")
                last_report = time.monotonic()
        # Reuse records whose source and referenced inputs are untouched (skip file parsing only;
        # classification + families always recompute below).
        reused, todo = {}, []
        for rel in prefabs:
            prev = old.get(rel)
            cur = stats.get(rel)
            if (
                prev is not None
                and cur is not None
                and prev.get("sourceModified") == str(cur[0])
                and prev.get("sourceSize") == str(cur[1])
                and prev.get("dependencyFingerprint") == fingerprints[rel]
                and prev.get("analysisSignature") == analysis_signature
            ):
                reused[rel] = prev
            else:
                todo.append(rel)
        self.journal = open(paths["journal"], "w", encoding="utf-8")
        self.journal.write("# Last line is the prefab currently being analyzed.\n")
        self.write_progress(paths, total - len(todo), total, True)
        done = total - len(todo)
        publish_partial = not os.path.exists(paths["catalog"])
        completed = list(reused)
        last_publish = time.monotonic()
        with concurrent.futures.ThreadPoolExecutor(max_workers=self.threads) as pool:
            future_of = {pool.submit(self.parse_one, rel): rel for rel in todo}
            for future in concurrent.futures.as_completed(future_of):
                rel = future_of[future]
                # Unexpected parser errors must fail the scan, not silently turn
                # a valid outfit into an empty candidate and overwrite the cache.
                self.local[rel] = future.result()
                self.journal.write(rel + "\n")
                done += 1
                completed.append(rel)
                if publish_partial and done % 500 == 0 and time.monotonic() - last_publish >= 5:
                    self.publish_preview_catalog(paths, completed, stats)
                    last_publish = time.monotonic()
                if done % 100 == 0:
                    self.write_progress(paths, done, total, True)
        self.journal.close()
        self.journal = None
        # Resolve + classify everything (fast: dict lookups + string ops).
        analyses = {}
        for rel in prefabs:
            if rel in reused:
                continue
            # resolve() only returns None for unreadable/binary files now;
            # variants with missing bases keep their local content.
            analyses[rel] = self.resolve(rel)
        self.share_fbx_content(prefabs, analyses, reused)
        avatar_toks = set()
        for rel, resolved in analyses.items():
            if resolved and self.is_avatar_scripts(resolved["scripts"]):
                disp = display_name(rel)
                avatar_toks |= avatar_tokens_for(disp, rel)
        for rel, prev in reused.items():
            if prev.get("hasAvatarDescriptor"):
                avatar_toks |= avatar_tokens_for(prev["displayName"], rel)
        records = []
        for rel in prefabs:
            if rel in reused:
                rec = dict(reused[rel])
                kind, conf = classify(
                    {
                        "avatar": rec["hasAvatarDescriptor"]
                        and not is_preview_utility(rel),
                        "skinned": rec["skinnedRendererCount"],
                        "renderers": rec["rendererCount"],
                        "humanoid": rec["hasHumanoidAnimator"],
                        "merge": rec["hasMergeArmature"],
                        "outfit_root": rec["hasOutfitRoot"],
                        "toggle": rec["hasObjectToggle"],
                        "menu": rec["hasMenuItem"],
                        "mod_mats": rec.get("modMaterials", False),
                        "mod_bones": rec.get("modBones", False),
                        "instance_mods": bool(rec.get("sourceGuids"))
                        and rec["rendererCount"] == 0
                        and rec.get("hasModelMods", False)
                        and any(
                            self.is_fbx_source(g) for g in rec.get("sourceGuids", [])
                        ),
                    },
                    len(rec.get("boneNames", [])),
                    rel,
                    avatar_toks,
                )
                rec["kind"], rec["confidence"] = kind, conf
                records.append(rec)
                continue
            resolved = analyses.get(rel)
            if resolved is None:
                records.append(
                    {
                        "guid": self.own_guid(rel),
                        "assetPath": rel,
                        "displayName": display_name(rel),
                        "kind": KIND_IGNORED,
                        "confidence": 0.0,
                        "hasAvatarDescriptor": False,
                        "hasHumanoidAnimator": False,
                        "hasMergeArmature": False,
                        "hasOutfitRoot": False,
                        "hasObjectToggle": False,
                        "hasMenuItem": False,
                        "rendererCount": 0,
                        "skinnedRendererCount": 0,
                        "meshIds": [],
                        "materialIds": [],
                        "textureIds": [],
                        "boneNames": [],
                        "dependencies": [],
                        "familyId": "",
                        "familyName": "",
                        "variantName": "Default",
                        "rendererNames": [],
                        "partGroups": [],
                        "physBoneCount": 0,
                        "contactCount": 0,
                        "materialNames": [],
                        "colorway": "",
                        "sourceModified": str(stats.get(rel, (0, 0))[0]),
                        "sourceSize": str(stats.get(rel, (0, 0))[1]),
                    }
                )
                continue
            records.append(
                self.build_record(rel, resolved, avatar_toks, *stats.get(rel, (0, 0)))
            )
        # Variant metadata for outfits (mirrors DeriveVariantMetadata).
        for rec in records:
            if rec["kind"] == KIND_OUTFIT:
                base = os.path.splitext(os.path.basename(rec["assetPath"]))[0]
                rec["familyName"] = family_name(base, avatar_toks) or rec["displayName"]
                rec["variantName"] = variant_name(base)
                file_base = self.match_base(
                    base
                    + " "
                    + rec["displayName"]
                    + " "
                    + os.path.basename(os.path.dirname(rec["assetPath"]))
                )
                rec["familyId"] = family_id(
                    rec["meshIds"],
                    rec["skinnedRendererCount"],
                    rec["boneNames"],
                    rec["assetPath"],
                    avatar_toks,
                    file_base,
                )
        # Unite mesh-distinct colorways: same folder + base + cleaned name.
        triple_groups = {}
        for rec in records:
            if rec["kind"] != KIND_OUTFIT:
                continue
            stem = os.path.splitext(os.path.basename(rec["assetPath"]))[0]
            # Parent folder joins the match text so bare names like "Color 1"
            # in .../SecretServant/Prefab/Shinano still resolve to Shinano.
            triple_base = self.match_base(
                stem
                + " "
                + rec["displayName"]
                + " "
                + os.path.basename(os.path.dirname(rec["assetPath"]))
            )
            if not triple_base:
                continue
            cleaned = " ".join(
                t
                for t in tokenize(stem)
                if t not in VARIANT_TOKENS and t not in avatar_toks and not is_int_token(t)
            )
            triple_groups.setdefault(
                (os.path.dirname(rec["assetPath"]), triple_base, cleaned), []
            ).append(rec)
        for members in triple_groups.values():
            ids = {m["familyId"] for m in members}
            if len(ids) <= 1:
                continue
            target = sorted(ids)[0]
            for m in members:
                m["familyId"] = target
        # Same folder + identical sources + no local geometry: model
        # instances whose entire content resolves through one base, so they
        # differ only in local overrides (materials/toggles). Same base +
        # same folder = colorways of one product, whatever the file names
        # (Wondercraze 1..14.prefab, LookVook's 71 colorways in one folder).
        # Records with local meshes keep their mesh-based verdict.
        model_groups = {}
        for rec in records:
            if rec["kind"] != KIND_OUTFIT or rec.get("meshIds"):
                continue
            srcs = tuple(sorted(rec.get("sourceGuids") or []))
            if not srcs:
                continue
            model_groups.setdefault(
                (os.path.dirname(rec["assetPath"]), srcs), []).append(rec)
        for members in model_groups.values():
            ids = {m["familyId"] for m in members}
            if len(ids) <= 1:
                continue
            target = sorted(ids)[0]
            for m in members:
                m["familyId"] = target
        # One display name per family: most common member name, else folder.
        fam_members = {}
        for rec in records:
            if rec["kind"] == KIND_OUTFIT:
                fam_members.setdefault(rec["familyId"], []).append(rec)
        for members in fam_members.values():
            if len(members) < 2:
                continue
            counts = Counter(m["familyName"] for m in members if m.get("familyName"))
            if counts:
                top, topn = counts.most_common(1)[0]
            else:
                top, topn = "", 0
            if top and not is_weak_name(top) and (topn > 1 or len(counts) == 1):
                disp_name = top
            else:
                # Weak ("Color") or missing consensus: nearest product folder
                # that is not generic, avatar-named, or itself weak
                # (.../SecretServant/Prefab/Shinano -> "Secret Servant").
                disp_name = None
                folder = os.path.dirname(members[0]["assetPath"])
                for _ in range(5):
                    candidate = os.path.basename(folder)
                    folder = os.path.dirname(folder)
                    if not candidate or is_generic_asset_name(candidate):
                        continue
                    if self.match_base(candidate) is not None:
                        continue
                    name_words = [t for t in tokenize(candidate) if not is_int_token(t)]
                    if name_words and all(w in WEAK_NAME_TOKENS for w in name_words):
                        continue
                    disp_name = humanize(candidate)
                    break
                if disp_name is None:
                    disp_name = top or "Outfits"
            for m in members:
                m["familyName"] = disp_name
        # Hair/outfit/gimmick, smoothed at family level: model-instance
        # colorways carry no geometry in text, so members pool evidence and
        # share the verdict. Candidates have no family: each stands alone.
        fam_evidence = {}
        for rec in records:
            if rec["kind"] not in (KIND_OUTFIT, KIND_CANDIDATE):
                rec["category"] = "unknown"
                rec["categoryConfidence"] = 0.0
                continue
            key = rec.get("familyId") or ("asset:" + rec.get("guid", ""))
            hair, outfit, gimmick = category_scores(
                rec.get("rendererNames"), rec.get("boneNames"),
                rec.get("assetPath"), rec.get("familyName"),
                rec.get("displayName"), rec.get("materialNames"))
            slot = fam_evidence.setdefault(key, [0.0, 0.0, 0.0])
            slot[0] += hair
            slot[1] += outfit
            slot[2] += gimmick
        for rec in records:
            if rec["kind"] not in (KIND_OUTFIT, KIND_CANDIDATE):
                continue
            key = rec.get("familyId") or ("asset:" + rec.get("guid", ""))
            label, conf = classify_category(*fam_evidence.get(key, (0.0, 0.0, 0.0)))
            rec["category"] = label
            rec["categoryConfidence"] = conf
        for rec in records:
            relative = rec["assetPath"]
            rec["analysisSignature"] = analysis_signature
            rec["dependencyFingerprint"] = fingerprints[relative]
            rec["dependencies"] = dependency_paths[relative]
        inputs.commit()
        atomic_json(paths["catalog"], {"version": 4, "records": records,
            "dirtyPaths": [], "generator": "wardrobe_index.py"}, unchanged_ok=True)
        atomic_json(os.path.join(os.path.dirname(paths["catalog"]), "scan-result.json"),
                    {"dirtyToken": dirty_token, "success": True, "pid": os.getpid()})
        marker = os.path.join(os.path.dirname(paths["catalog"]), "rebuild.pending")
        try:
            os.unlink(marker)
        except FileNotFoundError:
            pass
        self.write_progress(paths, total, total, False)
        kinds = {}
        for rec in records:
            kinds[rec["kind"]] = kinds.get(rec["kind"], 0) + 1
        print("prefabs: %d kinds: %s" % (len(records), kinds))
        return paths["catalog"]

def check(old_path, new_path):
    old = {r["assetPath"]: r for r in json.load(open(old_path))["records"]}
    new = {r["assetPath"]: r for r in json.load(open(new_path))["records"]}
    both = sorted(set(old) & set(new))
    fields = (
        "kind",
        "rendererCount",
        "skinnedRendererCount",
        "hasAvatarDescriptor",
        "hasHumanoidAnimator",
        "hasMergeArmature",
        "hasOutfitRoot",
    )
    agree = {f: 0 for f in fields}
    bone_ok = mesh_ok = fam_ok = 0
    mism = []
    for path in both:
        a, b = old[path], new[path]
        for f in fields:
            if a.get(f) == b.get(f):
                agree[f] += 1
            elif len(mism) < 25:
                mism.append((path, f, a.get(f), b.get(f)))
        if sorted(a.get("boneNames", [])) == sorted(b.get("boneNames", [])):
            bone_ok += 1
        if sorted(a.get("meshIds", [])) == sorted(b.get("meshIds", [])):
            mesh_ok += 1
        if (a.get("familyId") or "") == (b.get("familyId") or ""):
            fam_ok += 1
    n = max(1, len(both))
    print("overlap: %d (old-only %d, new-only %d)" % (len(both), len(set(old) - set(new)), len(set(new) - set(old))))
    for f in fields:
        print("  %-20s %6.2f%%" % (f, 100.0 * agree[f] / n))
    print("  %-20s %6.2f%%" % ("boneNames", 100.0 * bone_ok / n))
    print("  %-20s %6.2f%%" % ("meshIds", 100.0 * mesh_ok / n))
    print("  %-20s %6.2f%%" % ("familyId", 100.0 * fam_ok / n))
    for path, f, a, b in mism:
        print("  MISMATCH %s %s unity=%r ext=%r" % (path, f, a, b))

def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", default="")
    ap.add_argument("--full", action="store_true")
    ap.add_argument("--threads", type=int, default=16)
    ap.add_argument("--check", nargs="+", metavar="JSON")
    ap.add_argument("--config", default="")
    args = ap.parse_args(argv)
    if args.check:
        old = args.check[0]
        new = args.check[1] if len(args.check) > 1 else None
        if new is None:
            print("--check needs OLD.json [NEW.json]", file=sys.stderr)
            return 2
        check(old, new)
        return 0
    root = args.project
    if not root:
        from pathlib import Path
        root = next((str(p) for p in Path(__file__).resolve().parents if (p / "Assets").is_dir()), os.getcwd())
    root = os.path.abspath(root)
    if not os.path.isdir(os.path.join(root, "Assets")):
        ap.error("--project must contain an Assets directory")
    cfg_path = args.config or os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "wardrobe_config.json"
    )
    try:
        with open(cfg_path, encoding="utf-8") as f:
            config = json.load(f)
    except OSError:
        config = {}
    t0 = time.time()
    indexer = Indexer(root, config, threads=max(1, min(32, args.threads)))
    out = indexer.run(full=args.full)
    print("wrote %s in %.1fs" % (out, time.time() - t0))
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
