# Installation

## Requirements

- **Unity 2022.x** (tested on 2022.3.x)
- **VRChat Avatar SDK** (`com.vrchat.avatars`), installed via the VRChat Creator Companion

## Install

1. Install Avatar Wardrobe (see its README) — the uploader ships inside it at `Assets/OutfitToggleGenerator/Editor/Upload/`.
2. Unity compiles the scripts automatically.
3. Open the browser Upload page (or the standalone window via **Tools → Shiro → Outfit Batch Uploader**).

The folder name doesn't matter — the tool finds its own assets (e.g. the completion sound) by name, so you can rename or move the install folder freely.

## Optional integrations (none are required)

The tool references no third-party types directly, so it compiles and runs without any of these. The related features simply activate when the package is present:

| Package | Enables |
|---|---|
| **VRCFury** | SPS/DPS auto-tag detection; VRCFury-aware Parameters counter |
| **FaceEmo** | The per-preset FaceEmo capture workflow |
| **Modular Avatar** | Required by FaceEmo to merge captured face menus at build |
| **Thry's Avatar Performance Tools** | Not needed — the VRAM optimizer reimplements its recommendations |

## Updating

Replace the old `Upload` folder with the new one. Settings (Blueprint IDs, defaults, per-preset selections) are stored in Unity **EditorPrefs** / **SessionState** and your scene, not in the scripts, so they survive updates.

## License

MIT.
