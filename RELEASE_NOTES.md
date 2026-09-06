Fix adding prefabs after marking them compatible.

Add to Avatar and preset assignment now accept descriptor-free prefabs regardless
of automatic asset classification. A compatible candidate is no longer rejected
with “Only outfits can be installed.” Compatibility checks remain in place.
The loaded prefab is checked for Avatar Descriptors in inactive children as well
as on its root before installation.

Validation: C# compilation using AWTest's Unity compiler and assembly references;
descriptor regression tests; browser update tests; release archive checks.
Interactive installation was not exercised in this validation.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
