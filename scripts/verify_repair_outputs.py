"""Read-only independent measurements of the repair slice and preserved original-app outputs."""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import trimesh

from verify_extended_outputs import surface_comparison, topology

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / ".local/evidence/repair-20261018"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--include-ui", action="store_true")
    args = parser.parse_args()
    names = [f"checks/{name}-repaired.{ext}" for name in
             ("missing-top", "inverted-cube", "duplicate-face", "degenerate-face") for ext in ("3mf", "stl")]
    names += ["checks/mixed-winding-repaired.3mf", "checks/concave-repaired.3mf"]
    if args.include_ui:
        names += ["ui-repaired.3mf", "ui-repaired.stl"]
    results = []
    for name in names:
        path = BASE / name
        scene = trimesh.load_scene(path, process=False)
        meshes = scene.dump()
        assert len(meshes) == 1, name
        raw = meshes[0]
        # Exact welding only. No face deletion, rounding, healing or winding correction.
        vertices, inverse = np.unique(raw.vertices, axis=0, return_inverse=True)
        mesh = trimesh.Trimesh(vertices=vertices, faces=inverse[raw.faces], process=False)
        report = topology(mesh)
        assert report["zero_area_triangles"] == report["duplicate_index_triangles"] == 0, name
        assert mesh.is_watertight and mesh.is_winding_consistent and len(mesh.split()) == 1, name
        assert np.allclose(mesh.bounds, [[0, 0, 0], [20, 20, 20]], atol=1e-10, rtol=0), name
        concave = "concave" in name
        assert abs(mesh.volume - (6000 if concave else 8000)) < 1e-8, name
        comparison = None if concave else surface_comparison(mesh, np.array([[0, 0, 0], [20, 20, 20]], dtype=float))
        if comparison:
            assert comparison["max_bidirectional_sample_mm"] < 1e-10, name
        results.append({"file": name, "sha256": digest(path), "raw": topology(raw),
                        "exact_welded": report, "bounds_mm": mesh.bounds.tolist(),
                        "volume_mm3": float(mesh.volume), "surface_comparison": comparison})
    unchanged = []
    for folder in ("generated", "failures"):
        directory = ROOT / "fixtures" / folder
        for case in json.loads((directory / "manifest.json").read_text())["cases"]:
            assert digest(directory / case["file"]) == case["sha256"], case["file"]
            unchanged.append({"file": f"fixtures/{folder}/{case['file']}", "sha256": case["sha256"]})
    # Locate only the research's named output, never treat our own files as original-app evidence.
    original = []
    for path in sorted((ROOT / ".local/evidence").glob("extended-*/outputs/missing-top-repaired.3mf")):
        raw = trimesh.load_scene(path, process=False).dump()[0]
        original.append({"file": str(path.relative_to(ROOT)), "sha256": digest(path),
                         "triangles": len(raw.faces), "volume_mm3": float(raw.volume),
                         "bounds_mm": raw.bounds.tolist(),
                         "surface_comparison": surface_comparison(raw, np.array([[0, 0, 0], [20, 20, 20]], dtype=float))})
    report = {"timestamp": datetime.now(timezone.utc).isoformat(), "trimesh_version": trimesh.__version__,
              "numpy_version": np.__version__, "preprocessing": "Exact vertex welding only; faces never removed or reoriented",
              "limits": "Finite surface samples, not exact Hausdorff bounds or general self-intersection certification",
              "outputs": results, "original_app_comparison": original, "unchanged_inputs": unchanged}
    (BASE / "output-verification.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"PASS: {len(results)} repair outputs; bounds, volume, winding, components, no duplicate/zero-area faces; {len(unchanged)} unchanged fixtures.")


if __name__ == "__main__":
    main()
