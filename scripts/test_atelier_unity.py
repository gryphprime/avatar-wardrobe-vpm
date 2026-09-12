#!/usr/bin/env python3
"""Compile the independent Atelier package and exercise its HTTP bridge end to end.

Use --output-directory to retain receipts and rendered PNGs for inspection. The
temporary Unity project is removed unless --keep is explicitly requested.
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
import tempfile
import time
import uuid
from pathlib import Path

from PIL import Image

REPOSITORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY))

from atelier.bridge import BridgeError
from atelier.project_runtime import UnityWorker, discover_unity, provision_bridge


FIXTURE = '''
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class AtelierFixture
{
    [InitializeOnLoadMethod] static void CreateFixture()
    {
        EditorApplication.delayCall += () =>
        {
            CreatePrefab("Assets/FixtureCube.prefab", Vector3.one);
            CreatePrefab("Assets/FixtureReplacement.prefab", new Vector3(1.7f, .6f, .8f));
            if (!System.IO.File.Exists("Assets/Fixture.unity"))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                root.name = "Atelier Target";
                EditorSceneManager.SaveScene(scene, "Assets/Fixture.unity");
            }
        };
    }
    static void CreatePrefab(string path, Vector3 scale)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
        var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
        source.transform.localScale = scale;
        PrefabUtility.SaveAsPrefabAsset(source, path);
        Object.DestroyImmediate(source);
    }
}
'''


def wait_for_context(worker: UnityWorker, seconds=120):
    deadline, last = time.monotonic() + seconds, None
    while time.monotonic() < deadline:
        try:
            context = worker.client().context()
            if context.get("targets"):
                return context
        except (BridgeError, OSError, AttributeError) as error:
            last = error
        time.sleep(.5)
    raise RuntimeError("bridge did not report a saved scene target: " + str(last))


def asset_guid(project: Path, relative_path: str) -> str:
    meta = project / (relative_path + ".meta")
    deadline = time.monotonic() + 30
    while not meta.exists() and time.monotonic() < deadline:
        time.sleep(.25)
    for line in meta.read_text(encoding="utf-8").splitlines():
        if line.startswith("guid:"):
            return line.partition(":")[2].strip()
    raise RuntimeError("Unity did not write a GUID for " + relative_path)


def reconcile(command_id, workspace_id, target, revision, prefab_guid, desired_revision):
    return {
        "id": command_id, "workspaceId": workspace_id,
        "target": {"sceneGuid": target["sceneGuid"], "objectId": target["objectId"]},
        "expectedRevision": revision, "desiredRevision": desired_revision, "action": "reconcile",
        "payload": {"recipe": {"items": ([] if not prefab_guid else [{"id": "fixture-item", "assetId": "fixture", "name": "Fixture Item", "prefabGuid": prefab_guid}]), "appearance": {}}}
    }


def assert_nonblank_png(path: Path):
    assert path.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"), path
    with Image.open(path) as image:
        rgba = image.convert("RGBA")
        assert rgba.size == (512, 512), rgba.size
        extrema = rgba.getextrema()
        # A valid dark clear colour alone is insufficient evidence of a rendered
        # target. Require visible variation in at least one RGB channel.
        assert any(high > low for low, high in extrema[:3]), extrema


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-directory", type=Path, help="retain bridge receipts and PNG artifacts here")
    parser.add_argument("--keep", action="store_true", help="retain the generated temporary Unity project")
    args = parser.parse_args()
    unity = discover_unity()
    if not unity:
        raise RuntimeError("Unity 2022.3.22f1 is not installed")
    fixture = Path(tempfile.mkdtemp(prefix="atelier-unity-"))
    state_dir = (args.output_directory.expanduser().resolve() if args.output_directory else fixture / ".atelier-state")
    state_dir.mkdir(parents=True, exist_ok=True)
    worker = None
    try:
        (fixture / "Assets" / "Editor").mkdir(parents=True)
        (fixture / "Packages").mkdir()
        (fixture / "ProjectSettings").mkdir()
        (fixture / "ProjectSettings" / "ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.22f1\n", encoding="utf-8")
        (fixture / "Packages" / "manifest.json").write_text(json.dumps({"dependencies": {"com.unity.modules.jsonserialize": "1.0.0"}}), encoding="utf-8")
        (fixture / "Assets" / "Editor" / "AtelierFixture.cs").write_text(FIXTURE, encoding="utf-8")
        provision_bridge(fixture)
        worker = UnityWorker(fixture, unity_path=unity, state_dir=state_dir)
        # Rendering is part of the acceptance test: do not use -nographics.
        worker.start(graphics=True)
        original = wait_for_context(worker)
        target, workspace = original["targets"][0], str(uuid.uuid4())
        cube_guid = asset_guid(fixture, "Assets/FixtureCube.prefab")
        replacement_guid = asset_guid(fixture, "Assets/FixtureReplacement.prefab")
        bridge = worker.client()

        added_command = reconcile(str(uuid.uuid4()), workspace, target, original["revision"], cube_guid, 1)
        added = bridge.submit(added_command)
        assert added["state"] == "succeeded", added
        # Exact duplicate receipt must be returned, never execute the mutation again.
        assert bridge.submit(added_command)["state"] == "succeeded"
        after_add = bridge.context()
        assert after_add["revision"] != original["revision"], after_add

        replacement_command = reconcile(str(uuid.uuid4()), workspace, target, after_add["revision"], replacement_guid, 2)
        replaced = bridge.submit(replacement_command)
        assert replaced["state"] == "succeeded", replaced
        after_replace = bridge.context()
        assert after_replace["revision"] != after_add["revision"], after_replace

        removed_command = reconcile(str(uuid.uuid4()), workspace, target, after_replace["revision"], "", 3)
        removed = bridge.submit(removed_command)
        assert removed["state"] == "succeeded", removed
        after_remove = bridge.context()
        assert after_remove["revision"] != after_replace["revision"], after_remove

        stale = bridge.snapshot(str(uuid.uuid4()), target["sceneGuid"], target["objectId"], "front", original["revision"], 4)
        assert stale["state"] == "failed" and "revision mismatch" in stale.get("error", ""), stale
        wrong_target = reconcile(str(uuid.uuid4()), workspace, {"sceneGuid": "0" * 32, "objectId": target["objectId"]}, after_remove["revision"], cube_guid, 5)
        wrong = bridge.submit(wrong_target)
        assert wrong["state"] == "failed" and "scene GUID" in wrong.get("error", ""), wrong

        render = bridge.snapshot(str(uuid.uuid4()), target["sceneGuid"], target["objectId"], "three-quarter", after_remove["revision"], 6)
        artifact = render.get("result", {}).get("artifact", {})
        artifact_path = Path(artifact.get("path", ""))
        assert render["state"] == "succeeded" and artifact_path.is_file(), render
        assert_nonblank_png(artifact_path)

        # A restart must reload terminal receipts and turn corrupt receipt files
        # into review-only tombstones rather than replaying unknown work.
        corrupt_id = str(uuid.uuid4())
        worker.stop()
        (state_dir / "receipts" / (corrupt_id + ".json")).write_text("not json", encoding="utf-8")
        worker.start(graphics=True)
        wait_for_context(worker)
        bridge = worker.client()
        replayed = bridge.submit(added_command)
        tombstone = bridge.poll(corrupt_id)
        assert replayed["state"] == "succeeded", replayed
        assert tombstone["state"] == "needs-review", tombstone
        result = {"fixture": str(fixture), "outputDirectory": str(state_dir), "added": added, "replaced": replaced, "removed": removed, "snapshot": render, "tombstone": tombstone}
        print(json.dumps(result, indent=2))
    finally:
        if worker is not None:
            worker.stop()
        if not args.keep:
            shutil.rmtree(fixture, ignore_errors=True)


if __name__ == "__main__":
    main()
