using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace OutfitToggleGenerator
{
    // Advanced scene editing intentionally has no wardrobe/menu interpretation.
    internal static class WardrobeSceneEditor
    {
        private const int MaxObjects = 8192, MaxComponents = 128, MaxProperties = 128;
        private const int MaxRevisionBytes = 32 * 1024 * 1024;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        [Serializable] internal sealed class VectorDto
        {
            public float x, y, z;
            internal VectorDto() { }
            internal VectorDto(Vector3 v) { x = v.x; y = v.y; z = v.z; }
            internal Vector3 Value() => new Vector3(x, y, z);
        }
        [Serializable] internal sealed class PropertyDto
        {
            public string path, label, kind, value;
            public bool editable;
            public string[] options = Array.Empty<string>();
        }
        [Serializable] internal sealed class ComponentDto
        {
            public int id;
            public string type;
            public List<PropertyDto> properties = new List<PropertyDto>();
        }
        [Serializable] internal sealed class ObjectDto
        {
            public int id, parentId, depth;
            public string name, path;
            public bool active;
            public VectorDto position, rotation, scale;
            public List<ComponentDto> components = new List<ComponentDto>();
        }
        [Serializable] internal sealed class SnapshotDto
        {
            public int ok, avatarId;
            public string revision, message;
            public List<ObjectDto> objects = new List<ObjectDto>();
        }
        [Serializable] internal sealed class CommandDto
        {
            public string action, revision, name, propertyPath, value;
            public int avatarId, objectId, parentId, componentId;
            public bool active;
            public VectorDto position, rotation, scale;
        }

        internal static SnapshotDto Snapshot(VRCAvatarDescriptor avatar)
        {
            try { return Capture(avatar); }
            catch (Exception error) { return new SnapshotDto { message = error.Message }; }
        }

        private static SnapshotDto Capture(VRCAvatarDescriptor avatar)
        {
            ValidateAvatar(avatar);
            var nodes = avatar.GetComponentsInChildren<Transform>(true);
            if (nodes.Length > MaxObjects)
                throw new InvalidOperationException("This avatar hierarchy is too large for the browser editor. Use Unity's Inspector.");
            var result = new SnapshotDto { ok = 1, avatarId = avatar.GetInstanceID() };
            using (var hash = SHA256.Create())
            {
                int bytes = 0;
                void Add(string value)
                {
                    var data = Encoding.UTF8.GetBytes((value ?? "") + "\n");
                    bytes += data.Length;
                    if (bytes > MaxRevisionBytes)
                        throw new InvalidOperationException("The avatar's serialized state is too large for safe browser editing. Use Unity's Inspector.");
                    hash.TransformBlock(data, 0, data.Length, data, 0);
                }
                Add(avatar.GetInstanceID() + ":" + avatar.gameObject.scene.handle + ":" + avatar.gameObject.scene.path);
                foreach (var node in nodes)
                {
                    var go = node.gameObject;
                    var dto = new ObjectDto {
                        id = go.GetInstanceID(), parentId = node == avatar.transform ? 0 : node.parent.gameObject.GetInstanceID(),
                        name = go.name, active = go.activeSelf,
                        path = AnimationUtility.CalculateTransformPath(node, avatar.transform),
                        position = new VectorDto(node.localPosition), rotation = new VectorDto(node.localEulerAngles), scale = new VectorDto(node.localScale)
                    };
                    for (var p = node; p != avatar.transform; p = p.parent) dto.depth++;
                    Add(dto.id + ":" + dto.parentId + ":" + node.GetSiblingIndex() + ":" + go.name + ":" + go.activeSelf);
                    Add(EditorJsonUtility.ToJson(go));
                    var components = go.GetComponents<Component>();
                    if (components.Length > MaxComponents)
                        throw new InvalidOperationException("An object has too many components for browser editing. Use Unity's Inspector.");
                    foreach (var component in components)
                    {
                        if (component == null) { Add("missing-script"); dto.components.Add(new ComponentDto { type = "Missing script — use Unity Inspector" }); continue; }
                        // Include all serialized state in the revision, including hidden/reference fields.
                        Add(component.GetInstanceID() + ":" + component.GetType().AssemblyQualifiedName);
                        Add(EditorJsonUtility.ToJson(component));
                        if (component is Transform) continue;
                        var c = new ComponentDto { id = component.GetInstanceID(), type = component.GetType().Name };
                        using (var serialized = new SerializedObject(component))
                        {
                            var property = serialized.GetIterator();
                            var first = true;
                            while (property.NextVisible(first))
                            {
                                first = false;
                                if (c.properties.Count == MaxProperties)
                                {
                                    c.properties.Add(new PropertyDto { label = "Additional fields", kind = "unsupported", value = "Use Unity Inspector" });
                                    break;
                                }
                                c.properties.Add(ReadProperty(property));
                            }
                        }
                        dto.components.Add(c);
                    }
                    result.objects.Add(dto);
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                result.revision = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
            return result;
        }

        private static PropertyDto ReadProperty(SerializedProperty property)
        {
            var dto = new PropertyDto { path = property.propertyPath, label = property.displayName, kind = "unsupported", value = property.type };
            if (!property.editable || property.propertyPath == "m_Script" || property.propertyPath == "m_Name" ||
                property.propertyPath == "m_ObjectHideFlags" || (property.isArray && property.propertyType != SerializedPropertyType.String))
                return dto;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: dto.kind = "bool"; dto.value = property.boolValue ? "true" : "false"; break;
                case SerializedPropertyType.Integer:
                    if (property.type != "int" && property.type != "long" && property.type != "long long") return dto;
                    dto.kind = "int"; dto.value = property.longValue.ToString(Invariant); break;
                case SerializedPropertyType.Float:
                    if (property.type != "float" && property.type != "double") return dto;
                    dto.kind = "float"; dto.value = property.doubleValue.ToString("R", Invariant); break;
                case SerializedPropertyType.String:
                    if (property.stringValue.Length > 4096) { dto.value = "Long text — use Unity Inspector"; return dto; }
                    dto.kind = "string"; dto.value = property.stringValue; break;
                case SerializedPropertyType.Enum:
                    if (property.enumValueIndex < 0 || property.enumNames.Length == 0) return dto;
                    dto.kind = "enum"; dto.value = property.enumValueIndex.ToString(Invariant); dto.options = property.enumDisplayNames; break;
                default: return dto;
            }
            dto.editable = true;
            return dto;
        }

        internal static AvatarWardrobeServer.ResultDto Execute(CommandDto command)
        {
            try
            {
                var avatar = AvatarWardrobeServer.SceneAvatar;
                ValidateAvatar(avatar);
                if (command == null || command.avatarId != avatar.GetInstanceID())
                    throw new InvalidOperationException("The pinned avatar changed. Refresh the Advanced Scene view.");
                if (string.IsNullOrEmpty(command.revision) || Capture(avatar).revision != command.revision)
                    throw new InvalidOperationException("The scene changed since this view was loaded. Refresh and review the object before editing.");
                var target = FindObject(avatar, command.objectId);
                if (command.action == "inspect")
                {
                    Selection.activeGameObject = target;
                    EditorGUIUtility.PingObject(target);
                    EditorApplication.ExecuteMenuItem("Window/General/Inspector");
                    return new AvatarWardrobeServer.ResultDto { ok = 1, id = target.GetInstanceID().ToString(), message = "Object selected in Unity Inspector." };
                }
                var edit = Prepare(avatar, target, command);
                return AvatarWardrobeServer.EditAvatar("Advanced Scene: " + command.action, () => {
                    // Recheck the pinned target at the write boundary; never read Selection.
                    if (AvatarWardrobeServer.SceneAvatar != avatar)
                        return new AvatarWardrobeServer.ResultDto { message = "The pinned avatar changed. Refresh the view." };
                    try
                    {
                        var changed = edit();
                        EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                        return new AvatarWardrobeServer.ResultDto { ok = 1, id = changed == null ? "" : changed.GetInstanceID().ToString(), message = "Scene updated. Undo is available in Unity." };
                    }
                    catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = "Scene edit failed and was rolled back: " + error.Message }; }
                }, migratePresets: false);
            }
            catch (Exception error) { return new AvatarWardrobeServer.ResultDto { message = error.Message }; }
        }

        private static Func<GameObject> Prepare(VRCAvatarDescriptor avatar, GameObject target, CommandDto command)
        {
            const string label = "Edit scene object";
            switch (command.action)
            {
                case "rename":
                    var name = ValidName(command.name);
                    if (target.name != name) CheckName(target.transform.parent, name, target);
                    return () => { Undo.RecordObject(target, label); target.name = name; RecordPrefab(target); return target; };
                case "active":
                    return () => { Undo.RecordObject(target, label); target.SetActive(command.active); RecordPrefab(target); return target; };
                case "transform":
                    ValidateVector(command.position); ValidateVector(command.rotation); ValidateVector(command.scale);
                    return () => {
                        Undo.RecordObject(target.transform, label);
                        target.transform.localPosition = command.position.Value();
                        target.transform.localEulerAngles = command.rotation.Value();
                        target.transform.localScale = command.scale.Value();
                        RecordPrefab(target.transform); return target;
                    };
                case "create":
                    var parent = command.parentId == 0 ? target : FindObject(avatar, command.parentId);
                    var childName = ValidName(string.IsNullOrWhiteSpace(command.name) ? UniqueName(parent.transform, "GameObject") : command.name);
                    CheckName(parent.transform, childName, null);
                    return () => {
                        var child = new GameObject(childName);
                        Undo.RegisterCreatedObjectUndo(child, label);
                        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(child, parent.scene);
                        Undo.SetTransformParent(child.transform, parent.transform, label);
                        child.transform.localPosition = Vector3.zero; child.transform.localRotation = Quaternion.identity; child.transform.localScale = Vector3.one;
                        return child;
                    };
                case "duplicate":
                    CheckNotRoot(avatar, target);
                    var duplicateName = UniqueName(target.transform.parent, target.name + " Copy");
                    return () => {
                        var duplicate = UnityEngine.Object.Instantiate(target, target.transform.parent, false);
                        Undo.RegisterCreatedObjectUndo(duplicate, label);
                        duplicate.name = duplicateName;
                        duplicate.transform.SetSiblingIndex(target.transform.GetSiblingIndex() + 1);
                        return duplicate;
                    };
                case "delete":
                    CheckNotRoot(avatar, target);
                    return () => { Undo.DestroyObjectImmediate(target); return null; };
                case "reparent":
                    CheckNotRoot(avatar, target);
                    var destination = FindObject(avatar, command.parentId);
                    if (destination == target || destination.transform.IsChildOf(target.transform))
                        throw new InvalidOperationException("An object cannot be moved into itself or one of its descendants.");
                    CheckName(destination.transform, target.name, target);
                    return () => { Undo.SetTransformParent(target.transform, destination.transform, label); RecordPrefab(target.transform); return target; };
                case "property":
                    var component = EditorUtility.InstanceIDToObject(command.componentId) as Component;
                    if (component == null || component.gameObject != target || component is Transform)
                        throw new InvalidOperationException("The component changed. Refresh and select it again.");
                    using (var serialized = new SerializedObject(component))
                    {
                        var property = FindExposedProperty(serialized, command.propertyPath);
                        ValidatePropertyValue(property, command.value);
                    }
                    return () => {
                        Undo.RecordObject(component, label);
                        using (var serialized = new SerializedObject(component))
                        {
                            var property = FindExposedProperty(serialized, command.propertyPath);
                            SetProperty(property, command.value);
                            serialized.ApplyModifiedProperties();
                        }
                        RecordPrefab(component); return target;
                    };
                default: throw new InvalidOperationException("Unsupported scene command.");
            }
        }

        private static SerializedProperty FindExposedProperty(SerializedObject serialized, string path)
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Select a supported field.");
            var property = serialized.GetIterator();
            var first = true;
            for (int i = 0; i < MaxProperties && property.NextVisible(first); i++)
            {
                first = false;
                if (property.propertyPath == path && ReadProperty(property).editable) return property.Copy();
            }
            throw new InvalidOperationException("This field must be edited in Unity Inspector.");
        }

        private static void ValidatePropertyValue(SerializedProperty property, string value)
        {
            if (value == null || value.Length > 4096) throw new InvalidOperationException("The field value is too long.");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    if (value != "true" && value != "false") throw new InvalidOperationException("Use true or false for this field."); break;
                case SerializedPropertyType.Integer:
                    if (!long.TryParse(value, NumberStyles.Integer, Invariant, out var integer)) throw new InvalidOperationException("Use a whole number within the field's range.");
                    if (property.type != "long" && property.type != "long long" && (integer < int.MinValue || integer > int.MaxValue)) throw new InvalidOperationException("The integer is outside this field's supported range.");
                    break;
                case SerializedPropertyType.Float:
                    if (!double.TryParse(value, NumberStyles.Float, Invariant, out var number) || double.IsNaN(number) || double.IsInfinity(number) ||
                        (property.type != "double" && Math.Abs(number) > float.MaxValue)) throw new InvalidOperationException("Use a finite number within the field's range."); break;
                case SerializedPropertyType.Enum:
                    if (!int.TryParse(value, NumberStyles.Integer, Invariant, out var option) || option < 0 || option >= property.enumNames.Length)
                        throw new InvalidOperationException("Choose one of the listed values."); break;
                case SerializedPropertyType.String: break;
                default: throw new InvalidOperationException("Use Unity Inspector for this field.");
            }
        }
        private static void SetProperty(SerializedProperty property, string value)
        {
            ValidatePropertyValue(property, value);
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: property.boolValue = value == "true"; break;
                case SerializedPropertyType.Integer: property.longValue = long.Parse(value, Invariant); break;
                case SerializedPropertyType.Float: property.doubleValue = double.Parse(value, Invariant); break;
                case SerializedPropertyType.String: property.stringValue = value; break;
                case SerializedPropertyType.Enum: property.enumValueIndex = int.Parse(value, Invariant); break;
            }
        }
        private static void ValidateAvatar(VRCAvatarDescriptor avatar)
        {
            if (avatar == null || EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid() || !avatar.gameObject.scene.isLoaded ||
                EditorSceneManager.IsPreviewScene(avatar.gameObject.scene) || PrefabStageUtility.GetPrefabStage(avatar.gameObject) != null || AvatarWardrobeServer.IsStagingAvatar(avatar))
                throw new InvalidOperationException("Choose a scene avatar. Source prefabs and temporary build scenes cannot be edited here.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer ||
                AvatarWardrobeServer.UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow)
                throw new InvalidOperationException("Finish the active Unity operation before using Advanced Scene.");
        }
        private static GameObject FindObject(VRCAvatarDescriptor avatar, int id)
        {
            var target = EditorUtility.InstanceIDToObject(id) as GameObject;
            if (target == null || EditorUtility.IsPersistent(target) || target.scene != avatar.gameObject.scene ||
                (target != avatar.gameObject && !target.transform.IsChildOf(avatar.transform)))
                throw new InvalidOperationException("This exact object is no longer part of the pinned avatar. Refresh the view.");
            return target;
        }
        private static void CheckNotRoot(VRCAvatarDescriptor avatar, GameObject target)
        {
            if (target == avatar.gameObject) throw new InvalidOperationException("The pinned avatar root cannot be deleted, duplicated, or reparented here.");
        }
        private static void ValidateVector(VectorDto value)
        {
            if (value == null || !Finite(value.x) || !Finite(value.y) || !Finite(value.z)) throw new InvalidOperationException("Enter finite X, Y, and Z values.");
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static string ValidName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.IndexOfAny(new[] {'/', '\\'}) >= 0 || value.Any(char.IsControl))
                throw new InvalidOperationException("Use a nonempty object name without slashes or control characters (up to 200 characters).");
            return value;
        }
        private static void CheckName(Transform parent, string name, GameObject except)
        {
            if (parent == null) return;
            foreach (Transform child in parent)
                if (child.gameObject != except && child.name == name) throw new InvalidOperationException("A sibling already has this name. Choose a unique name.");
        }
        private static string UniqueName(Transform parent, string preferred)
        {
            var names = new HashSet<string>();
            foreach (Transform child in parent) names.Add(child.name);
            var name = preferred;
            for (var index = 2; names.Contains(name); index++) name = preferred + " " + index;
            return name;
        }
        private static void RecordPrefab(UnityEngine.Object target)
        {
            EditorUtility.SetDirty(target);
            if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }
    }
}
