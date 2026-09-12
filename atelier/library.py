"""Standalone Atelier content-addressed library facade.

The Unity parser is deliberately isolated in :mod:`atelier.sources.unity_library`;
callers of this module do not need Avatar Wardrobe (or Unity) installed.
"""
from pathlib import Path

from .sources.unity_library import (
    Library as _UnityLibrary,
    MAX_BYTES,
    MAX_FILES,
    MAX_DEPTH,
)

__all__ = ["Library", "MAX_BYTES", "MAX_FILES", "MAX_DEPTH"]


class Library:
    """Persistent immutable archive library.

    ``root`` is a directory owned by Atelier.  ``add`` copies and validates the
    input archive, while import operations only copy staged files into a project.
    """

    def __init__(self, root):
        self._backend = _UnityLibrary(root)
        self.root = self._backend.root

    @staticmethod
    def _public(record):
        files = record.get("files", [])
        prefabs = [
            {"guid": item["guid"], "path": item["path"]}
            for item in files
            if item.get("guid") and item.get("path", "").lower().endswith(".prefab")
        ]
        result = dict(record)
        result["id"] = record.get("hash")
        result["sha256"] = record.get("hash")
        result["name"] = record.get("filename") or record.get("product")
        result["prefabs"] = prefabs
        return result

    def list(self):
        rows = self._backend.list()
        # Keep the public index useful even if a compact backend implementation
        # omits its potentially large manifest column.
        enriched = []
        for row in rows:
            if not isinstance(row.get("files"), list) and row.get("hash"):
                row = self._backend.record(row["hash"])
            enriched.append(self._public(row))
        return enriched

    def record(self, identifier):
        return self._public(self._backend.record(identifier))

    def add(self, path, creator="", product="", source_url=""):
        added = self._backend.add(Path(path), creator, product, source_url)
        # ``add`` returns a compact acknowledgement; expose the same rich record
        # shape as ``list`` for both fresh and idempotent additions.
        if "hash" in added:
            return self._public(self._backend.record(added["hash"])) | {
                "duplicate": bool(added.get("duplicate", False)),
                **({"filesAdded": added["files"]} if "files" in added else {}),
            }
        return added

    def update_metadata(self, identifier, creator="", product="", source_url=""):
        return self._backend.update_metadata(identifier, creator, product, source_url)

    def import_plan(self, identifier, project, cancel_check=None):
        return self._backend.import_plan(identifier, Path(project), cancel_check)

    def apply_import(self, identifier, project, expected_plan, cancel_check=None):
        return self._backend.apply_import(identifier, Path(project), expected_plan, cancel_check)

    def reconcile_usage(self, project, snapshot):
        return self._backend.reconcile_usage(Path(project), snapshot)

    def update_impact(self, old_identifier, new_identifier):
        return self._backend.update_impact(old_identifier, new_identifier)
