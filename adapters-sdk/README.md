# Atelier adapter SDK (protocol 1)

Adapters are bounded JSON declarations. They describe resources, actions,
dependencies, recovery support, and simple UI metadata; the host owns execution,
queueing, target resolution, and rendering. Manifests never carry frontend code or
commands. Use `adapters_sdk` as the Python import package.

`AdapterRegistry.register(manifest, handlers, trusted_origin=...)` connects each
declared action to an explicit callable selected by the host. Every declared
action needs a handler, unknown actions/inputs are rejected, and an adapter
cannot grant itself an official badge. Merely copying a manifest into a directory
cannot install code or register an action. The initial host registers its bridge
and Modular Avatar manifests by name.

The built-in `inspect` and `preview` handlers submit durable `inspect` and
`snapshot` operations through Atelier's existing scheduler. They share exact
targets, revisions, operation receipts, recovery and artifact caching. A
successful inspect receipt carries `result.inspection`. The current SDK supports
workspace-derived inputs only; custom forms, external plugins and a marketplace
are outside this alpha. The package declares protocol range `>=1 <2`.

This is a public source-level API, not a new open-source license grant. Product,
SDK and adapter redistribution licensing remains a release decision; the
repository's license applies unless a file has a separate license.

The Unity archive parser in `atelier/sources/unity_library.py` is a vendored
extraction of Avatar Wardrobe code. It remains subject to the repository's existing
Avatar Wardrobe View-Only License; this SDK makes no new licensing or redistribution
claim about that source.
