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
import urllib.parse
import uuid
from pathlib import Path

from PIL import Image

REPOSITORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY))

from atelier.bridge import BridgeError
from atelier.core import validate_recipe
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
            CreateSourceMaterial("Assets/FixtureSource.mat", new Color(.1f, .2f, .9f, 1f));
            CreateSourceMaterial("Assets/FixtureHDR.mat", new Color(2f, .2f, .3f, 1f));
            CreatePrefab("Assets/FixtureCube.prefab", Vector3.one);
            CreatePrefab("Assets/FixtureReplacement.prefab", new Vector3(1.7f, .6f, .8f));
            CreateAppearancePrefab("Assets/FixtureAppearance.prefab");
            CreateAmbiguousPrefab("Assets/FixtureAmbiguous.prefab");
            CreatePrefab("Assets/FixtureHDR.prefab", new Vector3(.7f, .7f, .7f), "Assets/FixtureHDR.mat");
            if (!System.IO.File.Exists("Assets/Fixture.unity"))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                root.name = "Atelier Target";
                EditorSceneManager.SaveScene(scene, "Assets/Fixture.unity");
            }
        };
    }
    static void CreateSourceMaterial(string path, Color color)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Standard"));
            material.SetColor("_Color", color);
            AssetDatabase.CreateAsset(material, path);
        }
    }
    static void CreatePrefab(string path, Vector3 scale)
    {
        CreatePrefab(path, scale, "Assets/FixtureSource.mat");
    }
    static void CreatePrefab(string path, Vector3 scale, string materialPath)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
        var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
        source.transform.localScale = scale;
        source.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        PrefabUtility.SaveAsPrefabAsset(source, path);
        Object.DestroyImmediate(source);
    }
    static void CreateAppearancePrefab(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
        var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
        source.name = "Appearance Outfit";
        source.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/FixtureSource.mat");
        var meshPath = "Assets/FixtureShapeMesh.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (mesh == null)
        {
            mesh = new Mesh { name = "Fixture Static Shape" };
            mesh.vertices = new[] { new Vector3(-.4f, 0, 0), new Vector3(.4f, 0, 0), new Vector3(-.4f, 1, 0), new Vector3(.4f, 1, 0) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            var delta = new[] { new Vector3(0, 0, -.15f), new Vector3(0, 0, -.15f), new Vector3(0, 0, .15f), new Vector3(0, 0, .15f) };
            mesh.AddBlendShapeFrame("Smile", 100f, delta, new Vector3[4], new Vector3[4]);
            AssetDatabase.CreateAsset(mesh, meshPath);
        }
        var child = new GameObject("Static Shape");
        child.transform.SetParent(source.transform, false);
        var skin = child.AddComponent<SkinnedMeshRenderer>();
        skin.sharedMesh = mesh;
        skin.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/FixtureSource.mat");
        PrefabUtility.SaveAsPrefabAsset(source, path);
        Object.DestroyImmediate(source);
    }
    static void CreateAmbiguousPrefab(string path)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
        var source = new GameObject("Ambiguous Outfit");
        for (var i = 0; i < 2; i++)
        {
            var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            child.name = "Duplicate Renderer";
            child.transform.SetParent(source.transform, false);
            child.transform.localPosition = new Vector3(i * 1.2f - .6f, 0, 0);
            child.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/FixtureSource.mat");
        }
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


def reconcile(command_id, workspace_id, target, revision, prefab_guid, desired_revision, appearance=None, item_id="fixture-item", asset_id="fixture"):
    return {
        "id": command_id, "workspaceId": workspace_id,
        "target": {"sceneGuid": target["sceneGuid"], "objectId": target["objectId"]},
        "expectedRevision": revision, "desiredRevision": desired_revision, "action": "reconcile",
        "payload": {"recipe": {"items": ([] if not prefab_guid else [{"id": item_id, "assetId": asset_id, "name": "Fixture Item", "prefabGuid": prefab_guid}]), "appearance": ({} if appearance is None else appearance)}}
    }


def inspect(bridge, target):
    query = urllib.parse.urlencode({"sceneGuid": target["sceneGuid"], "objectId": target["objectId"]})
    return bridge._request("GET", "/inspect?" + query)


def assert_nonblank_png(path: Path):
    assert path.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"), path
    with Image.open(path) as image:
        rgba = image.convert("RGBA")
        assert rgba.size == (512, 512), rgba.size
        extrema = rgba.getextrema()
        # A valid dark clear colour alone is insufficient evidence of a rendered
        # target. Require visible variation in at least one RGB channel.
        assert any(high > low for low, high in extrema[:3]), extrema


def assert_color_close(actual, expected):
    assert len(actual) == 4 and all(abs(float(a) - float(b)) < 1e-4 for a, b in zip(actual, expected)), (actual, expected)


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
        appearance_guid = asset_guid(fixture, "Assets/FixtureAppearance.prefab")
        ambiguous_guid = asset_guid(fixture, "Assets/FixtureAmbiguous.prefab")
        hdr_guid = asset_guid(fixture, "Assets/FixtureHDR.prefab")
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

        # Appearance edits use a generated material copy and a static
        # SkinnedMeshRenderer blendshape. The source .mat bytes must never
        # change, including while the generated recipe is undone.
        appearance_add = reconcile(str(uuid.uuid4()), workspace, target, after_remove["revision"], appearance_guid, 4, item_id="appearance-item", asset_id="appearance-source")
        added_appearance = bridge.submit(appearance_add)
        assert added_appearance["state"] == "succeeded", added_appearance
        after_appearance_add = bridge.context()
        source_material = fixture / "Assets" / "FixtureSource.mat"
        source_bytes = source_material.read_bytes()
        baseline = inspect(bridge, target)
        # The observed recipe is also the recovery baseline; keep it strict and
        # directly consumable by the desktop validator. Option metadata remains
        # in appearanceOptions and is intentionally not part of recipe state.
        validate_recipe(baseline["recipe"])
        assert all(set(value) == {"rendererId", "slot", "property", "color"} for value in baseline["recipe"]["appearance"]["materials"]), baseline
        assert all(set(value) == {"rendererId", "index", "value"} for value in baseline["recipe"]["appearance"]["blendshapes"]), baseline
        assert baseline["recipe"]["items"] and baseline["recipe"]["items"][0]["assetId"] == "appearance-source", baseline
        material_state = next(value for value in baseline["recipe"]["appearance"]["materials"] if value["property"] == "_Color")
        shape_state = next(value for value in baseline["recipe"]["appearance"]["blendshapes"] if value["index"] == 0)
        assert_color_close(material_state["color"], [0.1, 0.2, 0.9, 1])
        assert shape_state["value"] == 0, baseline
        durable_inspect = bridge.submit({"id": str(uuid.uuid4()), "workspaceId": workspace, "target": {"sceneGuid": target["sceneGuid"], "objectId": target["objectId"]}, "expectedRevision": baseline["revision"], "desiredRevision": 4, "action": "inspect", "payload": {}})
        assert durable_inspect["state"] == "succeeded" and durable_inspect["result"]["inspection"]["recipe"]["items"], durable_inspect
        appearance_red = {"materials": [{"rendererId": material_state["rendererId"], "slot": material_state["slot"], "property": "_Color", "color": [1, 0, 0, 1]}], "blendshapes": [{"rendererId": shape_state["rendererId"], "index": shape_state["index"], "value": 42}]}
        red_command = reconcile(str(uuid.uuid4()), workspace, target, after_appearance_add["revision"], appearance_guid, 5, appearance=appearance_red, item_id="appearance-item", asset_id="appearance-source")
        red = bridge.submit(red_command)
        assert red["state"] == "succeeded", red
        after_red = bridge.context()
        red_state = inspect(bridge, target)
        validate_recipe(red_state["recipe"])
        red_actual = next(value for value in red_state["recipe"]["appearance"]["materials"] if value["rendererId"] == material_state["rendererId"] and value["property"] == "_Color")
        red_shape = next(value for value in red_state["recipe"]["appearance"]["blendshapes"] if value["rendererId"] == shape_state["rendererId"] and value["index"] == shape_state["index"])
        assert red_actual["color"] == [1, 0, 0, 1], red_state
        assert red_shape["value"] == 42, red_state
        assert source_material.read_bytes() == source_bytes, "source material changed while creating generated appearance"
        generated_materials = list((fixture / "Assets" / "AtelierGenerated").rglob("*.mat"))
        assert generated_materials, "appearance did not create a generated material asset"

        undo_appearance = reconcile(str(uuid.uuid4()), workspace, target, after_red["revision"], appearance_guid, 6, appearance={}, item_id="appearance-item", asset_id="appearance-source")
        undone = bridge.submit(undo_appearance)
        assert undone["state"] == "succeeded", undone
        after_undo = bridge.context()
        undo_state = inspect(bridge, target)
        restored_material = next(value for value in undo_state["recipe"]["appearance"]["materials"] if value["rendererId"] == material_state["rendererId"] and value["property"] == "_Color")
        restored_shape = next(value for value in undo_state["recipe"]["appearance"]["blendshapes"] if value["rendererId"] == shape_state["rendererId"] and value["index"] == shape_state["index"])
        assert_color_close(restored_material["color"], [0.1, 0.2, 0.9, 1])
        assert restored_shape["value"] == 0, undo_state
        assert source_material.read_bytes() == source_bytes, "source material changed while undoing appearance"

        removed_appearance = reconcile(str(uuid.uuid4()), workspace, target, after_undo["revision"], "", 7)
        removed_appearance_receipt = bridge.submit(removed_appearance)
        assert removed_appearance_receipt["state"] == "succeeded", removed_appearance_receipt
        after_appearance_remove = bridge.context()
        assert after_appearance_remove["revision"] != after_undo["revision"], after_appearance_remove

        # HDR source colors are observed with a warning and omitted from the
        # editable recipe. Attempting to submit one is rejected before any
        # generated asset or scene change occurs.
        hdr_add = reconcile(str(uuid.uuid4()), workspace, target, after_appearance_remove["revision"], hdr_guid, 8, item_id="hdr-item", asset_id="hdr-source")
        hdr_added = bridge.submit(hdr_add)
        assert hdr_added["state"] == "succeeded", hdr_added
        hdr_inspection = inspect(bridge, target)
        assert not any(value["property"] == "_Color" for value in hdr_inspection["recipe"]["appearance"]["materials"]), hdr_inspection
        assert any("HDR" in warning for warning in hdr_inspection["warnings"]), hdr_inspection
        hdr_options = next(value for value in hdr_inspection["appearanceOptions"]["materials"] if value["slot"] == 0)
        assert not any(value["name"] == "_Color" for value in hdr_options.get("properties", [])), hdr_options
        hdr_renderer_id = hdr_options["rendererId"]
        hdr_bad = reconcile(str(uuid.uuid4()), workspace, target, hdr_inspection["revision"], hdr_guid, 9, appearance={"materials": [{"rendererId": hdr_renderer_id, "slot": 0, "property": "_Color", "color": [1, 0, 0, 1]}], "blendshapes": []}, item_id="hdr-item", asset_id="hdr-source")
        hdr_rejected = bridge.submit(hdr_bad)
        assert hdr_rejected["state"] == "failed" and "HDR" in hdr_rejected.get("error", ""), hdr_rejected
        after_hdr_reject = bridge.context()
        assert after_hdr_reject["revision"] == hdr_inspection["revision"], (after_hdr_reject, hdr_inspection)

        hdr_removed = reconcile(str(uuid.uuid4()), workspace, target, after_hdr_reject["revision"], "", 10)
        hdr_removed_receipt = bridge.submit(hdr_removed)
        assert hdr_removed_receipt["state"] == "succeeded", hdr_removed_receipt
        after_hdr_remove = bridge.context()

        # Two supported renderers with the same relative transform path are
        # intentionally excluded from appearance controls. A path-only record
        # could otherwise restore the wrong sibling after a restart.
        ambiguous_add = reconcile(str(uuid.uuid4()), workspace, target, after_hdr_remove["revision"], ambiguous_guid, 11, item_id="ambiguous-item", asset_id="ambiguous-source")
        ambiguous_added = bridge.submit(ambiguous_add)
        assert ambiguous_added["state"] == "succeeded", ambiguous_added
        ambiguous_inspection = inspect(bridge, target)
        assert ambiguous_inspection["recipe"]["items"] and ambiguous_inspection["recipe"]["items"][0]["assetId"] == "ambiguous-source", ambiguous_inspection
        assert not ambiguous_inspection["recipe"]["appearance"]["materials"], ambiguous_inspection
        assert not ambiguous_inspection["recipe"]["appearance"]["blendshapes"], ambiguous_inspection
        assert not ambiguous_inspection["appearanceOptions"]["materials"], ambiguous_inspection
        assert any("ambiguous" in warning.lower() for warning in ambiguous_inspection["warnings"]), ambiguous_inspection
        ambiguous_removed = reconcile(str(uuid.uuid4()), workspace, target, ambiguous_inspection["revision"], "", 12)
        ambiguous_removed_receipt = bridge.submit(ambiguous_removed)
        assert ambiguous_removed_receipt["state"] == "succeeded", ambiguous_removed_receipt
        after_ambiguous_remove = bridge.context()

        stale = bridge.snapshot(str(uuid.uuid4()), target["sceneGuid"], target["objectId"], "front", original["revision"], 13)
        assert stale["state"] == "failed" and "revision mismatch" in stale.get("error", ""), stale
        wrong_target = reconcile(str(uuid.uuid4()), workspace, {"sceneGuid": "0" * 32, "objectId": target["objectId"]}, after_ambiguous_remove["revision"], cube_guid, 14)
        wrong = bridge.submit(wrong_target)
        assert wrong["state"] == "failed" and "scene GUID" in wrong.get("error", ""), wrong

        render = bridge.snapshot(str(uuid.uuid4()), target["sceneGuid"], target["objectId"], "three-quarter", after_ambiguous_remove["revision"], 15)
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
        result = {"fixture": str(fixture), "outputDirectory": str(state_dir), "added": added, "replaced": replaced, "removed": removed, "appearance": {"red": red, "undone": undone}, "snapshot": render, "tombstone": tombstone}
        print(json.dumps(result, indent=2))
    finally:
        if worker is not None:
            worker.stop()
        if not args.keep:
            shutil.rmtree(fixture, ignore_errors=True)


if __name__ == "__main__":
    main()
