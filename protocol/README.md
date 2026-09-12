# Atelier local protocol, version 1

Atelier uses two loopback-only boundaries:

1. Browser → desktop: per-launch `X-Atelier-Token`, same-origin requests, 128 KiB
   JSON body limit. The UI stores the fragment token in session storage.
2. Desktop → Unity: `Authorization: Bearer <worker-token>` and
   `X-Atelier-Protocol: 1`, with 256 KiB request/response bounds. Redirects and
   environment HTTP proxies are disabled for the bridge client.

`GET /context` identifies the actual project root, its observed revision and
saved-scene candidates. A target has `sceneGuid` and `objectId` (GlobalObjectId).
Names are labels only.

`POST /commands` accepts this command envelope:

```json
{
  "id": "a-stable-UUID",
  "workspaceId": "a-workspace-UUID",
  "target": {"sceneGuid": "32-hex-digits", "objectId": "GlobalObjectId_V1-..."},
  "expectedRevision": "observed-revision",
  "desiredRevision": 1,
  "action": "reconcile",
  "payload": {
    "recipe": {
      "items": [{"id": "exact-copy-id", "assetId": "source-hash", "name": "Layer", "prefabGuid": "32-hex-digits"}],
      "appearance": {}
    }
  }
}
```

`snapshot` uses the same target/precondition envelope and `payload.view` of
`front`, `three-quarter`, or `back`. Reconcile supports its owned prefab
instances and declared appearance controls on their renderers. Material entries
are `{rendererId, slot, property, color:[r,g,b,a]}` for `_Color`/`_BaseColor`;
blendshape entries are `{rendererId, index, value}`. Colors are finite 0–1 channels,
weights are finite -100–100. Exact renderer IDs and static supported controls are
required. Source materials stay immutable; generated assignments and original
values are recorded for whole-recipe undo. Empty appearance resets the overrides.

`GET /inspect?sceneGuid=...&objectId=...` returns the exact `projectPath`, `target`,
`revision`, actual `recipe`, `appearanceOptions`, and `warnings`. The durable
`inspect` command returns the same report at `result.inspection`; registered
adapter read actions use this queue. Unsupported controls are reported explicitly.

`GET /commands/<id>` recovers the original durable receipt. The desktop never
submits the command a second time after it crosses the dispatch boundary. The
Unity receipt contains `id`, `state`, `revision`, `result` and `error`; the
Unity journal also retains its request fingerprint. Reusing an ID with different
content is rejected. Missing, unreadable or interrupted receipts require review.

A successful snapshot result includes `artifact: {path, view}`. The desktop
checks that the PNG is inside the workspace output directory, copies it into its
immutable artifact store and binds it to the exact target/state/view. A late
artifact can enter history without replacing the current photograph.

## Desktop recovery and integrations

`POST /api/workspaces/<id>/recover-review` reads Unity in a background job and
persists a review with actual recipe, desired recipe/revision, exact target,
Unity revision and pending-operation identities. `POST .../recover` accepts its
`reviewId` plus `keep-draft` or `use-unity`. A fresh inspection must still match
the entire review. Resolution retires the ambiguous operation without replaying
it, establishes a newly numbered confirmed baseline, and preserves a distinct
desired revision when the draft differs. Running original receipts must first
be recovered; they cannot be abandoned by this flow.

Integration routes are `integration-plan`, `integration-apply`, and
`adapter-action`, using `integrationId`, a reviewed `planId`, or a declared
`actionId` respectively. Job responses are obtained through `/api/jobs/<id>`.
Package/import mutations additionally have durable `projectOperations` in state.
Interrupted jobs become `needs-review` on restart and block new work. The
`project-review` / `project-acknowledge` routes fingerprint current manifests and,
for imports, actual staged-file contents. Acknowledgement never replays the job.

These APIs are an alpha contract with a small host-selected adapter registry.
They do not imply universal Unity/VRChat plugin compatibility or processed NDMF
rendering. Package dependency resolution still occurs inside vrc-get at apply.
