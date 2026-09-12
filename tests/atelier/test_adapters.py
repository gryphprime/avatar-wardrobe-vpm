import json
import unittest
from pathlib import Path

from adapters_sdk import ManifestError, load_manifest, validate_manifest


BASE = {"protocol": 1, "id": "example", "name": "Example", "version": "1.0", "origin": "official",
        "sdk": {"protocol": 1}, "resources": [], "actions": [{"id": "inspect", "class": "read"}]}


class AdapterManifestTests(unittest.TestCase):
    def test_protocol_and_sdk_mismatch(self):
        for key in ("protocol", "sdk"):
            value = dict(BASE)
            value[key] = 2 if key == "protocol" else {"protocol": 2}
            with self.assertRaises(ManifestError): validate_manifest(value)

    def test_duplicate_actions_and_executable_frontend_rejected(self):
        duplicate = dict(BASE, actions=[{"id": "x", "class": "read"}, {"id": "x", "class": "preview"}])
        with self.assertRaises(ManifestError): validate_manifest(duplicate)
        with self.assertRaises(ManifestError): validate_manifest(dict(BASE, ui={"frontend": "code"}))

    def test_manifest_size_bound_and_first_party_example(self):
        with self.assertRaises(ManifestError): validate_manifest(dict(BASE, ui={"text": "x" * (1024 * 1024)}))
        example = load_manifest(Path("adapters/bridge_snapshot.json"))
        self.assertEqual(example.data["protocol"], 1)
        self.assertEqual(example.data["origin"], "official")


if __name__ == "__main__": unittest.main()
