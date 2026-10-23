"""Independently measure prototype outputs; never modifies synthetic inputs or model files."""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import trimesh

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--include-ui", action="store_true", help="Also require the two outputs from the documented live UI walkthrough")
    options = parser.parse_args()
    base = ROOT / ".local/evidence/prototype-20261018"
    expected = {
        "checks/scaled.3mf": ([30, 20, 20], [-5, 0, 0], 12000, 1),
        "checks/scaled.stl": ([30, 20, 20], [-5, 0, 0], 12000, 1),
        "checks/moved.stl": ([30, 20, 20], [10, 0, 0], 12000, 1),
        "checks/assembly.3mf": ([50, 20, 20], [0, 0, 0], 16000, 2),
        "checks/components.3mf": ([30, 20, 20], [10, 0, 0], 12000, 1),
    }
    if options.include_ui:
        for name in ("ui-scaled.3mf", "ui-scaled.stl"):
            expected[name] = ([30, 20, 20], [-5, 0, 0], 12000, 1)
    outputs = []
    for name, (extents, minimum, volume, instances) in expected.items():
        path = base / name
        scene = trimesh.load(path, force="scene", process=True)
        mesh = scene.to_mesh()
        assert np.allclose(mesh.extents, extents, atol=1e-8, rtol=0), name
        assert np.allclose(mesh.bounds[0], minimum, atol=1e-8, rtol=0), name
        assert abs(mesh.volume - volume) < 1e-7, name
        assert len(scene.graph.nodes_geometry) == instances, name
        assert mesh.is_watertight and mesh.is_winding_consistent, name
        outputs.append({
            "file": name, "sha256": digest(path), "bounds_mm": mesh.bounds.tolist(),
            "extents_mm": mesh.extents.tolist(), "volume_mm3": float(mesh.volume),
            "instances": instances, "triangles": len(mesh.faces),
            "watertight_after_vertex_processing": bool(mesh.is_watertight),
            "winding_consistent": bool(mesh.is_winding_consistent),
        })
    inputs = []
    for folder in ("generated", "failures"):
        directory = ROOT / "fixtures" / folder
        for case in json.loads((directory / "manifest.json").read_text())["cases"]:
            actual = digest(directory / case["file"])
            assert actual == case["sha256"], f"Input changed: {case['file']}"
            inputs.append({"file": f"fixtures/{folder}/{case['file']}", "sha256": actual})
    report = {
        "timestamp": datetime.now(timezone.utc).isoformat(), "trimesh_version": trimesh.__version__,
        "scope": "Analytic synthetic solids; processed vertex topology; not repair or general printability certification",
        "outputs": outputs, "unchanged_inputs": inputs,
    }
    report_path = base / "output-verification.json"
    report_path.write_text(json.dumps(report, indent=2) + "\n")
    print(f"PASS: {len(outputs)} outputs match dimensions, positions, volumes and instance counts; {len(inputs)} input hashes unchanged.")
    print(report_path)


if __name__ == "__main__":
    main()
