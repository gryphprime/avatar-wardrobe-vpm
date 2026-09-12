# Atelier Unity Bridge

This package is independent of Avatar Wardrobe. The desktop bridge owns transport,
authentication and command receipts; Unity code only applies validated operations.
Supported operations are exact-target reconcile, read-only inspection, snapshot
camera views, context enumeration, and durable receipt recovery. Targets require
a saved scene GUID and a parseable Unity `GlobalObjectId`; hierarchy names are
never used as a fallback. Reconcile changes are grouped into one Unity undo group
and rollback on failure. Reconcile instantiates mutable, Atelier-owned scene
copies from immutable source prefabs identified by recipe item IDs. The runtime
marker persists both `itemId` and `assetId`, plus the original material and static
blendshape state needed to undo an appearance after a worker restart.

`GET /inspect?sceneGuid=<32-hex-guid>&objectId=<GlobalObjectId>` is read-only and
returns the exact target, content revision, actual Atelier-owned item recipe,
current supported appearance values, editable options, and warnings. Recipe
appearance entries contain only the exact control fields accepted by reconcile;
renderer names, ranges, and current option values live under
`appearanceOptions`. The same
observation is available as durable command action `inspect`; its receipt stores
`result.inspection` and is never replayed as a mutation. Appearance edits use the
following recipe fields:

```json
{
  "appearance": {
    "materials": [{"rendererId":"GlobalObjectId_V1-...","slot":0,"property":"_Color","color":[1,0,0,1]}],
    "blendshapes": [{"rendererId":"GlobalObjectId_V1-...","index":0,"value":42}]
  }
}
```

Only explicit Color shader properties `_Color` and `_BaseColor` and static
`SkinnedMeshRenderer` blendshape weights in `[-100,100]` are supported. Animated
fields are rejected when Unity can identify an Animation/Animator curve. HDR or
out-of-range source colors are reported as warnings and omitted from editable
inspection; submitting one is rejected before mutation. Appearance material
copies are created beneath
`Assets/AtelierGenerated/<target-hash>/materials/`; source `.mat` assets are
never modified. Empty appearance restores the recorded original assignments and
values, and removing an item cleans its generated outputs after the scene commit.
Renderer controls are keyed by their saved Unity `GlobalObjectId`; if relative
transform paths are ambiguous because duplicate child names or multiple
supported renderers share an object, inspection omits those controls and any
attempted edit is rejected before mutation. A computed generated-material path
must be unused unless the exact persisted appearance record already owns it, and
cleanup retains an output still referenced by any live renderer outside the
Atelier marker.
Legacy markers without `assetId` are observed with a deterministic
`legacy-<sourcePrefabGuid>` identity and a warning, then repaired by the next
reconcile.

Revisions hash saved scene bytes, dirty state, serialized supported Atelier
state, referenced material bytes, and package manifest/lock inputs. External
material changes therefore invalidate an expected revision. Unsupported or
mixed ownership is rejected explicitly; third-party prefab components remain
untouched inside each owned copy, and this bridge does not claim universal
fitting, Modular Avatar/NDMF processing, or production-avatar compatibility.

The disposable Unity 2022.3.22f1 fixture exercises add/replace/remove,
appearance color and static shape edits, source-material byte preservation,
empty-appearance undo, HDR warning/rejection, durable inspect, strict observed
recipe validation, duplicate-path control omission, stale/wrong targets,
restart/tombstone recovery, and graphics PNG output. Evidence from the latest
schema/safety run is retained at `/private/tmp/atelier-bridge-safety-check3`; the
disposable project was
`/var/folders/8x/cjljyw0d0v3g7ftrqn5v8_t80000gn/T/atelier-unity-sry7g20b`.
