using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using UnityEngine;

// Catalog organization (tags, hidden items) and worn-item part editing routes.
namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        [Serializable]
        private sealed class TagDto
        {
            public string id = "";
            public string name = "";
            public string color = "";
            public int count;
        }

        [Serializable]
        private sealed class OrganizationDto
        {
            public int ok = 1;
            public string message = "";
            public List<TagDto> tags = new List<TagDto>();
            public List<string> hiddenPaths = new List<string>();
            public int hiddenCount;
            public TagDto tag;
        }

        [Serializable]
        private sealed class MeshDto
        {
            public string path = "";
            public string name = "";
            public List<string> shapes = new List<string>();
        }

        [Serializable]
        private sealed class PartDto
        {
            public string key = "";
            public string name = "";
            public int worn = 1;
            public int toggle;
            public List<WardrobeShapeEdit> shapes = new List<WardrobeShapeEdit>();
        }

        [Serializable]
        private sealed class PartsDto
        {
            public int ok;
            public string message = "";
            public int copies;
            public int partToggles;
            public int sync;
            public int syncCandidates;
            public List<MeshDto> meshes = new List<MeshDto>();
            public List<MeshDto> itemMeshes = new List<MeshDto>();
            public List<PartDto> parts = new List<PartDto>();
            public List<WardrobeSliderEdit> sliders = new List<WardrobeSliderEdit>();
            public int maxSliders;
        }

        private static readonly HashSet<string> organizationWriteRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/tag_save", "/api/tag_delete", "/api/family_tag", "/api/family_hide", "/api/folder_hide"
        };

        // Writes that change project settings or editor selection, never the avatar.
        internal static bool IsOrganizationWrite(string path) => organizationWriteRoutes.Contains(path) || path == "/api/reveal_asset";

        // Selects and pings an item's prefab in the Project window.
        private static ResultDto RevealAsset(string guid)
        {
            var path = IsAssetGuid(guid) ? UnityEditor.AssetDatabase.GUIDToAssetPath(guid) : "";
            var asset = string.IsNullOrEmpty(path) ? null : UnityEditor.AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) return new ResultDto { message = WardrobeStrings.T("err.notfound") };
            UnityEditor.EditorUtility.FocusProjectWindow();
            UnityEditor.Selection.activeObject = asset;
            UnityEditor.EditorGUIUtility.PingObject(asset);
            return new ResultDto { ok = 1 };
        }

        private static List<string> IdList(Dictionary<string, string> query)
        {
            query.TryGetValue("ids", out var json);
            if (string.IsNullOrEmpty(json) || json.Length > 32768) return new List<string>();
            try { return (JsonUtility.FromJson<RecentFamilyIds>(json).ids ?? new string[0]).Where(id => !string.IsNullOrEmpty(id)).Take(500).ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        private static OrganizationDto GetOrganization()
        {
            var families = CachedFamilies().Concat(CachedCandidates()).ToList();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var hidden = 0;
            foreach (var family in families)
            {
                if (WardrobeOrganization.IsHidden(family)) { hidden++; continue; }
                foreach (var id in WardrobeOrganization.TagsFor(family.id))
                    counts[id] = counts.TryGetValue(id, out var count) ? count + 1 : 1;
            }
            return new OrganizationDto
            {
                tags = WardrobeOrganization.Tags()
                    .OrderBy(tag => tag.name, StringComparer.OrdinalIgnoreCase)
                    .Select(tag => new TagDto { id = tag.id, name = tag.name, color = tag.color, count = counts.TryGetValue(tag.id, out var count) ? count : 0 })
                    .ToList(),
                hiddenPaths = WardrobeOrganization.HiddenPaths(),
                hiddenCount = hidden,
            };
        }

        private static OrganizationDto EditOrganization(Func<OrganizationDto> edit)
        {
            try
            {
                var result = edit() ?? GetOrganization();
                matchesCache.Clear();
                return result;
            }
            catch (ArgumentException error) { return new OrganizationDto { ok = 0, message = error.Message }; }
        }

        private static List<GameObject> PresetInstances(string guid, string target)
        {
            return AvatarWardrobePresets.PrefabInstances(SceneAvatar, guid)
                .Where(item => AvatarWardrobePresets.ItemPreset(item, SceneAvatar) == target).ToList();
        }

        private static PartsDto GetItemParts(string guid, string target)
        {
            if (SceneAvatar == null) return new PartsDto { message = WardrobeStrings.T("server.select.an.avatar.first") };
            var instances = PresetInstances(guid, target);
            if (instances.Count == 0) return new PartsDto { message = WardrobeStrings.T("server.the.prefab.is.not.installed.in.this.preset") };
            var item = instances[0];
            var meshes = OutfitToggleGenerator.BodyMeshes(SceneAvatar, item);
            var result = new PartsDto
            {
                ok = 1,
                copies = instances.Count,
                partToggles = OutfitToggleGenerator.HasPartToggles(item) ? 1 : 0,
                sync = OutfitToggleGenerator.HasBlendshapeSync(item) ? 1 : 0,
                syncCandidates = OutfitToggleGenerator.BlendshapeSyncCandidates(item, meshes.FirstOrDefault()),
                meshes = meshes.Select(renderer => new MeshDto
                {
                    path = OutfitToggleGenerator.AvatarPath(SceneAvatar, renderer.transform),
                    name = renderer.name,
                    shapes = Enumerable.Range(0, Math.Min(renderer.sharedMesh.blendShapeCount, 4000))
                        .Select(index => renderer.sharedMesh.GetBlendShapeName(index)).ToList(),
                }).ToList(),
            };
            result.itemMeshes = OutfitToggleGenerator.ItemMeshes(item).Select(renderer => new MeshDto
            {
                path = OutfitToggleGenerator.AvatarPath(SceneAvatar, renderer.transform),
                name = renderer.name,
                shapes = Enumerable.Range(0, Math.Min(renderer.sharedMesh.blendShapeCount, 4000))
                    .Select(index => renderer.sharedMesh.GetBlendShapeName(index)).ToList(),
            }).ToList();
            result.sliders = OutfitToggleGenerator.Sliders(SceneAvatar, item);
            result.maxSliders = OutfitToggleGenerator.MaxSliders;
            result.parts.Add(new PartDto { key = "", name = item.name, shapes = OutfitToggleGenerator.ShapeChanges(SceneAvatar, item) });
            foreach (var part in OutfitToggleGenerator.ItemParts(item))
                result.parts.Add(new PartDto
                {
                    key = part.name,
                    name = part.name,
                    worn = OutfitToggleGenerator.PartWorn(item, part) ? 1 : 0,
                    toggle = OutfitToggleGenerator.PartHasToggle(item, part) ? 1 : 0,
                    shapes = OutfitToggleGenerator.ShapeChanges(SceneAvatar, part),
                });
            return result;
        }

        private static ResultDto SaveItemParts(string guid, string target, string json)
        {
            if (SceneAvatar == null) return new ResultDto { message = WardrobeStrings.T("server.select.an.avatar.first") };
            WardrobePartsEdit edit;
            try { edit = JsonUtility.FromJson<WardrobePartsEdit>(json ?? ""); }
            catch (Exception) { return new ResultDto { message = WardrobeStrings.T("parts.invalid") }; }
            var instances = PresetInstances(guid, target);
            if (instances.Count == 0) return new ResultDto { message = WardrobeStrings.T("server.the.prefab.is.not.installed.in.this.preset") };
            // The page edits the first copy; sliders on that copy's own meshes map onto each copy.
            var first = OutfitToggleGenerator.AvatarPath(SceneAvatar, instances[0].transform);
            var edits = instances.Select(instance =>
            {
                var copy = JsonUtility.FromJson<WardrobePartsEdit>(JsonUtility.ToJson(edit));
                var path = OutfitToggleGenerator.AvatarPath(SceneAvatar, instance.transform);
                foreach (var slider in copy.sliders ?? new List<WardrobeSliderEdit>())
                    if (slider.mesh == first || (slider.mesh ?? "").StartsWith(first + "/", StringComparison.Ordinal))
                        slider.mesh = path + slider.mesh.Substring(first.Length);
                return copy;
            }).ToList();
            try { for (var i = 0; i < instances.Count; i++) OutfitToggleGenerator.ValidatePartsEdit(SceneAvatar, instances[i], edits[i]); }
            catch (ArgumentException error) { return new ResultDto { message = error.Message }; }
            return EditAvatar("Edit wardrobe item parts", () =>
            {
                for (var i = 0; i < instances.Count; i++) OutfitToggleGenerator.ApplyPartsEdit(SceneAvatar, instances[i], edits[i]);
                return new ResultDto { ok = 1, message = WardrobeStrings.T("parts.saved") };
            });
        }

        private static bool HandleOrganization(HttpListenerContext context, string path, string requestCode)
        {
            if (path == "/api/organization")
            {
                WriteMainJson(context, GetOrganization, requestCode);
                return true;
            }
            if (path == "/api/reveal_asset")
            {
                Query(context.Request.Url.Query).TryGetValue("guid", out var revealGuid);
                WriteMainJson(context, () => RevealAsset(revealGuid), requestCode);
                return true;
            }
            if (path == "/api/item_parts")
            {
                var query = Query(context.Request.Url.Query);
                query.TryGetValue("guid", out var guid);
                query.TryGetValue("target", out var target);
                query.TryGetValue("op", out var op);
                if (op == "save")
                {
                    query.TryGetValue("data", out var data);
                    WriteMainJson(context, () => SaveItemParts(guid, target, data), requestCode);
                }
                else WriteMainJson(context, () => GetItemParts(guid, target), requestCode);
                return true;
            }
            if (!organizationWriteRoutes.Contains(path)) return false;
            {
                var query = Query(context.Request.Url.Query);
                query.TryGetValue("id", out var id);
                query.TryGetValue("name", out var name);
                query.TryGetValue("color", out var color);
                query.TryGetValue("tag", out var tag);
                query.TryGetValue("on", out var on);
                query.TryGetValue("folder", out var folder);
                var ids = IdList(query);
                WriteMainJson(context, () => EditOrganization(() =>
                {
                    switch (path)
                    {
                        case "/api/tag_save":
                            var saved = WardrobeOrganization.SaveTag(id, name, color);
                            var result = GetOrganization();
                            result.tag = new TagDto { id = saved.id, name = saved.name, color = saved.color };
                            return result;
                        case "/api/tag_delete": WardrobeOrganization.DeleteTag(id); break;
                        case "/api/family_tag": WardrobeOrganization.SetFamilyTag(ids, tag, on == "1"); break;
                        case "/api/family_hide": WardrobeOrganization.SetHidden(ids, on == "1"); break;
                        case "/api/folder_hide": WardrobeOrganization.SetHiddenPath(folder, on == "1"); break;
                    }
                    return null;
                }), requestCode);
                return true;
            }
        }
    }
}
