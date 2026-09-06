Fix missing prefabs in Avatar Wardrobe.

- All includes descriptor-free prefabs regardless of their asset classification,
  including records previously marked as avatars or ignored.
- Missing thumbnails no longer hide cards in All.
- Correct an SDK Constraints DLL mapping that falsely identified outfits such as
  KE_Milltina as avatars. DLL descriptor detection now requires the exact GUID
  and fileID pair; source scripts retain their MonoScript fileID check.
- Preserve descriptor presence on preview-named prefabs so actual descriptor
  prefabs remain excluded. The next index refresh invalidates old analysis.

Validation: Unity compilation in AWTest, 53 indexer tests, descriptor regression
checks, JavaScript tests, and local release archive checks. All five KE_Milltina
prefabs were verified as outfits after rebuilding AWTest's catalog.

After updating, use Index changes to refresh an existing catalog, then select All.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
