// Web API for the Wardrobe browser Upload page: the same engine the uploader
// window drives, exposed over the local HTTP server. Reads and quick edits
// answer synchronously; uploads run as polled jobs. All entry points run on
// the Unity main thread via the server RunOnMain dispatch.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
namespace ShiroTools
{
    public partial class OutfitBatchUploader
    {
        private static OutfitBatchUploader _webEngine;
        internal static OutfitBatchUploader WebEngine()
        {
            if (_webEngine == null) _webEngine = CreateEmbeddedDrawer();
            var selected = WebParentAvatar();
            _webEngine._avatarRoot = selected;
            // Rebind records for mutations after a read returned detached defaults.
            _webEngine.AutoDetectSkin();
            _webEngine.RebuildOutfitList();
            return _webEngine;
        }
        internal static void MigrateSelectedAvatar()
        {
            if (BatchActiveNow) throw new InvalidOperationException("Wait for the active upload before migrating.");
            var engine = WebEngine();
            engine.RegisterLegacyPresets();
            var avatar = OutfitToggleGenerator.AvatarWardrobeServer.SceneAvatar;
            OutfitToggleGenerator.OutfitToggleGenerator.MigrateMenuGroups(avatar);
        }
        private void RegisterLegacyPresets()
        {
            var parent = WebParentAvatar();
            if (parent == null || parent != _avatarRoot || BatchActiveNow || _isBatchUploading || _isExpressBusy) return;
            if (!parent.scene.IsValid() || EditorUtility.IsPersistent(parent) ||
                parent.scene.path.StartsWith("Assets/Generated/WardrobeUploads/", StringComparison.Ordinal)) return;
            foreach (var outfit in _outfits)
            {
                if (outfit == null || outfit.Go == null) continue;
                OutfitToggleGenerator.AvatarWardrobePresets.RegisterLegacy(outfit.Go,
                    ScenePathOf(outfit.Go.transform, parent.transform), outfit.Data);
            }
        }

