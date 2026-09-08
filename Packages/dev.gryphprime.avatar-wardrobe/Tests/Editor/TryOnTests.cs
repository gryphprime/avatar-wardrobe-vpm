using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace OutfitToggleGenerator
{
    public sealed class TryOnTests
    {
        private Scene scene, previous;
        private GameObject root;
        private VRCAvatarDescriptor avatar;
        private Material material;

        [SetUp]
        public void SetUp()
        {
            previous = SceneManager.GetActiveScene();
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Try On fixture");
            SceneManager.MoveGameObjectToScene(root, scene);
            avatar = root.AddComponent<VRCAvatarDescriptor>();
            root.AddComponent<Animator>();
            material = new Material(Shader.Find("Standard")) { color = Color.white };
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            SceneManager.MoveGameObjectToScene(body, scene);
            body.transform.SetParent(root.transform, false);
            body.GetComponent<Renderer>().sharedMaterial = material;
        }

        [TearDown]
        public void TearDown()
        {
            WardrobeTryOnWorker.CancelAll();
            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            if (material != null) Object.DestroyImmediate(material);
        }

        [Test]
        public void ReplacementUsesSiblingIdentityAndKeepsPlacementAndOtherClothes()
        {
            var first = new GameObject("Coat"); first.transform.SetParent(root.transform, false);
            var second = new GameObject("Coat"); second.transform.SetParent(root.transform, false);
            second.transform.localPosition = new Vector3(1, 2, 3);
            second.transform.localRotation = Quaternion.Euler(10, 20, 30);
            second.transform.localScale = new Vector3(0.8f, 1.2f, 0.9f);
            second.SetActive(false);
            var path = WardrobeTryOnWorker.SiblingPath(root.transform, second.transform);
            var clone = Object.Instantiate(root);
            var candidate = new GameObject("Different variant");
            try
            {
                var oldIndex = second.transform.GetSiblingIndex();
                var replacement = WardrobeTryOnWorker.ComposeCandidate(clone, candidate, path);
                Assert.AreEqual(oldIndex, replacement.transform.GetSiblingIndex());
                Assert.AreEqual(second.transform.localPosition, replacement.transform.localPosition);
                Assert.AreEqual(second.transform.localRotation, replacement.transform.localRotation);
                Assert.AreEqual(second.transform.localScale, replacement.transform.localScale);
                Assert.IsFalse(replacement.activeSelf);
                Assert.AreEqual(3, clone.transform.childCount, "The body and other same-name coat must remain.");
                Assert.AreEqual("Body", clone.transform.GetChild(0).name);
                Assert.AreEqual("Coat", clone.transform.GetChild(1).name);
                Assert.AreSame(second.transform, WardrobeTryOnWorker.AtSiblingPath(root.transform, path));
            }
            finally { Object.DestroyImmediate(clone); Object.DestroyImmediate(candidate); }
        }

        [Test]
        public void FingerprintDetectsUnsavedMaterialAndInactiveHierarchyChanges()
        {
            var before = WardrobeTryOnWorker.SourceFingerprint(avatar);
            Assert.AreEqual(before, WardrobeTryOnWorker.SourceFingerprint(avatar));
            material.color = Color.red;
            var materialChange = WardrobeTryOnWorker.SourceFingerprint(avatar);
            Assert.AreNotEqual(before, materialChange);
            root.transform.GetChild(0).gameObject.SetActive(false);
            Assert.AreNotEqual(materialChange, WardrobeTryOnWorker.SourceFingerprint(avatar));
        }

        [Test]
        public void LargeMeshFingerprintDoesNotWalkNumericPayload()
        {
            var mesh = new Mesh();
            try
            {
                WardrobeTryOnWorker.SourceFingerprint(avatar);
                mesh.vertices = new Vector3[1000000];
                root.GetComponentInChildren<MeshFilter>().sharedMesh = mesh;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var before = WardrobeTryOnWorker.SourceFingerprint(avatar);
                Assert.Less(clock.Elapsed.TotalSeconds, 3, "Status must not walk one million vertex records.");
                EditorUtility.SetDirty(mesh);
                Assert.AreNotEqual(before, WardrobeTryOnWorker.SourceFingerprint(avatar));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void FingerprintStillIncludesAnimationObjectCurveMaterials()
        {
            var clip = new AnimationClip { legacy = true };
            var curveMaterial = new Material(Shader.Find("Standard"));
            try
            {
                AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve("", typeof(Renderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = curveMaterial } });
                root.AddComponent<Animation>().AddClip(clip, "fixture");
                var before = WardrobeTryOnWorker.SourceFingerprint(avatar);
                curveMaterial.color = Color.magenta;
                Assert.AreNotEqual(before, WardrobeTryOnWorker.SourceFingerprint(avatar));
            }
            finally { Object.DestroyImmediate(clip); Object.DestroyImmediate(curveMaterial); }
        }

        [Test]
        public void BuiltParameterDiagnosticsHandleMissingNamesConflictsAndCyclicMenus()
        {
            var menu = ScriptableObject.CreateInstance<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu>();
            var parameters = ScriptableObject.CreateInstance<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters>();
            try
            {
                avatar.expressionsMenu = menu; avatar.expressionParameters = parameters;
                parameters.parameters = new[] {
                    new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter { name="duplicate", valueType=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType.Bool },
                    new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter { name="duplicate", valueType=VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType.Int }
                };
                menu.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control { name="Missing", parameter=new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.Parameter { name="absent" } });
                menu.controls.Add(new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control { name="Puppet", subMenu=menu,
                    subParameters=new[] { new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.Parameter { name="duplicate" } } });
                var result = string.Join("\n", WardrobeTryOnWorker.ParameterDiagnostics(avatar));
                StringAssert.Contains("conflicting types", result);
                StringAssert.Contains("missing parameter 'absent'", result);
                StringAssert.Contains("requires Float", result);
                StringAssert.Contains("Synced parameter budget:", result);
            }
            finally { avatar.expressionsMenu = null; avatar.expressionParameters = null; Object.DestroyImmediate(menu); Object.DestroyImmediate(parameters); }
        }

        [Test]
        public void UnpreparedSkinnedCandidateIsRejectedWithActionableSetupReason()
        {
            var candidate = new GameObject("Unprepared coat");
            try
            {
                var skinned = candidate.AddComponent<SkinnedMeshRenderer>();
                var bone = new GameObject("Hips"); bone.transform.SetParent(candidate.transform);
                skinned.bones = new[] { bone.transform };
                var error = Assert.Throws<InvalidOperationException>(() => WardrobeTryOnWorker.CheckSupported(candidate, true));
                StringAssert.Contains("Setup Outfit", error.Message);
                Assert.IsNull(candidate.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarOutfitRoot>());
            }
            finally { Object.DestroyImmediate(candidate); }
        }

        [Test]
        public void ExternalSceneReferenceFailsBeforeProcessingAndLeavesSourceUntouched()
        {
            var external = new GameObject("External source");
            SceneManager.MoveGameObjectToScene(external, scene);
            var constraint = root.transform.GetChild(0).gameObject.AddComponent<ParentConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = external.transform, weight = 1f });
            var fingerprint = WardrobeTryOnWorker.SourceFingerprint(avatar);
            var previews = EditorSceneManager.previewSceneCount;
            var error = Assert.Throws<InvalidOperationException>(() => WardrobeTryOnWorker.Prepare(avatar, ""));
            StringAssert.Contains("external object reference", error.Message);
            Assert.AreEqual(fingerprint, WardrobeTryOnWorker.SourceFingerprint(avatar));
            Assert.AreEqual(previews, EditorSceneManager.previewSceneCount);
        }

        [Test]
        public void ProcessedConfigurationPreviewKeepsSourceAndCancelReleasesViews()
        {
            var fingerprint = WardrobeTryOnWorker.SourceFingerprint(avatar);
            var dirty = scene.isDirty;
            var selected = Selection.activeObject;
            var undoGroup = Undo.GetCurrentGroup();
            var previews = EditorSceneManager.previewSceneCount;
            var result = WardrobeTryOnWorker.Prepare(avatar, "", configureAfterClone: clone =>
            {
                clone.transform.GetChild(0).GetComponent<Renderer>().sharedMaterial.color = Color.blue;
                var extra = GameObject.CreatePrimitive(PrimitiveType.Cube);
                extra.name = "Candidate";
                extra.transform.SetParent(clone.transform, false);
                extra.transform.localPosition = Vector3.up * 1.5f;
            }, configurationId: "test-blue-and-cube");
            Assert.IsTrue(result.processed);
            Assert.AreEqual(1, result.beforeMetrics.renderers);
            Assert.AreEqual(2, result.afterMetrics.renderers);
            Assert.IsNotEmpty(result.environmentRevision);
            Assert.IsNotEmpty(result.sourceRevision);
            Assert.IsNotEmpty(result.recipeRevision);
            Assert.AreEqual(Color.white, material.color);
            Assert.AreEqual(fingerprint, WardrobeTryOnWorker.SourceFingerprint(avatar));
            Assert.AreEqual(dirty, scene.isDirty);
            Assert.AreEqual(selected, Selection.activeObject);
            Assert.AreEqual(undoGroup, Undo.GetCurrentGroup());
            Assert.IsTrue(WardrobeTryOnWorker.Validate(result.token, avatar, out var reason), reason);
            var photo = WardrobeTryOnWorker.RenderView(result.token, "three-quarter");
            Assert.Greater(photo.Length, 1000, "The composed photograph must contain rendered pixels.");
            Assert.AreEqual(137, photo[0]); Assert.AreEqual(80, photo[1]);
            System.IO.Directory.CreateDirectory("Library/AvatarWardrobe");
            System.IO.File.WriteAllBytes("Library/AvatarWardrobe/tryon-fixture.png", photo);

            root.transform.GetChild(0).localScale = Vector3.one * 2f;
            Assert.IsFalse(WardrobeTryOnWorker.Validate(result.token, avatar, out reason));
            StringAssert.Contains("avatar changed", reason);
            WardrobeTryOnWorker.Cancel(result.token);
            Assert.IsFalse(WardrobeTryOnWorker.Validate(result.token, avatar, out reason));
            Assert.AreEqual(previews, EditorSceneManager.previewSceneCount);
        }

        [Test]
        public void ShadowCapturePreservesUnsavedMaterialInSerializedInput()
        {
            material.color = new Color(0.2f, 0.4f, 0.8f, 1f);
            var controller = new UnityEditor.Animations.AnimatorController();
            var machine = new UnityEditor.Animations.AnimatorStateMachine();
            var state = machine.AddState("Idle");
            machine.defaultState = state;
            var transition = state.AddTransition(state);
            controller.layers = new[] { new UnityEditor.Animations.AnimatorControllerLayer { name = "Base", stateMachine = machine } };
            root.GetComponent<Animator>().runtimeAnimatorController = controller;
            Assert.IsNotNull(controller.layers[0].stateMachine, "The source fixture must contain its state machine.");
            var fingerprint = WardrobeTryOnWorker.SourceFingerprint(avatar);
            var active = SceneManager.GetActiveScene();
            var selected = Selection.activeObject;
            var previewCount = EditorSceneManager.previewSceneCount;
            WardrobeShadowCapture.Manifest capture = null;
            var verification = "Assets/__ShadowCaptureVerification_" + Guid.NewGuid().ToString("N");
            try
            {
                capture = WardrobeShadowCapture.Capture(avatar);
                Assert.AreEqual(fingerprint, capture.sourceRevision);
                Assert.AreEqual(fingerprint, WardrobeTryOnWorker.SourceFingerprint(avatar));
                Assert.AreEqual(active, SceneManager.GetActiveScene());
                Assert.AreEqual(selected, Selection.activeObject);
                Assert.AreEqual(previewCount, EditorSceneManager.previewSceneCount);
                Assert.IsFalse(AssetDatabase.IsValidFolder(System.IO.Path.GetDirectoryName(capture.avatarPrefabPath)));
                AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(verification));
                foreach (var entry in capture.files)
                {
                    if (!entry.path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                    var input = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(capture.manifestPath), "files", entry.path);
                    System.IO.File.Copy(input, verification + "/" + System.IO.Path.GetFileName(entry.path));
                }
                AssetDatabase.ImportAsset(verification, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);
                var resources = capture.files.Find(file => file.path.EndsWith(".asset", StringComparison.Ordinal) && file.path.StartsWith("Assets/", StringComparison.Ordinal));
                var loadedAssets = AssetDatabase.LoadAllAssetsAtPath(verification + "/" + System.IO.Path.GetFileName(resources.path));
                var loadedMaterial = System.Linq.Enumerable.First(System.Linq.Enumerable.OfType<Material>(loadedAssets));
                Assert.AreEqual(material.color, loadedMaterial.color);
                var loaded = System.Linq.Enumerable.First(System.Linq.Enumerable.OfType<UnityEditor.Animations.AnimatorController>(loadedAssets));
                var loadedState = loaded.layers[0].stateMachine.defaultState;
                Assert.IsNotNull(loadedState, "A serialized controller must retain its state-machine references.");
                Assert.AreSame(loadedState, loadedState.transitions[0].destinationState, "Self references must survive capture.");
                var repeated = WardrobeShadowCapture.Capture(avatar);
                try
                {
                    Assert.AreEqual(capture.visualRevision, repeated.visualRevision);
                    Assert.AreEqual(capture.packages.Count, repeated.packages.Count);
                    for (var index = 0; index < capture.packages.Count; index++)
                    {
                        Assert.AreEqual(capture.packages[index].sourcePath, repeated.packages[index].sourcePath, "Unchanged package inputs must reuse their immutable content snapshot.");
                        if (!capture.packages[index].builtIn) StringAssert.Contains("package-snapshots", capture.packages[index].sourcePath);
                    }
                }
                finally { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(repeated.manifestPath), true); }

                Assert.AreEqual(fingerprint, WardrobeTryOnWorker.SourceFingerprint(avatar));
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(verification)) AssetDatabase.DeleteAsset(verification);
                root.GetComponent<Animator>().runtimeAnimatorController = null;
                Object.DestroyImmediate(transition); Object.DestroyImmediate(state);
                Object.DestroyImmediate(machine); Object.DestroyImmediate(controller);
                if (capture != null)
                {
                    var smokeReceipt = Environment.GetEnvironmentVariable("WARDROBE_SHADOW_SMOKE_MANIFEST");
                    if (!string.IsNullOrEmpty(smokeReceipt)) System.IO.File.WriteAllText(smokeReceipt, capture.manifestPath);
                    else System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(capture.manifestPath), true);
                }
            }
        }

        [Test]
        public void ShadowCaptureRejectsNonpersistentTextureAndCleansStaging()
        {
            var texture = new Texture2D(2, 2);
            material.mainTexture = texture;
            var fingerprint = WardrobeTryOnWorker.SourceFingerprint(avatar);
            var sceneCount = SceneManager.sceneCount;
            var stagingCount = System.IO.Directory.GetDirectories("Assets", "__WardrobeCapture_*").Length;
            try
            {
                var error = Assert.Throws<InvalidOperationException>(() => WardrobeShadowCapture.Capture(avatar));
                StringAssert.Contains("nonpersistent Texture", error.Message);
                Assert.AreEqual(fingerprint, WardrobeTryOnWorker.SourceFingerprint(avatar));
                Assert.AreEqual(sceneCount, SceneManager.sceneCount);
                Assert.AreEqual(stagingCount, System.IO.Directory.GetDirectories("Assets", "__WardrobeCapture_*").Length);
            }
            finally { material.mainTexture = null; Object.DestroyImmediate(texture); }
        }

        [Test]
        public void AppearanceRecipeResolvesGeneratedDefaultsAndPresetVisibilityOnCopies()
        {
            var scope = new GameObject("Other preset"); scope.transform.SetParent(root.transform, false);
            var hidden = GameObject.CreatePrimitive(PrimitiveType.Cube); hidden.transform.SetParent(scope.transform, false);
            var marker = new GameObject("Part"); marker.transform.SetParent(root.transform, false);
            marker.AddComponent<OutfitToggleGeneratedMenu>().generatedKind = "part-toggle";
            var item = marker.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarMenuItem>();
            item.isDefault = false;
            var toggle = marker.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarObjectToggle>();
            toggle.Inverted = true;
            toggle.Objects.Add(new nadena.dev.modular_avatar.core.ToggledObject
            { Object = new nadena.dev.modular_avatar.core.AvatarObjectReference(root.transform.GetChild(0).gameObject), Active = false });
            var recipe = new WardrobeAppearanceRecipe.Recipe { scopeId = "common" };
            recipe.scopeRules.Add(new WardrobeAppearanceRecipe.Rule { path = WardrobeTryOnWorker.SiblingPath(root.transform, scope.transform), active = false });
            recipe.scopeRules.Add(new WardrobeAppearanceRecipe.Rule { path = new[] { 0 }, active = true });
            var fingerprint = WardrobeTryOnWorker.SourceFingerprint(avatar);
            var clone = Object.Instantiate(root);
            try
            {
                var bound = WardrobeAppearanceRecipe.Bind(clone, recipe);
                Assert.IsFalse(clone.transform.GetChild(0).gameObject.activeSelf, "Generated controls use isDefault, not the all-enabled authoring baseline.");
                Assert.IsFalse(clone.transform.GetChild(1).gameObject.activeSelf, "Other preset roots must stay hidden.");
                clone.transform.GetChild(0).gameObject.SetActive(true);
                bound.Apply();
                Assert.IsFalse(clone.transform.GetChild(0).gameObject.activeSelf, "The same defaults can be restored after processing.");
                Assert.AreEqual(fingerprint, WardrobeTryOnWorker.SourceFingerprint(avatar));
            }
            finally { Object.DestroyImmediate(clone); }
        }

        [Test]
        public void AppearanceRecipePlacesNewCandidateInRequestedScopeAndRejectsUnreproducedControls()
        {
            var scope = new GameObject("Preset"); scope.transform.SetParent(root.transform, false);
            var candidate = new GameObject("New coat"); candidate.transform.SetParent(root.transform, false); candidate.SetActive(false);
            var recipe = new WardrobeAppearanceRecipe.Recipe { scopeId = "preset", candidateParent = new[] { scope.transform.GetSiblingIndex() } };
            WardrobeAppearanceRecipe.PlaceCandidate(root, candidate, recipe, false);
            Assert.AreEqual(scope.transform, candidate.transform.parent);
            Assert.IsTrue(candidate.activeSelf);
            var identity = recipe.Identity;
            recipe.createToggles = true;
            Assert.AreNotEqual(identity, recipe.Identity);
            StringAssert.Contains("cannot yet reproduce", Assert.Throws<InvalidOperationException>(() => WardrobeAppearanceRecipe.CheckSupported(recipe)).Message);
        }

        [Test]
        public void LogicalMenuLabelChangesPreservePhotoIdentityButInvalidateOperationIdentity()
        {
            var layout = root.AddComponent<WardrobeMenuLayout>();
            layout.nodes.Add(new WardrobeMenuLayout.Node { id = "folder", label = "Old label", folder = true });
            var full = WardrobeTryOnWorker.SourceFingerprint(avatar);
            var visual = WardrobeTryOnWorker.VisualFingerprint(avatar);
            layout.nodes[0].label = "New label with \"quotes\" and\nline";
            EditorUtility.SetDirty(layout);
            Assert.AreNotEqual(full, WardrobeTryOnWorker.SourceFingerprint(avatar));
            Assert.AreEqual(visual, WardrobeTryOnWorker.VisualFingerprint(avatar));
            layout.nodes[0].parentId = "Different structural parent";
            Assert.AreNotEqual(visual, WardrobeTryOnWorker.VisualFingerprint(avatar));
        }

        [Test]
        public void InterruptedCaptureCleanupOnlyRemovesJournalOwnedStaging()
        {
            var id = Guid.NewGuid().ToString("N");
            var untrackedId = Guid.NewGuid().ToString("N");
            var project = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
            var journal = System.IO.Path.Combine(project, "Library", "AvatarWardrobe", "capture-staging", id + ".json");
            var partial = System.IO.Path.Combine(project, "Library", "AvatarWardrobe", "captures", id);
            var owned = "Assets/__WardrobeCapture_" + id;
            var untracked = "Assets/__WardrobeCapture_" + untrackedId;
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(owned));
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(untracked));
            System.IO.Directory.CreateDirectory(partial);
            WardrobeShadowCapture.WriteJson(journal, new WardrobeShadowCapture.StagingRecord { captureId = id, projectPath = project });
            try
            {
                WardrobeShadowCapture.CleanupInterruptedCaptures();
                Assert.IsFalse(AssetDatabase.IsValidFolder(owned));
                Assert.IsFalse(System.IO.Directory.Exists(partial));
                Assert.IsTrue(AssetDatabase.IsValidFolder(untracked));
                Assert.IsFalse(System.IO.File.Exists(journal));
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(owned)) AssetDatabase.DeleteAsset(owned);
                if (AssetDatabase.IsValidFolder(untracked)) AssetDatabase.DeleteAsset(untracked);
                if (System.IO.Directory.Exists(partial)) System.IO.Directory.Delete(partial, true);
                if (System.IO.File.Exists(journal)) System.IO.File.Delete(journal);
            }
        }
    }
}
