"""Independently measure retained transform outputs; never changes models or fixtures.

Run with .local/verification-venv/Scripts/python.exe. This checks written geometry,
not desktop input, frame rate, original-app behavior or general mesh validity.
"""
import argparse
import hashlib
import itertools
import json
import platform
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import trimesh

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / ".local/evidence/transforms-20261019"
NATIVE_TOLERANCE_MM = 1e-8
STL_TOLERANCE_MM = 3e-6
# Recorded by the parent during this dated native walkthrough; a mesh file cannot
# establish its generating executable. Baseline's earlier desktop digest is unknown.
LIVE_SCALED_DESKTOP_SHA256 = "f8374d8a5ebaf9c6c5bb42e75b7d2d0f6eb090ce2d59c9ed8282acf9e895327b"
LIVE_CORE_SHA256 = "6f4666d5de2c92107cdde5b0279fe5f1e65e6e1cf37d13cd013a070df1195c15"


def require(value, message):
    if not value:
        raise AssertionError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def rotation(degrees):
    """Independent column-vector, right-handed world X then Y then Z matrices."""
    x, y, z = np.radians(degrees)
    rx = np.array([[1, 0, 0], [0, np.cos(x), -np.sin(x)], [0, np.sin(x), np.cos(x)]])
    ry = np.array([[np.cos(y), 0, np.sin(y)], [0, 1, 0], [-np.sin(y), 0, np.cos(y)]])
    rz = np.array([[np.cos(z), -np.sin(z), 0], [np.sin(z), np.cos(z), 0], [0, 0, 1]])
    return rz @ ry @ rx


def box(center, size, volume, degrees=(0, 0, 0), world_factors=(1, 1, 1)):
    center = np.asarray(center, dtype=float)
    linear = np.diag(world_factors) @ rotation(degrees) @ np.diag(size)
    corners = np.array(list(itertools.product([-0.5, 0.5], repeat=3))) @ linear.T + center
    require(abs(np.linalg.det(linear) - volume) < 1e-7, "Analytic volume/transform mismatch")
    return {"center": center, "linear": linear, "corners": corners, "volume": volume}


def load_parts(path, process):
    scene = trimesh.load(path, force="scene", process=process)
    parts = []
    for node in scene.graph.nodes_geometry:
        transform, geometry = scene.graph[node]
        mesh = scene.geometry[geometry].copy()
        mesh.apply_transform(transform)
        parts.append(mesh)
    return scene, parts


def vertex_distance(vertices, corners):
    distances = np.linalg.norm(vertices[:, None, :] - corners[None, :, :], axis=2)
    return float(max(distances.min(axis=0).max(), distances.min(axis=1).max()))


def topology(mesh):
    _, edge_counts = np.unique(mesh.edges_sorted, axis=0, return_counts=True)
    unique_faces = len(np.unique(np.sort(mesh.faces, axis=1), axis=0))
    return {"vertices": len(mesh.vertices), "triangles": len(mesh.faces),
            "connected_components": len(mesh.split(only_watertight=False)),
            "boundary_edges": int(np.count_nonzero(edge_counts == 1)),
            "non_manifold_edges": int(np.count_nonzero(edge_counts > 2)),
            "zero_area_faces": int(np.count_nonzero(mesh.area_faces <= 0)),
            "duplicate_index_faces": len(mesh.faces) - unique_faces,
            "watertight": bool(mesh.is_watertight),
            "winding_consistent": bool(mesh.is_winding_consistent),
            "euler_characteristic": int(mesh.euler_number)}


def face_plane_deviation(mesh, expected):
    """Each triangle must lie on one analytic box face, also after a world-axis scale."""
    inverse = np.linalg.inv(expected["linear"])
    local = (mesh.triangles - expected["center"]) @ inverse.T
    # An inverse row is the normal of a local-coordinate plane in world space.
    normal_lengths = np.linalg.norm(inverse, axis=1)
    deviations = []
    for axis in range(3):
        for side in (-0.5, 0.5):
            deviations.append(np.abs(local[:, :, axis] - side).max(axis=1) / normal_lengths[axis])
    return float(np.min(deviations, axis=0).max())