        private static string ScenePathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent) parts.Add(cur.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
        private static Transform FindByScenePath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            var cur = root;
            foreach (var part in path.Split("/"[0]))
            {
                Transform next = null;
                foreach (Transform child in cur)
                    if (child != null && child.name == part) { next = child; break; }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }
        [Serializable]
        public class WebResultDto { public int ok; public string message = ""; }
        [Serializable]
        public class WebSkinDto { public string name = ""; public string path = ""; public int blendshapes; }
        [Serializable]
        public class WebAvatarDto { public string name = ""; public string id = ""; public string release = ""; }
        [Serializable]
        public class WebFailedDto { public string outfit = ""; public string platform = ""; }
        [Serializable]
        public class WebOutfitDto
        {
            public string name = ""; public int active; public string blueprintId = "";
            public int idValid = 1; public int include; public int win; public int and; public int ios;
            public string lastWin = ""; public string lastAnd = ""; public string lastIos = "";
            public int blendCount; public int itemsInc; public int itemsTotal;
            public string faceEmo = ""; public int faceEmoExists; public int isNew = 1;
            public int net; public int total; public int lights;
            public int paramCost; public int paramMax = 256; public int vrcfury; public double vramMB = -1;
        }
        [Serializable]
        public class WebBlendDto { public string name = ""; public float weight; public int pinned; }
        [Serializable]
        public class WebItemDto { public string name = ""; public int included; public int isDefault; }
        [Serializable]
        public class WebTagDto { public string key = ""; public int on; }
        [Serializable]
        public class WebMemberDto { public int instanceId; public string guid = ""; public string name = ""; public string path = ""; }
        [Serializable]
        public class WebPresetDto
        {
            public string id = ""; public string name = ""; public string outfitName = "";
            public List<WebMemberDto> members = new List<WebMemberDto>();
            public string blueprintId = ""; public string lastUpload = ""; public int include = 1;
            public int win = 1; public int and; public int ios; public int blendCount; public string faceEmo = "";
        }
        [Serializable]
        public class WebDefaultsDto
        {
            public string nameTemplate = ""; public string descTemplate = ""; public string release = "private";
            public string thumbMode = "scene"; public string thumbImage = ""; public string bgColor = "1A1A1FFF";
            public int autoSps = 1; public int autoFix = 1; public int autoConsent = 1;
            public List<WebTagDto> tags = new List<WebTagDto>();
            public int optEnabled; public int optAsk = 1; public int optMaxRes = 2048; public int optMinRes; public int optItems;
            public string version = ""; public int versionMode; public int sound = 1;
            public string outfitsParent = "Outfits"; public string itemsParent = "Items";
        }
        [Serializable]
        public class WebStateDto
        {
            public int ok; public string message = "";
            public string avatarRoot = ""; public List<string> avatars = new List<string>();
            public string outfitsParent = ""; public string itemsParent = "";
            public string skin = ""; public List<WebSkinDto> skins = new List<WebSkinDto>();
            public List<WebOutfitDto> outfits = new List<WebOutfitDto>(); public int newCount;
            public int batchActive; public int expressBusy;
            public List<WebFailedDto> failed = new List<WebFailedDto>();
            public string status = "";
            public List<WebPresetDto> presets = new List<WebPresetDto>();
            public WebDefaultsDto defaults = new WebDefaultsDto();
            public string logTail = "";
        }
        [Serializable]
        public class WebJobDto { public int ok; public string message = ""; public string job = ""; }
        [Serializable]
        public class WebJobResultDto
        {
            public int done; public int ok; public string job = ""; public string message = "";
            public int index; public int total; public string current = "";
            public string blueprintId = ""; public List<WebAvatarDto> avatars = new List<WebAvatarDto>();
        }
        [Serializable]
        public class WebReportDto { public int ok; public string message = ""; public int problems; public int warnings; public string report = ""; }
        [Serializable]
        public class WebMatchDto
        {
            public int ok; public string message = "";
            public List<WebMatchItemDto> matches = new List<WebMatchItemDto>();
        }
        [Serializable]
        public class WebMatchItemDto { public string outfit = ""; public string avatar = ""; public string id = ""; }
        [Serializable]
        public class WebVramItemDto { public string name = ""; public int res; public int target; public string format = ""; public double savedMB; }
        [Serializable]
        public class WebVramDto
        {
            public int ok; public string message = ""; public int count; public double savedMB; public int itemsInc;
            public List<WebVramItemDto> items = new List<WebVramItemDto>();
        }
        [Serializable]
        public class WebExportDto { public int ok; public string message = ""; public string filename = ""; public string json = ""; }
        [Serializable]
        public class WebFaceEmoDto { public int ok; public string message = ""; public string assigned = ""; public int assignedExists; public int strayExists; }
        [Serializable]
        public class WebThumbDto { public int ok; public string message = ""; public string token = ""; }
        internal static WebStateDto WebGetState()
        {
            using var readScope = OutfitProjectData.ReadOnly();
            try { return WebEngine().BuildWebState(); }
            catch (Exception ex) { return new WebStateDto { message = ex.Message }; }

        }
        private WebStateDto BuildWebState()
        {
            var st = new WebStateDto();
            try
            {
                LoadNewSetupDefaults();
                EnsureOptDefaults();
                EnsureItemsBuilt();
                RecomputeBudgetsNow();
                st.avatarRoot = _avatarRoot != null ? _avatarRoot.name : "";
                foreach (var av in _avatarsInScene) if (av != null) st.avatars.Add(av.name);
                st.outfitsParent = _outfitsParentName ?? ConfiguredOutfitsParentName;
                st.itemsParent = _itemsParentName ?? "Items";
                if (_avatarRoot != null)
                    foreach (var smr in _avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr == null || smr.sharedMesh == null) continue;
                        st.skins.Add(new WebSkinDto { name = smr.name, path = ScenePathOf(smr.transform, _avatarRoot.transform), blendshapes = smr.sharedMesh.blendShapeCount });
                    }
                st.skin = _skinRenderer != null ? _skinRenderer.name : "";
                foreach (var o in _outfits)
                {
                    if (o == null || o.Go == null) continue;
                    var d = new WebOutfitDto { name = o.Name };
                    d.active = o.Go.CompareTag("Untagged") ? 1 : 0;
                    d.blueprintId = o.BlueprintId ?? "";
                    d.idValid = string.IsNullOrWhiteSpace(d.blueprintId) || IsValidBlueprintId(d.blueprintId) ? 1 : 0;
                    d.include = o.IncludeInBatch ? 1 : 0;
                    d.win = o.BuildWindows ? 1 : 0; d.and = o.BuildAndroid ? 1 : 0; d.ios = o.BuildIOS ? 1 : 0;
                    if (o.Data != null) { d.lastWin = o.Data.lastUploadWindows ?? ""; d.lastAnd = o.Data.lastUploadAndroid ?? ""; d.lastIos = o.Data.lastUploadIOS ?? ""; }
                    d.blendCount = o.BlendShapes != null ? o.BlendShapes.Count : 0;
                    if (_items != null) { d.itemsTotal = _items.Count; foreach (var it in _items) if (it.Go != null && ItemIncludedFor(o.Name, it.Name)) d.itemsInc++; }
                    d.faceEmo = GetFaceEmoName(o.Name) ?? "";
                    d.faceEmoExists = string.IsNullOrEmpty(d.faceEmo) || FindAvatarChild(d.faceEmo) != null ? 1 : 0;
                    d.isNew = string.IsNullOrWhiteSpace(d.blueprintId) ? 1 : 0;
                    ContactsFor(o, out int net, out int tot); d.net = net; d.total = tot;
                    d.lights = LightsFor(o);
                    d.paramCost = _paramCost; d.paramMax = _paramMax; d.vrcfury = _hasVRCFury ? 1 : 0;
                    if (TryGetVramFor(o, out long vb)) d.vramMB = vb / 1048576.0;
                    st.outfits.Add(d);
                }
                st.newCount = st.outfits.Count(o => o.isNew == 1);
                st.batchActive = (BatchActiveNow || _isBatchUploading || _isExpressBusy) ? 1 : 0;
                st.expressBusy = _isExpressBusy ? 1 : 0;
                foreach (var fl in LoadQueue(SESSION_FAILED)) st.failed.Add(new WebFailedDto { outfit = fl.outfit, platform = fl.platform });
                st.status = _statusMessage ?? "";
                var df = st.defaults;
                df.nameTemplate = _nsNameTemplate ?? ""; df.descTemplate = _nsDescTemplate ?? "";
                df.release = NormalizeUploadRelease(_nsRelease); df.thumbMode = _nsThumbMode ?? "scene";
                df.thumbImage = _nsThumbImagePath ?? "";
                try { df.bgColor = ColorUtility.ToHtmlStringRGBA(_nsBgColor); } catch { }
                df.autoSps = _nsAutoSps ? 1 : 0; df.autoFix = _nsAutoFix ? 1 : 0; df.autoConsent = _nsAutoConsent ? 1 : 0;
                foreach (var tag in CONTENT_TAGS) df.tags.Add(new WebTagDto { key = tag, on = (_nsTagDefaults.TryGetValue(tag, out var tb) && tb) ? 1 : 0 });
                df.optEnabled = _nsOptEnabled ? 1 : 0; df.optAsk = _nsOptAsk ? 1 : 0;
                df.optMaxRes = _nsOptMaxRes; df.optMinRes = _nsOptMinRes; df.optItems = _nsOptItems ? 1 : 0;
                df.version = _avatarVersion ?? ""; df.versionMode = _versionMode; df.sound = _soundEnabled ? 1 : 0;
                df.outfitsParent = st.outfitsParent; df.itemsParent = st.itemsParent;
                string baseKey, baseName;
                OutfitToggleGenerator.AvatarWardrobePresets.CurrentBase(out baseKey, out baseName);
                foreach (var p in OutfitToggleGenerator.AvatarWardrobePresets.PresetsForBase(baseKey))
                {
                    if (p == null) continue;
                    var pd = new WebPresetDto { id = p.id ?? "", name = p.name ?? "", outfitName = p.outfitName ?? "" };
                    try
                    {
                        var pdata = OutfitProjectData.GetOutfit(p.avatarRootName ?? "", p.outfitName ?? "");
                        pd.include = pdata.includeInBatch ? 1 : 0;
                        pd.win = pdata.buildWindows ? 1 : 0; pd.and = pdata.buildAndroid ? 1 : 0; pd.ios = pdata.buildIOS ? 1 : 0;
                        pd.blendCount = pdata.blendShapes != null ? pdata.blendShapes.Count : 0;
                        pd.faceEmo = pdata.faceEmoName ?? "";
                    }
                    catch { }
                    foreach (var a in OutfitToggleGenerator.AvatarWardrobePresets.AssignmentsForBase(baseKey))
                    {
                        if (a == null || a.target != p.id) continue;
                        string mname = a.guid;
                        try { var rec = OutfitToggleGenerator.AvatarWardrobeCatalog.GetRecord(a.guid); if (rec != null) mname = OutfitToggleGenerator.AvatarWardrobeCatalog.DisplayVariant(rec) ?? a.guid; } catch { }
                        var avatar = OutfitToggleGenerator.AvatarWardrobeServer.SceneAvatar;
                        foreach (var memberObject in OutfitToggleGenerator.AvatarWardrobePresets.PrefabInstances(avatar, a.guid)
                            .Where(item => OutfitToggleGenerator.AvatarWardrobePresets.ItemPreset(item, avatar) == p.id))
                            pd.members.Add(new WebMemberDto { instanceId = memberObject.GetInstanceID(), guid = a.guid, name = mname,
                                path = AnimationUtility.CalculateTransformPath(memberObject.transform, _avatarRoot.transform) });
                    }
                    if (!string.IsNullOrEmpty(p.legacyPath) && _avatarRoot != null &&
                        (string.IsNullOrEmpty(p.legacyScene) || p.legacyScene == _avatarRoot.scene.path))
                    {
                        var source = FindByScenePath(_avatarRoot.transform, p.legacyPath);
                        if (source != null)
                        {
                            // Include inactive children and non-prefab objects; these are live scene contents.
                            foreach (var child in source.GetComponentsInChildren<Transform>(true))
                            {
                                if (child.GetComponent<OutfitToggleGenerator.OutfitToggleGeneratedMenu>() != null) continue;
                                // A bare Transform is a preset folder, even when it is empty.
                                // Keep standalone prefab/component objects as actual scene items.
                                if (child == source && (source.childCount > 0 ||
                                    (!PrefabUtility.IsAnyPrefabInstanceRoot(source.gameObject) &&
                                     source.GetComponents<Component>().All(component => component is Transform)))) continue;
                                if (child != source && child.parent != source &&
                                    !PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject)) continue;
                                pd.members.Add(new WebMemberDto { instanceId = child.gameObject.GetInstanceID(), guid = WebPrefabGuid(child.gameObject), name = ScenePathOf(child, source), path = AnimationUtility.CalculateTransformPath(child, _avatarRoot.transform) });
                            }
                            if (source.childCount == 0 && pd.members.Count > 0) pd.members[pd.members.Count - 1].name = source.name;
                        }
                    }
                    try { var ps = OutfitToggleGenerator.AvatarWardrobePresets.GetPresetStatus(p.id); pd.blueprintId = ps.blueprintId ?? ""; pd.lastUpload = ps.lastUpload ?? ""; } catch { }
                    pd.members = pd.members.GroupBy(m => string.IsNullOrEmpty(m.path) ? "guid:" + m.guid : m.path).Select(g => g.First()).ToList();
                    st.presets.Add(pd);
                }
                var logPath = UPLOAD_LOG_PATH;
                st.logTail = backgroundLogTail.Get(() =>
                {
                    if (!File.Exists(logPath)) return "";
                    var tail = new Queue<string>();
                    using (var reader = new StreamReader(File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) { tail.Enqueue(line); if (tail.Count > 80) tail.Dequeue(); }
                    }
                    return string.Join("\n", tail);
                }) ?? "";
                st.ok = 1;
            }
            catch (Exception ex) { st.message = ex.Message; }
            return st;
        }
        private OutfitEntry WebFindOutfit(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var matches = _outfits.Where(o => o != null && o.Go != null && o.Name == name).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Multiple preset holders share this name. Rename the holder before using this control.");
            return matches.SingleOrDefault();
        }
        internal static WebResultDto WebOutfitSet(string name, string blueprint, string include, string win, string and, string ios)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found.in.the.scene") + name };
                if (blueprint != null)
                {
                    string clean = (blueprint ?? "").Trim();
                    if (!string.IsNullOrEmpty(clean) && !IsValidBlueprintId(clean))
                        return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.not.a.valid.blueprint.id.expected.avtr.guid") };
                    o.BlueprintId = clean;
                    if (o.Data != null) { o.Data.blueprintId = clean; OutfitProjectData.Save(); }
                }
                bool touched = false;
                if (include != null) { o.IncludeInBatch = include == "1"; touched = true; }
                if (win != null) { o.BuildWindows = win == "1"; touched = true; }
                if (and != null) { o.BuildAndroid = and == "1"; touched = true; }
                if (ios != null) { o.BuildIOS = ios == "1"; touched = true; }
                if (touched && o.Data != null)
                {
                    o.Data.includeInBatch = o.IncludeInBatch;
                    o.Data.buildWindows = o.BuildWindows; o.Data.buildAndroid = o.BuildAndroid; o.Data.buildIOS = o.BuildIOS;
                    OutfitProjectData.Save();
                }
                eng.SetStatus("Updated " + o.Name + ".", MessageType.Info);
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebSelect(string name)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found.in.the.scene") + name };
                eng.ActivateOutfit(o);
                r.ok = 1; r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.activated") + name + ".";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebPing(string name)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null || o.Go == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found.in.the.scene") + name };
                EditorGUIUtility.PingObject(o.Go);
                Selection.activeGameObject = o.Go;
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebConfigSet(string version, string versionMode, string sound, string outfitsParent, string itemsParent, string avatar, string skinPath)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                if (avatar != null)
                {
                    var pick = eng._avatarsInScene.FirstOrDefault(a => a != null && a.name == avatar);
                    if (pick == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.avatar.not.found.in.the.scene") + avatar };
                    eng._avatarRoot = pick;
                    eng.AutoDetectSkin();
                    eng.RebuildOutfitList();
                    eng.LoadAvatarVersion();
                }
                if (outfitsParent != null)
                {
                    eng._outfitsParentName = outfitsParent;
                    EditorPrefs.SetString(PREFS_PARENT_NAME, outfitsParent);
                    eng.RebuildOutfitList();
                }
                if (itemsParent != null)
                {
                    eng._itemsParentName = itemsParent;
                    EditorPrefs.SetString(ITEMS_PARENT_NAME, itemsParent);
                    eng.RebuildItemList();
                }
                if (skinPath != null)
                {
                    if (eng._avatarRoot == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.select.an.avatar.first") };
                    Transform found = null;
                    if (!string.IsNullOrEmpty(skinPath))
                    {
                        found = FindByScenePath(eng._avatarRoot.transform, skinPath);
                        if (found == null)
                            foreach (var smr in eng._avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                                if (smr != null && smr.name == skinPath) { found = smr.transform; break; }
                    }
                    var smrPick = found != null ? found.GetComponent<SkinnedMeshRenderer>() : null;
                    if (!string.IsNullOrEmpty(skinPath) && smrPick == null)
                        return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.skin.mesh.not.found") + skinPath };
                    eng._skinRenderer = smrPick;
                }
                if (version != null)
                {
                    eng._avatarVersion = version ?? "";
                    string mainId = eng.GetMainBlueprintId();
                    if (!string.IsNullOrEmpty(mainId)) AvatarVersionManager.SetVersion(mainId, eng._avatarVersion);
                }
                if (versionMode != null)
                {
                    eng._versionMode = versionMode == "1" ? 1 : 0;
                    EditorPrefs.SetInt(PREFS_VERSION_MODE, eng._versionMode);
                }
                if (sound != null)
                {
                    eng._soundEnabled = sound == "1";
                    EditorPrefs.SetBool(PREFS_SOUND_ENABLED, eng._soundEnabled);
                }
                eng.SetStatus("Settings updated.", MessageType.Info);
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebDefaultsSet(Dictionary<string, string> q)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                eng.LoadNewSetupDefaults();
                eng.EnsureOptDefaults();
                string v;
                if (q.TryGetValue("nameTemplate", out v)) eng._nsNameTemplate = v ?? "";
                if (q.TryGetValue("descTemplate", out v)) eng._nsDescTemplate = v ?? "";
                if (q.TryGetValue("release", out v)) eng._nsRelease = v == "public" ? "public" : "private";
                if (q.TryGetValue("thumbMode", out v)) eng._nsThumbMode = (v == "image" || v == "sceneview") ? v : "scene";
                if (q.TryGetValue("thumbImage", out v)) eng._nsThumbImagePath = v ?? "";
                if (q.TryGetValue("bgColor", out v) && !string.IsNullOrEmpty(v))
                {
                    Color parsed;
                    if (ColorUtility.TryParseHtmlString("#" + v, out parsed)) eng._nsBgColor = parsed;
                }
                if (q.TryGetValue("autoSps", out v)) eng._nsAutoSps = v == "1";
                if (q.TryGetValue("autoFix", out v)) eng._nsAutoFix = v == "1";
                if (q.TryGetValue("autoConsent", out v)) eng._nsAutoConsent = v == "1";
                foreach (var tag in CONTENT_TAGS)
                    if (q.TryGetValue("tag_" + tag, out v)) eng._nsTagDefaults[tag] = v == "1";
                if (q.TryGetValue("optEnabled", out v)) eng._nsOptEnabled = v == "1";
                if (q.TryGetValue("optAsk", out v)) eng._nsOptAsk = v == "1";
                if (q.TryGetValue("optMaxRes", out v)) { int n; if (int.TryParse(v, out n)) eng._nsOptMaxRes = Math.Max(32, Math.Min(8192, n)); }
                if (q.TryGetValue("optMinRes", out v)) { int n; if (int.TryParse(v, out n)) eng._nsOptMinRes = Math.Max(0, Math.Min(8192, n)); }
                if (q.TryGetValue("optItems", out v)) eng._nsOptItems = v == "1";
                eng.SaveNewSetupDefaults();
                eng.SaveOptDefaults();
                eng.SetStatus("Defaults saved.", MessageType.Info);
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        [Serializable]
        public class WebBlendListDto { public int ok; public string message = ""; public string skin = ""; public List<WebBlendDto> items = new List<WebBlendDto>(); }
        internal static WebBlendListDto WebBlendshapes(string name)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebBlendListDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebBlendListDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found") + name };
                if (eng._skinRenderer == null || eng._skinRenderer.sharedMesh == null)
                    return new WebBlendListDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.skin.mesh.selected") };
                var mesh = eng._skinRenderer.sharedMesh;
                r.skin = eng._skinRenderer.name;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string bs = mesh.GetBlendShapeName(i);
                    float w;
                    bool pinned = o.BlendShapes.TryGetValue(bs, out w);
                    if (!pinned) w = eng._skinRenderer.GetBlendShapeWeight(i);
                    r.items.Add(new WebBlendDto { name = bs, weight = w, pinned = pinned ? 1 : 0 });
                }
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebBlendshapeSet(string name, string bs, string pinned, string weight)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null || string.IsNullOrEmpty(bs)) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.or.blendshape.missing") };
                if (pinned == "1")
                {
                    float w;
                    if (!float.TryParse(weight, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w)) w = 0;
                    o.BlendShapes[bs] = Math.Max(0f, Math.Min(100f, w));
                }
                else o.BlendShapes.Remove(bs);
                SaveBlendShapes(o);
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebBlendshapeCapture(string name)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found") + name };
                if (eng._skinRenderer == null || eng._skinRenderer.sharedMesh == null)
                    return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.skin.mesh.selected") };
                var mesh = eng._skinRenderer.sharedMesh;
                int n = 0;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    float w = eng._skinRenderer.GetBlendShapeWeight(i);
                    if (w > 0f) { o.BlendShapes[mesh.GetBlendShapeName(i)] = w; n++; }
                }
                SaveBlendShapes(o);
                r.ok = 1; r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.captured") + n + " blendshape(s).";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebBlendshapeClear(string name)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found") + name };
                o.BlendShapes.Clear();
                SaveBlendShapes(o);
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        [Serializable]
        public class WebItemListDto { public int ok; public string message = ""; public string parent = ""; public List<WebItemDto> items = new List<WebItemDto>(); }
        internal static WebItemListDto WebItems(string name)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebItemListDto();
            try
            {
                var eng = WebEngine();
                eng.EnsureItemsBuilt();
                r.parent = eng._itemsParentName ?? "";
                if (eng._items == null) return new WebItemListDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.avatar.selected") };
                foreach (var it in eng._items)
                {
                    if (it == null || it.Go == null) continue;
                    r.items.Add(new WebItemDto { name = it.Name, included = eng.ItemIncludedFor(name ?? "", it.Name) ? 1 : 0, isDefault = eng.ItemDefaultOn(it.Name) ? 1 : 0 });
                }
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebItemSet(string outfit, string item, string include)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                eng.EnsureItemsBuilt();
                if (string.IsNullOrEmpty(outfit) || string.IsNullOrEmpty(item)) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.or.item.missing") };
                eng.SetItemIncluded(outfit, item, include == "1");
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebItemAll(string outfit, string include, string filter)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                eng.EnsureItemsBuilt();
                if (string.IsNullOrEmpty(outfit) || eng._items == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.or.items.missing") };
                string f = (filter ?? "").ToLowerInvariant();
                var names = new List<string>();
                foreach (var it in eng._items)
                    if (it != null && it.Go != null && (f.Length == 0 || it.Name.ToLowerInvariant().Contains(f))) names.Add(it.Name);
                eng.SetItemsIncluded(outfit, names, include == "1");
                r.ok = 1; r.message = names.Count + " item(s) updated.";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebItemDefault(string item, string include)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                eng.EnsureItemsBuilt();
                if (string.IsNullOrEmpty(item)) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.item.missing") };
                OutfitProjectData.SetItemDefault(eng.ItemAvatarKey, item, include == "1");
                eng.ClearVramCache();
                eng.MarkBudgetsDirty();
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebFaceEmoDto WebFaceEmo(string name)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebFaceEmoDto();
            try
            {
                var eng = WebEngine();
                r.assigned = eng.GetFaceEmoName(name ?? "") ?? "";
                r.assignedExists = !string.IsNullOrEmpty(r.assigned) && eng.FindAvatarChild(r.assigned) != null ? 1 : 0;
                r.strayExists = eng.FindAvatarChild(FACEEMO_PREFAB_NAME) != null ? 1 : 0;
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebFaceEmoCapture(string name)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found") + name };
                eng.CaptureFaceEmoFor(o);
                r.ok = 1; r.message = eng._statusMessage ?? "";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebFaceEmoClear(string name)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found") + name };
                eng.ClearFaceEmoFor(o);
                r.ok = 1; r.message = eng._statusMessage ?? "";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebFaceEmoOpen()
        {
            var r = new WebResultDto();
            try { OpenFaceEmoWindow(); r.ok = 1; }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebVramDto WebVramPreview(string name)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebVramDto();
            try
            {
                var eng = WebEngine();
                eng.EnsureOptDefaults();
                var o = eng.WebFindOutfit(name);
                if (o == null || o.Go == null) return new WebVramDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found") + name };
                int itemsInc;
                var plan = eng.BuildOptimizationPlan(o, out itemsInc);
                r.itemsInc = itemsInc;
                long saved = 0;
                foreach (var p in plan)
                {
                    if (p == null) continue;
                    saved += p.SavedBytes;
                    string nm = p.Path;
                    try { nm = Path.GetFileName(p.Path); } catch { }
                    r.items.Add(new WebVramItemDto { name = nm ?? "", res = p.CurrentRes, target = p.TargetRes, format = p.ChangeFormat ? p.TargetFormat.ToString() : "", savedMB = p.SavedBytes / 1048576.0 });
                }
                r.count = plan.Count;
                r.savedMB = saved / 1048576.0;
                r.ok = 1;
                if (plan.Count == 0) r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.textures.already.optimal.nothing.to.do");
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebVramApply(string name)
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                eng.EnsureOptDefaults();
                var o = eng.WebFindOutfit(name);
                if (o == null || o.Go == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found") + name };
                int itemsInc;
                var plan = eng.BuildOptimizationPlan(o, out itemsInc);
                if (plan.Count == 0) return new WebResultDto { ok = 1, message = global::OutfitToggleGenerator.WardrobeStrings.T("server.textures.already.optimal.nothing.to.do") };
                long saved = 0;
                foreach (var p in plan) saved += p != null ? p.SavedBytes : 0;
                ApplyPlan(plan);
                eng.ClearVramCache();
                LogUpload("OK    " + o.Name + " (web VRAM optimize): " + plan.Count + " texture(s)");
                r.ok = 1; r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.optimized") + plan.Count + " texture(s).";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static double WebVramSync(string name)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return -1;
                eng.ComputeVramFor(o);
                long bytes;
                if (eng.TryGetVramFor(o, out bytes)) return bytes / 1048576.0;
            }
            catch { }
            return -1;

        }
        private sealed class WebJobState
        {
            public DateTime created = DateTime.UtcNow;
            public bool done; public bool ok; public string message = "";
            public int index; public int total; public string current = "";
            public string blueprintId = ""; public List<WebAvatarDto> avatars;
        }
        private static readonly Dictionary<string, WebJobState> _webJobs = new Dictionary<string, WebJobState>();
        private static readonly object _webJobsLock = new object();
        private static string _webPresetJob = "";
        internal static bool HasWebJob(string id) { lock (_webJobsLock) return _webJobs.ContainsKey(id); }
        internal static string WebRequestId;
        private static string NewWebJob()
        {
            string id = WebRequestId ?? Guid.NewGuid().ToString("N");
            WebRequestId = null;
            lock (_webJobsLock)
            {
                foreach (var expired in _webJobs.Where(kv => kv.Value.done).OrderByDescending(kv => kv.Value.created).Skip(63).Select(kv => kv.Key).ToList()) _webJobs.Remove(expired);
                _webJobs[id] = new WebJobState();
            }
            return id;
        }
        private static void WebJobProgress(string job, int index, int total, string current)
        {
            lock (_webJobsLock)
                if (_webJobs.TryGetValue(job, out var st)) { st.index = index; st.total = total; st.current = current ?? ""; }
        }
        private static void WebJobFinish(string job, bool ok, string message, string blueprintId)
        {
            lock (_webJobsLock)
                if (_webJobs.TryGetValue(job, out var st)) { st.done = true; st.ok = ok; st.message = message ?? ""; st.blueprintId = blueprintId ?? ""; }
        }
        internal static WebJobResultDto WebJobResult(string job)
        {
            var r = new WebJobResultDto();
            lock (_webJobsLock)
            {
                if (string.IsNullOrEmpty(job)) job = _webPresetJob;
                r.job = job ?? "";
                if (!_webJobs.TryGetValue(job ?? "", out var st))
                {
                    r.done = 1;
                    r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.unknown.or.expired.job.if.unity.reloaded.mid.upload.check");
                    return r;
                }
                r.done = st.done ? 1 : 0; r.ok = st.ok ? 1 : 0; r.message = st.message ?? "";
                r.index = st.index; r.total = st.total; r.current = st.current ?? "";
                r.blueprintId = st.blueprintId ?? "";
                if (st.avatars != null) r.avatars = st.avatars;
            }
            return r;
        }
        private static List<string> SplitNames(string names)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(names)) return list;
            foreach (var n in names.Split("\n"[0])) if (!string.IsNullOrEmpty(n)) list.Add(n);
            return list;
        }
        internal static WebJobDto WebUploadScene(string names, bool expressNew)
        {
            var list = SplitNames(names);
            if (list.Count == 0) return new WebJobDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.presets.selected") };
            if (BatchActiveNow) return new WebJobDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.a.batch.is.already.running") };
            string job = NewWebJob();
            RunWebSceneJob(job, list, expressNew);
            return new WebJobDto { ok = 1, job = job };
        }
        private static async void RunWebSceneJob(string jobId, List<string> names, bool expressNew)
        {
            try
            {
                var eng = WebEngine();
                if (!TryGetWardrobeBuilder(out _))
                    throw new Exception("VRC SDK builder not available — open the VRChat SDK window first.");
                if (!APIUser.IsLoggedIn) throw new Exception("Not logged in. Open the VRChat SDK Control Panel and log in first.");
                var targets = new List<OutfitEntry>();
                foreach (var n in names) { var o = eng.WebFindOutfit(n); if (o != null) targets.Add(o); }
                if (targets.Count == 0) throw new Exception("No matching presets in the scene.");
                string runBefore = SessionState.GetString(SESSION_BATCH_RUN_ID, "");
                string runId = null;
                eng.LoadNewSetupDefaults();
                eng.EnsureOptDefaults();
                bool savedOptAsk = eng._nsOptAsk;
                eng._nsOptAsk = false;
                WardrobeHeadless = true;
                try
                {
                    if (expressNew)
                    {
                        int i = 0;
                        foreach (var o in targets.Where(o => o != null && o.Go != null && string.IsNullOrWhiteSpace(o.BlueprintId)).ToList())
                        {
                            WebJobProgress(jobId, i++, targets.Count, o.Name);
                            await eng.ExpressSetupAsync(o, null, true);
                            if (!await WaitForExpressAsync() || !eng._expressSucceeded) throw new Exception(eng._statusMessage);
                        }
                    }
                    var configured = targets.Where(o => o != null && o.Go != null && !string.IsNullOrWhiteSpace(o.BlueprintId)).ToList();
                    if (configured.Count > 0)
                    {
                        WebJobProgress(jobId, 0, configured.Count, "batch");
                        await eng.StartBatchAsync(configured);
                        runId = SessionState.GetString(SESSION_BATCH_RUN_ID, "");
                        if (runId == runBefore) throw new Exception("Batch refused to start.");
                        if (!await WaitForBatchAsync()) throw new Exception("Timed out waiting for the upload to finish.");
                    }
                    else if (!expressNew) throw new Exception("Nothing to upload — presets need a Blueprint ID first (use Express).");
                }
                finally { eng._nsOptAsk = savedOptAsk; WardrobeHeadless = false; }
                bool ok = runId != null ? BatchRunSucceeded(runId) : expressNew && eng._expressSucceeded;
                WebJobFinish(jobId, ok, string.IsNullOrEmpty(eng._statusMessage) ? (ok ? "Done." : "Finished with failures.") : eng._statusMessage, null);
            }
            catch (Exception ex) { try { WebJobFinish(jobId, false, ex.Message, null); } catch { } }
        }
        internal static WebJobDto WebUploadPresets(string ids)
        {
            var list = SplitNames(ids);
            if (list.Count == 0) return new WebJobDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.upload.sets.selected") };
            var readinessError = WardrobeUploadReadinessError();
            if (readinessError != null) return new WebJobDto { message = readinessError };
            string job;
            lock (_webJobsLock)
            {
                if (_webJobs.TryGetValue(_webPresetJob, out var active) && !active.done)
                    return new WebJobDto { ok = 1, job = _webPresetJob };
                job = NewWebJob();
                _webPresetJob = job;
            }
            // Return the job handle before staging or SDK work can block the editor.
            EditorApplication.delayCall += () => RunWebPresetJob(job, list);
            return new WebJobDto { ok = 1, job = job };
        }
        private static async void RunWebPresetJob(string jobId, List<string> ids)
        {
            try
            {
                var lines = new List<string>();
                var uploaded = new List<string>();
                bool allOk = true;
                int i = 0;
                foreach (var id in ids)
                {
                    var preset = OutfitToggleGenerator.AvatarWardrobePresets.GetPreset(id);
                    string nm = preset != null && !string.IsNullOrEmpty(preset.name) ? preset.name : id;
                    WebJobProgress(jobId, i++, ids.Count, nm);
                    if (preset == null) { lines.Add(nm + ": preset not found."); allOk = false; continue; }
                    var outcome = await OutfitToggleGenerator.AvatarWardrobePresets.UploadPresetAsync(id);
                    if (outcome.ok) uploaded.Add(nm);
                    else lines.Add(nm + ": " + (outcome.message ?? "Upload failed."));
                    if (!outcome.ok) allOk = false;
                }
                WebJobProgress(jobId, ids.Count, ids.Count, "");
                string summary = uploaded.Count == 1 ? "Uploaded 1 preset: " : "Uploaded " + uploaded.Count + " presets: ";
                summary = uploaded.Count > 0 ? summary + string.Join(", ", uploaded.ToArray()) + "." : "No presets were uploaded.";
                if (lines.Count > 0) summary += "\nCould not upload:\n" + string.Join("\n", lines.ToArray());
                WebJobFinish(jobId, allOk, summary, null);
            }
            catch (Exception ex) { try { WebJobFinish(jobId, false, ex.Message, null); } catch { } }
        }
        internal static WebJobDto WebExpress(string name, Dictionary<string, string> q)
        {
            if (string.IsNullOrEmpty(name)) return new WebJobDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.missing") };
            if (BatchActiveNow) return new WebJobDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.a.batch.is.already.running") };
            AdvancedDraft draft = null;
            string adv;
            if (q != null && q.TryGetValue("advanced", out adv) && adv == "1")
            {
                draft = new AdvancedDraft();
                string v;
                if (q.TryGetValue("avatarName", out v)) draft.Name = v ?? "";
                if (q.TryGetValue("description", out v)) draft.Description = v ?? "";
                if (q.TryGetValue("release", out v)) draft.Release = v == "public" ? "public" : "private";
                if (q.TryGetValue("thumbMode", out v)) draft.ThumbMode = (v == "image" || v == "sceneview") ? v : "scene";
                if (q.TryGetValue("imagePath", out v)) draft.ImagePath = v ?? "";
                if (q.TryGetValue("tags", out v))
                    foreach (var t in (v ?? "").Split(","[0]))
                        if (!string.IsNullOrEmpty(t)) draft.Tags[t] = true;
            }
            string job = NewWebJob();
            RunWebExpressJob(job, name, draft);
            return new WebJobDto { ok = 1, job = job };
        }
        private static async void RunWebExpressJob(string jobId, string name, AdvancedDraft draft)
        {
            try
            {
                var eng = WebEngine();
                if (!TryGetWardrobeBuilder(out _))
                    throw new Exception("VRC SDK builder not available — open the VRChat SDK window first.");
                if (!APIUser.IsLoggedIn) throw new Exception("Not logged in. Open the VRChat SDK Control Panel and log in first.");
                var o = eng.WebFindOutfit(name);
                if (o == null) throw new Exception("Preset not found in the scene: " + name);
                if (!string.IsNullOrWhiteSpace(o.BlueprintId)) throw new Exception("This preset already has a Blueprint ID.");
                WebJobProgress(jobId, 0, 1, name);
                eng.LoadNewSetupDefaults();
                eng.EnsureOptDefaults();
                bool savedOptAsk = eng._nsOptAsk;
                eng._nsOptAsk = false;
                WardrobeHeadless = true;
                try { await eng.ExpressSetupAsync(o, draft, true); }
                finally { eng._nsOptAsk = savedOptAsk; WardrobeHeadless = false; }
                if (!await WaitForExpressAsync()) throw new Exception("Timed out waiting for the first-time upload.");
                eng.ScanScene();
                var after = eng.WebFindOutfit(name);
                string id = after != null && after.Data != null ? after.Data.blueprintId ?? "" : "";
                if (!IsValidBlueprintId(id)) throw new Exception(string.IsNullOrEmpty(eng._statusMessage) ? "Upload finished without a Blueprint ID." : eng._statusMessage);
                WebJobFinish(jobId, true, "Created new avatar for " + name + " -> " + id, id);
            }
            catch (Exception ex) { try { WebJobFinish(jobId, false, ex.Message, null); } catch { } }
        }
        internal static WebJobDto WebFetchAvatars()
        {
            string job = NewWebJob();
            RunWebFetchJob(job);
            return new WebJobDto { ok = 1, job = job };
        }
        private static async void RunWebFetchJob(string jobId)
        {
            try
            {
                var eng = WebEngine();
                WebJobProgress(jobId, 0, 1, "fetch");
                bool ok = await eng.EnsureAvatarsFetchedAsync(true);
                var list = new List<WebAvatarDto>();
                if (ok && eng._fetchedAvatars != null)
                    foreach (var a in eng._fetchedAvatars)
                    {
                        string rel = "";
                        try { rel = "" + a.ReleaseStatus; } catch { }
                        list.Add(new WebAvatarDto { name = a.Name ?? "", id = a.ID ?? "", release = rel });
                    }
                lock (_webJobsLock)
                    if (_webJobs.TryGetValue(jobId, out var st)) { st.done = true; st.ok = ok && list.Count > 0; st.message = ok ? ("Fetched " + list.Count + " avatar(s).") : (eng._statusMessage ?? "Fetch failed."); st.avatars = list; }
            }
            catch (Exception ex) { try { WebJobFinish(jobId, false, ex.Message, null); } catch { } }
        }
        internal static WebReportDto WebDryRun()
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebReportDto();
            try
            {
                var eng = WebEngine();
                r.report = eng.BuildDryRunReport(out int p, out int w);
                r.problems = p; r.warnings = w; r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebDismissFailed()
        {
            var r = new WebResultDto();
            try { SessionState.EraseString(SESSION_FAILED); WebEngine()._failedDirty = true; r.ok = 1; }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebCancel()
        {
            var r = new WebResultDto();
            try
            {
                var eng = WebEngine();
                if (eng._isBatchUploading) { try { if (eng._cts != null) eng._cts.Cancel(); } catch { } eng.CancelBatch(); r.ok = 1; r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.batch.cancel.requested"); }
                else r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.scene.batch.is.running.upload.set.jobs.finish.on");
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebJobDto WebRetry()
        {
            var eng = WebEngine();
            var failed = LoadQueue(SESSION_FAILED);
            var valid = failed.Where(f => f != null && eng._outfits.Any(o => o != null && o.Name == f.outfit)).ToList();
            if (valid.Count == 0) return new WebJobDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.failed.uploads.to.retry") };
            if (BatchActiveNow) return new WebJobDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.a.batch.is.already.running") };
            string job = NewWebJob();
            RunWebRetryJob(job, valid);
            return new WebJobDto { ok = 1, job = job };
        }
        private static async void RunWebRetryJob(string jobId, List<QueueItem> valid)
        {
            try
            {
                var eng = WebEngine();
                string runBefore = SessionState.GetString(SESSION_BATCH_RUN_ID, "");
                string runId = null;
                WardrobeHeadless = true;
                try
                {
                    WebJobProgress(jobId, 0, valid.Count, "retry");
                    await eng.RetryFailedAsync(valid);
                    runId = SessionState.GetString(SESSION_BATCH_RUN_ID, "");
                    if (!await WaitForBatchAsync()) throw new Exception("Timed out waiting for the upload to finish.");
                }
                finally { WardrobeHeadless = false; }
                bool ok = runId != runBefore && BatchRunSucceeded(runId);
                WebJobFinish(jobId, ok, string.IsNullOrEmpty(eng._statusMessage) ? (ok ? "Retry finished." : "Retry finished with failures.") : eng._statusMessage, null);
            }
            catch (Exception ex) { try { WebJobFinish(jobId, false, ex.Message, null); } catch { } }
        }
        internal static WebMatchDto WebMatch(bool apply)
        {
            var r = new WebMatchDto();
            try
            {
                var eng = WebEngine();
                eng.LoadNewSetupDefaults();
                if (eng._fetchedAvatars == null || eng._fetchedAvatars.Count == 0)
                    return new WebMatchDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.fetch.your.avatars.first") };
                foreach (var o in eng._outfits.Where(o => o != null && o.Go != null && string.IsNullOrWhiteSpace(o.BlueprintId)).ToList())
                {
                    string templated = eng.ApplyTokens(eng._nsNameTemplate, o);
                    var hit = eng._fetchedAvatars.FirstOrDefault(a => string.Equals(a.Name, o.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Name, templated, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(hit.ID))
                        r.matches.Add(new WebMatchItemDto { outfit = o.Name, avatar = hit.Name ?? "", id = hit.ID ?? "" });
                }
                if (apply)
                {
                    foreach (var m in r.matches)
                    {
                        var o = eng.WebFindOutfit(m.outfit);
                        if (o == null) continue;
                        o.BlueprintId = m.id;
                        if (o.Data != null) o.Data.blueprintId = m.id;
                    }
                    OutfitProjectData.Save();
                    r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.assigned") + r.matches.Count + " Blueprint ID(s).";
                }
                else if (r.matches.Count == 0) r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.name.matches.found.pick.manually.per.preset");
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebExportDto WebExport()
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebExportDto();
            try
            {
                var bundle = new SettingsBundle
                {
                    data = OutfitProjectData.ExportRaw() ?? "",
                    versions = AvatarVersionManager.ExportRaw() ?? ""
                };
                r.json = JsonUtility.ToJson(bundle, true);
                r.filename = "ShiroOutfit_backup.json";
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebImport(string json)
        {
            try
            {
                ImportSettingsBundle(json);
                WebEngine().ScanScene();
                return new WebResultDto { ok = 1, message = global::OutfitToggleGenerator.WardrobeStrings.T("server.settings.imported") };
            }
            catch (Exception ex) { return new WebResultDto { message = ex.Message }; }
        }
        private sealed class WebThumbEntry { public string outfit = ""; public string path = ""; }
        private static readonly Dictionary<string, WebThumbEntry> _webThumbs = new Dictionary<string, WebThumbEntry>();
        private static readonly object _webThumbsLock = new object();
        internal static WebThumbDto WebThumbCapture(string name, string mode)
        {
            var r = new WebThumbDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebThumbDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found") + name };
                if (!APIUser.IsLoggedIn) return new WebThumbDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.log.in.to.the.vrchat.sdk.first") };
                eng.LoadNewSetupDefaults();
                eng.ActivateOutfit(o);
                AdvancedDraft draft = null;
                if (!string.IsNullOrEmpty(mode))
                    draft = new AdvancedDraft { ThumbMode = (mode == "image" || mode == "sceneview") ? mode : "scene", ImagePath = eng._nsThumbImagePath };
                string path = eng.ResolveThumbnailPath(o, draft);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return new WebThumbDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.could.not.capture.a.thumbnail.see.console") };
                string token = Guid.NewGuid().ToString("N");
                lock (_webThumbsLock) _webThumbs[token] = new WebThumbEntry { outfit = name, path = path };
                r.ok = 1; r.token = token;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        private static readonly OutfitToggleGenerator.WardrobeBackgroundValue<string> backgroundLogTail = new OutfitToggleGenerator.WardrobeBackgroundValue<string>();
        internal static byte[] WebThumbBytes(string token)
        {
            try
            {
                string path = null;
                lock (_webThumbsLock)
                {
                    WebThumbEntry e;
                    if (!string.IsNullOrEmpty(token) && _webThumbs.TryGetValue(token, out e)) path = e.path;
                }
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                return File.ReadAllBytes(path);
            }
            catch { return null; }
        }
        internal static WebJobDto WebThumbConfirm(string token)
        {
            string path = null;
            string outfit = null;
            lock (_webThumbsLock)
            {
                WebThumbEntry e;
                if (!string.IsNullOrEmpty(token) && _webThumbs.TryGetValue(token, out e)) { path = e.path; outfit = e.outfit; }
            }
            if (string.IsNullOrEmpty(path)) return new WebJobDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.unknown.or.expired.thumbnail") };
            string job = NewWebJob();
            RunWebThumbJob(job, outfit, path, token);
            return new WebJobDto { ok = 1, job = job };
        }
        private static async void RunWebThumbJob(string jobId, string outfit, string path, string token)
        {
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(outfit);
                if (o == null) throw new Exception("Preset not found in the scene: " + outfit);
                WebJobProgress(jobId, 0, 1, outfit);
                bool succeeded = await eng.UploadThumbnailAsync(o, path, false);
                WebJobFinish(jobId, succeeded, string.IsNullOrEmpty(eng._statusMessage) ? "Thumbnail updated." : eng._statusMessage, null);
            }
            catch (Exception ex) { try { WebJobFinish(jobId, false, ex.Message, null); } catch { } }
            finally
            {
                lock (_webThumbsLock) _webThumbs.Remove(token);
                try { CleanupTempThumb(path); } catch { }
            }
        }
        internal static WebResultDto WebThumbDiscard(string token)
        {
            try
            {
                string path = null;
                lock (_webThumbsLock)
                {
                    WebThumbEntry e;
                    if (!string.IsNullOrEmpty(token) && _webThumbs.TryGetValue(token, out e)) { path = e.path; _webThumbs.Remove(token); }
                }
                try { CleanupTempThumb(path); } catch { }
                return new WebResultDto { ok = 1 };
            }
            catch (Exception ex) { return new WebResultDto { message = ex.Message }; }
        }
        // ---- Unified presets: a wardrobe preset IS the batch unit. Engine
        // config (include/platforms/blendshapes/items/faceemo) persists in
        // OutfitProjectData under the preset staging keys, so staging and
        // upload pick it up untouched. Live scene names (meshes, items,
        // FaceEmo objects) resolve against the parent scene avatar; values
        // persist per preset without opening staging scenes.
        private static bool PresetKeys(string id, out string avatarKey, out string outfitName, out string message)
        {
            avatarKey = null; outfitName = null;
            var preset = OutfitToggleGenerator.AvatarWardrobePresets.GetPreset(id);
            if (preset == null) { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.upload.set.not.found"); return false; }
            avatarKey = preset.avatarRootName ?? ""; outfitName = preset.outfitName ?? "";
            if (string.IsNullOrEmpty(avatarKey) || string.IsNullOrEmpty(outfitName)) { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.upload.set.is.incomplete"); return false; }
            message = null; return true;
        }
        private static GameObject WebParentAvatar()
        {
            try { var av = OutfitToggleGenerator.AvatarWardrobeServer.SceneAvatar; return av != null ? av.gameObject : null; }
            catch { return null; }
        }
        private static GameObject WebFindInParent(string name)
        {
            try
            {
                var av = OutfitToggleGenerator.AvatarWardrobeServer.SceneAvatar;
                if (av == null || string.IsNullOrEmpty(name)) return null;
                var t = FindDeepChild(av.gameObject.transform, name);
                return t != null ? t.gameObject : null;
            }
            catch { return null; }
        }
        private static SkinnedMeshRenderer WebParentSkin()
        {
            try
            {
                var av = OutfitToggleGenerator.AvatarWardrobeServer.SceneAvatar;
                if (av == null) return null;
                SkinnedMeshRenderer best = null;
                foreach (var smr in av.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (smr == null || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;
                    if (best == null || smr.sharedMesh.blendShapeCount > best.sharedMesh.blendShapeCount) best = smr;
                }
                return best;
            }
            catch { return null; }
        }
        private static string WebPrefabGuid(GameObject go)
        {
            try
            {
                if (go == null) return "";
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (string.IsNullOrEmpty(path)) return "";
                return AssetDatabase.AssetPathToGUID(path) ?? "";
            }
            catch { return ""; }
        }
        [Serializable]
        public class WebPresetConfigDto
        {
            public int ok; public string message = "";
            public string id = ""; public string name = "";
            public string blueprintId = ""; public string lastUpload = "";
            public int include = 1; public int win = 1; public int and; public int ios;
            public int blendCount; public string faceEmo = ""; public int faceEmoExists;
        }
        internal static WebPresetConfigDto WebPresetConfig(string id)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebPresetConfigDto { id = id ?? "" };
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebPresetConfigDto { id = id ?? "", message = msg };
                var preset = OutfitToggleGenerator.AvatarWardrobePresets.GetPreset(id);
                r.name = preset != null && preset.name != null ? preset.name : "";
                var data = OutfitProjectData.GetOutfit(avatarKey, outfitName);
                r.include = data.includeInBatch ? 1 : 0;
                r.win = data.buildWindows ? 1 : 0; r.and = data.buildAndroid ? 1 : 0; r.ios = data.buildIOS ? 1 : 0;
                r.blendCount = data.blendShapes != null ? data.blendShapes.Count : 0;
                r.faceEmo = data.faceEmoName ?? "";
                r.faceEmoExists = !string.IsNullOrEmpty(r.faceEmo) && WebFindInParent(r.faceEmo) != null ? 1 : 0;
                try { var ps = OutfitToggleGenerator.AvatarWardrobePresets.GetPresetStatus(id); r.blueprintId = ps.blueprintId ?? ""; r.lastUpload = ps.lastUpload ?? ""; } catch { }
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebPresetInclude(string id, string include)
        {
            return WebPresetConfigSet(id, include, null, null, null);
        }
        internal static WebResultDto WebPresetConfigSet(string id, string include, string win, string and, string ios, string blueprint = null)
        {
            var r = new WebResultDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebResultDto { message = msg };
                string cleanBlueprint = blueprint == null ? null : blueprint.Trim();
                if (cleanBlueprint != null && cleanBlueprint.Length > 0 && !IsValidBlueprintId(cleanBlueprint))
                    return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.invalid.avatar.id.use.avtr.followed.by.a.uuid.or") };
                var data = OutfitProjectData.GetOutfit(avatarKey, outfitName);
                if (include != null) data.includeInBatch = include == "1";
                if (win != null) data.buildWindows = win == "1";
                if (and != null) data.buildAndroid = and == "1";
                if (ios != null) data.buildIOS = ios == "1";
                if (cleanBlueprint != null) data.blueprintId = cleanBlueprint;
                OutfitProjectData.Save();
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        [Serializable]
        public class WebPresetBlendListDto { public int ok; public string message = ""; public string skin = ""; public List<WebBlendDto> items = new List<WebBlendDto>(); }
        internal static WebPresetBlendListDto WebPresetBlends(string id)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebPresetBlendListDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebPresetBlendListDto { message = msg };
                var data = OutfitProjectData.GetOutfit(avatarKey, outfitName);
                var map = new Dictionary<string, float>();
                if (data.blendShapes != null) foreach (var bs in data.blendShapes) if (bs != null && !string.IsNullOrEmpty(bs.name)) map[bs.name] = bs.value;
                var smr = WebParentSkin();
                if (smr != null && smr.sharedMesh != null)
                {
                    r.skin = smr.name;
                    var mesh = smr.sharedMesh;
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        string bs = mesh.GetBlendShapeName(i);
                        float w;
                        bool pinned = map.TryGetValue(bs, out w);
                        if (!pinned) w = smr.GetBlendShapeWeight(i);
                        r.items.Add(new WebBlendDto { name = bs, weight = w, pinned = pinned ? 1 : 0 });
                    }
                    foreach (var kv in map) if (!r.items.Any(x => x.name == kv.Key)) r.items.Add(new WebBlendDto { name = kv.Key, weight = kv.Value, pinned = 1 });
                }
                else foreach (var kv in map) r.items.Add(new WebBlendDto { name = kv.Key, weight = kv.Value, pinned = 1 });
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebPresetBlendSet(string id, string bs, string pinned, string weight)
        {
            var r = new WebResultDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebResultDto { message = msg };
                if (string.IsNullOrEmpty(bs)) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.blendshape.missing") };
                var entry = new OutfitEntry { Name = outfitName, Data = OutfitProjectData.GetOutfit(avatarKey, outfitName) };
                LoadBlendShapes(entry);
                if (pinned == "1")
                {
                    float w;
                    if (!float.TryParse(weight, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w)) w = 0;
                    entry.BlendShapes[bs] = Math.Max(0f, Math.Min(100f, w));
                }
                else entry.BlendShapes.Remove(bs);
                SaveBlendShapes(entry);
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebPresetBlendCapture(string id)
        {
            var r = new WebResultDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebResultDto { message = msg };
                var smr = WebParentSkin();
                if (smr == null || smr.sharedMesh == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.skinned.mesh.in.the.parent.scene") };
                var entry = new OutfitEntry { Name = outfitName, Data = OutfitProjectData.GetOutfit(avatarKey, outfitName) };
                LoadBlendShapes(entry);
                var mesh = smr.sharedMesh;
                int n = 0;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    float w = smr.GetBlendShapeWeight(i);
                    if (w > 0f) { entry.BlendShapes[mesh.GetBlendShapeName(i)] = w; n++; }
                }
                SaveBlendShapes(entry);
                r.ok = 1; r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.captured") + n + " blendshape(s).";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebPresetBlendClear(string id)
        {
            var r = new WebResultDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebResultDto { message = msg };
                var entry = new OutfitEntry { Name = outfitName, Data = OutfitProjectData.GetOutfit(avatarKey, outfitName) };
                LoadBlendShapes(entry);
                entry.BlendShapes.Clear();
                SaveBlendShapes(entry);
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        [Serializable]
        public class WebPresetItemListDto { public int ok; public string message = ""; public string parent = ""; public List<WebItemDto> items = new List<WebItemDto>(); }
        internal static WebPresetItemListDto WebPresetItems(string id)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebPresetItemListDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebPresetItemListDto { message = msg };
                var parent = WebParentAvatar();
                if (parent == null) return new WebPresetItemListDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.select.a.scene.avatar.first") };
                string itemsParent = EditorPrefs.GetString(ITEMS_PARENT_NAME, DEFAULT_ITEMS_PARENT);
                var t = FindDeepChild(parent.transform, itemsParent);
                r.parent = itemsParent;
                if (t != null)
                    foreach (Transform child in t)
                    {
                        if (child == null) continue;
                        r.items.Add(new WebItemDto { name = child.name, included = OutfitProjectData.GetItemIncluded(avatarKey, outfitName, child.name) ? 1 : 0, isDefault = OutfitProjectData.GetItemDefault(avatarKey, child.name) ? 1 : 0 });
                    }
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebPresetItemSet(string id, string item, string include)
        {
            var r = new WebResultDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebResultDto { message = msg };
                if (string.IsNullOrEmpty(item)) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.item.missing") };
                OutfitProjectData.SetItemIncluded(avatarKey, outfitName, item, include == "1");
                WebEngine().ClearVramCache();
                WebEngine().MarkBudgetsDirty();
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebPresetItemAll(string id, string include, string filter)
        {
            var r = new WebResultDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebResultDto { message = msg };
                var list = WebPresetItems(id);
                if (list == null || list.ok != 1) return new WebResultDto { message = list != null ? list.message : "No items." };
                string f = (filter ?? "").ToLowerInvariant();
                var names = new List<string>();
                foreach (var it in list.items)
                    if (f.Length == 0 || it.name.ToLowerInvariant().Contains(f)) names.Add(it.name);
                OutfitProjectData.SetItemsIncluded(avatarKey, outfitName, names, include == "1");
                WebEngine().ClearVramCache();
                WebEngine().MarkBudgetsDirty();
                r.ok = 1; r.message = names.Count + " item(s) updated.";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebFaceEmoDto WebPresetFaceEmo(string id)
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebFaceEmoDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebFaceEmoDto { message = msg };
                var data = OutfitProjectData.GetOutfit(avatarKey, outfitName);
                r.assigned = data.faceEmoName ?? "";
                r.assignedExists = !string.IsNullOrEmpty(r.assigned) && WebFindInParent(r.assigned) != null ? 1 : 0;
                r.strayExists = WebFindInParent(FACEEMO_PREFAB_NAME) != null ? 1 : 0;
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        internal static WebResultDto WebPresetFaceEmoSet(string id, string name)
        {
            var r = new WebResultDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebResultDto { message = msg };
                OutfitProjectData.SetFaceEmoName(avatarKey, outfitName, name ?? "");
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebPresetFaceEmoCapture(string id)
        {
            var r = new WebResultDto();
            try
            {
                string avatarKey, outfitName, msg;
                if (!PresetKeys(id, out avatarKey, out outfitName, out msg)) return new WebResultDto { message = msg };
                var parent = WebParentAvatar();
                if (parent == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.select.a.scene.avatar.first") };
                var src = WebFindInParent(FACEEMO_PREFAB_NAME);
                if (src == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.freshly.generated.faceemoprefab.found.in.the.parent.scene") };
                string newName = "FaceEmo__" + outfitName;
                var existing = WebFindInParent(newName);
                if (existing != null && existing != src) Undo.DestroyObjectImmediate(existing);
                Undo.RecordObject(src, "Capture FaceEmo");
                src.name = newName;
                EditorUtility.SetDirty(src);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                OutfitProjectData.SetFaceEmoName(avatarKey, outfitName, newName);
                r.ok = 1; r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.captured.faceemo.for") + outfitName + ".";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        internal static WebResultDto WebPingObject(string guid)
        {
            var r = new WebResultDto();
            try
            {
                var parent = WebParentAvatar();
                if (parent == null) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.select.a.scene.avatar.first") };
                if (string.IsNullOrEmpty(guid)) return new WebResultDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.no.prefab.link") };
                var seen = new HashSet<GameObject>();
                foreach (var t in parent.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    var root = PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject);
                    if (root == null || !seen.Add(root)) continue;
                    string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (AssetDatabase.AssetPathToGUID(path) == guid)
                    {
                        EditorGUIUtility.PingObject(root);
                        Selection.activeGameObject = root;
                        r.ok = 1;
                        return r;
                    }
                }
                r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.not.installed.in.the.parent.scene");
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
        [Serializable]
        public class WebUnassignedItemDto { public string name = ""; public string guid = ""; public int hasBlueprint; }
        [Serializable]
        public class WebUnassignedDto { public int ok; public string message = ""; public List<WebUnassignedItemDto> items = new List<WebUnassignedItemDto>(); }
        internal static WebUnassignedDto WebUnassigned()
        {
            using var readScope = OutfitProjectData.ReadOnly();
            var r = new WebUnassignedDto();
            try
            {
                string baseKey, baseName;
                OutfitToggleGenerator.AvatarWardrobePresets.CurrentBase(out baseKey, out baseName);
                var assigned = new HashSet<string>();
                if (!string.IsNullOrEmpty(baseKey))
                    foreach (var a in OutfitToggleGenerator.AvatarWardrobePresets.AssignmentsForBase(baseKey))
                        if (a != null && !string.IsNullOrEmpty(a.guid)) assigned.Add(a.guid);
                var eng = WebEngine();
                foreach (var o in eng._outfits)
                {
                    if (o == null || o.Go == null) continue;
                    if (OutfitToggleGenerator.AvatarWardrobePresets.PresetsForBase(baseKey).Any(p =>
                        p.legacyPath == ScenePathOf(o.Go.transform, eng._avatarRoot.transform) &&
                        (string.IsNullOrEmpty(p.legacyScene) || p.legacyScene == o.Go.scene.path))) continue;
                    string guid = WebPrefabGuid(o.Go);
                    if (!string.IsNullOrEmpty(guid) && assigned.Contains(guid)) continue;
                    r.items.Add(new WebUnassignedItemDto { name = o.Name, guid = guid ?? "", hasBlueprint = string.IsNullOrWhiteSpace(o.BlueprintId) ? 0 : 1 });
                }
                r.ok = 1;
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;

        }
        [Serializable]
        public class WebPresetCreateDto { public int ok; public string message = ""; public string id = ""; }
        internal static WebPresetCreateDto WebPresetFromScene(string name)
        {
            var r = new WebPresetCreateDto();
            try
            {
                var eng = WebEngine();
                var o = eng.WebFindOutfit(name);
                if (o == null) return new WebPresetCreateDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.preset.not.found.in.the.scene") + name };
                var preset = OutfitToggleGenerator.AvatarWardrobePresets.RegisterLegacy(o.Go,
                    ScenePathOf(o.Go.transform, eng._avatarRoot.transform), o.Data);
                if (preset == null) return new WebPresetCreateDto { message = global::OutfitToggleGenerator.WardrobeStrings.T("server.select.a.scene.avatar.first") };
                r.ok = 1; r.id = preset.id; r.message = global::OutfitToggleGenerator.WardrobeStrings.T("server.detected.preset") + preset.name + ".";
            }
            catch (Exception ex) { r.message = ex.Message; }
            return r;
        }
    }
}
