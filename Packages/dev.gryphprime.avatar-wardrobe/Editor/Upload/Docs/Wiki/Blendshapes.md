# Blendshapes (per-preset overrides)

Different presets often need different body shape settings — heel offsets, hidden chest/hips, shrunk hands, etc. Each preset can store its own blendshape values for the avatar's skin mesh, applied automatically when that preset is activated/uploaded.

## Setup

1. Make sure **Avatar skin** in the top bar points to the `SkinnedMeshRenderer` that has the blendshapes (usually the body — auto-detected).
2. Expand an preset's **Blendshapes** foldout.

## Capturing values

- Set the body blendshapes the way you want for this preset (in the Inspector), then click **"Capture current skin values as overrides"** — every non-zero blendshape is saved as that preset's override.
- Or tick individual blendshapes and set their value with the slider.
- Use the **Search** box to find a blendshape; the list is scrollable so long lists don't overflow.
- **Clear all overrides** removes them for that preset.

## How it's applied

When you Select / Upload / Express / Batch an preset, its saved blendshape values are written to the skin mesh. After a batch finishes, the skin's original values are **restored**, so your working scene isn't left modified.

## Notes

- Only blendshapes you've pinned/captured are applied — others are left untouched.
- Overrides are stored per preset in EditorPrefs.
