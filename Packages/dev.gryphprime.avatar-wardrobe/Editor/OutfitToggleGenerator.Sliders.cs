using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

// In-game radial sliders for blendshapes: one MA radial menu item, synced float and
// generated FX layer per slider. Sliders belong to the item and stay active while it is
// on the avatar.
namespace OutfitToggleGenerator
{
    [Serializable]
    internal sealed class WardrobeSliderEdit
    {
        public string mesh = "";
        public string shape = "";
        public string label = "";
        public float value;
    }

    internal static partial class OutfitToggleGenerator
    {
        private const string SlidersKind = "shape-sliders";
        private const string SlidersName = "Avatar Wardrobe Sliders";
        private const string SliderKind = "shape-slider";
        private const string SliderFolder = "Assets/AvatarWardrobeGenerated/Sliders";
        internal const int MaxSliders = 16;

        // Sliders may drive the body or the item's own meshes (skirt length, sleeves…).
        internal static Dictionary<string, SkinnedMeshRenderer> SliderMeshes(VRCAvatarDescriptor avatar, GameObject item)
        {
            var map = MeshMap(avatar, item);
            foreach (var renderer in item.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null || renderer.sharedMesh.blendShapeCount == 0) continue;
                var path = AvatarPath(avatar, renderer.transform);
                if (!map.ContainsKey(path)) map.Add(path, renderer);
            }
            return map;
        }

