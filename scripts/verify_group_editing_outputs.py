"""Independent analytic geometry and standard-group checks for group editing/selection export outputs."""
import argparse
import hashlib
import itertools
import json
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

import numpy as np
import trimesh

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / ".local/evidence/group-editing-20261019"
NS = {"c": "http://schemas.microsoft.com/3dmanufacturing/core/2015/02"}
TOLERANCE = 1e-8


def box(low, high):
    bounds = np.array([low, high], dtype=float)
    return {"bounds": bounds, "vertices": np.array(list(itertools.product(*zip(low, high))), dtype=float),
            "volume": float(np.prod(bounds[1] - bounds[0]))}


SOURCE = [box([0, 0, 0], [20, 20, 20]), box([30, 0, 0], [50, 20, 20]), box([75, -30, 20], [85, 0, 60])]
COPIES = [box([90, 0, 0], [110, 20, 20]), box([120, 0, 0], [140, 20, 20]), box([165, -30, 20], [175, 0, 60])]


def exact_weld(mesh):
    vertices, inverse = np.unique(mesh.vertices, axis=0, return_inverse=True)
    return trimesh.Trimesh(vertices=vertices, faces=inverse[mesh.faces], process=False)


def transform(points, text):
    if text is None:
        return points
    matrix = np.array([float(value) for value in text.split()])
    assert len(matrix) == 12 and np.all(np.isfinite(matrix))
    return points @ matrix[:9].reshape((3, 3)) + matrix[9:]


def group_member_bounds(model, definitions):
    by_id = {d.attrib["id"]: d for d in definitions}
    groups = []
    for item in model.findall("c:build/c:item", NS):
        definition = by_id[item.attrib["objectid"]]
        components = definition.find("c:components", NS)
        if components is None:
            continue
        members = []
        for component in components.findall("c:component", NS):
            mesh = by_id[component.attrib["objectid"]].find("c:mesh", NS)
            assert mesh is not None
            points = np.array([[float(v.attrib[axis]) for axis in "xyz"] for v in mesh.findall("c:vertices/c:vertex", NS)])
            points = transform(transform(points, component.attrib.get("transform")), item.attrib.get("transform"))
            members.append(np.array([points.min(axis=0), points.max(axis=0)]))
        groups.append(members)
    return groups


def same_bounds_set(actual, expected):
    if len(actual) != len(expected):
        return False
    return any(all(np.allclose(actual[i], expected[j]["bounds"], atol=TOLERANCE, rtol=0) for i, j in enumerate(order))
               for order in itertools.permutations(range(len(expected))))


