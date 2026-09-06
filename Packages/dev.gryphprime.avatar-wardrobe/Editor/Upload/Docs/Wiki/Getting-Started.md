# Getting Started

## The core concept

You have **one avatar** with **many presets**, and you want each preset to be its own uploaded VRChat avatar.

Put your presets as GameObjects under a parent named **`Outfits`** (configurable). The tool, for any given preset, sets that preset to the `Untagged` tag (so it's included in the build) and every *other* preset to `EditorOnly` (so VRChat strips it at build). Each preset remembers its own **Blueprint ID** (`avtr_...`), which is what makes it a separate avatar on VRChat.

```
Avatar (VRCAvatarDescriptor + PipelineManager)
├── Body (SkinnedMeshRenderer with blendshapes)
├── Presets
│   ├── Outfit_A      ← Untagged when active  → uploaded
│   ├── Outfit_B      ← EditorOnly            → stripped
│   └── Outfit_C      ← EditorOnly            → stripped
└── Items             ← optional accessories (see [[Items]])
```

## The top bar

1. **Avatar root** — drag your avatar's root GameObject here (auto-detected if there's only one avatar in the scene).
2. **Avatar skin** — the `SkinnedMeshRenderer` that has the blendshapes (auto-detected). Used for [[Blendshapes]] overrides.
3. **Outfits parent** — the name of the GameObject holding your presets as direct children (default `Outfits`).
4. **Base Version** — *(optional)* a version string (e.g. `v1.2`) that gets stamped into the VRChat description of every uploaded preset.

## The three workspaces

- **Presets** contains every detected preset in a compact list. Expand a card to edit its Blueprint ID, platforms, blendshapes, items, FaceEmo, thumbnail, or VRAM settings.
- **New Preset** filters the list to presets that still need their first Blueprint ID.
- **Defaults** contains the shared Express Setup, thumbnail, optimization, item, and backup settings.

The batch upload controls stay below the scrolling preset list so they remain accessible even on avatars with many presets. See [[Interface|User-Interface]].

## Your first upload

**Already-uploaded outfit (you have its `avtr_...` ID):**
1. Paste the Blueprint ID into the preset's field.
2. Press **Upload** on that row (or tick **Include in batch** and use **Upload All**).

**Brand-new preset (no ID yet):**
1. Just press **⚡ Express setup** on the preset row — the tool creates a new avatar, uploads it, and fills in the new ID for you. See [[New Preset Setup|New-Preset-Setup]].

That's it. From there, explore per-preset [[Items]], [[FaceEmo]], [[Budget counters|Budget-Counters]], and [[Texture / VRAM optimization|VRAM-Optimization]].

## Where settings live

- **Blueprint IDs & per-outfit selections** → `ProjectSettings/ShiroOutfit_data.json`, scoped by avatar + outfit name.
- **Defaults** (templates, tags, thumbnail, optimization) → EditorPrefs.
- **Base Version** → `ProjectSettings/ShiroOutfit_versions.json`.
- **Tags / active state** → your scene.
