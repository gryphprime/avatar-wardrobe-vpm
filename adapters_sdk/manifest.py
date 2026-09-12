import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping

PROTOCOL = 1
_CLASSES = {"read", "preview", "mutation"}
_ALLOWED_TOP = {"protocol", "id", "name", "version", "origin", "sdk", "targets",
                "resources", "actions", "dependencies", "ui", "recovery"}
_FORBIDDEN = {"script", "code", "frontend", "javascript", "command", "exec", "executable"}


class ManifestError(ValueError):
    pass


def _text(value, field, max_len=256):
    if not isinstance(value, str) or not value.strip() or len(value) > max_len or any(ord(c) < 32 for c in value):
        raise ManifestError(f"{field} must be bounded text")
    return value.strip()


def _walk_forbidden(value, path="manifest"):
    if isinstance(value, Mapping):
        for key, item in value.items():
            if str(key).lower() in _FORBIDDEN:
                raise ManifestError(f"{path}.{key} is executable or unsupported")
            _walk_forbidden(item, f"{path}.{key}")
    elif isinstance(value, list):
        for index, item in enumerate(value):
            _walk_forbidden(item, f"{path}[{index}]")


def validate_manifest(manifest: Mapping[str, Any]) -> dict:
    if not isinstance(manifest, Mapping):
        raise ManifestError("adapter manifest must be an object")
    _walk_forbidden(manifest)
    unknown = set(manifest) - _ALLOWED_TOP
    if unknown:
        raise ManifestError("unsupported manifest fields: " + ", ".join(sorted(unknown)))
    if manifest.get("protocol") != PROTOCOL:
        raise ManifestError("protocol must be 1")
    for field in ("id", "name", "version", "origin"):
        _text(manifest.get(field), field)
    if manifest["origin"] not in {"official", "community"}:
        raise ManifestError("origin must be official or community")
    sdk = manifest.get("sdk", {})
    if not isinstance(sdk, Mapping) or sdk.get("protocol") != PROTOCOL:
        raise ManifestError("sdk.protocol must be 1")
    targets = manifest.get("targets", [])
    if not isinstance(targets, list) or any(not isinstance(x, str) for x in targets) or len(targets) > 64:
        raise ManifestError("targets must be a bounded list of names")
    resources = manifest.get("resources", [])
    if not isinstance(resources, list) or len(resources) > 256:
        raise ManifestError("resources must be a bounded list")
    for resource in resources:
        if not isinstance(resource, Mapping):
            raise ManifestError("resource must be an object")
        _text(resource.get("id"), "resource.id")
        if resource.get("type") is not None:
            _text(resource["type"], "resource.type")
    actions = manifest.get("actions", [])
    if not isinstance(actions, list) or len(actions) > 256:
        raise ManifestError("actions must be a bounded list")
    action_ids = set()
    for action in actions:
        if not isinstance(action, Mapping):
            raise ManifestError("action must be an object")
        _text(action.get("id"), "action.id")
        if action["id"] in action_ids:
            raise ManifestError("duplicate action id: " + action["id"])
        action_ids.add(action["id"])
        _text(action.get("name", action.get("id")), "action.name")
        classification = action.get("class", action.get("classification"))
        if classification not in _CLASSES:
            raise ManifestError("action.class must be read, preview, or mutation")
        for field in ("inputs", "outputs", "changes"):
            if field in action and not isinstance(action[field], (dict, list)):
                raise ManifestError(f"action.{field} must be declarative JSON")
        recovery = action.get("recovery", manifest.get("recovery", {}))
        if recovery is not None and not isinstance(recovery, Mapping):
            raise ManifestError("recovery must be an object")
    dependencies = manifest.get("dependencies", [])
    if not isinstance(dependencies, list) or len(dependencies) > 128:
        raise ManifestError("dependencies must be a bounded list")
    for dependency in dependencies:
        if not isinstance(dependency, Mapping):
            raise ManifestError("dependency must be an object")
        _text(dependency.get("id"), "dependency.id")
        _text(dependency.get("version", "*"), "dependency.version")
    if "ui" in manifest and not isinstance(manifest["ui"], Mapping):
        raise ManifestError("ui must contain simple metadata only")
    cloned = json.loads(json.dumps(manifest))
    if len(json.dumps(cloned, ensure_ascii=False).encode("utf-8")) > 1024 * 1024:
        raise ManifestError("adapter manifest exceeds 1 MiB")
    return cloned


@dataclass(frozen=True)
class AdapterManifest:
    data: dict

    def __post_init__(self):
        object.__setattr__(self, "data", validate_manifest(self.data))

    @classmethod
    def from_json(cls, value):
        if isinstance(value, (str, bytes)):
            value = json.loads(value)
        return cls(value)

    @classmethod
    def from_file(cls, path):
        return cls.from_json(Path(path).read_text(encoding="utf-8"))

    def to_json(self):
        return json.dumps(self.data, sort_keys=True, separators=(",", ":"))

    def action(self, action_id):
        return next((a for a in self.data["actions"] if a["id"] == action_id), None)


def load_manifest(path):
    return AdapterManifest.from_file(path)
