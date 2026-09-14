"""Dynamic discovery and transport for the disposable Unity Wardrobe bridge.

The desktop host is intentionally longer lived than Unity's editor domain.  A
small, project-local bridge record lets it find the current Unity listener
after a reload while the HTTP identity check prevents a reused localhost port
from ever being treated as the wrong project.
"""
import json
from pathlib import Path
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


UNITY_PORT_MIN = 8909
UNITY_PORT_MAX = 8929
BRIDGE_PROTOCOL = 4


class BridgeUnavailable(OSError):
    """Raised when Unity's bridge record or its live identity is unavailable."""


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise urllib.error.HTTPError(req.full_url, code, "Bridge redirects are not allowed.", headers, fp)


def canonical_project(project):
    return str(Path(project).resolve()) if project else ""


def bridge_path(project):
    return Path(canonical_project(project)) / "Library" / "AvatarWardrobe" / "bridge.json"


class BridgeLocator:
    """Resolve the current Unity bridge from a validated project-local record."""

    def __init__(self, project, fallback="", timeout=2.0, record_path=None):
        self.project = canonical_project(project)
        self.fallback = self._validate_fallback(fallback)
        self.timeout = timeout
        self.path = Path(record_path) if record_path else bridge_path(self.project)
        self._lock = threading.RLock()
        self._record_key = None
        self._record = None
        self._verified_key = None
        self._verified_at = 0.0
        self._verified_session = ""

    @staticmethod
    def _validate_fallback(value):
        if not value:
            return ""
        try:
            parsed = urllib.parse.urlsplit(value.rstrip("/"))
            if parsed.scheme != "http" or parsed.hostname != "localhost" or parsed.username or parsed.password:
                return ""
            if parsed.path not in ("", "/") or parsed.query or parsed.fragment:
                return ""
            if not UNITY_PORT_MIN <= (parsed.port or 0) <= UNITY_PORT_MAX:
                return ""
            return "http://localhost:" + str(parsed.port)
        except (TypeError, ValueError):
            return ""

    @staticmethod
    def _record_key_for(path):
        try:
            stat = path.stat()
            return (stat.st_mtime_ns, stat.st_size)
        except OSError:
            return None

    def _read_record(self):
        key = self._record_key_for(self.path)
        with self._lock:
            if key == self._record_key:
                return self._record
            self._record_key = key
            self._record = None
            self._verified_key = None
            self._verified_session = ""
            if key is None:
                return None
            try:
                value = json.loads(self.path.read_text(encoding="utf-8"))
            except (OSError, ValueError, TypeError, UnicodeError):
                return None
            if not isinstance(value, dict):
                return None
            project = value.get("project")
            port = value.get("port")
            protocol = value.get("protocol")
            online = value.get("online")
            session = value.get("session")
            if (type(protocol) is not int or protocol != BRIDGE_PROTOCOL or not isinstance(project, str) or project != self.project or
                    type(online) is not bool or
                    type(port) is not int or not UNITY_PORT_MIN <= port <= UNITY_PORT_MAX or
                    not isinstance(session, str) or (online and not session)):
                return None
            value = dict(value)
            value["port"] = port
            value["online"] = online
            self._record = value
            return value

    def snapshot(self):
        """Return the defensively parsed bridge record, or an offline record."""
        record = self._read_record()
        if record is None and self.fallback and not self.path.exists():
            port = urllib.parse.urlsplit(self.fallback).port
            return {"protocol": BRIDGE_PROTOCOL, "project": self.project,
                    "port": port, "session": "", "online": True, "fallback": True}
        return dict(record) if record is not None else {
            "protocol": BRIDGE_PROTOCOL, "project": self.project,
            "port": 0, "session": "", "online": False,
        }

    def invalidate(self):
        with self._lock:
            self._verified_key = None
            self._verified_at = 0.0
            self._verified_session = ""

    def _candidate(self):
        record = self._read_record()
        if record is not None:
            if not record.get("online"):
                raise BridgeUnavailable("Unity bridge is offline.")
            return "http://localhost:" + str(record["port"]), record
        if self.fallback and not self.path.exists():
            return self.fallback, {"port": urllib.parse.urlsplit(self.fallback).port,
                                   "session": "", "online": True, "fallback": True}
        raise BridgeUnavailable("Unity bridge record is missing or invalid.")

    def _verify(self, base_url, record):
        # operation_context is a cheap identity endpoint and does not traverse
        # the catalog.  Never follow redirects or use a system HTTP proxy.
        headers = {"X-Wardrobe-Project": urllib.parse.quote(self.project, safe="")}
        request = urllib.request.Request(base_url + "/api/operation_context", headers=headers, method="GET")
        opener = urllib.request.build_opener(_NoRedirect, urllib.request.ProxyHandler({}))
        try:
            with opener.open(request, timeout=self.timeout) as response:
                raw = response.read(2 * 1024 * 1024 + 1)
        except (urllib.error.URLError, OSError, ValueError) as error:
            raise BridgeUnavailable("Unity bridge is unavailable.") from error
        try:
            value = json.loads(raw)
        except (ValueError, TypeError, UnicodeError) as error:
            raise BridgeUnavailable("Unity bridge identity is invalid.") from error
        if not isinstance(value, dict) or value.get("projectId") != self.project:
            raise BridgeUnavailable("The Unity bridge belongs to another project.")
        session = value.get("session")
        if not isinstance(session, str) or not session:
            raise BridgeUnavailable("Unity bridge identity is missing its session.")
        # A reload can race the atomic bridge.json write.  The HTTP identity is
        # authoritative; a non-empty record session is only a fast diagnostic.
        return session

    def endpoint(self):
        """Return a verified ``http://localhost:<port>`` endpoint."""
        base_url, record = self._candidate()
        record_key = (self._record_key, base_url, record.get("session", ""))
        now = time.monotonic()
        with self._lock:
            if self._verified_key == record_key and now - self._verified_at < 1.0:
                return base_url
        session = self._verify(base_url, record)
        with self._lock:
            self._verified_key = record_key
            self._verified_at = now
            self._verified_session = session
        return base_url

    @property
    def session(self):
        with self._lock:
            return self._verified_session
