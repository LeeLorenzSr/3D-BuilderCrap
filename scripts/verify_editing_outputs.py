"""Independent physical measurements of new editor outputs; reads fixtures/models without changing them."""
import hashlib
import itertools
import json
import math
import platform
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZipFile
from xml.etree import ElementTree

import numpy as np
import trimesh

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / ".local/evidence/overnight-20261018"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def meshes(path):
    scene = trimesh.load(path, force="scene", process=True)
    result = []
    for node in scene.graph.nodes_geometry:
        transform, geometry = scene.graph[node]
        mesh = scene.geometry[geometry].copy()
        mesh.apply_transform(transform)
        result.append(mesh)
    return scene, result


def measure(path, low, high, volumes, relative=1e-9, tolerance=3e-6):
    scene, parts = meshes(path)
    assert np.allclose(scene.bounds, [low, high], atol=tolerance, rtol=0), path
    assert len(parts) == len(volumes), path
    assert np.allclose(sorted(p.volume for p in parts), sorted(volumes), rtol=relative, atol=1e-6), path
    values = []
    for mesh in parts:
        assert mesh.is_watertight and mesh.is_winding_consistent and mesh.volume > 0, path
        assert np.all(mesh.area_faces > 0), path
        assert len(np.unique(np.sort(mesh.faces, axis=1), axis=0)) == len(mesh.faces), path
        values.append({"bounds_mm": mesh.bounds.tolist(), "volume_mm3": float(mesh.volume),
                       "triangles": len(mesh.faces), "connected_components": len(mesh.split(only_watertight=False)),
                       "watertight_after_vertex_processing": True, "winding_consistent": True,
                       "zero_area_faces": 0, "duplicate_faces": 0})
    return {"file": str(path.relative_to(BASE)), "sha256": digest(path), "parts": values}


