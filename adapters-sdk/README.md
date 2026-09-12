# Atelier adapter SDK (protocol 1)

Adapters are bounded JSON declarations. They describe resources, actions,
dependencies, recovery support, and simple UI metadata; the host owns execution,
queueing, target resolution, and rendering. Manifests never carry frontend code or
commands. Use `adapters_sdk` as the Python import package.

The Unity archive parser in `atelier/sources/unity_library.py` is a vendored
extraction of Avatar Wardrobe code. It remains subject to the repository's existing
Avatar Wardrobe View-Only License; this SDK makes no new licensing or redistribution
claim about that source.
