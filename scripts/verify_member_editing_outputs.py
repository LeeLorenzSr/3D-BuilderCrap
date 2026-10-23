"""Independent analytic geometry checks for scoped member editing outputs.

Only exact duplicate-vertex welding is applied; no healing, face deletion or reorientation.
"""
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

from verify_group_editing_outputs import box, exact_weld, group_member_bounds, same_bounds_set, NS

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / ".local/evidence/member-editing-20261019"


def compare(mesh, expected):
    mesh = exact_weld(mesh)
    assert mesh.is_watertight and mesh.is_winding_consistent and len(mesh.split()) == 1
    assert np.all(mesh.area_faces > 0)
    assert len(np.unique(np.sort(mesh.faces, axis=1), axis=0)) == len(mesh.faces)
    assert np.allclose(mesh.bounds, expected["bounds"], atol=1e-8, rtol=0)
    assert abs(float(mesh.volume) - expected["volume"]) < 1e-7
    distances = np.linalg.norm(mesh.vertices[:, None, :] - expected["vertices"][None, :, :], axis=2)
    corner_residual = float(distances.min(axis=0).max())
    assert corner_residual < 1e-8
    # Split/boolean tessellation may add vertices along an analytic face or edge.
    # Every triangle must lie on one expected boundary plane; each face must have full analytic area.
    assert np.all(mesh.vertices >= expected["bounds"][0] - 1e-8)
    assert np.all(mesh.vertices <= expected["bounds"][1] + 1e-8)
    areas = np.zeros((3, 2))
    residual = 0.0
    for triangle, area in zip(mesh.triangles, mesh.area_faces):
        distance, axis, side = min((float(np.abs(triangle[:, axis] - expected["bounds"][side, axis]).max()), axis, side)
                                   for axis in range(3) for side in range(2))
        assert distance < 1e-8
        residual = max(residual, distance)
        areas[axis, side] += area
    sizes = expected["bounds"][1] - expected["bounds"][0]
    for axis in range(3):
        assert np.allclose(areas[axis], np.prod(np.delete(sizes, axis)), atol=1e-7, rtol=0)
    return {"vertices": len(mesh.vertices), "triangles": len(mesh.faces), "bounds_mm": mesh.bounds.tolist(),
            "size_mm": mesh.extents.tolist(), "signed_volume_mm3": float(mesh.volume),
            "max_analytic_corner_residual_mm": corner_residual, "max_triangle_boundary_plane_residual_mm": residual,
            "closed": bool(mesh.is_watertight), "winding_consistent": bool(mesh.is_winding_consistent)}


