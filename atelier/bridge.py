"""Authenticated, bounded client for the local Atelier Unity bridge."""
from __future__ import annotations

import json
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass
from typing import Optional


class BridgeError(RuntimeError):
    pass


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


@dataclass
class Receipt:
    id: str
    state: str
    revision: Optional[str] = None
    result: Optional[dict] = None
    error: Optional[str] = None

    @classmethod
    def from_json(cls, value):
        return cls(value.get("id", ""), value.get("state", "failed"), value.get("revision"), value.get("result"), value.get("error"))


class BridgeClient:
    max_payload_bytes = 256 * 1024

    def __init__(self, endpoint, token, protocol_version="1", timeout=15):
        parsed = urllib.parse.urlparse(endpoint)
        if parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost", "::1") or not parsed.port:
            raise ValueError("Bridge endpoint must be an explicit loopback HTTP endpoint")
        if not token:
            raise ValueError("Bridge token is required")
        self.endpoint, self.token = endpoint.rstrip("/"), token
        self.protocol_version = str(protocol_version)
        self.timeout = min(max(float(timeout), 0.01), 30)
        self._opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect())

    def _request(self, method, path, body=None):
        payload = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
        if payload is not None and len(payload) > self.max_payload_bytes:
            raise BridgeError("Bridge request exceeds 256 KiB")
        request = urllib.request.Request(self.endpoint + path, payload, method=method, headers={"Authorization": "Bearer " + self.token, "X-Atelier-Protocol": self.protocol_version, "Content-Type": "application/json"})
        try:
            with self._opener.open(request, timeout=self.timeout) as response:
                raw = response.read(self.max_payload_bytes + 1)
        except (urllib.error.URLError, urllib.error.HTTPError) as error:
            raise BridgeError(str(error)) from error
        if len(raw) > self.max_payload_bytes:
            raise BridgeError("Bridge response exceeds 256 KiB")
        try:
            return json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise BridgeError("Bridge returned invalid JSON") from error

    def context(self):
        return self._request("GET", "/context")

    def shutdown(self):
        """Ask the authenticated private Unity worker to exit cleanly."""
        return self._request("POST", "/shutdown", {})

    def submit(self, command: dict):
        command = dict(command)
        command.setdefault("id", str(uuid.uuid4()))
        command.setdefault("expectedRevision", "")
        if not isinstance(command.get("target"), dict):
            raise ValueError("command.target must contain sceneGuid and objectId")
        return self._request("POST", "/commands", command)

    def poll(self, operation_id):
        return self._request("GET", "/commands/" + urllib.parse.quote(str(operation_id), safe=""))

    def reconcile(self, workspace_id, scene_guid, object_id, recipe, expected_revision="", desired_revision=0):
        return self.submit({"workspaceId": workspace_id, "target": {"sceneGuid": scene_guid, "objectId": object_id}, "expectedRevision": str(expected_revision), "desiredRevision": int(desired_revision), "action": "reconcile", "payload": {"recipe": recipe}})

    def snapshot(self, workspace_id, scene_guid, object_id, view="front", expected_revision="", desired_revision=0):
        return self.submit({"workspaceId": workspace_id, "target": {"sceneGuid": scene_guid, "objectId": object_id}, "expectedRevision": str(expected_revision), "desiredRevision": int(desired_revision), "action": "snapshot", "payload": {"view": view}})

    def context_candidates(self):
        return self.context().get("targets", [])
