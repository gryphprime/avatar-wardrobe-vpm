"""Import-friendly alias for the source-tree ``adapters-sdk`` SDK package."""
from .manifest import AdapterManifest, ManifestError, load_manifest, validate_manifest

__all__ = ["AdapterManifest", "ManifestError", "load_manifest", "validate_manifest"]
