"""Measure the three original-app outputs from the documented first workflow.

This validates the analytic box cases only, not general repair or printability.
Uses trimesh to resolve scene transforms, independently of 3D Builder.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

import numpy as np
import trimesh


ROOT = Path(__file__).resolve().parent.parent
CORE = "{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}"


def inspect(path: Path) -> dict:
    scene = trimesh.load_scene(path)
    meshes = list(scene.dump())  # Applies the complete scene graph to each instance.
    result = {
        "file": path.name,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "bytes": path.stat().st_size,
        "world_bounds_mm": scene.bounds.tolist(),
        "instances": [],
    }
    for mesh in meshes:
        result["instances"].append({
            "bounds_mm": mesh.bounds.tolist(),
            "extents_mm": mesh.extents.tolist(),
            "signed_volume_mm3": float(mesh.volume),
            "triangles": len(mesh.faces),
            "watertight": bool(mesh.is_watertight),
            "winding_consistent": bool(mesh.is_winding_consistent),
            "connected_components": len(mesh.split(only_watertight=False)),
        })
    result["instances"].sort(key=lambda item: item["bounds_mm"][0][2])
    if path.suffix.lower() == ".3mf":
        with zipfile.ZipFile(path) as archive:
            model = ET.fromstring(archive.read("3D/3dmodel.model"))
        result["declared_unit"] = model.get("unit")
        result["resource_objects"] = [dict(obj.attrib) for obj in model.findall(f"{CORE}resources/{CORE}object")]
        result["build_items"] = [dict(item.attrib) for item in model.findall(f"{CORE}build/{CORE}item")]
        assert result["declared_unit"] == "millimeter", "This probe expects millimeter 3MF output"
    else:
        result["unit_interpretation"] = "millimeters, per the recorded app session; STL has no declared unit"
    return result


def verify_box(item: dict, bounds: list, volume: float, tolerance: float) -> None:
    np.testing.assert_allclose(item["bounds_mm"], bounds, rtol=0, atol=tolerance)
    np.testing.assert_allclose(item["signed_volume_mm3"], volume, rtol=0, atol=0.01)
    assert item["watertight"] and item["winding_consistent"]
    assert item["connected_components"] == 1


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=ROOT / ".local/evidence/workflow-20261018/outputs")
    args = parser.parse_args()
    names = ["scaled-30x20x20.3mf", "split-both.3mf", "undo-split-30x20x20.stl"]
    reports = [inspect(args.directory / name) for name in names]
    for report in (reports[0], reports[2]):
        assert len(report["instances"]) == 1
        verify_box(report["instances"][0], [[-5, 0, 0], [25, 20, 20]], 12000, 1e-6)
        assert report["instances"][0]["triangles"] == 12
    assert len(reports[1]["instances"]) == 2
    verify_box(reports[1]["instances"][0], [[-5, 0, 0], [25, 20, 10]], 6000, 1e-4)
    verify_box(reports[1]["instances"][1], [[-5, 0, 10], [25, 20, 20]], 6000, 1e-4)
    manifest = json.loads((ROOT / "fixtures/generated/manifest.json").read_text())
    for case in manifest["cases"]:
        original = ROOT / "fixtures/generated" / case["file"]
        assert hashlib.sha256(original.read_bytes()).hexdigest() == case["sha256"], f"Input changed: {original}"
    print(json.dumps({
        "schema_version": 1,
        "tool": {"trimesh": trimesh.__version__, "numpy": np.__version__},
        "target": "Microsoft.3DBuilder_20.0.4.0_x64__8wekyb3d8bbwe",
        "all_original_fixture_hashes_unchanged": True,
        "checks_passed": True,
        "tolerances": {"uncut_bounds_mm": 1e-6, "split_bounds_mm": 1e-4, "volume_mm3": 0.01},
        "limitations": ["Analytic box cases only; not a general self-intersection or repair-quality test.",
                        "Scene reading uses trimesh's default vertex processing.",
                        "No latency benchmark; UI action timestamps include investigator delays."],
        "outputs": reports,
    }, indent=2))


if __name__ == "__main__":
    main()
