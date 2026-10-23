"""Read-only geometry measurements of the independent repair review outputs.

Inputs and outputs belong to the prototype review, not the preserved original app.
Exact welding retains every face; finite surface samples are not an intersection certificate.
"""
import hashlib
import json
import math
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import trimesh

from verify_extended_outputs import topology

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / ".local/evidence/overnight-20261018/repair-review"


def load(path):
    raw = trimesh.load_mesh(path, process=False)
    vertices, inverse = np.unique(raw.vertices, axis=0, return_inverse=True)
    return raw, trimesh.Trimesh(vertices=vertices, faces=inverse[raw.faces], process=False)


def face_keys(mesh):
    # STL export intentionally stores float32 positions. Compare the same representation.
    points = np.asarray(mesh.vertices, dtype=np.float32)
    return {tuple(sorted(tuple(v) for v in points[face])) for face in mesh.faces}


def max_sample_distance(source, target):
    samples = np.vstack((source.vertices, source.triangles_center))
    maximum = 0.0
    for offset in range(0, len(samples), 64):
        _, distances, _ = trimesh.proximity.closest_point_naive(target, samples[offset:offset+64])
        maximum = max(maximum, float(distances.max()))
    return maximum


def main():
    reports = []
    for name, original, expected_faces, expected_bounds, expected_volume in (
        ("tetrahedron-repaired", "inverted-tetrahedron", 4, [[0, 0, 0], [30, 20, 10]], 1000),
        ("cylinder-256-repaired", "cylinder-open-256", 1022, [[-10, -10, 0], [10, 10, 20]],
         256 * 100 * math.sin(2 * math.pi / 256) * 10),
    ):
        path = BASE / (name + ".stl")
        raw, mesh = load(path)
        _, source = load(BASE / (original + ".stl"))
        welded = topology(mesh)
        assert len(mesh.faces) == expected_faces, name
        assert welded["zero_area_triangles"] == welded["duplicate_index_triangles"] == 0, name
        assert mesh.is_watertight and mesh.is_winding_consistent and len(mesh.split()) == 1, name
        assert np.allclose(mesh.bounds, expected_bounds, atol=1e-7, rtol=0), name
        assert abs(mesh.volume - expected_volume) < 1e-4, name
        assert face_keys(source).issubset(face_keys(mesh)), "A retained source face changed: " + name
        assert {tuple(v) for v in np.asarray(source.vertices, dtype=np.float32)} == {tuple(v) for v in mesh.vertices}, name
        if name.startswith("tetrahedron"):
            reference = source
        else:
            # An independent fan constructs the missing convex top. Geometry is the same
            # polygonal cylinder even though the implementation used ear clipping.
            vertices = source.vertices.tolist()
            top = np.flatnonzero(source.vertices[:, 2] == 20)
            top = top[np.argsort(np.arctan2(source.vertices[top, 1], source.vertices[top, 0]))]
            center = len(vertices)
            vertices.append([0, 0, 20])
            faces = source.faces.tolist()
            faces.extend([[center, int(a), int(b)] for a, b in zip(top, np.roll(top, -1))])
            reference = trimesh.Trimesh(vertices=vertices, faces=faces, process=False)
        deviation = max(max_sample_distance(mesh, reference), max_sample_distance(reference, mesh))
        assert deviation < 1e-6, (name, deviation)
        reports.append({"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                        "raw": topology(raw), "exact_welded": welded,
                        "bounds_mm": mesh.bounds.tolist(), "volume_mm3": float(mesh.volume),
                        "expected_volume_mm3": expected_volume,
                        "max_bidirectional_vertex_centroid_sample_mm": deviation,
                        "retained_source_faces": len(source.faces)})
    report = {"timestamp": datetime.now(timezone.utc).isoformat(), "trimesh_version": trimesh.__version__,
              "numpy_version": np.__version__, "preprocessing": "Exact welding; no face deletion or reorientation",
              "limitations": "Finite vertex/centroid samples, not exact Hausdorff bounds or general self-intersection certification. STL float32 export precision applies.",
              "outputs": reports}
    (BASE / "output-verification.json").write_text(json.dumps(report, indent=2) + "\n")
    print("PASS: 2 independent repair review outputs; dimensions, topology, volume, retained faces/vertices and finite surface samples.")


if __name__ == "__main__":
    main()
