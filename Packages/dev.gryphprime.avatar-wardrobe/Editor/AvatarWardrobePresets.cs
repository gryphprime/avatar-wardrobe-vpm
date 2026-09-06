// Preset store for multi-avatar uploads: each preset is a named outfit set
// uploaded to its own avatar. Assignments (outfit guid to preset id or
// common) are scoped per parent avatar. Display names are labels only:
// avatarRootName, outfitName and scenePath are frozen at creation so renames
// never orphan Blueprint IDs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobePresets
    {
        internal const string CommonTarget = "common";

        [Serializable]
        internal sealed class WardrobePreset
        {
            public string id = "";
            public string name = "";
            public string baseKey = "";
            public string baseName = "";
            public string avatarRootName = "";
            public string outfitName = "";
            public string scenePath = "";
            public string updated = "";
            // Optional, additive fields for presets discovered from existing scene folders.
            public string legacyScene = "";
            public string legacyPath = "";
            public string legacyObjectId = "";
            public List<MenuGroup> menuGroups = new List<MenuGroup>();
        }

        [Serializable]
        internal sealed class MenuGroup
        {
            public string id = "";
            public string name = "";
            public List<string> paths = new List<string>();
        }

        internal static List<MenuGroup> MenuGroups(string id)
        {
            CurrentBase(out var key, out var unused);
            return (id == CommonTarget ? CommonPreset(key) : GetPreset(id))?.menuGroups ?? new List<MenuGroup>();
        }

        internal static WardrobePreset CommonPreset(string baseKey)
        {
            return LoadFile().commonPresets.FirstOrDefault(p => p.baseKey == baseKey)
                ?? new WardrobePreset { id = CommonTarget, name = "Common Preset", baseKey = baseKey };
        }

        internal static void EnsureSceneHolder(string id)
        {
            CurrentBase(out var key, out var unused);
            var file = CloneFile(LoadFile());
            var preset = file.presets.FirstOrDefault(p => p.id == id && p.baseKey == key);
            if (preset == null || !string.IsNullOrEmpty(preset.legacyPath)) return;
            EnsureSceneHolder(preset);
            SaveFile(file);
        }

        private static void EnsureSceneHolder(WardrobePreset preset)
        {
            var avatar = AvatarWardrobeServer.SceneAvatar;
            if (avatar == null || !string.IsNullOrEmpty(preset.legacyPath)) return;
            var members = SceneMembers(preset, avatar);
            var parent = avatar.transform.Find("Outfits");
            if (parent == null)
            {
                var folder = new GameObject("Outfits");
                Undo.RegisterCreatedObjectUndo(folder, "Create preset folder");
                folder.transform.SetParent(avatar.transform, false);
                parent = folder.transform;
            }
            string name = preset.name.Replace('/', '-');
            string unique = name;
            for (int n = 2; parent.Find(unique) != null; n++) unique = name + " (" + n + ")";
            var holder = new GameObject(unique);
            Undo.RegisterCreatedObjectUndo(holder, "Create preset folder");
            holder.transform.SetParent(parent, false);
            foreach (var member in members)
            {
                OutfitToggleGenerator.RemoveWardrobeOutfit(avatar, member);
                Undo.SetTransformParent(member.transform, holder.transform, "Organize preset items");
            }
            preset.legacyPath = AnimationUtility.CalculateTransformPath(holder.transform, avatar.transform);
            preset.legacyScene = avatar.gameObject.scene.path;
            preset.legacyObjectId = GlobalObjectId.GetGlobalObjectIdSlow(holder).ToString();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
        }

        internal static string UpdateMenuGroup(string presetId, string groupId, string name, string path, string guid, string op)
        {
            if (AvatarWardrobeServer.SceneAvatar == null) throw new InvalidOperationException("Select an avatar first.");
            CurrentBase(out var key, out var unused);
            var file = CloneFile(LoadFile());
            var preset = presetId == CommonTarget ? file.commonPresets.FirstOrDefault(p => p.baseKey == key)
                : file.presets.FirstOrDefault(p => p.id == presetId && p.baseKey == key);
            if (presetId == CommonTarget && preset == null)
            {
                preset = new WardrobePreset { id = CommonTarget, name = "Common Preset", baseKey = key };
                file.commonPresets.Add(preset);
            }
            if (preset == null) throw new InvalidOperationException("Select a preset first.");
            if (preset.menuGroups == null) preset.menuGroups = new List<MenuGroup>();
            var group = preset.menuGroups.FirstOrDefault(g => g.id == groupId);
            if (op == "save")
            {
                if (presetId != CommonTarget) EnsureSceneHolder(preset);
                name = (name ?? "").Trim();
                if (name.Length == 0) throw new InvalidOperationException("Enter a menu group name.");
                if (group == null) { group = new MenuGroup { id = Guid.NewGuid().ToString("N") }; preset.menuGroups.Add(group); }
                group.name = name;
            }
            else if (op == "delete")
            {
                if (group != null) preset.menuGroups.Remove(group);
            }
            else if (op == "assign")
            {
                var avatar = AvatarWardrobeServer.SceneAvatar;
                if (avatar == null) throw new InvalidOperationException("Select an avatar first.");
                Transform target = string.IsNullOrEmpty(path) ? null : avatar.transform.Find(path);
                if (target == null && !string.IsNullOrEmpty(guid))
                {
                    var instance = PrefabInstances(avatar, guid).FirstOrDefault(item => ItemPreset(item, avatar) == presetId);
                    if (instance != null) target = instance.transform;
                }
                if (target == null || target == avatar.transform)
                    throw new InvalidOperationException("The item must be installed under this preset.");
                if (presetId == CommonTarget)
                {
                    var namedRoots = PresetsForBase(key).SelectMany(p => SceneMembers(p, avatar));
                    if (namedRoots.Any(r => target == r.transform || target.IsChildOf(r.transform) || r.transform.IsChildOf(target)))
                        throw new InvalidOperationException("Move the item to Common Preset before grouping it here.");
                    var assetGuid = AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target.gameObject));
                    if (string.IsNullOrEmpty(assetGuid) || AvatarWardrobeCatalog.GetRecord(assetGuid) == null)
                        throw new InvalidOperationException("Choose an installed wardrobe item.");
                    // Explicitly shared assignment keeps previously unassigned items in every staged upload.
                    file.assignments.RemoveAll(a => a.baseKey == key && a.guid == assetGuid && a.target == CommonTarget);
                    file.assignments.Add(new WardrobePresetAssignment { baseKey = key, guid = assetGuid, target = CommonTarget });
                }
                else if (!SceneMembers(preset, avatar).Any(r => target == r.transform || target.IsChildOf(r.transform)))
                    throw new InvalidOperationException("The item must be installed under this preset.");
                path = AnimationUtility.CalculateTransformPath(target, avatar.transform);
                if (!string.IsNullOrEmpty(groupId) && group == null) throw new InvalidOperationException("Menu group not found.");
                // A parent and its child cannot be independent exclusive options.
                foreach (var other in preset.menuGroups.SelectMany(g => g.paths))
                    if (other != path && (other.StartsWith(path + "/", StringComparison.Ordinal) || path.StartsWith(other + "/", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Group either the parent item or its children, not both.");
                foreach (var g in preset.menuGroups) g.paths.Remove(path);
                if (group != null) group.paths.Add(path);
            }
            SaveFile(file);
            OutfitToggleGenerator.RegeneratePresetToggles(AvatarWardrobeServer.SceneAvatar);
            return group?.id ?? "";
        }

        [Serializable]
        internal sealed class WardrobePresetAssignment
        {
            public string guid = "";
            public string baseKey = "";
            public string target = "";
        }

        [Serializable]
        private sealed class PresetFile
        {
            public bool separateAvatarUploads;
            public List<WardrobePreset> commonPresets = new List<WardrobePreset>();
            public List<WardrobePreset> presets = new List<WardrobePreset>();
            public List<WardrobePresetAssignment> assignments = new List<WardrobePresetAssignment>();
        }

        internal sealed class PresetSaveResult
        {
            public bool ok;
            public string message = "";
            public WardrobePreset preset;
        }

        private static string PresetFilePath
        {
            get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "ProjectSettings", "AvatarWardrobePresets.json"); }
        }

        private static readonly Dictionary<VRCAvatarDescriptor, string> instanceScopes = new Dictionary<VRCAvatarDescriptor, string>();

        private sealed class ScopeLookup
        {
            public string scenePath, key, name;
            public PresetFile migratedFile;
            public int migratedRevision = -1;
        }
        private static readonly Dictionary<VRCAvatarDescriptor, ScopeLookup> scopeLookups = new Dictionary<VRCAvatarDescriptor, ScopeLookup>();
        private static bool lookupEventsRegistered;
        internal static int HierarchyRevision { get; private set; }
        private static void WatchHierarchy()
        {
            if (lookupEventsRegistered) return;
            lookupEventsRegistered = true;
            EditorApplication.hierarchyChanged += InvalidateHierarchyLookups;
            Undo.undoRedoPerformed += InvalidateHierarchyLookups;
        }
        private static void InvalidateHierarchyLookups()
        {
            HierarchyRevision++;
        }

        // Presets belong to a scene instance, not to the shared avatar prefab asset.
        internal static void CurrentBase(out string baseKey, out string baseName)
        {
            WatchHierarchy();
            var avatar = AvatarWardrobeServer.SceneAvatar;
            baseName = avatar == null ? string.Empty : avatar.gameObject.name;
            if (avatar == null) { baseKey = "none"; return; }
            ScopeLookup lookup;
            if (!scopeLookups.TryGetValue(avatar, out lookup)) scopeLookups[avatar] = lookup = new ScopeLookup();
            var scenePath = avatar.gameObject.scene.path;
            if (lookup.key == null || lookup.scenePath != scenePath)
            {
                lookup.scenePath = scenePath;
                lookup.key = string.IsNullOrEmpty(scenePath) ? "scene-session:" + avatar.GetInstanceID()
                    : "scene-object:" + GlobalObjectId.GetGlobalObjectIdSlow(avatar.gameObject);
            }
            baseKey = lookup.key;
            string previousScope;
            if (instanceScopes.TryGetValue(avatar, out previousScope) && previousScope != baseKey)
            {
                // Saving an unsaved scene gives the same live instance a durable identity.
                var file = CloneFile(LoadFile());
                foreach (var preset in file.presets.Concat(file.commonPresets).Where(p => p.baseKey == previousScope)) preset.baseKey = baseKey;
                foreach (var assignment in file.assignments.Where(a => a.baseKey == previousScope)) assignment.baseKey = baseKey;
                SaveFile(file);
            }
            instanceScopes[avatar] = baseKey;
            var fileSnapshot = LoadFile();
            if (!ReferenceEquals(lookup.migratedFile, fileSnapshot) || lookup.migratedRevision != HierarchyRevision || lookup.name != baseName)
            {
                MigrateLegacyOwner(avatar, baseKey, baseName);
                lookup.migratedFile = LoadFile();
                lookup.migratedRevision = HierarchyRevision;
                lookup.name = baseName;
            }
        }

        private static bool IsLegacyBaseKey(string key)
        {
            return key != null && (key.StartsWith("avatar:", StringComparison.Ordinal) || key.StartsWith("name:", StringComparison.Ordinal));
        }

        private static void MigrateLegacyOwner(VRCAvatarDescriptor avatar, string baseKey, string baseName)
        {
            var original = LoadFile();
            var owned = original.presets.Where(p => IsLegacyBaseKey(p.baseKey) && LegacyPresetBelongsTo(p, avatar)).ToList();
            // Common data had no owner identity. Copy it only when its concrete member paths
            // exist in this instance; retain the legacy record for other existing avatars.
            var common = original.commonPresets.FirstOrDefault(p => IsLegacyBaseKey(p.baseKey) &&
                (p.baseName == baseName || owned.Any(preset => preset.baseKey == p.baseKey)) &&
                p.menuGroups != null && p.menuGroups.SelectMany(g => g.paths).Any() &&
                p.menuGroups.SelectMany(g => g.paths).All(path => !string.IsNullOrEmpty(path) && avatar.transform.Find(path) != null));
            bool copyCommon = common != null && !original.commonPresets.Any(p => p.baseKey == baseKey);
            if (owned.Count == 0 && !copyCommon) return;
            var file = CloneFile(original);
            var ids = new HashSet<string>(owned.Select(p => p.id));
            foreach (var preset in file.presets.Where(p => ids.Contains(p.id)))
            {
                preset.baseKey = baseKey;
                preset.baseName = baseName;
            }
            foreach (var assignment in file.assignments.Where(a => IsLegacyBaseKey(a.baseKey) && ids.Contains(a.target))) assignment.baseKey = baseKey;
            if (copyCommon)
            {
                var copy = JsonUtility.FromJson<WardrobePreset>(JsonUtility.ToJson(common));
                copy.baseKey = baseKey; copy.baseName = baseName;
                file.commonPresets.Add(copy);
                foreach (var assignment in original.assignments.Where(a => a.baseKey == common.baseKey && a.target == CommonTarget))
                    file.assignments.Add(new WardrobePresetAssignment { baseKey = baseKey, target = CommonTarget, guid = assignment.guid });
            }
            // Staging keys, preset IDs, Blueprint IDs and engine configuration are unchanged.
            SaveFile(file);
        }

        private static bool LegacyPresetBelongsTo(WardrobePreset preset, VRCAvatarDescriptor avatar)
        {
            if (!string.IsNullOrEmpty(preset.legacyObjectId) && GlobalObjectId.TryParse(preset.legacyObjectId, out var id))
            {
                var holder = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
                return holder != null && holder != avatar.gameObject && holder.transform.IsChildOf(avatar.transform);
            }
            // Older records without object IDs require both the recorded avatar name and a live holder.
            return preset.baseName == avatar.gameObject.name && !string.IsNullOrEmpty(preset.legacyPath) &&
                (string.IsNullOrEmpty(preset.legacyScene) || preset.legacyScene == avatar.gameObject.scene.path) &&
                avatar.transform.Find(preset.legacyPath) != null;
        }

        internal static bool SeparateAvatarUploads => LoadFile().separateAvatarUploads;

        internal static void SetSeparateAvatarUploads(bool enabled)
        {
            var file = CloneFile(LoadFile());
            file.separateAvatarUploads = enabled;
            SaveFile(file);
        }

        internal static List<GameObject> PrefabInstances(VRCAvatarDescriptor avatar, string guid)
        {
            if (avatar == null || string.IsNullOrEmpty(guid)) return new List<GameObject>();
            return avatar.GetComponentsInChildren<Transform>(true)
                .Select(t => PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject))
                .Where(root => root != null && root != avatar.gameObject && root.transform.IsChildOf(avatar.transform))
                .Distinct().Where(root => AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root)) == guid).ToList();
        }

        internal static string ItemPreset(GameObject item, VRCAvatarDescriptor avatar)
        {
            CurrentBase(out var key, out var unused);
            var path = AnimationUtility.CalculateTransformPath(item.transform, avatar.transform);
            var presets = PresetsForBase(key);
            var owner = presets.Where(p => !string.IsNullOrEmpty(p.legacyPath) &&
                (string.IsNullOrEmpty(p.legacyScene) || p.legacyScene == avatar.gameObject.scene.path) &&
                (path == p.legacyPath || path.StartsWith(p.legacyPath + "/", StringComparison.Ordinal)))
                .OrderByDescending(p => p.legacyPath.Length).FirstOrDefault();
            if (owner != null) return owner.id;
            var guid = AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(item));
            var target = GetAssignment(guid, key);
            // Folder-backed presets are resolved by the actual instance, never a global GUID assignment.
            return presets.Any(p => p.id == target && string.IsNullOrEmpty(p.legacyPath)) ? target : CommonTarget;
        }

        internal static void ForgetRemovedItems(string target, IEnumerable<string> paths)
        {
            CurrentBase(out var key, out var unused);
            var file = CloneFile(LoadFile());
            var removed = paths.ToList();
            var preset = target == CommonTarget ? file.commonPresets.FirstOrDefault(p => p.baseKey == key)
                : file.presets.FirstOrDefault(p => p.id == target && p.baseKey == key);
            foreach (var group in preset?.menuGroups ?? new List<MenuGroup>())
                group.paths.RemoveAll(path => removed.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal)));
            file.assignments.RemoveAll(a => a.baseKey == key && a.target == target &&
                !PrefabInstances(AvatarWardrobeServer.SceneAvatar, a.guid).Any(instance => ItemPreset(instance, AvatarWardrobeServer.SceneAvatar) == target));
            SaveFile(file);
        }

        internal static List<GameObject> SceneMembers(WardrobePreset preset, VRCAvatarDescriptor avatar)
        {
            var result = new List<GameObject>();
            if (preset == null || avatar == null) return result;
            if (!string.IsNullOrEmpty(preset.legacyPath) &&
                (string.IsNullOrEmpty(preset.legacyScene) || preset.legacyScene == avatar.gameObject.scene.path))
            {
                var holder = avatar.transform.Find(preset.legacyPath);
                if (holder != null && holder != avatar.transform) result.Add(holder.gameObject);
            }
            foreach (var assignment in AssignmentsForBase(preset.baseKey))
            {
                if (assignment.target != preset.id) continue;
                foreach (var instance in PrefabInstances(avatar, assignment.guid))
                    if (ItemPreset(instance, avatar) == preset.id && !result.Any(root => instance.transform.IsChildOf(root.transform))) result.Add(instance);
            }
            return result;
        }

        internal static void ShowInUnity(string id, VRCAvatarDescriptor avatar)
        {
            if (avatar == null) throw new InvalidOperationException("Select an avatar in the Unity launcher first.");
            CurrentBase(out var baseKey, out var baseName);
            var preset = GetPreset(id);
            bool commonOnly = id == CommonTarget;
            if (!commonOnly && (preset == null || preset.baseKey != baseKey))
                throw new InvalidOperationException("Preset does not belong to the selected avatar.");
            var selected = commonOnly ? new List<GameObject>() : SceneMembers(preset, avatar);
            if (!commonOnly && selected.Count == 0) throw new InvalidOperationException("This preset has no installed scene items.");
            // Resolve the current hierarchy on every click, including manually activated roots.
            var roots = PresetsForBase(baseKey).SelectMany(p => SceneMembers(p, avatar)).Distinct().ToList();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Show wardrobe preset");
            foreach (var root in roots)
            {
                bool active = selected.Any(item => item == root || item.transform.IsChildOf(root.transform));
                Undo.RecordObject(root, "Show wardrobe preset");
                root.SetActive(active);
                PrefabUtility.RecordPrefabInstancePropertyModifications(root);
            }
            foreach (var root in selected)
                for (var parent = root.transform.parent; parent != null && parent != avatar.transform; parent = parent.parent)
                {
                    if (parent.gameObject.activeSelf) continue;
                    Undo.RecordObject(parent.gameObject, "Show wardrobe preset");
                    parent.gameObject.SetActive(true);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(parent.gameObject);
                }
            Undo.CollapseUndoOperations(group);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            var focus = commonOnly ? avatar.gameObject : selected[0];
            Selection.activeGameObject = focus;
            EditorGUIUtility.PingObject(focus);
        }

        internal static WardrobePreset GetPreset(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return LoadFile().presets.FirstOrDefault(p => p != null && p.id == id);
        }

        internal static string GetPresetName(string id)
        {
            var preset = GetPreset(id);
            return preset == null ? string.Empty : preset.name ?? string.Empty;
        }

        private static PresetFile orderedPresetFile;
        private static VRCAvatarDescriptor orderedPresetAvatar;
        private static string orderedPresetKey;
        private static int orderedPresetRevision = -1;
        private static List<WardrobePreset> orderedPresets;
        internal static List<WardrobePreset> PresetsForBase(string baseKey)
        {
            var file = LoadFile();
            var currentAvatar = AvatarWardrobeServer.SceneAvatar;
            if (orderedPresets != null && ReferenceEquals(file, orderedPresetFile) && currentAvatar == orderedPresetAvatar &&
                orderedPresetKey == baseKey && orderedPresetRevision == HierarchyRevision)
                return new List<WardrobePreset>(orderedPresets);
            var presets = file.presets.Where(p => p != null && p.baseKey == baseKey).ToList();
            var avatar = AvatarWardrobeServer.SceneAvatar;
            if (avatar == null) return presets;
            // Transform traversal preserves Unity sibling order, including inactive presets.
            // Presets without a live holder retain their saved insertion order at the end.
            var hierarchy = avatar.GetComponentsInChildren<Transform>(true)
                .Select((transform, index) => new { transform, index })
                .ToDictionary(entry => entry.transform, entry => entry.index);
            orderedPresets = presets.OrderBy(p =>
            {
                var holder = string.IsNullOrEmpty(p.legacyPath) ||
                    (!string.IsNullOrEmpty(p.legacyScene) && p.legacyScene != avatar.gameObject.scene.path)
                    ? null : avatar.transform.Find(p.legacyPath);
                return holder != null && hierarchy.TryGetValue(holder, out var index) ? index : int.MaxValue;
            }).ToList();
            orderedPresetFile = file; orderedPresetAvatar = currentAvatar;
            orderedPresetKey = baseKey; orderedPresetRevision = HierarchyRevision;
            return new List<WardrobePreset>(orderedPresets);
        }

        internal static List<WardrobePresetAssignment> AssignmentsForBase(string baseKey)
        {
            return LoadFile().assignments
                .Where(a => a != null && a.baseKey == baseKey && !string.IsNullOrEmpty(a.guid))
                .ToList();
        }

        internal static string GetAssignment(string guid, string baseKey)
        {
            if (string.IsNullOrEmpty(guid)) return string.Empty;
            var found = LoadFile().assignments
                .FirstOrDefault(a => a != null && a.guid == guid && a.baseKey == baseKey);
            return found == null ? string.Empty : found.target ?? string.Empty;
        }

        internal static int CountAssigned(string presetId, string baseKey)
        {
            return LoadFile().assignments
                .Count(a => a != null && a.baseKey == baseKey && a.target == presetId);
        }

        internal static PresetSaveResult SavePreset(string id, string name)
        {
            string baseKey;
            string baseName;
            CurrentBase(out baseKey, out baseName);
            if (string.IsNullOrEmpty(baseName))
                return new PresetSaveResult { message = WardrobeStrings.T("install.noavatar") };
            var file = CloneFile(LoadFile());
            WardrobePreset preset = null;
            if (!string.IsNullOrEmpty(id))
                preset = file.presets.FirstOrDefault(p => p != null && p.id == id && p.baseKey == baseKey);
            if (!string.IsNullOrEmpty(id) && preset == null)
                return new PresetSaveResult { message = WardrobeStrings.T("preset.notfound") };
            var clean = (name ?? string.Empty).Trim();
            if (preset == null)
            {
                if (string.IsNullOrEmpty(clean))
                    clean = "Preset " + (file.presets.Count(p => p != null && p.baseKey == baseKey) + 1);
                clean = UniqueName(file, baseKey, clean, null);
                var newId = UniqueId(file);
                preset = new WardrobePreset
                {
                    id = newId,
                    name = clean,
                    baseKey = baseKey,
                    baseName = baseName,
                    avatarRootName = AvatarWardrobeUpload.Safe(baseName) + "_Wardrobe_" + newId,
                    outfitName = clean,
                    scenePath = "Assets/Generated/WardrobeUploads/"
                        + AvatarWardrobeUpload.Safe(baseName) + "_" + newId + "/Upload.unity",
                };
                file.presets.Add(preset);
                EnsureSceneHolder(preset);
            }
            else
            {
                if (string.IsNullOrEmpty(clean)) clean = preset.name;
                preset.name = UniqueName(file, preset.baseKey, clean, preset.id);
                EnsureSceneHolder(preset);
                var avatar = AvatarWardrobeServer.SceneAvatar;
                var holder = string.IsNullOrEmpty(preset.legacyPath) ? null : avatar.transform.Find(preset.legacyPath);
                if (holder != null)
                {
                    var oldPath = preset.legacyPath;
                    Undo.RecordObject(holder.gameObject, "Rename wardrobe preset");
                    holder.name = preset.name.Replace('/', '-');
                    PrefabUtility.RecordPrefabInstancePropertyModifications(holder.gameObject);
                    preset.legacyPath = AnimationUtility.CalculateTransformPath(holder, avatar.transform);
                    foreach (var g in preset.menuGroups ?? new List<MenuGroup>())
                        for (int i = 0; i < g.paths.Count; i++)
                            if (g.paths[i] == oldPath || g.paths[i].StartsWith(oldPath + "/", StringComparison.Ordinal))
                                g.paths[i] = preset.legacyPath + g.paths[i].Substring(oldPath.Length);
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                }
                // Staging keys stay fixed so Blueprint IDs and upload configuration survive renames.
            }
            SaveFile(file);
            OutfitToggleGenerator.SyncPresetSelection(AvatarWardrobeServer.SceneAvatar, SeparateAvatarUploads);
            OutfitToggleGenerator.SyncMenuGroups(AvatarWardrobeServer.SceneAvatar);
            return new PresetSaveResult { ok = true, preset = preset };
        }



        internal static WardrobePreset RegisterLegacy(GameObject source, string path,
            ShiroTools.OutfitProjectData.OutfitData sourceData)
        {
            string baseKey, baseName;
            CurrentBase(out baseKey, out baseName);
            if (source == null || string.IsNullOrEmpty(baseName)) return null;
            var scenePath = source.scene.path;
            // Unsaved scenes have no durable object identity; the hierarchy path is the fallback.
            var objectId = string.IsNullOrEmpty(scenePath) ? "" : GlobalObjectId.GetGlobalObjectIdSlow(source).ToString();
            var file = CloneFile(LoadFile());
            var preset = file.presets.FirstOrDefault(p => p != null && p.baseKey == baseKey &&
                !string.IsNullOrEmpty(p.legacyPath) &&
                ((!string.IsNullOrEmpty(objectId) && p.legacyObjectId == objectId) ||
                 (string.IsNullOrEmpty(p.legacyObjectId) && p.legacyPath == path &&
                  (p.legacyScene == scenePath || string.IsNullOrEmpty(p.legacyScene)))));
            if (preset != null)
            {
                if (preset.legacyPath != path || preset.legacyScene != scenePath || preset.legacyObjectId != objectId)
                {
                    preset.legacyPath = path; preset.legacyScene = scenePath; preset.legacyObjectId = objectId;
                    SaveFile(file);
                }
                return preset;
            }
            var id = UniqueId(file);
            preset = new WardrobePreset {
                id = id, name = UniqueName(file, baseKey, source.name, null),
                baseKey = baseKey, baseName = baseName,
                avatarRootName = AvatarWardrobeUpload.Safe(baseName) + "_Wardrobe_" + id,
                outfitName = source.name,
                scenePath = "Assets/Generated/WardrobeUploads/" + AvatarWardrobeUpload.Safe(baseName) + "_" + id + "/Upload.unity",
                legacyPath = path, legacyScene = scenePath, legacyObjectId = objectId
            };
            // Copy the full engine record once. Subsequent scans never overwrite preset edits.
            var data = ShiroTools.OutfitProjectData.GetOutfit(preset.avatarRootName, preset.outfitName);
            if (sourceData != null) JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(sourceData), data);
            data.name = preset.outfitName;
            var sourceAvatar = ShiroTools.OutfitProjectData.GetAvatar(baseName);
            var targetAvatar = ShiroTools.OutfitProjectData.GetAvatar(preset.avatarRootName);
            targetAvatar.itemDefaults = new List<string>(sourceAvatar.itemDefaults ?? new List<string>());
            targetAvatar.itemDefaultsDecided = new List<string>(sourceAvatar.itemDefaultsDecided ?? new List<string>());
            ShiroTools.OutfitProjectData.Save();
            file.presets.Add(preset);
            SaveFile(file);
            return preset;
        }

        internal static PresetSaveResult DeletePreset(string id)
        {
            string baseKey, baseName;
            CurrentBase(out baseKey, out baseName);
            var file = CloneFile(LoadFile());
            var preset = file.presets.FirstOrDefault(p => p != null && p.id == id && p.baseKey == baseKey);
            if (preset == null)
                return new PresetSaveResult { message = WardrobeStrings.T("preset.notfound") };
            file.presets.Remove(preset);
            file.assignments.RemoveAll(a => a != null && a.target == id);
            SaveFile(file);
            OutfitToggleGenerator.SyncPresetSelection(AvatarWardrobeServer.SceneAvatar, SeparateAvatarUploads);
            OutfitToggleGenerator.SyncMenuGroups(AvatarWardrobeServer.SceneAvatar);
            return new PresetSaveResult { ok = true, preset = preset };
        }

        // Overwrites any previous assignment for the guid. Assigning a
        // variant to a preset evicts its same-family siblings from that
        // preset (one variant per family per avatar). Common is shared and
        // never evicts.
        internal static void SetAssignment(string guid, string baseKey, string target)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(baseKey)) return;
            var file = CloneFile(LoadFile());
            if (!string.IsNullOrEmpty(target) && target != CommonTarget &&
                !file.presets.Any(p => p != null && p.id == target && p.baseKey == baseKey))
                throw new InvalidOperationException(WardrobeStrings.T("preset.notfound"));
            if (!string.IsNullOrEmpty(target) && file.assignments.Any(a => a.guid == guid && a.baseKey == baseKey && a.target == target)) return;
            var owners = new HashSet<string>(PrefabInstances(AvatarWardrobeServer.SceneAvatar, guid)
                .Select(item => ItemPreset(item, AvatarWardrobeServer.SceneAvatar)));
            file.assignments.RemoveAll(a => a != null && a.guid == guid && a.baseKey == baseKey &&
                (string.IsNullOrEmpty(target) || !owners.Contains(a.target)));
            if (!string.IsNullOrEmpty(target))
            {
                if (target != CommonTarget && owners.Contains(CommonTarget) &&
                    !file.assignments.Any(a => a.baseKey == baseKey && a.guid == guid && a.target == CommonTarget))
                    file.assignments.Add(new WardrobePresetAssignment { guid = guid, baseKey = baseKey, target = CommonTarget });
                file.assignments.Add(new WardrobePresetAssignment
                {
                    guid = guid,
                    baseKey = baseKey,
                    target = target,
                });
                if (!string.Equals(target, CommonTarget, StringComparison.Ordinal))
                    EvictSiblings(file, guid, baseKey, target);
            }
            SaveFile(file);
        }

        private static void EvictSiblings(PresetFile file, string guid, string baseKey, string presetId)
        {
            try
            {
                foreach (var family in AvatarWardrobeCatalog.Families())
                {
                    if (family == null || family.variants == null) continue;
                    if (!family.variants.Any(v => v != null && v.guid == guid)) continue;
                    var doomed = new HashSet<string>(family.variants
                        .Where(v => v != null && v.guid != guid)
                        .Select(v => v.guid));
                    // Variants explicitly managed by a menu group may coexist in one preset.
                    var grouped = new HashSet<string>();
                    var preset = file.presets.FirstOrDefault(p => p.id == presetId);
                    var avatar = AvatarWardrobeServer.SceneAvatar;
                    foreach (var group in preset?.menuGroups ?? new List<MenuGroup>())
                        foreach (var path in group.paths)
                        {
                            var item = avatar != null ? avatar.transform.Find(path) : null;
                            if (item != null) grouped.Add(AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(item.gameObject)));
                        }
                    file.assignments.RemoveAll(a => a != null && a.baseKey == baseKey &&
                        a.target == presetId && doomed.Contains(a.guid) && !grouped.Contains(a.guid));
                    return;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Avatar Wardrobe sibling eviction failed: " + exception.Message);
            }
        }

        private static string UniqueId(PresetFile file)
        {
            var taken = new HashSet<string>(file.presets.Where(p => p != null).Select(p => p.id));
            for (;;)
            {
                var candidate = Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private static string UniqueName(PresetFile file, string baseKey, string want, string selfId)
        {
            var taken = new HashSet<string>(file.presets
                .Where(p => p != null && p.baseKey == baseKey && p.id != selfId)
                .Select(p => p.name ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(want)) return want;
            for (var suffix = 2; ; suffix++)
            {
                var candidate = want + " (" + suffix + ")";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private static PresetFile cachedFile;
        private static DateTime cachedStamp;
        private static long cachedLength;
        private static double nextStat;

        private static PresetFile CloneFile(PresetFile file)
        {
            return JsonUtility.FromJson<PresetFile>(JsonUtility.ToJson(file));
        }

        private static PresetFile LoadFile()
        {
            // Called per variant when building the list. Stat at most once per second,
            // and never reparse unchanged settings thousands of times per query.
            if (cachedFile != null && EditorApplication.timeSinceStartup < nextStat) return cachedFile;
            nextStat = EditorApplication.timeSinceStartup + 1;
            var info = new FileInfo(PresetFilePath);
            if (cachedFile != null && (info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue) == cachedStamp &&
                (info.Exists ? info.Length : 0) == cachedLength) return cachedFile;
            var parsed = info.Exists ? JsonUtility.FromJson<PresetFile>(File.ReadAllText(info.FullName)) : new PresetFile();
            if (parsed == null) throw new InvalidDataException("Could not read wardrobe presets. Restore the file before editing it.");
            if (parsed.presets == null) parsed.presets = new List<WardrobePreset>();
            if (parsed.commonPresets == null) parsed.commonPresets = new List<WardrobePreset>();
            if (parsed.assignments == null) parsed.assignments = new List<WardrobePresetAssignment>();
            cachedFile = parsed;
            cachedStamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            cachedLength = info.Exists ? info.Length : 0;
            return cachedFile;
        }

        private static void SaveFile(PresetFile file)
        {
            WardrobeAtomicFile.WriteText(PresetFilePath, JsonUtility.ToJson(file, true));
            cachedFile = file;
            var info = new FileInfo(PresetFilePath);
            cachedStamp = info.LastWriteTimeUtc;
            cachedLength = info.Length;
            nextStat = 0;
        }

        internal static string CaptureSettings()
        {
            return File.Exists(PresetFilePath) ? File.ReadAllText(PresetFilePath) : null;
        }

        internal static void RestoreSettings(string snapshot)
        {
            if (snapshot == null) { if (File.Exists(PresetFilePath)) File.Delete(PresetFilePath); }
            else WardrobeAtomicFile.WriteText(PresetFilePath, snapshot);
            cachedFile = null;
            nextStat = 0;
        }
    }
}
