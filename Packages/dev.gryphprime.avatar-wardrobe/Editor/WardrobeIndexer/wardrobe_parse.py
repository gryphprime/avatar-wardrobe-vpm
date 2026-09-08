"""Unity prefab text parsing for Avatar Wardrobe.
Extracted move-only from wardrobe_index.py; behavior unchanged. Only stdlib.
"""
import re
from collections import Counter
from wardrobe_text import normalize

DOC_RE = re.compile(r"(?m)^--- !u!(\d+)\s+&(-?\d+)( stripped)?\s*$")

SCRIPT_RE = re.compile(r"m_Script: \{fileID: (-?\d+), guid: ([0-9a-f]{32})")

MESH_RE = re.compile(r"m_Mesh: \{fileID: (-?\d+)(?:, guid: ([0-9a-f]{32}))?")

AVATAR_RE = re.compile(r"m_Avatar: \{fileID: (-?\d+)(?:, guid: ([0-9a-f]{32}))?")

ROOTBONE_RE = re.compile(r"m_RootBone: \{fileID: (-?\d+)")

GONAME_RE = re.compile(r"(?m)^  m_Name: (.*)$")

GOFROMTRANS_RE = re.compile(r"m_GameObject: \{fileID: (-?\d+)")

SOURCE_RE = re.compile(r"m_SourcePrefab: \{fileID: -?\d+, guid: ([0-9a-f]{32})")

META_GUID_RE = re.compile(r"(?m)^guid: ([0-9a-f]{32})\s*$")

MODMAT_RE = re.compile(r"objectReference: \{fileID: 2100000, guid: ([0-9a-f]{32})")

CLS_GAMEOBJECT = "1"

CLS_TRANSFORM = "4"

CLS_MESH_RENDERER = "23"
CLS_MESH_FILTER = "33"

CLS_ANIMATOR = "95"

CLS_MONOBEHAVIOUR = "114"

CLS_SKINNED = "137"

CLS_PREFABINSTANCE = "1001"

PERF_SCRIPTS = {
    "phys": ("2a2c05204084d904aa4945ccff20d8e5", "1661641543"),
    "contact": ("80f1b8067b0760e4bb45023bc2e9de66", "-1450912254"),
}

def list_item_refs(body, key):
    """(fileID, guid-or-None) list items directly under a '  key:' block."""
    refs = []
    lines = body.split("\n")
    for i, line in enumerate(lines):
        if line == "  " + key + ":":
            for item in lines[i + 1:]:
                m = re.match(r"  - \{fileID: (-?\d+)(?:, guid: ([0-9a-f]{32}))?", item)
                if not m:
                    break
                refs.append((m.group(1), m.group(2)))
            break
    return refs

def parse_prefab_text(text):
    """Local-only analysis: counts, flags, refs. No cross-file resolution."""
    docs = DOC_RE.split(text)
    go_names, trans_go = {}, {}
    n_mesh = n_skinned = 0
    scripts = set()
    script_counts = Counter()
    renderer_go = []
    avatar_ref = False
    mesh_refs, mat_refs, bone_anchors = [], [], []
    sources = set()
    source_counts = Counter()
    mod_mats = mod_bones = has_mods = False
    # split() yields [pre, cls, anchor, stripped, body] * N: stride 4, offset 1.
    for i in range(1, len(docs) - 3, 4):
        cls, anchor = docs[i], docs[i + 1]
        stripped = bool(docs[i + 2])
        body = docs[i + 3]
        for m in SOURCE_RE.finditer(body):
            sources.add(m.group(1))
            if cls == CLS_PREFABINSTANCE and not stripped:
                source_counts[m.group(1)] += 1
        if cls == CLS_PREFABINSTANCE:
            # Variant overrides prove base content: material/bone overrides mean
            # base renderers exist even when the base is a binary model file.
            if "m_Bones" in body:
                mod_bones = True
            if "m_Materials" in body:
                mod_mats = True
            if "m_Modification" in body:
                has_mods = True
        if stripped:
            continue  # placeholder content; resolved through the base file
        if cls == CLS_GAMEOBJECT:
            m = GONAME_RE.search(body)
            if m:
                go_names[anchor] = m.group(1).strip()
        elif cls == CLS_TRANSFORM:
            m = GOFROMTRANS_RE.search(body)
            if m:
                trans_go[anchor] = m.group(1)
        elif cls == CLS_MESH_FILTER:
            m = MESH_RE.search(body)
            if m:
                mesh_refs.append((m.group(1), m.group(2)))
        elif cls == CLS_MESH_RENDERER:
            n_mesh += 1
            g = GOFROMTRANS_RE.search(body)
            if g:
                renderer_go.append(g.group(1))
            m = MESH_RE.search(body)
            if m:
                mesh_refs.append((m.group(1), m.group(2)))
            mat_refs.extend(list_item_refs(body, "m_Materials"))
        elif cls == CLS_SKINNED:
            n_skinned += 1
            g = GOFROMTRANS_RE.search(body)
            if g:
                renderer_go.append(g.group(1))
            m = MESH_RE.search(body)
            if m:
                mesh_refs.append((m.group(1), m.group(2)))
            mat_refs.extend(list_item_refs(body, "m_Materials"))
            for ref_fid, _ref_guid in list_item_refs(body, "m_Bones"):
                bone_anchors.append(ref_fid)
            m = ROOTBONE_RE.search(body)
            if m:
                bone_anchors.append(m.group(1))
        elif cls == CLS_ANIMATOR:
            m = AVATAR_RE.search(body)
            if m and (m.group(1) != "0" or m.group(2)):
                avatar_ref = True
        elif cls == CLS_MONOBEHAVIOUR:
            m = SCRIPT_RE.search(body)
            if m:
                scripts.add((m.group(2), m.group(1)))  # (guid, fileID)
                script_counts[(m.group(2), m.group(1))] += 1
    bones = []
    for anchor in bone_anchors:
        go = trans_go.get(anchor)
        name = go_names.get(go) if go else None
        if name:
            bones.append(normalize(name))
    renderer_names = []
    for anchor in renderer_go:
        name = go_names.get(anchor)
        if name and name.strip() and name.strip() not in renderer_names:
            renderer_names.append(name.strip())
    return {
        "renderers": n_mesh + n_skinned,
        "skinned": n_skinned,
        "scripts": scripts,
        "script_counts": script_counts,
        "avatar_ref": avatar_ref,
        "mesh_refs": mesh_refs,
        "mat_refs": mat_refs,
        "bones": sorted(set(b for b in bones if b)),
        "renderer_names": renderer_names,
        "mod_mat_guids": sorted(set(MODMAT_RE.findall(text))),
        "sources": sources,
        "source_counts": source_counts,
        "mod_mats": mod_mats,
        "mod_bones": mod_bones,
        "has_mods": has_mods or bool(scripts),
    }

def ref_id(file_id, guid, own_guid):
    if file_id in ("0", "-0", None):
        return None
    return (guid or own_guid) + ":" + str(file_id)
