"""Small declarative Atelier adapter SDK (protocol 1).

Adapters describe capabilities as JSON data.  They do not supply frontend code or
execute arbitrary manifest values; a host decides how to run each declared action.
"""
from .manifest import AdapterManifest, ManifestError, load_manifest, validate_manifest
from adapters_sdk import AdapterRegistry

__all__ = ["AdapterManifest", "ManifestError", "load_manifest", "validate_manifest", "AdapterRegistry"]
