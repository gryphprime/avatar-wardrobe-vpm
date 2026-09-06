Preserve menu groups when an avatar's saved-scene identity changes.

Remember the previous owner scope across Editor script reloads. Recover orphaned
Common records from an old session only when exactly one candidate matches the
marked scene group IDs and all member paths. Preserve the original recovery data.

Menu rebuilding now stops before deleting generated hosts when their saved owner
cannot be resolved. Missing ownership is no longer treated as proof of stale data.
Unmarked scene objects remain untouched.

Validation: compiled using AWTest's Unity compiler and references. AWTest's saved
Outfits group association was recovered against its scene group ID and references,
with the original JSON backed up. Interactive reload/migration remains unverified.

View-only license; see LICENSE and THIRD_PARTY_NOTICES.md.
