Mark OutfitToggleGeneratedMenu as a VRChat editor-only component.

The marker stores Avatar Wardrobe's generated-menu bookkeeping in Unity. It now
implements VRC.SDKBase.IEditorOnly so the SDK recognizes it as editor tooling,
rather than reporting it as an unsupported runtime avatar script. Existing marker
components gain this behavior automatically after recompilation.

Validation: runtime assembly compiled against AWTest's Unity/VRChat references.
SDK UI validation after reload has not been exercised.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