        internal static List<SkinnedMeshRenderer> ItemMeshes(GameObject item)
        {
            return item.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0).ToList();
        }

        private static string SliderKey(string mesh, string shape) => mesh + "|" + shape;

        internal static List<WardrobeSliderEdit> Sliders(VRCAvatarDescriptor avatar, GameObject item)
        {
            var host = item == null ? null : FindGeneratedHost(item.transform, SlidersKind, SlidersName);
            if (host == null) return new List<WardrobeSliderEdit>();
            var parameters = host.GetComponent<ModularAvatarParameters>();
            var result = new List<WardrobeSliderEdit>();
            foreach (Transform child in host)
            {
                var marker = child.GetComponent<OutfitToggleGeneratedMenu>();
                var menu = child.GetComponent<ModularAvatarMenuItem>();
                if (marker == null || marker.generatedKind != SliderKind || menu == null || string.IsNullOrEmpty(marker.ownerId)) continue;
                var split = marker.ownerId.LastIndexOf('|');
                if (split < 0) continue;
                var parameter = menu.Control?.subParameters?.FirstOrDefault()?.name ?? "";
                var config = parameters == null ? default : parameters.parameters.FirstOrDefault(entry => entry.nameOrPrefix == parameter);
                result.Add(new WardrobeSliderEdit
                {
                    mesh = marker.ownerId.Substring(0, split),
                    shape = marker.ownerId.Substring(split + 1),
                    label = menu.label ?? "",
                    value = Mathf.Round(config.defaultValue * 100f),
                });
            }
            return result;
        }

        internal static void ValidateSliders(VRCAvatarDescriptor avatar, GameObject item, WardrobePartsEdit edit)
        {
            var sliders = edit.sliders ?? new List<WardrobeSliderEdit>();
            if (sliders.Count > MaxSliders) throw new ArgumentException(WardrobeStrings.T("sliders.limit", MaxSliders));
            var meshes = SliderMeshes(avatar, item);
            var seen = new HashSet<string>();
            var fixedShapes = new HashSet<string>((edit.parts ?? new List<WardrobePartEdit>())
                .SelectMany(part => part.shapes ?? new List<WardrobeShapeEdit>())
                .Select(shape => SliderKey(shape.mesh, shape.shape)));
            foreach (var slider in sliders)
            {
                if (!meshes.TryGetValue(slider.mesh ?? "", out var renderer))
                    throw new ArgumentException(WardrobeStrings.T("parts.meshMissing", slider.mesh));
                if (renderer.sharedMesh.GetBlendShapeIndex(slider.shape ?? "") < 0)
                    throw new ArgumentException(WardrobeStrings.T("parts.shapeMissing", slider.shape, renderer.name));
                var key = SliderKey(slider.mesh, slider.shape);
                if (!seen.Add(key)) throw new ArgumentException(WardrobeStrings.T("sliders.duplicate", slider.shape));
                if (fixedShapes.Contains(key)) throw new ArgumentException(WardrobeStrings.T("sliders.conflict", slider.shape));
                if (float.IsNaN(slider.value)) throw new ArgumentException(WardrobeStrings.T("parts.invalid"));
            }
        }

        internal static void SetSliders(VRCAvatarDescriptor avatar, GameObject item, List<WardrobeSliderEdit> sliders)
        {
            var old = FindGeneratedHost(item.transform, SlidersKind, SlidersName);
            var parameterByKey = new Dictionary<string, string>();
            if (old != null)
            {
                foreach (Transform child in old)
                {
                    var marker = child.GetComponent<OutfitToggleGeneratedMenu>();
                    var name = child.GetComponent<ModularAvatarMenuItem>()?.Control?.subParameters?.FirstOrDefault()?.name;
                    if (marker != null && !string.IsNullOrEmpty(marker.ownerId) && !string.IsNullOrEmpty(name)) parameterByKey[marker.ownerId] = name;
                }
                Undo.DestroyObjectImmediate(old.gameObject);
            }
            if (sliders == null || sliders.Count == 0) return;

            var meshes = SliderMeshes(avatar, item);
            var rows = sliders.Select(slider =>
            {
                var key = SliderKey(slider.mesh, slider.shape);
                if (!parameterByKey.TryGetValue(key, out var parameter))
                    parameter = GeneratedParameterPrefix + "Slider_" + Guid.NewGuid().ToString("N").Substring(0, 12);
                var label = string.IsNullOrWhiteSpace(slider.label) ? slider.shape : slider.label.Trim();
                if (label.Length > 40) label = label.Substring(0, 40);
                return new
                {
                    key, parameter, label, slider.shape,
                    path = AvatarPath(avatar, meshes[slider.mesh].transform),
                    value = Mathf.Clamp(slider.value, 0f, 100f),
                };
            }).ToList();

            var controller = SliderController(rows.Select(row => (row.path, row.shape, row.parameter, row.value / 100f)).ToList());
            var host = new GameObject(SlidersName);
            Undo.RegisterCreatedObjectUndo(host, "Add wardrobe sliders");
            host.transform.SetParent(item.transform, false);
            TagGenerated(host, SlidersKind);
            Undo.AddComponent<ModularAvatarMenuInstaller>(host);
            var submenu = Undo.AddComponent<ModularAvatarMenuItem>(host);
            submenu.MenuSource = SubmenuSource.Children;
            submenu.label = WardrobeStrings.T("sliders.menu", item.name);
            submenu.Control = new VRCExpressionsMenu.Control { name = submenu.label, type = VRCExpressionsMenu.Control.ControlType.SubMenu };
            var parameters = Undo.AddComponent<ModularAvatarParameters>(host);
            parameters.parameters = rows.Select(row => new ParameterConfig
            {
                nameOrPrefix = row.parameter,
                syncType = ParameterSyncType.Float,
                saved = true,
                defaultValue = row.value / 100f,
                hasExplicitDefaultValue = true,
            }).ToList();
            var merge = Undo.AddComponent<ModularAvatarMergeAnimator>(host);
            merge.animator = controller;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = true;
            foreach (var row in rows)
            {
                var control = new GameObject(row.label);
                Undo.RegisterCreatedObjectUndo(control, "Add wardrobe slider");
                control.transform.SetParent(host.transform, false);
                TagGenerated(control, SliderKind, row.key);
                var menu = Undo.AddComponent<ModularAvatarMenuItem>(control);
                menu.label = row.label;
                menu.Control = new VRCExpressionsMenu.Control
                {
                    name = row.label,
                    type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                    subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = row.parameter } },
                };
            }
            EditorUtility.SetDirty(host);
        }

        // Content-addressed, never deleted while Unity runs: Undo and Redo can always
        // return to a component that still references its controller.
        private static AnimatorController SliderController(List<(string path, string shape, string parameter, float value)> rows)
        {
            var identity = string.Join("\n", rows.Select(row => row.path + "\t" + row.shape + "\t" + row.parameter + "\t" + row.value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
            var assetPath = SliderFolder + "/Sliders_" + hash + ".controller";
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
            if (existing != null) return existing;
            var folder = "Assets";
            foreach (var segment in SliderFolder.Split('/').Skip(1))
            {
                if (!AssetDatabase.IsValidFolder(folder + "/" + segment)) AssetDatabase.CreateFolder(folder, segment);
                folder += "/" + segment;
            }
            var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
            // Drop Unity's empty "Base Layer" so only slider layers merge into FX.
            controller.RemoveLayer(0);
            foreach (var row in rows)
            {
                controller.AddParameter(new AnimatorControllerParameter { name = row.parameter, type = AnimatorControllerParameterType.Float, defaultFloat = row.value });
                var clip = new AnimationClip { name = "Slider " + row.shape };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(row.path, typeof(SkinnedMeshRenderer), "blendShape." + row.shape),
                    AnimationCurve.Linear(0f, 0f, 1f, 100f));
                AssetDatabase.AddObjectToAsset(clip, controller);
                controller.AddLayer("Slider " + row.shape);
                var layers = controller.layers;
                var layer = layers[layers.Length - 1];
                var state = layer.stateMachine.AddState(row.shape);
                state.motion = clip;
                state.timeParameterActive = true;
                state.timeParameter = row.parameter;
                state.writeDefaultValues = false;
                layer.defaultWeight = 1f;
                controller.layers = layers;
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
            return controller;
        }
    }
}
