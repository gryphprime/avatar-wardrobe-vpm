# Focused appearance editing

Appearance reads the pinned scene avatar's exact mesh renderers, material slots, and static blendshape weights. Renderer IDs distinguish duplicate names. A material command also includes the slot number and current material ID; a shape command includes both the mesh's shape index and name. The conservative review revision includes scene state, material serialization, and asset dependency hashes.

Each change is a draft until **Review change**, then **Apply reviewed change**. Review creates no material assets and does not mutate the avatar. Its one-use token expires after two minutes and fails if the avatar, source material, selected texture, or reviewed state changes. The color swatch/value comparison is not a rendered avatar preview. Static shape weights may be overwritten by expression animations.

Supported material adapters cover Unity Standard and Standard Specular, plus the installed lilToon 2.3.4 main shader and `Hidden/lilToon*` variants. They expose only main tint, main Texture2D, and existing bounded metallic, smoothness, alpha-cutoff, and shadow-strength properties. Shader keywords, rendering modes, layer composition, UV matching, unsupported shader properties, and HDR tints remain native-Inspector work. Texture selection is explicit and does not infer compatibility from a region or filename.

Apply creates a fresh material under `Assets/AvatarWardrobeGenerated/Appearance/`, changes that copy, and assigns it only to the reviewed renderer slot. It never edits a shared source material or a texture importer. Unity Undo restores the previous assignment; the generated asset remains available for redo. The generated file is deliberately retained rather than deleted during Undo. Scene saving remains explicit. Preset and upload configuration stores are preserved; the existing preset engine remains the owner of per-preset recipes and shape overrides.

The view uses the Advanced Scene layout tokens with isolated `appearance-editor.css`. Renderer selection, material slots, static shape search, review/cancel/apply controls, and optional tools remain usable without pointer gestures. Unsupported capabilities provide an explanation and a native or official-guide path.

## Optional tools

- **Avatar Optimizer 1.9.18:** the adapter uses the public `TraceAndOptimize` component type with its own defaults. It never configures internal fields. Preview runs the existing isolated-copy NDMF processor twice and adds AAO only to the candidate copy, producing matching before/after photographs and measured resource counts. A short-lived review token gates adding the same default component to the live avatar with Undo. An existing optimizer configuration is preserved and opened in its native Inspector. Texture allocation is an editor estimate, not exact platform VRAM; no FPS or rank improvement is promised.
- **Gesture Manager 3.9.9:** the native `Tools/Gesture Manager Emulator` entry creates an emulator in the pinned scene if needed. The adapter also checks that the tool was compiled with its VRChat SDK descriptor contract; an installation compiled without the SDK symbol stays unavailable. The public serialized `settings.favourite` descriptor field pins its target, matching the tool's own Favourite Avatar UI. This is recorded with Undo. `SetModule` is deliberately not called because it initializes animator state. Play Mode and actual emulator testing remain explicit user actions. An emulator targeting another avatar is preserved.
- **MochiFitter:** the installed binary at `Assets/OutfitRetargetingSystem/Editor/OutfitRetargetingSystem.dll` declares public `OutfitRetargetingSystem : EditorWindow` and public static `ShowWindow()` with `MenuItem("Tools/MochiFitter")`. The adapter verifies that public contract before opening the exact native menu. Its assembly version is `0.0.0.0`, which does not identify a release, so the UI reports the release version as unavailable. Source outfit, target avatar, conversion profiles, licensing, optional dependencies and conversion remain in the native workflow. The adapter does not call conversion or download methods, change internal settings, or distribute plugin files.
- **TexTransTool and VRCQuestTools:** no validated installed adapters were found during this implementation. The view provides official guidance and prerequisites without automatic downloads.

## Bridge contract

All endpoints retain the host's pinned project/session/avatar request guards and execute Unity code on its main thread.

