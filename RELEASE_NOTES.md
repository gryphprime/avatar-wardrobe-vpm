Outfit and hair switching controls are now generated only by explicit menu groups.

Adding an item to a preset no longer creates an automatic Presets selector.
Existing generated preset selectors and legacy Wardrobe master switches are
removed during menu migration/synchronization. Part toggles remain available
independently, and legacy control-generation calls now generate part toggles only.

Validation: compiled with AWTest's Unity compiler and assembly references;
descriptor and browser update regression tests passed. Live scene migration
has not been exercised in this validation.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