def compare(mesh, expected):
    mesh = exact_weld(mesh)
    assert len(mesh.vertices) == 8 and len(mesh.faces) == 12
    assert mesh.is_watertight and mesh.is_winding_consistent and len(mesh.split()) == 1
    assert np.all(mesh.area_faces > 0) and len(np.unique(np.sort(mesh.faces, axis=1), axis=0)) == 12
    assert np.allclose(mesh.bounds, expected["bounds"], atol=TOLERANCE, rtol=0)
    assert abs(float(mesh.volume) - expected["volume"]) < 1e-7
    distances = np.linalg.norm(mesh.vertices[:, None, :] - expected["vertices"][None, :, :], axis=2)
    deviation = max(float(distances.min(axis=0).max()), float(distances.min(axis=1).max()))
    assert deviation < TOLERANCE
    areas = np.zeros((3, 2))
    residual = 0.0
    for triangle, area in zip(mesh.triangles, mesh.area_faces):
        distance, axis, side = min((float(np.abs(triangle[:, axis] - expected["bounds"][side, axis]).max()), axis, side)
                                   for axis in range(3) for side in range(2))
        assert distance < TOLERANCE
        residual = max(residual, distance)
        areas[axis, side] += area
    sizes = expected["bounds"][1] - expected["bounds"][0]
    for axis in range(3):
        assert np.allclose(areas[axis], np.prod(np.delete(sizes, axis)), atol=1e-7, rtol=0)
    return {"vertices": len(mesh.vertices), "triangles": len(mesh.faces), "bounds_mm": mesh.bounds.tolist(),
            "size_mm": mesh.extents.tolist(), "signed_volume_mm3": float(mesh.volume),
            "max_vertex_corner_deviation_mm": deviation, "max_triangle_boundary_plane_residual_mm": residual,
            "closed": bool(mesh.is_watertight), "winding_consistent": bool(mesh.is_winding_consistent)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exports-only", action="store_true", help="Verify selection-export files before group-editing outputs are available.")
    parser.add_argument("--include-desktop", action="store_true", help="Require and verify the isolated WPF tree-selection 3MF/STL exports.")
    parser.add_argument("--include-ui", action="store_true", help="Require and verify the live pair exports, saved whole scene and duplicated scene.")
    args = parser.parse_args()
    cases = [
        ("exports/whole-scene.3mf", SOURCE, 1),
        ("exports/selected-group.3mf", SOURCE[:2], 1),
        ("exports/selected-group.stl", SOURCE[:2], None),
        ("exports/selected-third.3mf", SOURCE[2:], 0),
        ("exports/explicit-all.3mf", SOURCE, 1),
        ("exports/dirty-selected-group.3mf", SOURCE[:2], 1),
        ("exports/last-good.3mf", SOURCE[:2], 1),
    ]
    if not args.exports_only:
        cases += [("core/renamed.3mf", SOURCE, 1), ("core/duplicated.3mf", SOURCE + COPIES, 2),
                  ("core/deleted.3mf", SOURCE, 1), ("core/bounded-copy-names.3mf", SOURCE + COPIES, 2)]
    if args.include_desktop:
        cases += [("desktop-checks/tree-selection.3mf", SOURCE[:2], 1), ("desktop-checks/tree-selection.stl", SOURCE[:2], None)]
    if args.include_ui:
        cases += [("ui/live-pair.3mf", SOURCE[:2], 1), ("ui/live-pair.stl", SOURCE[:2], None),
                  ("ui/live-scene.3mf", SOURCE, 1), ("ui/live-duplicated.3mf", SOURCE + COPIES, 2)]
    reports = []
    for name, expected, expected_groups in cases:
        path = BASE / name
        metadata = None
        if path.suffix == ".3mf":
            scene = trimesh.load_scene(path, process=False)
            objects = list(scene.dump())
            with zipfile.ZipFile(path) as package:
                model = ET.fromstring(package.read("3D/3dmodel.model"))
            definitions = model.findall("c:resources/c:object", NS)
            groups = [d for d in definitions if d.find("c:components", NS) is not None]
            assert len(groups) == expected_groups, name
            assert all(len(d.findall("c:components/c:component", NS)) == 2 for d in groups), name
            assert model.attrib.get("unit") == "millimeter", name
            actual_members = group_member_bounds(model, definitions)
            expected_members = [] if expected_groups == 0 else [SOURCE[:2]] if expected_groups == 1 else [SOURCE[:2], COPIES[:2]]
            assert len(actual_members) == len(expected_members), name
            assert any(all(same_bounds_set(actual_members[i], expected_members[j]) for i, j in enumerate(order))
                       for order in itertools.permutations(range(len(expected_members)))), "Group membership differs: " + name
            names = [d.attrib.get("name") for d in groups]
            if name in ("core/renamed.3mf", "core/deleted.3mf"):
                assert names == ["Print plate & brackets <A>"], names
            elif name == "core/duplicated.3mf":
                assert names == ["Print plate & brackets <A>", "Print plate & brackets <A> copy"], names
            elif name == "core/bounded-copy-names.3mf":
                assert names == ["x" * 94 + "😀yyyy", "x" * 94 + " copy"], names
            elif name.startswith("exports/") and groups:
                assert names == ["Export pair & brackets"], names
            elif name.startswith("desktop-checks/") and groups:
                assert names == ["Renamed tree assembly"], names
            elif name == "ui/live-duplicated.3mf":
                assert names == ["Morning print pair", "Morning print pair copy"], names
            elif name.startswith("ui/") and groups:
                assert names == ["Morning print pair"], names
            metadata = {"group_resources": len(groups), "mesh_resources": len(definitions) - len(groups),
                        "build_items": len(model.findall("c:build/c:item", NS)), "group_names": names}
        else:
            objects = list(exact_weld(trimesh.load_mesh(path, process=False)).split(only_watertight=False))
        assert len(objects) == len(expected), (name, len(objects))
        remaining = list(expected)
        measured = []
        for mesh in objects:
            index = min(range(len(remaining)), key=lambda i: float(np.abs(mesh.bounds - remaining[i]["bounds"]).max()))
            measured.append(compare(mesh, remaining.pop(index)))
        reports.append({"file": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        "object_instances": len(measured), "triangles": sum(o["triangles"] for o in measured),
                        "summed_signed_solid_volume_mm3": sum(o["signed_volume_mm3"] for o in measured),
                        "max_vertex_corner_deviation_mm": max(o["max_vertex_corner_deviation_mm"] for o in measured),
                        "format": metadata, "objects": measured})
    report = {"utc": datetime.now(timezone.utc).isoformat(), "trimesh_version": trimesh.__version__, "numpy_version": np.__version__,
              "method": "Independent analytic box corners/faces; per-instance exact welding only, no healing/deletion/reorientation",
              "mode": "exports-only" if args.exports_only else "group-editing-and-exports", "tolerance_mm": TOLERANCE,
              "include_desktop": args.include_desktop, "include_ui": args.include_ui,
              "passed_files": len(reports), "outputs": reports,
              "limits": "Analytic original boxes; no live desktop/Microsoft app observation or general self-intersection/printability certificate. Volumes are sums per solid instance."}
    result = BASE / ("export-output-verification.json" if args.exports_only else "output-verification.json")
    result.write_text(json.dumps(report, indent=2) + "\n")
    print(f"PASS: {len(reports)} files, {sum(r['object_instances'] for r in reports)} object instances, {sum(r['triangles'] for r in reports)} triangles; dimensions/topology/volume/vertex/face-plane checks.")
    print("RESULT SHA256 " + hashlib.sha256(result.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
