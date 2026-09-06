# VRC Preset Batch Uploader

**v3.3** · Unity 2022.x · VRChat Avatars SDK · MIT

A Unity Editor tool for VRChat avatar creators who keep many presets under a single avatar and upload each one as its own avatar. It handles the whole multi-preset workflow: setting up brand-new presets, per-preset accessories and face expressions, performance budgets, and uploading a mix of new and existing presets in one guided pass.

Use it from the browser Upload page, or open it via **Tools → Shiro → Outfit Batch Uploader**.
It now ships inside Avatar Wardrobe, so the same UI also lives in the
Wardrobe window under **Tools → Avatar Wardrobe → Upload**.

## Start here

- **[[Installation]]** — drop it in, what's required, optional integrations
- **[[Getting Started|Getting-Started]]** — the core concept and your first upload
- **[[Uploading]]** — Select / Upload / Upload All, batching, cross-platform

## Features

- **[[Interface|User-Interface]]** — focused tabs, compact preset cards, expandable details, fixed batch footer
- **[[New Preset Setup|New-Preset-Setup]]** — one-click Express / Advanced creation of new presets
- **[[Items (accessories)|Items]]** — per-preset accessory selection
- **[[FaceEmo (per preset)|FaceEmo]]** — per-preset face-expression menus
- **[[Texture / VRAM optimization|VRAM-Optimization]]** — compress + cap preset and optionally selected-item textures
- **[[Budget counters|Budget-Counters]]** — Contacts, Lights, Parameters per preset
- **[[Blendshapes]]** — per-preset blendshape overrides

## What changed in v3.3

v3.3 separates the window into **Presets**, **New Preset**, and **Defaults**, making large avatar projects much easier to scan. It also adds a broad backend reliability pass: deterministic blendshape switching, safer avatar changes, more accurate outfit/item budgets, atomic settings writes with backup recovery, stronger batch/domain-reload cleanup, and safer Express thumbnail/resume handling.

## Help

- **[[Troubleshooting]]** — common issues and fixes

## How it works (in one paragraph)

Your presets live as GameObjects under an **Outfits** parent. Activating a preset sets it to `Untagged` (uploaded) and every other preset to `EditorOnly` (stripped at build). The same tag trick drives per-preset **Items** and **FaceEmo**. Each preset stores its own VRChat **Blueprint ID** (`avtr_...`), so one Unity project uploads many separate avatars.

> The tool references no third-party types directly — it compiles and runs on its own, and integrations (VRCFury, FaceEmo, Modular Avatar) light up only when those packages are installed. See [[Installation]].