def measure(path, expected_parts):
    initial_hash = digest(path)
    scene, parts = load_parts(path, process=True)
    _, raw_parts = load_parts(path, process=False)
    label = str(path.relative_to(BASE))
    require(len(parts) == len(expected_parts) == len(raw_parts), f"{label}: wrong object/instance count")
    tolerance = STL_TOLERANCE_MM if path.suffix == ".stl" else NATIVE_TOLERANCE_MM
    volume_tolerance = 0.003 if path.suffix == ".stl" else 1e-6
    all_corners = np.concatenate([e["corners"] for e in expected_parts])
    expected_bounds = np.array([all_corners.min(axis=0), all_corners.max(axis=0)])
    require(np.allclose(scene.bounds, expected_bounds, atol=tolerance, rtol=0), f"{label}: wrong scene bounds")
    # Importer graph order is not a geometry contract. Match complete analytic corner sets.
    order = min(itertools.permutations(range(len(parts))),
                key=lambda p: max(vertex_distance(parts[index].vertices, e["corners"])
                                  for index, e in zip(p, expected_parts)))
    raw_order = min(itertools.permutations(range(len(raw_parts))),
                    key=lambda p: max(vertex_distance(raw_parts[index].vertices, e["corners"])
                                      for index, e in zip(p, expected_parts)))
    values = []
    for index, raw_index, expected in zip(order, raw_order, expected_parts):
        mesh, raw = parts[index], raw_parts[raw_index]
        deviation = vertex_distance(mesh.vertices, expected["corners"])
        plane_deviation = face_plane_deviation(mesh, expected)
        require(deviation < tolerance, f"{label}: analytic corner deviation {deviation}")
        require(plane_deviation < tolerance, f"{label}: analytic face plane deviation {plane_deviation}")
        require(abs(mesh.volume - expected["volume"]) < volume_tolerance, f"{label}: wrong physical volume")
        processed_topology = topology(mesh)
        raw_topology = topology(raw)
        require(processed_topology["connected_components"] == 1, f"{label}: disconnected object")
        require(processed_topology["watertight"] and processed_topology["winding_consistent"], f"{label}: open/inconsistently wound object")
        require(processed_topology["boundary_edges"] == processed_topology["non_manifold_edges"] == 0, f"{label}: bad edge topology")
        require(processed_topology["zero_area_faces"] == processed_topology["duplicate_index_faces"] == 0, f"{label}: degenerate/duplicate faces")
        require(processed_topology["euler_characteristic"] == 2 and processed_topology["triangles"] == 12, f"{label}: unexpected box topology")
        normals = np.cross(mesh.triangles[:, 1] - mesh.triangles[:, 0], mesh.triangles[:, 2] - mesh.triangles[:, 0])
        facing = np.einsum("ij,ij->i", normals, mesh.triangles_center - expected["center"])
        require(np.all(facing > 0), f"{label}: non-outward convex face")
        values.append({"bounds_mm": mesh.bounds.tolist(), "volume_mm3": float(mesh.volume),
                       "expected_volume_mm3": expected["volume"],
                       "absolute_volume_deviation_mm3": float(abs(mesh.volume - expected["volume"])),
                       "maximum_symmetric_analytic_corner_deviation_mm": deviation,
                       "maximum_face_vertex_plane_deviation_mm": plane_deviation,
                       "raw_index_topology": raw_topology, "after_vertex_processing": processed_topology})
    require(digest(path) == initial_hash, f"{label}: model changed during verification")
    return {"file": label, "sha256": initial_hash, "instances": len(parts),
            "bounds_mm": scene.bounds.tolist(), "bound_and_vertex_tolerance_mm": tolerance,
            "absolute_volume_tolerance_mm3": volume_tolerance, "parts": values}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--include-ui", action="store_true", help="Also require and independently measure the two retained native desktop walkthrough outputs")
    options = parser.parse_args()
    core_box = box([-12, 7, 40], [10, 20, 30], 6000, [45, -45, 90])
    scaled_box = box([-12, 7, 40], [10, 20, 30], 5625, [45, -45, 90], [1.25, 0.75, 1])
    expected = {
        "core/move-snapped.3mf": [box([0, 20, 15], [20, 20, 20], 8000), box([40, 10, 10], [20, 20, 20], 8000)],
        "core/rotate-snapped.3mf": [core_box],
        "core/scale-snapped.3mf": [scaled_box],
        "core/scale-snapped.stl": [scaled_box],
        "desktop-checks/handle-move.3mf": [box([16, 10, 10], [20, 20, 20], 8000)],
        "desktop-checks/handle-rotate.3mf": [box([10, 10, 10], [20, 20, 20], 8000, [0, 0, 30])],
        "desktop-checks/handle-scale.3mf": [box([10, 10, 10], [25, 25, 25], 15625)],
    }
    ui_provenance = None
    if options.include_ui:
        expected.update({
            "ui/live-baseline.3mf": [box([10, 10, 10], [20, 20, 20], 8000)],
            "ui/live-scaled.3mf": [box([14, 10, 10], [25, 25, 25], 15625)],
        })
        observation_path = BASE / "ui/observations.json"
        observation_bytes = observation_path.read_bytes()
        observations = json.loads(observation_bytes)
        require(len(observations) >= 30, "Native walkthrough observation record is incomplete")
        ui_provenance = {
            "file": str(observation_path.relative_to(BASE)),
            "sha256": hashlib.sha256(observation_bytes).hexdigest(),
            "observation_count": len(observations),
            "first_observation_utc": observations[0]["utc"],
            "last_observation_utc": observations[-1]["utc"],
            "source": "Parent-reported native walkthrough provenance; this script independently measures output files and does not replay GUI actions.",
            "outputs": [
                {"file": "ui/live-baseline.3mf", "desktop_sha256": None, "core_sha256": LIVE_CORE_SHA256,
                 "producer_scope": "Generated during observations 1–8 by the prior layout build. Its desktop digest was not captured; do not attribute this file to the final desktop build."},
                {"file": "ui/live-scaled.3mf", "desktop_sha256": LIVE_SCALED_DESKTOP_SHA256, "core_sha256": LIVE_CORE_SHA256,
                 "producer_scope": "Final desktop build used for the move/apply, rotation/cancel, scale/apply, save and reopen walkthrough beginning at observation 9."},
            ],
        }
    model_hashes = {name: digest(BASE / name) for name in expected}
    producers = []
    for folder in ("core", "desktop-checks"):
        path = BASE / folder / "results.json"
        producer_bytes = path.read_bytes()
        metadata = json.loads(producer_bytes)
        artifacts = metadata["outputs" if folder == "core" else "artifacts"]
        files = {entry["file"]: entry["sha256"].lower() for entry in artifacts}
        for name, actual in model_hashes.items():
            if Path(name).parts[0] == folder:
                require(files.get(Path(name).name) == actual, f"Producer output hash mismatch: {name}; rerun its checks first")
        producers.append({"file": str(path.relative_to(BASE)), "sha256": hashlib.sha256(producer_bytes).hexdigest(),
                          "target": metadata.get("target"), "core_sha256": metadata.get("coreSha256"),
                          "desktop_sha256": metadata.get("desktopSha256"),
                          "method": metadata.get("method", metadata.get("observation"))})
    results = [measure(BASE / name, parts) for name, parts in expected.items()]
    _, native_scale = load_parts(BASE / "core/scale-snapped.3mf", True)
    _, stl_scale = load_parts(BASE / "core/scale-snapped.stl", True)
    native_to_stl = vertex_distance(stl_scale[0].vertices, native_scale[0].vertices)
    require(native_to_stl < STL_TOLERANCE_MM, "STL float32 vertex drift exceeds tolerance")
    inputs = []
    for folder in ("generated", "failures"):
        directory = ROOT / "fixtures" / folder
        for case in json.loads((directory / "manifest.json").read_text(encoding="utf-8"))["cases"]:
            actual = digest(directory / case["file"])
            require(actual == case["sha256"], f"Fixture changed: {folder}/{case['file']}")
            inputs.append({"file": f"fixtures/{folder}/{case['file']}", "sha256": actual})
    for name, original_hash in model_hashes.items():
        require(digest(BASE / name) == original_hash, f"Output regenerated during verification: {name}; rerun this script")
    for producer in producers:
        require(digest(BASE / producer["file"]) == producer["sha256"], "Producer report changed during verification; rerun this script")
    if ui_provenance is not None:
        require(digest(BASE / ui_provenance["file"]) == ui_provenance["sha256"], "Native observation JSON changed during verification; rerun this script")
        for item in ui_provenance["outputs"]:
            item["sha256"] = model_hashes[item["file"]]
    report = {"utc": datetime.now(timezone.utc).isoformat(),
              "observation": "Independent measurements of retained replacement-app model files; no GUI input or original-app observation",
              "tools": {"python": platform.python_version(), "trimesh": trimesh.__version__, "numpy": np.__version__},
              "verifier": {"file": str(Path(__file__).resolve().relative_to(ROOT)), "sha256": digest(Path(__file__).resolve())},
              "producer_reports": producers, "native_ui_provenance": ui_provenance, "outputs": results,
              "stl_maximum_symmetric_nearest_vertex_drift_from_native_mm": native_to_stl,
              "unchanged_inputs": inputs,
              "limits": [f"{len(results)} small analytic box outputs only; this verifier measures files and does not exercise live pointer/input, accessibility, large-model performance or original-app parity.",
                         "Raw importer index topology is recorded separately from processed vertex topology. STL repeats vertices per face and native 3MF may retain those source vertices; processed closure is not evidence of raw indexed closure.",
                         "Analytic corners and face planes, bounds, volume, outward faces, components and topology are checked; no general self-intersection/repair/printability or full 3MF conformance claim.",
                         "Native bounds/corners/faces use 1e-8 mm tolerance; STL uses 3e-6 mm and 0.003 mm³ absolute volume tolerance for float32 export."]}
    report_path = BASE / "output-verification.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    report_hash = digest(report_path)
    report_path.with_suffix(".json.sha256").write_text(f"{report_hash}  {report_path.name}\n", encoding="ascii")
    print(f"PASS: {len(results)} transform outputs; analytic corners/face planes, bounds, volume, components/topology; {len(inputs)} unchanged fixtures.")
    print(f"Maximum STL/native vertex drift: {native_to_stl:.12g} mm")
    print(f"Report: {report_path}\nSHA256: {report_hash}")


if __name__ == "__main__":
    main()