- GET `/api/appearance_snapshot` returns `{ok,avatarId,revision,renderers}` with renderer identity, material slots/properties, and shape controls.
- GET `/api/appearance_textures?search=...` searches at most 50 Texture2D assets in the project's Assets tree. Queries require 2–80 characters.
- POST `/api/appearance_review?command=<JSON>` accepts `{action,avatarId,revision,rendererId,slot,materialId,property,kind,color,value,textureGuid,shapeIndex,shapeName}`. Actions are `material` and `blendshape`. The response contains `{ok,token,target,message,before,after,beforeColor,afterColor}`.
- POST `/api/appearance_apply?review=<JSON>` accepts `{avatarId,token}`.
- GET `/api/appearance_tools` reports version-gated optional tools. POST `/api/appearance_tool?command=<JSON>` opens a supported native tool using `{avatarId,revision,id}`.
- POST `/api/appearance_optimizer_review?command=<JSON>` produces `{ok,token,message,beforeImage,afterImage,preview}`. Images are bounded PNG base64 and `preview` includes the existing worker's metrics and limitations. POST `/api/appearance_optimizer_apply?review=<JSON>` consumes `{avatarId,token}`.

Browser export: `WardrobeAppearanceEditor.configure({api,T,root}).show()`. `api` is the existing pinned-session adapter. Localization uses `appearance.*` keys with English fallbacks. Refresh the module whenever the pinned avatar changes.

Tests use a marked disposable Unity fixture. Coverage includes read-only review, exact-slot cloning and source preservation, Undo/redo, changed-source rejection, one-use tokens, invalid properties/identities, explicit texture replacement, exact static-shape edits, shader fallback, optional-tool version gates, AAO copied-preview/reviewed-apply, and Gesture Manager's public target setting.

## Saved appearance on an existing preset

**Save current appearance** adds a versioned `appearance` field to the existing wardrobe preset record, including Common. It preserves the preset's upload and engine settings. The saved recipe covers Common/body plus the selected preset, excluding copies assigned to other presets. It records exact saved scene object identities, garment source GUID/local-file ID/dependency version, material slot references and versions, mesh and named blendshape layouts and values, garment placement, object visibility, and generated menu default values. Source, body-fit and default-state revision hashes remain available with the metadata.

**Review restore** is read-only. It validates every reference and shows the proposed changes before creating a one-use review token valid for five minutes. **Apply reviewed restore** requires the same pinned avatar, scene, recipe, asset versions and project settings as the review. It restores the reviewed values as one native Unity Undo operation and leaves the scene unsaved. Changing an upload record after review invalidates the review and preserves the newer record.

Restore requires the existing saved garment copies in their original scene avatar. It does not reinstall missing copies or guess replacements. Missing or moved copies, changed prefab/material/mesh versions, changed material-slot or blendshape layouts, and incompatible generated controls stop the entire restore before edits. Review the changed content and save a new recipe when intentional changes replace the saved references. Scene-only materials or meshes must be saved as project assets before they can be referenced by a recipe.

**Export recipe references** downloads JSON metadata only: relative object names and identities, asset GUID/local-file IDs and hashes, plus appearance values. It contains no models, textures, material files, absolute project paths or purchased asset contents. This export is a reference record; it is not an asset package or a portable cross-avatar installer.

The saved-appearance routes are GET `/api/preset_appearance` and `/api/preset_appearance_export`, and POST `/api/preset_appearance_save`, `/api/preset_appearance_review` and `/api/preset_appearance_apply`. All accept `presetId`. Save also requires `revision`; Apply requires the returned review `token`. Existing project/session/avatar guards apply. Recipes use schema version 1 and remain additive to the existing preset store.

Async wear, replace and remove receipts separately offer a session-only scoped Undo token. That command verifies the exact post-operation scene/configuration revision and the captured native Undo group, including later same-group edits, before reverting the owned operation. A newer native edit, changed settings, a consumed token, or a Unity restart makes that token unavailable; it never falls back to global Undo.
