# Atelier Unity Bridge

This package is independent of Avatar Wardrobe. The desktop bridge owns transport,
authentication and command receipts; Unity code only applies validated operations.
Supported operations are exact-target reconcile, snapshot camera views and context
enumeration. Targets require a saved scene GUID and a parseable Unity
`GlobalObjectId`; hierarchy names are never used as a fallback. Reconcile changes
are grouped into one Unity undo group and rollback on failure. Material appearance
payloads are explicitly rejected in this version. Reconcile instantiates mutable,
Atelier-owned scene copies from immutable source prefabs identified by recipe item IDs.