def verify(name, expected, expected_groups):
    path = BASE / name
    metadata = None
    if path.suffix == ".3mf":
        objects = list(trimesh.load_scene(path, process=False).dump())
        with zipfile.ZipFile(path) as package:
            model = ET.fromstring(package.read("3D/3dmodel.model"))
        definitions = model.findall("c:resources/c:object", NS)
        groups = [d for d in definitions if d.find("c:components", NS) is not None]
        assert model.attrib.get("unit") == "millimeter", name
        assert len(groups) == len(expected_groups), (name, len(groups), len(expected_groups))
        actual_members = group_member_bounds(model, definitions)
        for actual, definition, (group_name, members) in zip(actual_members, groups, expected_groups):
            assert definition.attrib.get("name") == group_name, (name, definition.attrib.get("name"))
            assert same_bounds_set(actual, members), "Group membership differs: " + name
        metadata = {"groups": [{"name": d.attrib.get("name"), "members": len(m)} for d, m in zip(groups, actual_members)],
                    "build_items": len(model.findall("c:build/c:item", NS))}
    else:
        objects = list(exact_weld(trimesh.load_mesh(path, process=False)).split(only_watertight=False))
    assert len(objects) == len(expected), (name, len(objects), len(expected))
    remaining = list(expected)
    measured = []
    for mesh in objects:
        index = min(range(len(remaining)), key=lambda i: float(np.abs(mesh.bounds - remaining[i]["bounds"]).max()))
        measured.append(compare(mesh, remaining.pop(index)))
    return {"file": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "object_instances": len(measured), "triangles": sum(o["triangles"] for o in measured),
            "max_analytic_surface_residual_mm": max(o["max_triangle_boundary_plane_residual_mm"] for o in measured),
            "format": metadata, "objects": measured}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--include-ui", action="store_true", help="Require native walkthrough outputs.")
    args = parser.parse_args()
    moved = box([5, 0, 0], [25, 20, 20])
    sibling = box([30, 0, 0], [50, 20, 20])
    outsider = box([70, -25, 30], [90, -5, 50])
    cases = [
        ("desktop-checks/member-moved.3mf", [moved, sibling, outsider], [("Member assembly", [moved, sibling])]),
        ("desktop-checks/member-selection.3mf", [moved], []),
        ("desktop-checks/member-selection.stl", [moved], []),
    ]
    if args.include_ui:
        cases += [
            ("ui/live-member-selection.3mf", [moved], []),
            ("ui/live-member-scene.3mf", [moved, sibling, outsider], [("Member assembly", [moved, sibling])]),
        ]
    a = box([0, 0, 0], [20, 20, 20])
    b = sibling
    c = box([60, 0, 0], [80, 20, 20])
    other = box([105, -30, 20], [115, 0, 60])
    scaled = box([-10, 5, 0], [30, 15, 20])
    rotated = box([10, -5, 0], [20, 25, 20])
    moved_c = box([65, 0, 0], [85, 20, 20])
    copy = box([25, 0, 0], [45, 20, 20])
    split = [box([0, 0, 0], [20, 20, 10]), box([0, 0, 10], [20, 20, 20])]
    union = box([0, 0, 0], [30, 20, 20])
    def grouped(name, members):
        return (name, members + [other], [("Member assembly", members)])
    cases += [
        grouped("core/source.3mf", [a, b, c]),
        grouped("core/source-pair.3mf", [a, b]),
        grouped("core/member-scaled.3mf", [scaled, b, c]),
        grouped("core/member-rotated.3mf", [rotated, b, c]),
        grouped("core/members-moved.3mf", [moved, b, moved_c]),
        grouped("core/member-repaired.3mf", [a, b, c]),
        grouped("core/member-split.3mf", split + [b, c]),
        grouped("core/member-union.3mf", [union, c]),
        grouped("core/member-duplicated.3mf", [a, b, c, copy]),
        grouped("core/member-deleted.3mf", [b, c]),
        ("exports/member-one.3mf", [a], []),
        ("exports/member-one.stl", [a], []),
        ("exports/member-pair.3mf", [a, b], []),
        ("exports/last-good.3mf", [a, b], []),
        ("exports/member-whole-group.3mf", [a, b, c], [("Member assembly", [a, b, c])]),
        grouped("exports/whole-document-in-scope.3mf", [a, b, c]),
    ]
    reports = [verify(*case) for case in cases]
    inputs = []
    for directory in ("generated", "failures"):
        manifest = json.loads((ROOT / "fixtures" / directory / "manifest.json").read_text())
        for case in manifest["cases"]:
            file = ROOT / "fixtures" / directory / case["file"]
            digest = hashlib.sha256(file.read_bytes()).hexdigest()
            assert digest == case["sha256"], file
            inputs.append({"file": file.relative_to(ROOT).as_posix(), "sha256": digest})
    result = {"utc": datetime.now(timezone.utc).isoformat(), "trimesh_version": trimesh.__version__,
              "numpy_version": np.__version__, "tolerance_mm": 1e-8, "include_ui": args.include_ui,
              "method": "Independent analytic box corners/faces; exact vertex welding only; per-instance bounds, volume, closure, winding, nondegeneracy and surface coverage",
              "passed_files": len(reports), "unchanged_fixtures": inputs, "outputs": reports,
              "limits": "Small synthetic analytic boxes; not general repair, full 3MF conformance, native interaction or printability certification."}
    output = BASE / "output-verification.json"
    output.write_text(json.dumps(result, indent=2) + "\n")
    print(f"PASS: {len(reports)} files, {sum(r['object_instances'] for r in reports)} objects, {sum(r['triangles'] for r in reports)} triangles; {len(inputs)} fixture digests unchanged.")
    print("RESULT SHA256 " + hashlib.sha256(output.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
