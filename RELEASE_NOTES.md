Clean up stale Avatar Wardrobe objects using AW markers only.

Generated-host lookup no longer adopts objects solely by name. Menu migration
now detects duplicate marked owners, obsolete layouts, and marked containers
whose preset or menu groups no longer exist. Stale marked preset selectors and
empty marked legacy menus are cleaned up with Undo support.

Unmarked objects are not adopted or deleted. If a marked container has unmarked
children, cleanup leaves it untouched and logs a warning. Menu-group replacement
stops before deletion/creation when such a mixed tree exists, avoiding duplicates.
Part toggles remain independent of outfit/hair switching groups.

Validation: compiled with AWTest's Unity compiler and references. Live scene
migration has not been exercised; cleanup runs on the next menu migration/sync.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