def main():
    results = []
    curved = []
    for kind, ideal, relative in (("cube", 18*26*34, 1e-9),
                                  ("cylinder", math.pi*18*26*34/4, 0.003),
                                  ("sphere", math.pi*18*26*34/6, 0.01)):
        native = BASE / "editing-checks" / (kind+".3mf")
        _, original = meshes(native)
        exact_mesh_volume = original[0].volume
        for extension in (".3mf", ".stl"):
            path = native.with_suffix(extension)
            record = measure(path, [0, 0, 0], [18, 26, 34], [ideal], relative)
            record["ideal_volume_mm3"] = ideal
            record["relative_volume_deficit"] = 1-record["parts"][0]["volume_mm3"]/ideal
            results.append(record)
        split = native.with_name(kind+"-split.3mf")
        results.append(measure(split, [0, 0, 0], [18, 26, 34], [exact_mesh_volume/2]*2))
        _, split_meshes = meshes(split)
        assert sorted(round(p.bounds[0, 2], 8) for p in split_meshes) == [0, 17], split
        assert sorted(round(p.bounds[1, 2], 8) for p in split_meshes) == [17, 34], split
        _, stl = meshes(native.with_suffix(".stl"))
        points = original[0].vertices
        drift = np.sqrt(((stl[0].vertices[:, None, :]-points[None, :, :])**2).sum(axis=2)).min(axis=1).max()
        assert drift < 3e-6, kind
        detail = {"primitive": kind, "stl_maximum_nearest_vertex_drift_mm": float(drift)}
        if kind != "cube":
            centers = original[0].triangles_center
            if kind == "cylinder":
                z = original[0].triangles[:, :, 2]
                centers = centers[np.ptp(z, axis=1) > 0]
                delta = centers[:, :2]-[9, 13]
                normalized = np.linalg.norm(delta/[9, 13], axis=1)
            else:
                delta = centers-[9, 13, 17]
                normalized = np.linalg.norm(delta/[9, 13, 17], axis=1)
            radial_deficit = np.linalg.norm(delta, axis=1)*(1/normalized-1)
            detail["maximum_sampled_facet_center_radial_deficit_mm"] = float(radial_deficit.max())
            detail["surface_error_scope"] = "Triangle centroid samples along rays from analytic center; not a global Hausdorff bound."
        curved.append(detail)

    # Independent column-vector rotation matrices and analytic corners.
    x, y, z = np.radians([23, -37, 61])
    rx = np.array([[1, 0, 0], [0, np.cos(x), -np.sin(x)], [0, np.sin(x), np.cos(x)]])
    ry = np.array([[np.cos(y), 0, np.sin(y)], [0, 1, 0], [-np.sin(y), 0, np.cos(y)]])
    rz = np.array([[np.cos(z), -np.sin(z), 0], [np.sin(z), np.cos(z), 0], [0, 0, 1]])
    corners = np.array(list(itertools.product([-5, 5], [-10, 10], [-15, 15]))) @ (rz@ry@rx).T + [43, -12, 30]
    path = BASE / "editing-checks/rotated.3mf"
    results.append(measure(path, corners.min(axis=0), corners.max(axis=0), [6000], tolerance=1e-8))
    _, rotated = meshes(path)
    drift = np.sqrt(((rotated[0].vertices[:, None, :]-corners[None, :, :])**2).sum(axis=2)).min(axis=1).max()
    assert drift < 1e-8, "rotated vertices"

    for name, low, high, volumes in (
        ("editing-checks/renamed.3mf", [0, 0, 0], [20, 20, 20], [8000]),
        ("editing-checks/insert-baseline.3mf", [-40, 0, 0], [-20, 20, 20], [8000]),
        ("editing-checks/inserted.3mf", [-40, 0, 0], [50, 20, 20], [8000]*3),
        ("desktop-checks/ui-repaired.3mf", [0, 0, 0], [20, 20, 20], [8000]),
        ("desktop-checks/ui-split.3mf", [0, 0, 0], [20, 20, 20], [4000]*2),
        ("desktop-checks/ui-boolean-0.3mf", [0, 0, 0], [30, 20, 20], [12000]),
        ("desktop-checks/ui-boolean-1.3mf", [0, 0, 0], [10, 20, 20], [4000]),
        ("desktop-checks/ui-boolean-2.3mf", [10, 0, 0], [20, 20, 20], [4000]),
        ("desktop-checks/ui-insert-baseline.3mf", [0, 0, 0], [20, 20, 20], [8000]),
    ):
        results.append(measure(BASE/name, low, high, volumes))

    with ZipFile(BASE / "editing-checks/renamed.3mf") as package:
        xml = ElementTree.fromstring(package.read("3D/3dmodel.model"))
        assert xml.find("{*}resources/{*}object").attrib["name"] == "Bracket & washer <A>"
    inputs = []
    for folder in ("generated", "failures"):
        directory = ROOT / "fixtures" / folder
        for case in json.loads((directory/"manifest.json").read_text())["cases"]:
            assert digest(directory/case["file"]) == case["sha256"], case["file"]
            inputs.append({"file": f"fixtures/{folder}/{case['file']}", "sha256": case["sha256"]})
    report = {"utc": datetime.now(timezone.utc).isoformat(), "python": platform.python_version(),
              "trimesh": trimesh.__version__, "numpy": np.__version__, "outputs": results,
              "curved_surface_samples": curved, "rotation_maximum_vertex_deviation_mm": float(drift),
              "unchanged_inputs": inputs,
              "limits": "Analytic synthetic inputs only. Closure does not establish general solid validity, self-intersection freedom, repair parity or 3MF conformance."}
    (BASE/"editing-output-verification.json").write_text(json.dumps(report, indent=2)+"\n")
    print(f"PASS: {len(results)} editing/WPF outputs; physical bounds, volume, topology, rotation vertices and sampled curvature; {len(inputs)} unchanged fixtures.")


if __name__ == "__main__":
    main()
