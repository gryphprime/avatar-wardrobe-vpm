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
`front`, `three-quarter`, or `back`. Appearance values are rejected by the
initial bridge. Reconcile supports only its owned prefab instances.

`GET /commands/<id>` recovers the original durable receipt. The desktop never
submits the command a second time after it crosses the dispatch boundary. The
Unity receipt contains `id`, `state`, `revision`, `result` and `error`; the
Unity journal also retains its request fingerprint. Reusing an ID with different
content is rejected. Missing, unreadable or interrupted receipts require review.

A successful snapshot result includes `artifact: {path, view}`. The desktop
checks that the PNG is inside the workspace output directory, copies it into its
immutable artifact store and binds it to the exact target/state/view. A late
artifact can enter history without replacing the current photograph.

These APIs are an alpha contract. They do not claim a general third-party action
execution surface or universal Unity/VRChat plugin compatibility.
