"""Measure the captured units, multi-object, boolean and repair probes.

This reproduces observations, including defects; it is not a product-quality pass.
Original app outputs are read only. Diagnostic cleanup affects in-memory copies.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import platform
import xml.etree.ElementTree as ET
import zipfile

import numpy as np
import trimesh


ROOT = Path(__file__).resolve().parents[1]
CORE = "{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}"
CUBE = [[0, 0, 0], [20, 20, 20]]
ASSEMBLY = [CUBE, [[30, 0, 0], [50, 20, 20]]]
OVERLAP = [CUBE, [[10, 0, 0], [30, 20, 20]]]
EXPECTED = {
    "units-mm.3mf": ASSEMBLY,
    "units-inch.3mf": ASSEMBLY,
    "overlap-objects.3mf": OVERLAP,
    "overlap-stl-import.3mf": OVERLAP,
    "stl-as-inches.3mf": [[[-244, -244, 0], [264, 264, 508]]],
    "subtract.3mf": [[[0, 0, 0], [10, 20, 20]]],
    "union.3mf": [[[0, 0, 0], [30, 20, 20]]],
    "intersection.3mf": [[[10, 0, 0], [20, 20, 20]]],
    "missing-top-before-repair.3mf": [CUBE],
    **{f"{kind}-repaired.3mf": [CUBE]
       for kind in ("missing-top", "inverted", "duplicate", "degenerate")},
}


def topology(mesh: trimesh.Trimesh) -> dict:
    # Index topology can be closed even with coincident vertices/zero-area faces.
    return {
        "vertices": len(mesh.vertices),
        "triangles": len(mesh.faces),
        "zero_area_triangles": int(np.count_nonzero(mesh.area_faces == 0)),
        "duplicate_index_triangles": int(len(mesh.faces) - mesh.unique_faces().sum()),
        "watertight_by_edge_incidence": bool(mesh.is_watertight),
        "winding_consistent": bool(mesh.is_winding_consistent),
        "edge_connected_components": len(mesh.split(only_watertight=False)),
    }


def surface_comparison(mesh: trimesh.Trimesh, bounds: np.ndarray) -> dict:
    """Finite bidirectional distance samples, not an exact Hausdorff bound."""
    ideal = trimesh.creation.box(extents=bounds[1] - bounds[0])
    ideal.apply_translation(bounds.mean(axis=0))
    output_points = np.vstack((mesh.vertices, mesh.triangles_center))
    grid = []
    for axis in range(3):
        others = [i for i in range(3) if i != axis]
        for side in range(2):
            for u in np.linspace(0, 1, 11):
                for v in np.linspace(0, 1, 11):
                    point = bounds[0].copy()
                    point[axis] = bounds[side, axis]
                    for i, fraction in zip(others, (u, v)):
                        point[i] += fraction * (bounds[1, i] - bounds[0, i])
                    grid.append(point)
    reference_points = np.unique(np.asarray(grid), axis=0)
    to_ideal = trimesh.proximity.closest_point_naive(ideal, output_points)[1]
    to_output = trimesh.proximity.closest_point_naive(mesh, reference_points)[1]
    return {
        "output_vertex_and_centroid_samples": len(output_points),
        "ideal_face_grid_samples": len(reference_points),
        "max_output_to_ideal_mm": float(to_ideal.max()),
        "max_ideal_to_output_mm": float(to_output.max()),
        "max_bidirectional_sample_mm": float(max(to_ideal.max(), to_output.max())),
    }


def inspect(path: Path, ideal_bounds: list) -> dict:
    # Disable implicit welding/cleanup so raw indexed topology remains visible.
    scene = trimesh.load_scene(path, process=False)
    meshes = sorted(scene.dump(), key=lambda m: tuple(m.bounds[0]))
    assert len(meshes) == len(ideal_bounds), path.name
    with zipfile.ZipFile(path) as archive:
        model = ET.fromstring(archive.read("3D/3dmodel.model"))
    assert model.get("unit") == "millimeter", path.name
    objects = model.findall(f"{CORE}resources/{CORE}object")
    resource_report = [{
        "id": obj.get("id"),
        "vertices": len(obj.findall(f"{CORE}mesh/{CORE}vertices/{CORE}vertex")),
        "triangles": len(obj.findall(f"{CORE}mesh/{CORE}triangles/{CORE}triangle")),
        "components": [dict(c.attrib) for c in obj.findall(f"{CORE}components/{CORE}component")],
    } for obj in objects]
    # Compare unique resources, not instances: an assembly can reuse one mesh.
    assert sorted(r["triangles"] for r in resource_report if r["triangles"]) == sorted(
        len(m.faces) for m in scene.geometry.values()), path.name
    instances = []
    for mesh, bounds in zip(meshes, ideal_bounds):
        bounds = np.asarray(bounds, dtype=float)
        welded = mesh.copy()
        welded.merge_vertices()
        cleaned = welded.copy()
        cleaned.update_faces(cleaned.nondegenerate_faces())
        cleaned.update_faces(cleaned.unique_faces())
        cleaned.remove_unreferenced_vertices()
        ideal_volume = float(np.prod(bounds[1] - bounds[0]))
        item = {
            "bounds_mm": mesh.bounds.tolist(),
            "extents_mm": mesh.extents.tolist(),
            "signed_volume_integral_mm3": float(mesh.volume),
            "raw_index_topology": topology(mesh),
            "welded_topology": topology(welded),
            "diagnostic_cleaned_topology": topology(cleaned),
            "ideal_solid_bounds_mm": bounds.tolist(),
            "ideal_solid_volume_mm3": ideal_volume,
            "max_bound_deviation_mm": float(np.abs(mesh.bounds - bounds).max()),
            "volume_deviation_mm3": float(mesh.volume - ideal_volume) if mesh.is_watertight else None,
            "volume_deviation_percent": float(100 * (mesh.volume / ideal_volume - 1)) if mesh.is_watertight else None,
            "surface_samples_against_ideal_solid": surface_comparison(cleaned, bounds),
        }
        instances.append(item)
    return {
        "file": path.name, "bytes": path.stat().st_size,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "declared_unit": model.get("unit"),
        "resources": resource_report,
        "build_items": [dict(x.attrib) for x in model.findall(f"{CORE}build/{CORE}item")],
        "instances": instances,
    }


def verify_recorded_observations(reports: dict) -> None:
    """Check measured historical results, deliberately not ideal geometry tolerances."""
    for name, report in reports.items():
        for item in report["instances"]:
            raw = item["raw_index_topology"]
            if name == "missing-top-before-repair.3mf":
                assert raw["triangles"] == 10 and not raw["watertight_by_edge_incidence"]
            elif name.endswith("-repaired.3mf"):
                assert raw["triangles"] == 96 and raw["watertight_by_edge_incidence"]
                np.testing.assert_allclose(item["extents_mm"], [19.9980029249] * 3, rtol=0, atol=1e-9)
                np.testing.assert_allclose(item["signed_volume_integral_mm3"], 7997.603749170572, rtol=0, atol=1e-6)
            elif name in ("subtract.3mf", "union.3mf"):
                assert raw["triangles"] == (76 if name == "subtract.3mf" else 128)
                assert raw["watertight_by_edge_incidence"]
                np.testing.assert_allclose(item["extents_mm"][1:], [19.992, 19.992], rtol=0, atol=1e-9)
                np.testing.assert_allclose(item["volume_deviation_percent"], -0.079984, rtol=0, atol=1e-8)
            elif name == "intersection.3mf":
                assert raw["triangles"] == 32 and raw["zero_area_triangles"] == 12
                assert raw["watertight_by_edge_incidence"]
                assert not item["welded_topology"]["watertight_by_edge_incidence"]
                assert item["diagnostic_cleaned_topology"]["triangles"] == 20
                assert item["diagnostic_cleaned_topology"]["watertight_by_edge_incidence"]
                np.testing.assert_allclose(item["signed_volume_integral_mm3"], 4000, rtol=0, atol=1e-6)
            else:
                assert raw["triangles"] == 12 and raw["watertight_by_edge_incidence"]
                assert item["max_bound_deviation_mm"] < 1e-6
                assert abs(item["volume_deviation_mm3"]) < 1e-3
            assert raw["winding_consistent"]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=ROOT / ".local/evidence/extended-20261018/outputs")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    reports = {name: inspect(args.directory / name, bounds) for name, bounds in EXPECTED.items()}
    verify_recorded_observations(reports)
    originals = []
    for folder in ("generated", "failures"):
        parent = ROOT / "fixtures" / folder
        for case in json.loads((parent / "manifest.json").read_text())["cases"]:
            assert hashlib.sha256((parent / case["file"]).read_bytes()).hexdigest() == case["sha256"]
            originals.append({"file": f"fixtures/{folder}/{case['file']}", "sha256": case["sha256"]})
    assert not (args.directory / "cancelled-save.3mf").exists()
    report = {
        "schema_version": 1,
        "target": "Microsoft.3DBuilder_20.0.4.0_x64__8wekyb3d8bbwe",
        "executable_sha256": "50add8d2e1362407105c9b0432d0ada43b6ba5da1326919d0d4fec2b133e93ab",
        "tools": {"python": platform.python_version(), "trimesh": trimesh.__version__, "numpy": np.__version__},
        "recorded_observations_reproduced": True,
        "original_input_hashes_unchanged": originals,
        "cancelled_save_file_absent": True,
        "limitations": [
            "Captured analytic boxes only. Reproducing an observed defect is not a product-quality pass.",
            "No self-intersection, thin-wall, material, large-file, or performance certification.",
            "Raw meshes load with process=False; welded metrics merge coincident vertices using trimesh defaults.",
            "Cleanup removes nondegenerate_faces failures and duplicate index faces from an in-memory copy only.",
            "Surface distances use finite samples and are not exact Hausdorff bounds.",
            "An open mesh's signed integral is not an enclosed volume; its volume deviation is null.",
            "Per-instance overlap volumes double-count common space; their sum is not union volume.",
        ],
        "outputs": list(reports.values()),
    }
    encoded = json.dumps(report, indent=2) + "\n"
    if args.output:
        args.output.write_text(encoded, encoding="utf-8")
        print(f"Reproduced measurements for {len(reports)} outputs; verified {len(originals)} unchanged inputs. Report: {args.output}")
    else:
        print(encoded, end="")


if __name__ == "__main__":
    main()
