using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// HTTP routing and same-origin request validation.
namespace OutfitToggleGenerator
{
    internal static partial class AvatarWardrobeServer
    {
        private static void Handle(HttpListenerContext context)
        {
            var request = context.Request;
            if (!ValidateRequest(context)) return;
            // The page speaks the browser's language, Unity speaks the OS one.
            // Prefer the explicit locale sent by the page so a saved language
            // override also localizes Unity-generated badges and messages.
            // Older/direct clients still work through Accept-Language.
            // Thread-local locale also travels with queued main-thread work.
            string requestCode = null;
            try
            {
                var query = Query(request.Url.Query);
                string requestedCode;
                query.TryGetValue("lang", out requestedCode);
                requestCode = WardrobeStrings.ResolveCode(requestedCode);
                if (requestCode == null)
                {
                    var header = request.Headers["Accept-Language"];
                    if (!string.IsNullOrEmpty(header))
                        requestCode = WardrobeStrings.ResolveCode(header.Split(',')[0]);
                }
            }
            catch (Exception) { }
            var path = request.Url.AbsolutePath;
            if (path == "/" || path == "/index.html" || path == "/wardrobe.html")
            {
                // Static file, served straight off the listener thread: even a
                // saturated Unity main thread cannot slow the page load itself.
                if (thumbDir != null && File.Exists(htmlPath))
                    WriteBytes(context, 200, "text/html", File.ReadAllBytes(htmlPath));
                else WriteText(context, 500, "text/plain", "wardrobe.html is missing.");
                return;
            }
            if (path == "/lang.json")
            {
                // Same file the Unity side reads: one source of truth, and the
                // page picks up translation edits with a plain reload.
                if (langPath != null && File.Exists(langPath))
                    WriteBytes(context, 200, "application/json", File.ReadAllBytes(langPath));
                else WriteText(context, 500, "text/plain", "lang.json is missing.");
                return;
            }
            // Serve only shipped UI files; never map arbitrary request paths to disk.
            string uiFile = null;
            string uiMime = null;
            switch (path)
            {
                case "/wardrobe.css": uiFile = "wardrobe.css"; uiMime = "text/css"; break;
                case "/runtime.js": uiFile = "runtime.js"; uiMime = "application/javascript"; break;
                case "/previews.js": uiFile = "previews.js"; uiMime = "application/javascript"; break;
                case "/upload.js": uiFile = "upload.js"; uiMime = "application/javascript"; break;
                case "/wardrobe.js": uiFile = "wardrobe.js"; uiMime = "application/javascript"; break;
                case "/assets/header-portrait.webp": uiFile = "assets/header-portrait.webp"; uiMime = "image/webp"; break;
                case "/assets/rail-landscape.webp": uiFile = "assets/rail-landscape.webp"; uiMime = "image/webp"; break;
                case "/assets/avatar-placeholder.webp": uiFile = "assets/avatar-placeholder.webp"; uiMime = "image/webp"; break;
            }
            if (uiFile != null)
            {
                var webRoot = string.IsNullOrEmpty(htmlPath) ? null : Path.GetDirectoryName(htmlPath);
                var uiPath = webRoot == null ? null : Path.Combine(webRoot, uiFile);
                if (uiPath != null && File.Exists(uiPath))
                    WriteBytes(context, 200, uiMime, File.ReadAllBytes(uiPath));
                else WriteText(context, 404, "text/plain", "UI file not found.");
                return;
            }
            if (path == "/favicon.ico")
            {
                WriteText(context, 204, "text/plain", string.Empty);
                return;
            }
            if (path == "/api/target")
            {
                var query = Query(request.Url.Query);
                WriteJson(context, 200, RunOnMain(() => SelectTarget(query.ContainsKey("id") ? query["id"] : ""), requestCode));
                return;
            }
            if (path == "/api/state")
            {
                WriteJson(context, 200, RunOnMain(GetState, requestCode));
                return;
            }
            if (path == "/api/families")
            {
                var query = Query(request.Url.Query);
                WriteJson(context, 200, RunOnMain(() => GetFamilies(query), requestCode));
                return;
            }
            if (path == "/api/family")
            {
                var query = Query(request.Url.Query);
                string id;
                query.TryGetValue("id", out id);
                string target;
                query.TryGetValue("target", out target);
                var detail = RunOnMain(() => GetFamily(id, target), requestCode);
                if (detail == null)
                {
                    string notFound;
                    WardrobeStrings.RequestCode = requestCode;
                    try { notFound = WardrobeStrings.T("err.notfound"); }
                    finally { WardrobeStrings.RequestCode = null; }
                    WriteJson(context, 404, new ResultDto { ok = 0, message = notFound });
                }
                else WriteJson(context, 200, detail);
                return;
            }
            if (path == "/api/installed")
            {
                WriteJson(context, 200, RunOnMain(GetInstalled, requestCode));
                return;
            }
            if (path == "/api/name")
            {
                var query = Query(request.Url.Query);
                string id;
                query.TryGetValue("id", out id);
                WriteJson(context, 200, RunOnMain(() => StartNameJob(id), requestCode));
                return;
            }
            if (path == "/api/nameResult")
            {
                NameResultDto nameResult;
                WardrobeStrings.RequestCode = requestCode;
                try { nameResult = GetNameResult(Query(request.Url.Query)); }
                finally { WardrobeStrings.RequestCode = null; }
                WriteJson(context, 200, nameResult);
                return;
            }
            if (path == "/api/shops")
            {
                WriteJson(context, 200, RunOnMain(GetShops, requestCode));
                return;
            }
            if (path == "/api/diag")
            {
                WriteJson(context, 200, RunOnMain(GetDiag, requestCode));
                return;
            }
            if (path == "/api/active")
            {
                // Focus heartbeat from the page: a 15s lease, tolerant of
                // two missed 5s beats; cleared the moment the page hides.
                var query = Query(request.Url.Query);
                string on;
                query.TryGetValue("on", out on);
                string grid;
                query.TryGetValue("grid", out grid);
                lock (webActiveLock)
                {
                    webActiveUntil = on == "1" ? DateTime.UtcNow.AddSeconds(15) : DateTime.MinValue;
                    if (on == "1" && grid != null) previewGridPriority = grid.Length <= 4096 ? grid : grid.Substring(0, 4096);
                }
                WriteJson(context, 200, new ResultDto { ok = 1 });
                return;
            }
            if (path == "/api/thumb")
            {
                var query = Query(request.Url.Query);
                string guid;
                query.TryGetValue("guid", out guid);
                string hiFlag;
                query.TryGetValue("hi", out hiFlag);
                var hi = hiFlag == "1";
                string retryFlag;
                query.TryGetValue("retry", out retryFlag);
                if (!IsAssetGuid(guid)) { WriteText(context, 400, "text/plain", "Invalid asset GUID."); return; }
                if (retryFlag == "1") RunOnMain(() => { ResetThumb(guid); return 0; }, requestCode);
                var dir = hi ? hiDir : thumbDir;
                // Disk-cached thumbnails skip the main thread entirely, so
                // scrolling the grid stays fast while Unity is busy.
                if (dir != null && !string.IsNullOrEmpty(guid) && IsAssetGuid(guid))
                {
                    var cached = ThumbPath(guid, hi);
                    if (File.Exists(cached))
                    {
                        try
                        {
                            WriteBytes(context, 200, "image/png", File.ReadAllBytes(cached));
                            return;
                        }
                        catch (Exception) { }
                    }
                }
                // Disk hits above serve any time; fresh bakes wait for web
                // focus so Unity keeps its main thread while editing.
                if (!WebActive)
                {
                    WriteJson(context, 202, new ResultDto { ok = 0, message = "pending" });
                    return;
                }
                // Bakes are background: instant metadata first, pixels when idle.
                // Starved past 30s the wait times out and the client falls
                // back to low-res, which already heals on the next render.
                var outcome = RunOnMain(() => hi ? BakeThumbHi(guid) : BakeThumb(guid), requestCode, true);
                if (outcome == null) WriteJson(context, 202, new ResultDto { ok = 0, message = "pending" });
                else if (outcome.Length == 0) WriteJson(context, 404, new ResultDto { ok = 0, message = "unavailable" });
                else WriteBytes(context, 200, "image/png", outcome);
                return;
            }
            if (path == "/api/compatibility_override")
            {
                var query = Query(request.Url.Query);
                string guid, enabled;
                query.TryGetValue("guid", out guid);
                query.TryGetValue("enabled", out enabled);
                WriteJson(context, 200, RunOnMain(() =>
                {
                    if (AvatarWardrobeCatalog.GetRecord(guid) == null || (enabled != "0" && enabled != "1"))
                        return new ResultDto { ok = 0, message = WardrobeStrings.T("msg.nooutfit") };
                    return EditAvatar("Trust this avatar fit", () => {
                        AvatarWardrobeCatalog.SetFitTrust(guid, ActiveAvatarGuid(), enabled == "1");
                        return new ResultDto { ok = 1 };
                    });
                }, requestCode));
                return;
            }
            if (path == "/api/install")
            {
                var query = Query(request.Url.Query);
                string guid;
                query.TryGetValue("guid", out guid);
                string allow;
                query.TryGetValue("allow", out allow);
                string toggles;
                query.TryGetValue("toggles", out toggles);
                string switchVariant;
                query.TryGetValue("switch", out switchVariant);
                string target;
                query.TryGetValue("target", out target);
                string group;
                query.TryGetValue("group", out group);
                // Missing means enabled for clients from before this option.
                WriteJson(context, 200, RunOnMain(() => Install(
                    guid, allow == "1", toggles != "0", switchVariant == "1", target ?? string.Empty, group ?? string.Empty, query.ContainsKey("replaceId") ? query["replaceId"] : "", query.ContainsKey("copy") && query["copy"] == "1"), requestCode));
                return;
            }
            if (path == "/api/scene_upload_thumbnail")
            {
                Query(request.Url.Query).TryGetValue("avatarId", out var id);
                var png = RunOnMain(() => {
                    var avatar = SceneAvatar;
                    if (avatar == null || avatar.GetInstanceID().ToString() != id) return null;
                    var key = GlobalObjectId.GetGlobalObjectIdSlow(avatar.gameObject).ToString();
                    var file = AvatarWardrobeUpload.ResolveSceneUploadThumbnail(avatar.gameObject, key, false);
                    return File.ReadAllBytes(file);
                }, requestCode, true);
                if (png == null) WriteJson(context, 404, new ResultDto { message = "Preview unavailable." });
                else WriteBytes(context, 200, "image/png", png);
                return;
            }
            if (path == "/api/scene_upload_review")
            {
                WriteJson(context, 200, RunOnMain(ReviewSceneUpload, requestCode));
                return;
            }
            if (path == "/api/scene_upload")
            {
                var query = Query(request.Url.Query);
                WriteJson(context, 200, RunOnMain(() => StartSceneUpload(query), requestCode));
                return;
            }
            if (path == "/api/scene_upload_cancel")
            {
                Query(request.Url.Query).TryGetValue("job", out var job);
                WriteJson(context, 200, CancelSceneUpload(job));
                return;
            }
            if (path == "/api/upload")
            {
                var query = Query(request.Url.Query);
                string guid;
                query.TryGetValue("guid", out guid);
                string preset;
                query.TryGetValue("preset", out preset);
                WriteJson(context, 200, RunOnMain(() => StartUpload(guid, preset), requestCode));
                return;
            }
            if (path == "/api/upload_result")
            {
                UploadResultDto uploadResult;
                WardrobeStrings.RequestCode = requestCode;
                try { uploadResult = GetUploadResult(Query(request.Url.Query)); }
                finally { WardrobeStrings.RequestCode = null; }
                WriteJson(context, 200, uploadResult);
                return;
            }
            if (path == "/api/upload_status")
            {
                var query = Query(request.Url.Query);
                string guid;
                query.TryGetValue("guid", out guid);
                string preset;
                query.TryGetValue("preset", out preset);
                WriteJson(context, 200, RunOnMain(() => GetUploadStatus(guid, preset), requestCode));
                return;
            }
            if (path == "/api/batch_state")
            {
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebGetState(), requestCode));
                return;
            }
            if (path == "/api/batch_outfit_set")
            {
                var query = Query(request.Url.Query);
                string name, blueprint, include, win, and, ios;
                query.TryGetValue("name", out name);
                query.TryGetValue("blueprint", out blueprint);
                query.TryGetValue("include", out include);
                query.TryGetValue("win", out win);
                query.TryGetValue("and", out and);
                query.TryGetValue("ios", out ios);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebOutfitSet(name, blueprint, include, win, and, ios), requestCode));
                return;
            }
            if (path == "/api/batch_select")
            {
                var query = Query(request.Url.Query);
                string name;
                query.TryGetValue("name", out name);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebSelect(name), requestCode));
                return;
            }
            if (path == "/api/batch_ping")
            {
                var query = Query(request.Url.Query);
                string name;
                query.TryGetValue("name", out name);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPing(name), requestCode));
                return;
            }
            if (path == "/api/batch_config_set")
            {
                var query = Query(request.Url.Query);
                string version, versionMode, sound, outfitsParent, itemsParent, avatar, skinPath;
                query.TryGetValue("version", out version);
                query.TryGetValue("versionMode", out versionMode);
                query.TryGetValue("sound", out sound);
                query.TryGetValue("outfitsParent", out outfitsParent);
                query.TryGetValue("itemsParent", out itemsParent);
                query.TryGetValue("avatar", out avatar);
                query.TryGetValue("skinPath", out skinPath);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebConfigSet(version, versionMode, sound, outfitsParent, itemsParent, avatar, skinPath), requestCode));
                return;
            }
            if (path == "/api/batch_defaults_set")
            {
                var query = Query(request.Url.Query);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebDefaultsSet(query), requestCode));
                return;
            }
            if (path == "/api/batch_upload_one")
            {
                var query = Query(request.Url.Query);
                string name;
                query.TryGetValue("name", out name);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebUploadScene(name, false), requestCode));
                return;
            }
            if (path == "/api/batch_upload_scene")
            {
                var query = Query(request.Url.Query);
                string names, express;
                query.TryGetValue("names", out names);
                query.TryGetValue("express", out express);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebUploadScene(names, express == "1"), requestCode));
                return;
            }
            if (path == "/api/batch_upload_presets")
            {
                var query = Query(request.Url.Query);
                string ids;
                query.TryGetValue("ids", out ids);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebUploadPresets(ids), requestCode));
                return;
            }
            if (path == "/api/batch_job")
            {
                var query = Query(request.Url.Query);
                string job;
                query.TryGetValue("job", out job);
                WriteJson(context, 200, ShiroTools.OutfitBatchUploader.WebJobResult(job));
                return;
            }
            if (path == "/api/batch_cancel")
            {
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebCancel(), requestCode));
                return;
            }
            if (path == "/api/batch_retry")
            {
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebRetry(), requestCode));
                return;
            }
            if (path == "/api/batch_dismiss_failed")
            {
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebDismissFailed(), requestCode));
                return;
            }
            if (path == "/api/batch_dryrun")
            {
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebDryRun(), requestCode));
                return;
            }
            if (path == "/api/batch_express")
            {
                var query = Query(request.Url.Query);
                string exname;
                query.TryGetValue("name", out exname);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebExpress(exname, query), requestCode));
                return;
            }
            if (path == "/api/batch_fetch")
            {
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebFetchAvatars(), requestCode));
                return;
            }
            if (path == "/api/batch_match")
            {
                var query = Query(request.Url.Query);
                string apply;
                query.TryGetValue("apply", out apply);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebMatch(apply == "1"), requestCode));
                return;
            }
            if (path == "/api/batch_export")
            {
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebExport(), requestCode));
                return;
            }
            if (path == "/api/batch_import")
            {
                var query = Query(request.Url.Query);
                string op, token, data;
                query.TryGetValue("op", out op);
                query.TryGetValue("token", out token);
                query.TryGetValue("data", out data);
                if (op == "chunk") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebImportChunk(token, data), requestCode));
                else if (op == "commit") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebImportCommit(token), requestCode));
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebImportBegin(), requestCode));
                return;
            }
            if (path == "/api/batch_blendshape")
            {
                var query = Query(request.Url.Query);
                string op, name, bs, pinned, weight;
                query.TryGetValue("op", out op);
                query.TryGetValue("name", out name);
                query.TryGetValue("bs", out bs);
                query.TryGetValue("pinned", out pinned);
                query.TryGetValue("weight", out weight);
                if (op == "set") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebBlendshapeSet(name, bs, pinned, weight), requestCode));
                else if (op == "capture") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebBlendshapeCapture(name), requestCode));
                else if (op == "clear") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebBlendshapeClear(name), requestCode));
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebBlendshapes(name), requestCode));
                return;
            }
            if (path == "/api/batch_item")
            {
                var query = Query(request.Url.Query);
                string op, outfit, item, include, filter;
                query.TryGetValue("op", out op);
                query.TryGetValue("outfit", out outfit);
                query.TryGetValue("item", out item);
                query.TryGetValue("include", out include);
                query.TryGetValue("filter", out filter);
                if (op == "set") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebItemSet(outfit, item, include), requestCode));
                else if (op == "all") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebItemAll(outfit, include, filter), requestCode));
                else if (op == "default") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebItemDefault(item, include), requestCode));
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebItems(outfit), requestCode));
                return;
            }
            if (path == "/api/batch_faceemo")
            {
                var query = Query(request.Url.Query);
                string op, name;
                query.TryGetValue("op", out op);
                query.TryGetValue("name", out name);
                if (op == "capture") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebFaceEmoCapture(name), requestCode));
                else if (op == "clear") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebFaceEmoClear(name), requestCode));
                else if (op == "open") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebFaceEmoOpen(), requestCode));
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebFaceEmo(name), requestCode));
                return;
            }
            if (path == "/api/batch_vram")
            {
                var query = Query(request.Url.Query);
                string op, name;
                query.TryGetValue("op", out op);
                query.TryGetValue("name", out name);
                if (op == "apply") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebVramApply(name), requestCode));
                else if (op == "sync") WriteJson(context, 200, new ShiroTools.OutfitBatchUploader.WebVramDto { ok = 1, savedMB = RunOnMain(() => ShiroTools.OutfitBatchUploader.WebVramSync(name), requestCode) });
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebVramPreview(name), requestCode));
                return;
            }
            if (path == "/api/batch_thumb")
            {
                var query = Query(request.Url.Query);
                string op, name, mode, token;
                query.TryGetValue("op", out op);
                query.TryGetValue("name", out name);
                query.TryGetValue("mode", out mode);
                query.TryGetValue("token", out token);
                if (op == "confirm") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebThumbConfirm(token), requestCode));
                else if (op == "discard") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebThumbDiscard(token), requestCode));
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebThumbCapture(name, mode), requestCode));
                return;
            }
            if (path == "/api/batch_thumb_img")
            {
                var query = Query(request.Url.Query);
                string token;
                query.TryGetValue("token", out token);
                var bytes = RunOnMain(() => ShiroTools.OutfitBatchUploader.WebThumbBytes(token), requestCode);
                if (bytes == null) WriteJson(context, 404, new ResultDto { message = "Thumbnail expired." });
                else WriteBytes(context, 200, "image/png", bytes);
                return;
            }
            if (path == "/api/menu_groups")
            {
                var query = Query(request.Url.Query);
                string id, group, name, item, guid, op;
                query.TryGetValue("id", out id); query.TryGetValue("group", out group);
                query.TryGetValue("name", out name); query.TryGetValue("item", out item);
                query.TryGetValue("guid", out guid); query.TryGetValue("op", out op);
                WriteJson(context, 200, RunOnMain(() =>
                {
                    try
                    {
                        var changed = "";
                        if (!string.IsNullOrEmpty(op))
                        {
                            var result = EditAvatar("Organize wardrobe menu", () => {
                                changed = AvatarWardrobePresets.UpdateMenuGroup(id, group, name, item, guid, op);
                                return new ResultDto { ok = 1 };
                            });
                            if (result.ok != 1) return new MenuGroupsDto { message = result.message };
                        }
                        return new MenuGroupsDto { ok = 1, id = changed, groups = AvatarWardrobePresets.MenuGroups(id) };
                    }
                    catch (Exception ex) { return new MenuGroupsDto { message = ex.Message }; }
                }, requestCode));
                return;
            }
            if (path == "/api/regenerate_toggles")
            {
                WriteJson(context, 200, RunOnMain(() =>
                {
                    if (SceneAvatar == null) return new ResultDto { message = "Select an avatar in the Unity launcher first." };
                    return EditAvatar("Regenerate wardrobe toggles", () =>
                    {
                        OutfitToggleGenerator.RegeneratePresetToggles(SceneAvatar);
                        return new ResultDto { ok = 1, message = "Preset and Menu Group toggles regenerated." };
                    });
                }, requestCode));
                return;
            }
            if (path == "/api/preset_show")
            {
                var query = Query(request.Url.Query);
                string id;
                query.TryGetValue("id", out id);
                WriteJson(context, 200, RunOnMain(() =>
                {
                    try
                    {
                        AvatarWardrobePresets.ShowInUnity(id, SceneAvatar);
                        return new ResultDto { ok = 1 };
                    }
                    catch (Exception ex) { return new ResultDto { message = ex.Message }; }
                }, requestCode));
                return;
            }
            if (path == "/api/preset_include")
            {
                var query = Query(request.Url.Query);
                string id, include;
                query.TryGetValue("id", out id);
                query.TryGetValue("include", out include);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetInclude(id, include), requestCode));
                return;
            }
            if (path == "/api/batch_preset_config")
            {
                var query = Query(request.Url.Query);
                string id, include, win, and, ios, blueprint;
                query.TryGetValue("id", out id);
                query.TryGetValue("include", out include);
                query.TryGetValue("win", out win);
                query.TryGetValue("and", out and);
                query.TryGetValue("ios", out ios);
                query.TryGetValue("blueprint", out blueprint);
                if (blueprint != null || include != null || win != null || and != null || ios != null)
                    WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetConfigSet(id, include, win, and, ios, blueprint), requestCode));
                else
                    WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetConfig(id), requestCode));
                return;
            }
            if (path == "/api/batch_preset_blends")
            {
                var query = Query(request.Url.Query);
                string id, op, bs, pinned, weight;
                query.TryGetValue("id", out id);
                query.TryGetValue("op", out op);
                query.TryGetValue("bs", out bs);
                query.TryGetValue("pinned", out pinned);
                query.TryGetValue("weight", out weight);
                if (op == "capture") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetBlendCapture(id), requestCode));
                else if (op == "clear") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetBlendClear(id), requestCode));
                else if (bs != null) WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetBlendSet(id, bs, pinned, weight), requestCode));
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetBlends(id), requestCode));
                return;
            }
            if (path == "/api/batch_preset_items")
            {
                var query = Query(request.Url.Query);
                string id, op, item, include, filter;
                query.TryGetValue("id", out id);
                query.TryGetValue("op", out op);
                query.TryGetValue("item", out item);
                query.TryGetValue("include", out include);
                query.TryGetValue("filter", out filter);
                if (op == "all") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetItemAll(id, include, filter), requestCode));
                else if (item != null) WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetItemSet(id, item, include), requestCode));
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetItems(id), requestCode));
                return;
            }
            if (path == "/api/batch_preset_faceemo")
            {
                var query = Query(request.Url.Query);
                string id, op, name;
                query.TryGetValue("id", out id);
                query.TryGetValue("op", out op);
                query.TryGetValue("name", out name);
                if (op == "capture") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetFaceEmoCapture(id), requestCode));
                else if (op == "clear") WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetFaceEmoSet(id, ""), requestCode));
                else if (name != null) WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetFaceEmoSet(id, name), requestCode));
                else WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetFaceEmo(id), requestCode));
                return;
            }
            if (path == "/api/batch_unassigned")
            {
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebUnassigned(), requestCode));
                return;
            }
            if (path == "/api/batch_ping_object")
            {
                var query = Query(request.Url.Query);
                string guid;
                query.TryGetValue("guid", out guid);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPingObject(guid), requestCode));
                return;
            }
            if (path == "/api/batch_preset_from_scene")
            {
                var query = Query(request.Url.Query);
                string name;
                query.TryGetValue("name", out name);
                WriteJson(context, 200, RunOnMain(() => ShiroTools.OutfitBatchUploader.WebPresetFromScene(name), requestCode));
                return;
            }
            if (path == "/api/presets")
            {
                WriteJson(context, 200, RunOnMain(GetPresets, requestCode));
                return;
            }
            if (path == "/api/workflow")
            {
                var query = Query(request.Url.Query);
                string mode, selected;
                query.TryGetValue("mode", out mode);
                query.TryGetValue("selected", out selected);
                WriteJson(context, 200, RunOnMain(() =>
                {
                    if (!string.IsNullOrEmpty(selected) && (mode ?? ReadWorkflow().wardrobeMode) == "multi-avatar")
                        AvatarWardrobePresets.ShowInUnity(selected, SceneAvatar);
                    return SaveWorkflow(mode, selected);
                }, requestCode));
                return;
            }
            if (path == "/api/preset_save")
            {
                var query = Query(request.Url.Query);
                string id;
                query.TryGetValue("id", out id);
                string name;
                query.TryGetValue("name", out name);
                WriteJson(context, 200, RunOnMain(() => SavePreset(id, name), requestCode));
                return;
            }
            if (path == "/api/preset_delete")
            {
                var query = Query(request.Url.Query);
                string id;
                query.TryGetValue("id", out id);
                WriteJson(context, 200, RunOnMain(() => DeletePreset(id), requestCode));
                return;
            }
            if (path == "/api/preset_assign")
            {
                var query = Query(request.Url.Query);
                string guid;
                query.TryGetValue("guid", out guid);
                string assignTarget;
                query.TryGetValue("target", out assignTarget);
                WriteJson(context, 200, RunOnMain(() => AssignPreset(guid, assignTarget), requestCode));
                return;
            }
            if (path == "/api/part_toggles")
            {
                var query = Query(request.Url.Query);
                string guid, target, enabled;
                query.TryGetValue("guid", out guid); query.TryGetValue("target", out target); query.TryGetValue("enabled", out enabled);
                WriteJson(context, 200, RunOnMain(() => SetPartToggles(guid, target, enabled == "1"), requestCode));
                return;
            }
            if (path == "/api/prefab_presets")
            {
                var query = Query(request.Url.Query);
                string guid;
                query.TryGetValue("guid", out guid);
                WriteJson(context, 200, RunOnMain(() => GetPrefabPresets(guid), requestCode));
                return;
            }
            if (path == "/api/preset_remove_item")
            {
                var query = Query(request.Url.Query);
                string guid, target, item;
                query.TryGetValue("guid", out guid); query.TryGetValue("target", out target); query.TryGetValue("item", out item);
                WriteJson(context, 200, RunOnMain(() => RemovePresetItem(guid, target, item, query.ContainsKey("instanceId") ? query["instanceId"] : null), requestCode));
                return;
            }
            if (path == "/api/remove")
            {
                var query = Query(request.Url.Query);
                string guid;
                query.TryGetValue("guid", out guid);
                WriteJson(context, 200, RunOnMain(() => Remove(guid), requestCode));
                return;
            }
            if (path == "/api/avatar_base")
            {
                var query = Query(request.Url.Query);
                query.TryGetValue("guid", out var guid);
                WriteJson(context, 200, RunOnMain(() =>
                {
                    return EditAvatar("Correct avatar base", () => {
                        AvatarWardrobeCatalog.SetAvatarOverride(SceneAvatar, guid);
                        return new ResultDto { ok = 1 };
                    });
                }, requestCode));
                return;
            }
            if (path == "/api/cache_clear")
            {
                WriteJson(context, 200, RunOnMain(() =>
                {
                    if (UploadTargetLocked || ShiroTools.OutfitBatchUploader.BatchActiveNow)
                        return new ResultDto { message = "Wait for the upload to finish before clearing the cache." };
                    AvatarWardrobeCatalog.WipeCache();
                    RefreshPreviewVersions();
                    return StartIndex(true);
                }, requestCode));
                return;
            }
            if (path == "/api/index")
            {
                var query = Query(request.Url.Query);
                string full;
                query.TryGetValue("full", out full);
                WriteJson(context, 200, RunOnMain(() => StartIndex(full == "1"), requestCode));
                return;
            }
            WriteText(context, 404, "text/plain", "Unknown wardrobe endpoint.");
        }

        private static bool ValidateRequest(HttpListenerContext context)
        {
            var r = context.Request;
            if (!r.IsLocal || r.Url == null || !WardrobeHttpPolicy.SameOrigin(r.Url.GetLeftPart(UriPartial.Authority), Port))
            { WriteText(context, 403, "text/plain", "Local requests only."); return false; }
            var origin = r.Headers["Origin"];
            if ((!string.IsNullOrEmpty(origin) && !WardrobeHttpPolicy.SameOrigin(origin, Port)) ||
                r.Headers["Sec-Fetch-Site"] == "cross-site")
            { WriteText(context, 403, "text/plain", "Cross-origin requests are not allowed."); return false; }
            var api = r.Url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal);
            if (r.HttpMethod == "OPTIONS")
            { WriteText(context, 405, "text/plain", "Cross-origin access is not supported."); return false; }
            if (r.Url.Query.Length > 65536)
            { WriteText(context, 414, "text/plain", "Request is too large."); return false; }
            var query = Query(r.Url.Query);
            string op, retry;
            query.TryGetValue("op", out op); query.TryGetValue("retry", out retry);
            var read = !api || WardrobeHttpPolicy.IsRead(r.Url.AbsolutePath, op, retry);
            if (r.HttpMethod != (read ? "GET" : "POST"))
            { context.Response.Headers["Allow"] = read ? "GET" : "POST"; WriteText(context, 405, "text/plain", "Reload Wardrobe; this action requires " + (read ? "GET" : "POST") + "."); return false; }
            if (!read && r.Headers["X-Wardrobe-Request"] != "1")
            { WriteText(context, 403, "text/plain", "Use the Wardrobe page to perform this action."); return false; }
            var suppliedSession = r.Headers["X-Wardrobe-Session"];
            if (!string.IsNullOrEmpty(suppliedSession) && suppliedSession != serverSession)
            {
                WriteText(context, 409, "text/plain", "The Unity session changed. Refresh the wardrobe before editing.");
                return false;
            }
            requestWritesAvatar = !read &&
                r.Url.AbsolutePath != "/api/index" && r.Url.AbsolutePath != "/api/active" && r.Url.AbsolutePath != "/api/thumb" && r.Url.AbsolutePath != "/api/name";
            int.TryParse(r.Headers["X-Wardrobe-Avatar"], out requestAvatarId);
            if (requestWritesAvatar && (suppliedSession != serverSession ||
                (requestAvatarId == 0 && r.Url.AbsolutePath != "/api/target")))
            { WriteText(context, 409, "text/plain", "Choose an avatar and refresh Wardrobe before editing."); return false; }
            return true;
        }
        private static bool IsAssetGuid(string guid)
        {
            if (guid == null || guid.Length != 32) return false;
            foreach (var c in guid)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }
    }
}
