using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace OutfitToggleGenerator
{
    internal static class WardrobeAppearanceEditor
    {
        private const string OutputRoot = "Assets/AvatarWardrobeGenerated/Appearance";
        private static readonly Dictionary<string, Pending> Reviews = new Dictionary<string, Pending>();
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        [Serializable] internal sealed class ColorDto
        {
            public float r, g, b, a = 1;
            internal ColorDto() { }
            internal ColorDto(Color color) { r = color.r; g = color.g; b = color.b; a = color.a; }
            internal Color Value => new Color(r, g, b, a);
        }
        [Serializable] internal sealed class PropertyDto
        {
            public string name, label, kind, textureGuid, textureName, reason;
            public float value, min, max = 1;
            public ColorDto color;
            public bool editable;
        }
        [Serializable] internal sealed class SlotDto
        {
            public int slot, materialId;
            public string name, shader, path, reason;
            public List<PropertyDto> properties = new List<PropertyDto>();
        }
        [Serializable] internal sealed class ShapeDto { public int index; public string name; public float value; public bool editable; }
        [Serializable] internal sealed class RendererDto
        {
            public int id, objectId;
            public string name, path;
            public List<SlotDto> slots = new List<SlotDto>();
            public List<ShapeDto> shapes = new List<ShapeDto>();
        }
        [Serializable] internal sealed class SnapshotDto
        {
            public int ok, avatarId;
            public string revision, message;
            public List<RendererDto> renderers = new List<RendererDto>();
        }
        [Serializable] internal sealed class CommandDto
        {
            public string action, revision, property, kind, textureGuid, shapeName;
            public int avatarId, rendererId, slot, materialId, shapeIndex;
            public float value;
            public ColorDto color;
        }
        [Serializable] internal sealed class ReviewDto
        {
            public int ok;
            public string token, message, target, before, after;
            public ColorDto beforeColor, afterColor;
        }
        [Serializable] internal sealed class ApplyDto { public int avatarId; public string token; }
        [Serializable] internal sealed class TextureDto { public string guid, name, path; public int width, height; }
        [Serializable] internal sealed class TexturesDto { public int ok; public string message; public List<TextureDto> textures = new List<TextureDto>(); }
        private sealed class Pending { internal CommandDto command; internal DateTime expires; internal string textureHash; }

        internal static SnapshotDto Snapshot(VRCAvatarDescriptor avatar)
        {
            try { return Capture(avatar); }
            catch (Exception error) { return new SnapshotDto { message = error.Message }; }
        }
        private static SnapshotDto Capture(VRCAvatarDescriptor avatar)
        {
            var scene = WardrobeSceneEditor.Snapshot(avatar);
            if (scene.ok != 1) throw new InvalidOperationException(scene.message);
            var renderers = avatar.GetComponentsInChildren<Renderer>(true).Where(x => x is MeshRenderer || x is SkinnedMeshRenderer).ToArray();
            if (renderers.Length > 256) throw new InvalidOperationException("This avatar has too many renderers for this focused editor. Use Unity Inspector.");
            var result = new SnapshotDto { ok = 1, avatarId = avatar.GetInstanceID() };
            var revision = new StringBuilder(scene.revision);
            foreach (var renderer in renderers)
            {
                var dto = new RendererDto { id = renderer.GetInstanceID(), objectId = renderer.gameObject.GetInstanceID(), name = renderer.name,
                    path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform) };
                var materials = renderer.sharedMaterials;
                if (materials.Length > 64) throw new InvalidOperationException("A renderer exceeds 64 material slots. Use Unity Inspector.");
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    var slot = new SlotDto { slot = i, materialId = material == null ? 0 : material.GetInstanceID(), name = material == null ? "Empty slot" : material.name,
                        shader = material == null || material.shader == null ? "" : material.shader.name, path = material == null ? "" : AssetDatabase.GetAssetPath(material) };
                    if (material == null) slot.reason = "This slot has no material. Assign one in Unity Inspector.";
                    else
                    {
                        revision.Append('\n').Append(material.GetInstanceID()).Append(EditorJsonUtility.ToJson(material));
                        if (!string.IsNullOrEmpty(slot.path)) revision.Append(AssetDatabase.GetAssetDependencyHash(slot.path));
                        if (!SupportedShader(material.shader)) slot.reason = "This shader has no validated appearance adapter. Use Unity Inspector.";
                        else slot.properties = Properties(material);
                    }
                    dto.slots.Add(slot);
                }
                if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                {
                    var mesh = skin.sharedMesh;
                    if (mesh.blendShapeCount > 512) throw new InvalidOperationException("A mesh exceeds 512 shape controls. Use Unity Inspector.");
                    revision.Append('\n').Append(mesh.GetInstanceID());
                    var path = AssetDatabase.GetAssetPath(mesh); if (!string.IsNullOrEmpty(path)) revision.Append(AssetDatabase.GetAssetDependencyHash(path));
                    for (var i = 0; i < mesh.blendShapeCount; i++)
                    {
                        var value = skin.GetBlendShapeWeight(i); var name = mesh.GetBlendShapeName(i);
                        dto.shapes.Add(new ShapeDto { index = i, name = name, value = value, editable = Finite(value) && value >= 0 && value <= 100 });
                        revision.Append('\n').Append(name).Append(':').Append(value.ToString("R", Invariant));
                    }
                }
                result.renderers.Add(dto);
            }
            if (revision.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Material state exceeds the review limit. Use Unity Inspector.");
            using (var hash = SHA256.Create()) result.revision = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(revision.ToString()))).Replace("-", "");
            return result;
        }
        private static bool SupportedShader(Shader shader)
        {
            if (shader == null) return false;
            return shader.name == "Standard" || shader.name == "Standard (Specular setup)" || shader.name == "lilToon" || shader.name.StartsWith("Hidden/lilToon", StringComparison.Ordinal);
        }
        private static List<PropertyDto> Properties(Material material)
        {
            var result = new List<PropertyDto>();
            var known = new Dictionary<string, string> { { "_Color", "Main tint" }, { "_MainTex", "Main texture" },
                { "_Metallic", "Metallic" }, { "_Glossiness", "Smoothness" }, { "_Cutoff", "Alpha cutoff" }, { "_ShadowStrength", "Shadow strength" } };
            foreach (var pair in known)
            {
                var index = material.shader.FindPropertyIndex(pair.Key); if (index < 0 || !material.HasProperty(pair.Key)) continue;
                var type = material.shader.GetPropertyType(index);
                var dto = new PropertyDto { name = pair.Key, label = pair.Value, editable = true };
                if (pair.Key == "_Color" && type == ShaderPropertyType.Color)
                {
                    dto.kind = "color"; dto.color = new ColorDto(material.GetColor(pair.Key));
                    if (!ValidColor(dto.color)) { dto.editable = false; dto.reason = "HDR or out-of-range tints require Unity Inspector."; }
                }
                else if (pair.Key == "_MainTex" && type == ShaderPropertyType.Texture && material.shader.GetPropertyTextureDimension(index) == TextureDimension.Tex2D)
                {
                    dto.kind = "texture"; var texture = material.GetTexture(pair.Key); var path = texture == null ? "" : AssetDatabase.GetAssetPath(texture);
                    dto.textureGuid = string.IsNullOrEmpty(path) ? "" : AssetDatabase.AssetPathToGUID(path); dto.textureName = texture == null ? "None" : texture.name;
                }
                else if (type == ShaderPropertyType.Range || type == ShaderPropertyType.Float)
                {
                    dto.kind = "float"; dto.value = material.GetFloat(pair.Key);
                    if (type == ShaderPropertyType.Range) { var range = material.shader.GetPropertyRangeLimits(index); dto.min = Mathf.Max(0, range.x); dto.max = Mathf.Min(1, range.y); }
                    if (!Finite(dto.value) || dto.value < dto.min || dto.value > dto.max) { dto.editable = false; dto.reason = "Out-of-range shader values require Unity Inspector."; }
                }
                else continue;
                result.Add(dto);
            }
            return result;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool ValidColor(ColorDto color) => color != null && new[] { color.r, color.g, color.b, color.a }.All(x => Finite(x) && x >= 0 && x <= 1);
        private static Renderer Target(VRCAvatarDescriptor avatar, CommandDto command)
        {
            var target = EditorUtility.InstanceIDToObject(command.rendererId) as Renderer;
            if (target == null || EditorUtility.IsPersistent(target) || target.gameObject.scene != avatar.gameObject.scene ||
                (target.transform != avatar.transform && !target.transform.IsChildOf(avatar.transform)) || !(target is MeshRenderer || target is SkinnedMeshRenderer))
                throw new InvalidOperationException("This exact renderer is no longer on the pinned avatar. Refresh Appearance.");
            return target;
        }
        private static Texture2D Texture(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (guid.Length != 32 || !guid.All(Uri.IsHexDigit)) throw new InvalidOperationException("Choose a texture from this project.");
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal)) throw new InvalidOperationException("Choose a texture from this project.");
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) throw new InvalidOperationException("The selected Texture2D is missing. Choose it again.");
            return texture;
        }
        private static Material Material(Renderer target, CommandDto command)
        {
            var materials = target.sharedMaterials;
            if (command.slot < 0 || command.slot >= materials.Length || materials[command.slot] == null || materials[command.slot].GetInstanceID() != command.materialId)
                throw new InvalidOperationException("The exact material slot changed. Refresh Appearance.");
            return materials[command.slot];
        }
        private static void Validate(VRCAvatarDescriptor avatar, CommandDto command, SnapshotDto snapshot)
        {
            if (command == null || command.avatarId != snapshot.avatarId || string.IsNullOrEmpty(command.revision) || command.revision != snapshot.revision)
                throw new InvalidOperationException("The pinned avatar or its appearance changed. Refresh and review again.");
            var target = Target(avatar, command);
            if (command.action == "blendshape")
            {
                var skin = target as SkinnedMeshRenderer; var mesh = skin == null ? null : skin.sharedMesh;
                if (mesh == null || command.shapeIndex < 0 || command.shapeIndex >= mesh.blendShapeCount || mesh.GetBlendShapeName(command.shapeIndex) != command.shapeName)
                    throw new InvalidOperationException("This exact shape control changed. Refresh Appearance.");
                if (!Finite(command.value) || command.value < 0 || command.value > 100) throw new InvalidOperationException("Use a static shape value between 0 and 100.");
            }
            else if (command.action == "material")
            {
                var material = Material(target, command);
                if (!SupportedShader(material.shader)) throw new InvalidOperationException("This shader requires Unity Inspector.");
                var property = Properties(material).SingleOrDefault(x => x.name == command.property && x.kind == command.kind && x.editable);
                if (property == null) throw new InvalidOperationException("This property is outside the validated appearance controls.");
                if (property.kind == "color" && !ValidColor(command.color)) throw new InvalidOperationException("Use tint channels between 0 and 1.");
                if (property.kind == "float" && (!Finite(command.value) || command.value < property.min || command.value > property.max)) throw new InvalidOperationException("Use a value inside this property's supported range.");
                if (property.kind == "texture") Texture(command.textureGuid);
            }
            else throw new InvalidOperationException("Unknown appearance change.");
        }
        internal static ReviewDto Review(CommandDto command)
        {
            try
            {
                var avatar = AvatarWardrobeServer.SceneAvatar; var snapshot = Capture(avatar); Validate(avatar, command, snapshot);
                var target = Target(avatar, command);
                var result = new ReviewDto { ok = 1, target = AnimationUtility.CalculateTransformPath(target.transform, avatar.transform) + " · #" + target.GetInstanceID() };
                if (command.action == "blendshape")
                {
                    result.before = ((SkinnedMeshRenderer)target).GetBlendShapeWeight(command.shapeIndex).ToString("0.###", Invariant);
                    result.after = command.value.ToString("0.###", Invariant);
                    result.message = "Static shape: " + command.shapeName + ". Expression animations may overwrite this weight. The review does not render a candidate avatar.";
                }
                else
                {
                    var material = Material(target, command); var property = Properties(material).Single(x => x.name == command.property);
                    result.target += " · slot " + command.slot + " · " + material.name;
                    if (command.kind == "color") { result.beforeColor = property.color; result.afterColor = command.color; result.before = ColorText(property.color); result.after = ColorText(command.color); }
                    else if (command.kind == "float") { result.before = property.value.ToString("0.###", Invariant); result.after = command.value.ToString("0.###", Invariant); }
                    else { result.before = property.textureName; result.after = string.IsNullOrEmpty(command.textureGuid) ? "None" : Texture(command.textureGuid).name; }
                    result.message = "Change " + property.label + " on this slot using a new project-local material. Other slots and source materials keep their current values. Undo restores the assignment; the generated material remains available for redo.";
                    if (command.kind == "texture") result.message += " Choose a texture authored for this slot's UV layout; matching is not inferred.";
                }
                foreach (var key in Reviews.Where(x => x.Value.expires < DateTime.UtcNow).Select(x => x.Key).ToArray()) Reviews.Remove(key);
                while (Reviews.Count >= 16) Reviews.Remove(Reviews.OrderBy(x => x.Value.expires).First().Key);
                result.token = Guid.NewGuid().ToString("N");
                Reviews[result.token] = new Pending { command = JsonUtility.FromJson<CommandDto>(JsonUtility.ToJson(command)), expires = DateTime.UtcNow.AddMinutes(2), textureHash = TextureHash(command) };
                return result;
            }
            catch (Exception error) { return new ReviewDto { message = error.Message }; }
        }
        private static string ColorText(ColorDto color) => string.Join(", ", new[] { color.r, color.g, color.b, color.a }.Select(x => x.ToString("0.###", Invariant)));
        private static string TextureHash(CommandDto command) => command.kind != "texture" || string.IsNullOrEmpty(command.textureGuid) ? "" : AssetDatabase.GetAssetDependencyHash(AssetDatabase.GUIDToAssetPath(command.textureGuid)).ToString();

        internal static AvatarWardrobeServer.ResultDto Apply(ApplyDto request)
        {
            string createdPath = null;
            try
            {
                if (request == null || string.IsNullOrEmpty(request.token) || !Reviews.TryGetValue(request.token, out var pending)) throw new InvalidOperationException("This appearance review is missing or already used. Review the change again.");
                Reviews.Remove(request.token);
                if (pending.expires < DateTime.UtcNow) throw new InvalidOperationException("This appearance review expired. Review the change again.");
                var avatar = AvatarWardrobeServer.SceneAvatar;
                if (avatar == null || request.avatarId != avatar.GetInstanceID()) throw new InvalidOperationException("The pinned avatar changed. Review the change again.");
                var command = pending.command; Validate(avatar, command, Capture(avatar));
                if (pending.textureHash != TextureHash(command)) throw new InvalidOperationException("The texture changed after review. Review the change again.");
                var renderer = Target(avatar, command);
                var result = AvatarWardrobeServer.EditAvatar("Apply appearance", () => {
                    if (AvatarWardrobeServer.SceneAvatar != avatar) return new AvatarWardrobeServer.ResultDto { message = "The pinned avatar changed." };
                    try
                    {
                        Undo.RegisterCompleteObjectUndo(renderer, "Apply appearance");
                        if (command.action == "blendshape") ((SkinnedMeshRenderer)renderer).SetBlendShapeWeight(command.shapeIndex, command.value);
                        else
                        {
                            var source = Material(renderer, command);
                            EnsureOutputFolder();
                            createdPath = OutputRoot + "/" + Guid.NewGuid().ToString("N") + ".mat";
                            var copy = new Material(source) { name = source.name + " (Wardrobe appearance)" };
                            try
                            {
                                if (command.kind == "color") copy.SetColor(command.property, command.color.Value);
                                else if (command.kind == "float") copy.SetFloat(command.property, command.value);
                                else copy.SetTexture(command.property, Texture(command.textureGuid));
                                AssetDatabase.CreateAsset(copy, createdPath); AssetDatabase.SaveAssetIfDirty(copy);
                                var materials = renderer.sharedMaterials; materials[command.slot] = copy; renderer.sharedMaterials = materials;
                            }
                            catch { if (!EditorUtility.IsPersistent(copy)) Object.DestroyImmediate(copy); throw; }
                        }
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer); EditorUtility.SetDirty(renderer); EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                        return new AvatarWardrobeServer.ResultDto { ok = 1, id = createdPath ?? "", message = "Appearance applied. Save the scene to keep it; Undo is available in Unity." };
                    }
                    catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
                }, migratePresets: false);
                if (result.ok != 1 && createdPath != null) AssetDatabase.DeleteAsset(createdPath);
                return result;
            }
            catch (Exception error) { if (createdPath != null) AssetDatabase.DeleteAsset(createdPath); return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
        }
        private static void EnsureOutputFolder()
        {
            var current = "Assets";
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Appearance output requires a project-local Assets folder.");
            foreach (var part in OutputRoot.Split('/').Skip(1))
            {
                var path = current + "/" + part;
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("The appearance output folder must be project-local, without symbolic links.");
                if (!AssetDatabase.IsValidFolder(path))
                {
                    if (File.Exists(path) || Directory.Exists(path)) throw new InvalidOperationException("The appearance output folder is not a Unity folder. Resolve it in Unity first.");
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, part))) throw new InvalidOperationException("Could not create the appearance output folder.");
                }
                current = path;
            }
        }
        internal static TexturesDto Textures(string search)
        {
            try
            {
                Capture(AvatarWardrobeServer.SceneAvatar);
                search = (search ?? "").Trim();
                if (search.Length < 2 || search.Length > 80) throw new InvalidOperationException("Enter 2–80 characters from a texture name.");
                var result = new TexturesDto { ok = 1 };
                foreach (var guid in AssetDatabase.FindAssets(search + " t:Texture2D", new[] { "Assets" }).Take(50))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid); var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path); if (texture == null) continue;
                    result.textures.Add(new TextureDto { guid = guid, path = path, name = texture.name, width = texture.width, height = texture.height });
                }
                result.message = "Up to 50 project textures. Choose one authored for the selected slot's UV layout."; return result;
            }
            catch (Exception error) { return new TexturesDto { message = error.Message }; }
        }
    }
}
