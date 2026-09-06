# Items (accessories)

Keep accessory objects (props, weapons, jewelry, …) under a second configurable parent (default **Items**) and choose, **per preset**, which of them upload with that preset.

```
Avatar
├── Presets
│   ├── Outfit_A
│   └── Outfit_B
└── Items
    ├── Sword
    ├── Glasses
    └── Tail
```

## How it works

Every preset row has an **Items** foldout listing each child of the Items parent with an "include with this preset" checkbox (saved per preset). On activation (Select / Upload / Express / Batch) the active preset's selection is applied:

- **included** items → tag `Untagged` (uploaded with this outfit)
- **excluded** items → tag `EditorOnly` (stripped at build)

So Preset A can ship the Sword + Tail while Preset B ships only the Tail.

## Per-preset controls

- **Search** box to filter long item lists.
- **All / None** apply to the *currently filtered* items.
- **Ping** selects the item in the hierarchy.
- The list is scrollable (height-capped) so it never overflows the window.

## Defaults ("included on every preset")

In **Defaults → Items (accessories)** you set:

- the **Items parent** name, and
- a per-item **"included on every preset by default"** toggle.

Presets you haven't set per-item yet inherit these defaults; toggling an item on an preset overrides the default for that preset.

## Notes

- Item inclusion is stored per avatar **and** per outfit in `ProjectSettings/ShiroOutfit_data.json`; legacy EditorPrefs values are migrated automatically when first read.
- Items count toward an preset's [[Budget counters|Budget-Counters]] (contacts/lights) only when included for that preset.
- The optional [[Texture / VRAM optimizer|VRAM-Optimization]] can include the textures of the items selected for the current preset. This setting is disabled by default because texture-import changes affect the shared texture asset globally.
