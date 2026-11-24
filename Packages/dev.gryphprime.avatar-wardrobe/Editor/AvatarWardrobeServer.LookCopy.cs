// Copies Looks from the selected avatar to another avatar in the open scenes, even one
// made from a different base. Every item is installed again on the target (so Modular
// Avatar fits it to that armature), with the variant that suits the target base when the
// original does not. Part defaults, shape changes, sliders, Blendshape Sync, part toggles,
// menu groups and each Look's upload settings come along. VRChat IDs never do.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        [Serializable]
        internal sealed class LookCopyItemDto
        {
            public string name = "";
            public string variant = "";
            // same: the item itself · variant: another variant that suits the target ·
            // missing: nothing suits the target · already: the target look wears it (merge)
            public string status = "";
            public string guid = "";
        }

        [Serializable]
        internal sealed class LookCopyLookDto
        {
            public string id = "";
            public string name = "";
            public int exists;
            public List<LookCopyItemDto> items = new List<LookCopyItemDto>();
        }

        [Serializable]
        internal sealed class LookCopyReviewDto
        {
            public int ok;
            public string message = "";
            public string target = "";
            public List<LookCopyLookDto> looks = new List<LookCopyLookDto>();
        }

        [Serializable]
        internal sealed class LookCopyResultDto
        {
            public int ok;
            public string message = "";
            public string target = "";
            public int targetId;
            public int looks;
            public int items;
            public int variants;
            public int missing;
            public int settingsSkipped;
        }

        private sealed class CopyItem
        {
            public GameObject source;
            public string sourcePath;
            public WardrobeAssetRecord record;
            public string status;
            public WardrobePartsEdit edit;
            public bool partToggles;
            public bool active;
            public string groupName;
        }

        private sealed class CopyLook
        {
            public AvatarWardrobePresets.WardrobePreset preset;
            public List<CopyItem> items = new List<CopyItem>();
            public string existingId;
        }

        private static VRCAvatarDescriptor CopyTarget(string targetId, out string message)
        {
            message = "";
            var target = SceneTargets().FirstOrDefault(a => a.GetInstanceID().ToString() == targetId);
            if (target == null) message = "That avatar is no longer in the scene. Refresh and try again.";
            else if (target == SceneAvatar) { message = "Choose another avatar."; target = null; }
            return target;
        }

        private static string PrefabGuid(GameObject item) =>
            AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(item));

        // The wardrobe items a Look wears on the selected avatar (outermost prefab roots only).
        private static List<GameObject> LookItems(VRCAvatarDescriptor avatar, string lookId)
        {
            var roots = avatar.GetComponentsInChildren<Transform>(true)
                .Select(t => PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject))
                .Where(root => root != null && root != avatar.gameObject && root.transform.IsChildOf(avatar.transform))
                .Distinct()
                .Where(root => AvatarWardrobeCatalog.IsBrowsablePrefab(AvatarWardrobeCatalog.GetRecord(PrefabGuid(root))))
                .ToList();
            roots = roots.Where(root => !roots.Any(other => other != root && root.transform.IsChildOf(other.transform))).ToList();
            return roots.Where(root => AvatarWardrobePresets.ItemPreset(root, avatar) == lookId).ToList();
        }

        private static int CopyRank(WardrobeCompatibilityState state)
        {
            switch (state)
            {
                case WardrobeCompatibilityState.Compatible: return 3;
                case WardrobeCompatibilityState.ProbablyCompatible: return 2;
                case WardrobeCompatibilityState.Untested: return 1;
                default: return 0;
            }
        }

        // The item itself when the target can wear it, else the family's best variant for the
        // target base: most compatible first, then the same variant name, then the same colorway.
        private static WardrobeAssetRecord ResolveCopyVariant(string guid, string targetAvatarGuid, out string status)
        {
            var record = AvatarWardrobeCatalog.GetRecord(guid);
            status = "missing";
            if (record == null) return null;
            if (string.IsNullOrEmpty(targetAvatarGuid) ||
                AvatarWardrobeCatalog.Compatibility(record, targetAvatarGuid).state != WardrobeCompatibilityState.Incompatible)
            {
                status = "same";
                return record;
            }
            var family = CachedFamilies().FirstOrDefault(f => f.variants.Any(v => v.guid == guid));
            if (family == null) return null;
            var name = AvatarWardrobeCatalog.DisplayVariant(record);
            var best = family.variants.Where(v => v != null && v.guid != guid)
                .Select(v => new { v, rank = CopyRank(AvatarWardrobeCatalog.Compatibility(v, targetAvatarGuid).state) })
                .Where(x => x.rank > 0)
                .OrderByDescending(x => x.rank)
                .ThenByDescending(x => string.Equals(AvatarWardrobeCatalog.DisplayVariant(x.v), name, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => !string.IsNullOrEmpty(record.colorway) && string.Equals(x.v.colorway, record.colorway, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.v).FirstOrDefault();
            if (best == null) return null;
            status = "variant";
            return best;
        }

        private static WardrobePartsEdit CaptureParts(VRCAvatarDescriptor avatar, GameObject item)
        {
            var edit = new WardrobePartsEdit();
            edit.parts.Add(new WardrobePartEdit { key = "", worn = 1, shapes = OutfitToggleGenerator.ShapeChanges(avatar, item) });
            foreach (var part in OutfitToggleGenerator.ItemParts(item))
                edit.parts.Add(new WardrobePartEdit
                {
                    key = part.name,
                    worn = OutfitToggleGenerator.PartWorn(item, part) ? 1 : 0,
                    shapes = OutfitToggleGenerator.ShapeChanges(avatar, part),
                });
            edit.sliders = OutfitToggleGenerator.Sliders(avatar, item);
            edit.sync = OutfitToggleGenerator.HasBlendshapeSync(item) ? 1 : 0;
            return edit;
        }

        // Runs with SceneAvatar = source. Reads only.
        private static List<CopyLook> PlanLookCopy(VRCAvatarDescriptor source, VRCAvatarDescriptor target, IEnumerable<string> ids, bool merge)
        {
            AvatarWardrobePresets.CurrentBase(out var sourceKey, out var unused);
            var targetGuid = AvatarWardrobeCatalog.GetAvatarRecord(target)?.guid;
            var targetLooks = AvatarWardrobePresets.LooksOf(target);
            var plan = new List<CopyLook>();
            foreach (var id in ids)
            {
                var preset = AvatarWardrobePresets.GetPreset(id);
                if (preset == null || preset.baseKey != sourceKey) continue;
                var look = new CopyLook { preset = preset };
                if (merge)
                    look.existingId = targetLooks.FirstOrDefault(p => string.Equals((p.name ?? "").Trim(), (preset.name ?? "").Trim(), StringComparison.OrdinalIgnoreCase))?.id;
                var groups = preset.menuGroups ?? new List<AvatarWardrobePresets.MenuGroup>();
                foreach (var item in LookItems(source, id))
                {
                    var path = AnimationUtility.CalculateTransformPath(item.transform, source.transform);
                    var record = ResolveCopyVariant(PrefabGuid(item), targetGuid, out var status);
                    look.items.Add(new CopyItem
                    {
                        source = item,
                        sourcePath = path,
                        record = record,
                        status = status,
                        edit = CaptureParts(source, item),
                        partToggles = OutfitToggleGenerator.HasPartToggles(item),
                        active = item.activeSelf,
                        groupName = groups.FirstOrDefault(g => (g.paths ?? new List<string>()).Contains(path))?.name,
                    });
                }
                plan.Add(look);
            }
            return plan;
        }

        private static List<string> CopyIds(string ids) =>
            (ids ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();

        private static LookCopyReviewDto ReviewLookCopy(string ids, string targetId, string existing)
        {
            if (SceneAvatar == null) return new LookCopyReviewDto { message = WardrobeStrings.T("install.noavatar") };
            if (EditorApplication.isPlayingOrWillChangePlaymode) return new LookCopyReviewDto { message = WardrobeStrings.T("server.leave.play.mode.before.editing.the.avatar") };
            var target = CopyTarget(targetId, out var message);
            if (target == null) return new LookCopyReviewDto { message = message };
            var merge = existing == "merge";
            var plan = PlanLookCopy(SceneAvatar, target, CopyIds(ids), merge);
            var targetLooks = AvatarWardrobePresets.LooksOf(target);
            var review = new LookCopyReviewDto { ok = 1, target = target.gameObject.name };
            foreach (var look in plan)
            {
                var row = new LookCopyLookDto
                {
                    id = look.preset.id,
                    name = look.preset.name,
                    exists = targetLooks.Any(p => string.Equals((p.name ?? "").Trim(), (look.preset.name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ? 1 : 0,
                };
                var worn = look.existingId == null ? new HashSet<string>() : AvatarWardrobePresets.LookGuids(target, look.existingId);
                foreach (var item in look.items)
                {
                    var status = item.record != null && worn.Contains(item.record.guid) ? "already" : item.status;
                    row.items.Add(new LookCopyItemDto
                    {
                        name = AvatarWardrobeCatalog.GetRecord(PrefabGuid(item.source))?.displayName ?? item.source.name,
                        variant = item.record == null ? "" : AvatarWardrobeCatalog.DisplayVariant(item.record),
                        status = status,
                        guid = item.record?.guid ?? PrefabGuid(item.source),
                    });
                }
                review.looks.Add(row);
            }
            return review;
        }

        private static LookCopyResultDto CopyLooks(string ids, string targetId, string existing)
        {
            if (SceneAvatar == null) return new LookCopyResultDto { message = WardrobeStrings.T("install.noavatar") };
            if (EditorApplication.isPlayingOrWillChangePlaymode) return new LookCopyResultDto { message = WardrobeStrings.T("server.leave.play.mode.before.editing.the.avatar") };
            if (UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow)
                return new LookCopyResultDto { message = WardrobeStrings.T("server.finish.the.upload.or.batch.before.editing.the.avatar") };
            var source = SceneAvatar;
            var target = CopyTarget(targetId, out var message);
            if (target == null) return new LookCopyResultDto { message = message };
            var plan = PlanLookCopy(source, target, CopyIds(ids), existing == "merge");
            if (plan.Count == 0) return new LookCopyResultDto { message = WardrobeStrings.T("preset.notfound") };

            var result = new LookCopyResultDto { target = target.gameObject.name, targetId = target.GetInstanceID() };
            // The edit runs on the target, as if it were selected; the selection comes back after.
            SceneAvatar = target;
            try
            {
                var edit = EditAvatar("Copy looks to " + target.gameObject.name, () =>
                {
                    ApplyLookCopy(target, plan, result);
                    return new ResultDto { ok = 1 };
                }, trackUploadSettingsInUndo: true);
                if (edit.ok != 1) return new LookCopyResultDto { message = edit.message };
            }
            finally { SceneAvatar = source; }
            result.ok = 1;
            return result;
        }

        private static void ApplyLookCopy(VRCAvatarDescriptor target, List<CopyLook> plan, LookCopyResultDto result)
        {
            AvatarWardrobePresets.CurrentBase(out var targetKey, out var unused);
            var installedItems = new List<(CopyItem item, GameObject instance, string lookId)>();
            foreach (var look in plan)
            {
                var lookId = look.existingId;
                if (string.IsNullOrEmpty(lookId))
                {
                    var saved = AvatarWardrobePresets.SavePreset("", look.preset.name);
                    if (!saved.ok) throw new InvalidOperationException(saved.message);
                    lookId = saved.preset.id;
                    AvatarWardrobePresets.CopyLookUploadProfile(look.preset.id, lookId);
                }
                AvatarWardrobePresets.EnsureSceneHolder(lookId);
                var preset = AvatarWardrobePresets.GetPreset(lookId);
                var holder = string.IsNullOrEmpty(preset?.legacyPath) ? target.transform : target.transform.Find(preset.legacyPath);
                if (holder == null) throw new InvalidOperationException("The look folder is missing from the scene.");
                var worn = AvatarWardrobePresets.LookGuids(target, lookId);
                result.looks++;
                foreach (var item in look.items)
                {
                    if (item.record == null) { result.missing++; continue; }
                    if (worn.Contains(item.record.guid)) continue;
                    var installed = AvatarWardrobeCatalog.Install(target, item.record, false, false, false, lookId, true);
                    if (!installed.success || installed.instance == null) { result.missing++; continue; }
                    var instance = installed.instance;
                    if (instance.transform.parent != holder && !holder.IsChildOf(instance.transform))
                    {
                        OutfitToggleGenerator.RemoveWardrobeOutfit(target, instance);
                        Undo.SetTransformParent(instance.transform, holder, "Copy look");
                    }
                    if (!item.active) { Undo.RecordObject(instance, "Copy look"); instance.SetActive(false); }
                    AvatarWardrobePresets.SetAssignment(item.record.guid, targetKey, lookId);
                    worn.Add(item.record.guid);
                    installedItems.Add((item, instance, lookId));
                    result.items++;
                    if (item.status == "variant") result.variants++;
                }
            }
            OutfitToggleGenerator.SyncPresetSelection(target);

            // Menu groups by name: reuse a target group of the same name, else create it.
            foreach (var group in installedItems.Where(x => !string.IsNullOrEmpty(x.item.groupName)).GroupBy(x => (x.lookId, x.item.groupName)))
            {
                var existingGroup = AvatarWardrobePresets.MenuGroups(group.Key.lookId)
                    .FirstOrDefault(g => string.Equals(g.name, group.Key.groupName, StringComparison.OrdinalIgnoreCase));
                var groupId = existingGroup?.id ?? AvatarWardrobePresets.UpdateMenuGroup(group.Key.lookId, "", group.Key.groupName, null, null, "save");
                foreach (var (item, instance, lookId) in group)
                    AvatarWardrobePresets.UpdateMenuGroup(lookId, groupId, null,
                        AnimationUtility.CalculateTransformPath(instance.transform, target.transform), item.record.guid, "assign");
            }
            OutfitToggleGenerator.SyncMenuGroups(target);

            // Item settings last: part toggles first, since part defaults live on them.
            foreach (var (item, instance, lookId) in installedItems)
            {
                if (item.partToggles) OutfitToggleGenerator.GeneratePartToggles(target, instance);
                if (!ApplyCopiedParts(target, item, instance)) result.settingsSkipped++;
            }
        }

        // Moves the source's part settings onto the new instance. Body meshes are matched by
        // path, then by name, then by any body mesh with that shape; whatever has no match on
        // the target is left out. Returns false when something was left out.
        private static bool ApplyCopiedParts(VRCAvatarDescriptor target, CopyItem item, GameObject instance)
        {
            var edit = JsonUtility.FromJson<WardrobePartsEdit>(JsonUtility.ToJson(item.edit));
            var complete = true;
            var body = OutfitToggleGenerator.BodyMeshes(target, instance);
            var bodyPaths = body.GroupBy(r => OutfitToggleGenerator.AvatarPath(target, r.transform)).ToDictionary(g => g.Key, g => g.First());
            var parts = OutfitToggleGenerator.ItemParts(instance).GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.Count());
            string MapMesh(string path, string shape)
            {
                if (bodyPaths.TryGetValue(path ?? "", out var exact) && exact.sharedMesh.GetBlendShapeIndex(shape ?? "") >= 0) return path;
                var name = (path ?? "").Split('/').Last();
                var byName = body.FirstOrDefault(r => r.name == name && r.sharedMesh.GetBlendShapeIndex(shape ?? "") >= 0)
                    ?? body.FirstOrDefault(r => r.sharedMesh.GetBlendShapeIndex(shape ?? "") >= 0);
                return byName == null ? null : OutfitToggleGenerator.AvatarPath(target, byName.transform);
            }
            edit.parts = edit.parts.Where(part =>
            {
                if (string.IsNullOrEmpty(part.key)) return true;
                var ok = parts.TryGetValue(part.key, out var count) && count == 1;
                if (!ok) complete = false;
                return ok;
            }).ToList();
            foreach (var part in edit.parts)
            {
                var kept = new List<WardrobeShapeEdit>();
                foreach (var shape in part.shapes ?? new List<WardrobeShapeEdit>())
                {
                    var mesh = MapMesh(shape.mesh, shape.shape);
                    if (mesh == null) { complete = false; continue; }
                    shape.mesh = mesh;
                    kept.Add(shape);
                }
                part.shapes = kept;
            }
            // Sliders on the item's own meshes follow the item's new path; body sliders are matched like shapes.
            var newPath = OutfitToggleGenerator.AvatarPath(target, instance.transform);
            var sliderMeshes = OutfitToggleGenerator.SliderMeshes(target, instance);
            var sliders = new List<WardrobeSliderEdit>();
            foreach (var slider in edit.sliders ?? new List<WardrobeSliderEdit>())
            {
                string mesh;
                if (slider.mesh == item.sourcePath || (slider.mesh ?? "").StartsWith(item.sourcePath + "/", StringComparison.Ordinal))
                    mesh = newPath + slider.mesh.Substring(item.sourcePath.Length);
                else mesh = MapMesh(slider.mesh, slider.shape);
                if (mesh == null || !sliderMeshes.TryGetValue(mesh, out var renderer) || renderer.sharedMesh.GetBlendShapeIndex(slider.shape ?? "") < 0)
                { complete = false; continue; }
                slider.mesh = mesh;
                sliders.Add(slider);
            }
            edit.sliders = sliders;
            edit.syncMesh = "";
            try { OutfitToggleGenerator.ValidatePartsEdit(target, instance, edit); }
            catch (ArgumentException)
            {
                // Last resort: keep what cannot clash (part defaults and Blendshape Sync).
                complete = false;
                edit.sliders = new List<WardrobeSliderEdit>();
                foreach (var part in edit.parts) part.shapes = new List<WardrobeShapeEdit>();
            }
            OutfitToggleGenerator.ApplyPartsEdit(target, instance, edit);
            return complete;
        }
    }
}
