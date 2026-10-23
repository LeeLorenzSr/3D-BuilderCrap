"""Independently verify limit-sized saved/exported geometry against analytic boxes and original input vertices.

Topology is checked once per unique resource, then each affine instance is measured without global welding.
No Core library, healing, face removal, or reorientation is used.
"""
import argparse
import hashlib
import json
import platform
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

import numpy as np
import trimesh
from verify_fixtures import component_count

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / ".local/evidence/performance-20261019"
NS = "{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}"


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def exact_mesh(vertices, faces):
    vertices, inverse = np.unique(vertices, axis=0, return_inverse=True)
    return trimesh.Trimesh(vertices=vertices, faces=inverse[faces], process=False)


def box_measure(mesh, expected):
    require(np.allclose(mesh.bounds, expected, rtol=0, atol=1e-8), "World bounds differ from the analytic box")
    require(mesh.is_watertight and mesh.is_winding_consistent and mesh.euler_number == 2 and component_count(mesh.faces, len(mesh.vertices)) == 1,
            "Expected a closed outward genus-zero box shell")
    require(np.all(mesh.area_faces > 0), "Zero-area output triangle")
    require(len(np.unique(np.sort(mesh.faces, axis=1), axis=0)) == len(mesh.faces), "Duplicate output triangle")
    size = expected[1]-expected[0]
    volume = float(np.prod(size))
    require(np.isclose(mesh.volume, volume, rtol=1e-9), "Box volume differs")
    triangles = mesh.vertices[mesh.faces]
    normals = np.cross(triangles[:, 1]-triangles[:, 0], triangles[:, 2]-triangles[:, 0])
    residuals = np.stack([np.abs(triangles[:, :, axis]-expected[side, axis]).max(axis=1)
                          for axis in range(3) for side in range(2)], axis=1)
    plane = residuals.argmin(axis=1)
    residual = float(residuals.min(axis=1).max())
    require(residual < 1e-8, "Output triangle leaves an analytic box face")
    covered_areas = np.bincount(plane, weights=mesh.area_faces, minlength=6)
    for axis in range(3):
        area = float(np.prod(np.delete(size, axis)))
        require(np.allclose(covered_areas[axis*2:axis*2+2], area, rtol=1e-9, atol=1e-7), "Analytic face coverage differs")
        for side, direction in ((0, -1), (1, 1)):
            require(np.all(normals[plane == axis*2+side, axis]*direction > 0), "Output face points inward")
    distances = np.stack([np.abs(mesh.vertices[:, axis]-expected[side, axis])
                          for axis in range(3) for side in range(2)], axis=1)
    return {"vertices_welded": len(mesh.vertices), "triangles": len(mesh.faces), "bounds_mm": mesh.bounds.tolist(),
            "components": 1, "watertight": True, "winding_consistent": True, "signed_volume_mm3": float(mesh.volume),
            "max_vertex_analytic_surface_deviation_mm": float(distances.min(axis=1).max()),
            "max_triangle_analytic_face_residual_mm": residual, "face_areas_mm2": covered_areas.tolist()}


def read_3mf(path):
    with zipfile.ZipFile(path) as package:
        model = ET.fromstring(package.read("3D/3dmodel.model"))
    require(model.attrib.get("unit") == "millimeter", "Unexpected output units")
    resources = {e.attrib["id"]: e for e in model.find(NS+"resources")}
    meshes = {}
    for resource_id, resource in resources.items():
        element = resource.find(NS+"mesh")
        if element is None:
            continue
        vertices = np.array([[float(v.attrib[a]) for a in "xyz"] for v in element.find(NS+"vertices")])
        faces = np.array([[int(t.attrib[a]) for a in ("v1", "v2", "v3")] for t in element.find(NS+"triangles")])
        meshes[resource_id] = (exact_mesh(vertices, faces), len(vertices))

    def matrix(text):
        result = np.eye(4)
        if text:
            values = np.array([float(x) for x in text.split()])
            require(len(values) == 12 and np.all(np.isfinite(values)), "Invalid affine output transform")
            result[:3, :3], result[3, :3] = values[:9].reshape(3, 3), values[9:]
        return result

    instances, groups = [], []

    def expand(resource_id, transform, ancestors):
        require(resource_id not in ancestors, "Output component cycle")
        if resource_id in meshes:
            instances.append((resource_id, transform))
            return
        for component in resources[resource_id].find(NS+"components"):
            expand(component.attrib["objectid"], matrix(component.attrib.get("transform")) @ transform, ancestors+(resource_id,))

    for item in model.find(NS+"build"):
        resource_id = item.attrib["objectid"]
        start = len(instances)
        expand(resource_id, matrix(item.attrib.get("transform")), ())
        if resources[resource_id].find(NS+"components") is not None:
            groups.append({"name": resources[resource_id].attrib.get("name"), "member_indices": list(range(start, len(instances)))})
    return meshes, instances, groups, len(resources)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=BASE / "safety/outputs")
    parser.add_argument("--native-ui", action="store_true", help="Verify the separately saved native desktop +10 mm dense output")
    args = parser.parse_args()
    manifest = json.loads((BASE / "inputs/manifest.json").read_text(encoding="utf-8"))
    for case in manifest["original_inputs"]:
        require(hashlib.sha256((ROOT / case["file"]).read_bytes()).hexdigest() == case["sha256"], "Original input changed")
    for case in manifest["cases"]:
        require(hashlib.sha256((BASE / "inputs" / case["file"]).read_bytes()).hexdigest() == case["sha256"], "Performance input changed")
    original = trimesh.load_mesh(BASE / "inputs/dense-100000.stl", process=False)
    if args.native_ui:
        path = BASE / "ui/live-dense-moved.3mf"
        meshes, instances, groups, resource_count = read_3mf(path)
        require(len(instances) == len(meshes) == resource_count == 1 and not groups, "Native output object/resource counts differ")
        resource_id, transform = instances[0]
        mesh, raw_vertices = meshes[resource_id]
        mesh = exact_mesh(mesh.vertices @ transform[:3, :3] + transform[3, :3], mesh.faces)
        require(raw_vertices == 300000 and len(mesh.vertices) == 50002 and len(mesh.faces) == 100000, "Native output counts differ")
        measured = box_measure(mesh, np.array([[10, 0, 0], [110, 100, 100]], dtype=float))
        expected = np.unique(original.vertices, axis=0) + np.array([10, 0, 0])
        require(mesh.vertices.shape == expected.shape, "Native output vertex set changed size")
        deviation = float(np.linalg.norm(mesh.vertices - expected, axis=1).max())
        require(deviation < 1e-8, "Native move differs from the exact 10 mm translation")
        report = {"status": "passed", "python": platform.python_version(), "numpy": np.__version__, "trimesh": trimesh.__version__,
                  "file": str(path.relative_to(ROOT)), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                  "original_hashes_unchanged": len(manifest["original_inputs"]), "object_instances": 1, "raw_vertices": raw_vertices,
                  "max_source_vertex_deviation_mm": deviation, **measured,
                  "limitations": "Independent saved-geometry verification; native actions are recorded separately in ui/observations.json. Synthetic box only."}
        result = BASE / "native-output-verification.json"
        result.write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
        print("PASS native saved output: 1 object, 100000 triangles, exact +10 mm X translation, original11 unchanged")
        print("RESULT SHA256 "+hashlib.sha256(result.read_bytes()).hexdigest())
        return
    original_vertices = np.unique(original.vertices, axis=0)+np.array([12, 0, 0])
    expected_dense = np.array([[12, 0, 0], [112, 100, 100]], dtype=float)
    records = []
    for name in ("dense-moved.3mf", "dense-moved.stl"):
        path = args.directory / name
        if path.suffix == ".3mf":
            meshes, instances, groups, resource_count = read_3mf(path)
            require(len(instances) == len(meshes) == resource_count == 1 and not groups, "Dense native output changed object/resource counts")
            resource_id, transform = instances[0]
            mesh, raw_vertices = meshes[resource_id]
            world = mesh.vertices @ transform[:3, :3] + transform[3, :3]
            mesh = exact_mesh(world, mesh.faces)
        else:
            mesh = trimesh.load_mesh(path, process=False)
            raw_vertices = len(mesh.vertices)
            mesh = exact_mesh(mesh.vertices, mesh.faces)
        require(raw_vertices == 300000 and len(mesh.vertices) == 50002 and len(mesh.faces) == 100000, "Dense output limit counts differ")
        measured = box_measure(mesh, expected_dense)
        require(mesh.vertices.shape == original_vertices.shape, "Dense output vertex set changed size")
        vertex_deviation = float(np.linalg.norm(mesh.vertices-original_vertices, axis=1).max())
        require(vertex_deviation < 1e-8, "Dense transform moved a vertex beyond the exact 12 mm translation")
        records.append({"file": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "object_instances": 1,
                        "raw_vertices": raw_vertices, "max_source_vertex_deviation_mm": vertex_deviation, **measured})
    path = args.directory / "combined-member-moved.3mf"
    meshes, instances, groups, resource_count = read_3mf(path)
    require(len(instances) == 1000 and len(meshes) == 2 and resource_count == 3, "Combined output sharing/resource counts differ")
    require(groups == [{"name": "Performance assembly", "member_indices": list(range(1000))}], "Combined output group membership/name differs")
    unique_reports = {resource_id: box_measure(mesh, np.array([[0, 0, 0], [10, 10, 10]], dtype=float))
                      for resource_id, (mesh, _) in meshes.items()}
    total_faces, total_vertices, volume = 0, 0, 0.0
    minimum, maximum = np.full(3, np.inf), np.full(3, -np.inf)
    for i, (resource_id, transform) in enumerate(instances):
        offset = np.array([(i % 10)*15, ((i//10) % 10)*15, (i//100)*15], dtype=float)
        if i == 0:
            offset += [2, 3, 4]
        require(np.array_equal(transform[:3, :3], np.eye(3)) and np.array_equal(transform[3, :3], offset), "Combined member/sibling placement differs")
        mesh, raw_vertices = meshes[resource_id]
        require(len(mesh.faces) == (96 if i < 990 else 496), "Combined resource assignment differs")
        bounds = mesh.bounds+offset
        minimum, maximum = np.minimum(minimum, bounds[0]), np.maximum(maximum, bounds[1])
        total_faces += len(mesh.faces)
        total_vertices += raw_vertices
        volume += unique_reports[resource_id]["signed_volume_mm3"]
    require(total_faces == 100000 and total_vertices == 52000 and np.isclose(volume, 1000000), "Combined expanded counts/volume differ")
    require(np.array_equal([minimum, maximum], [[0, 0, 0], [145, 145, 145]]), "Combined aggregate bounds differ")
    records.append({"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "object_instances": 1000,
                    "components": 1000, "groups": groups, "resources": resource_count, "raw_vertices": total_vertices,
                    "triangles": total_faces, "bounds_mm": [minimum.tolist(), maximum.tolist()], "signed_volume_mm3": volume,
                    "unique_meshes": unique_reports, "max_vertex_analytic_surface_deviation_mm": 0.0,
                    "method": "Unique local meshes checked completely; every world transform checked against its independent analytic grid; disjoint instance bounds establish component count."})
    report = {"status": "passed", "python": platform.python_version(), "numpy": np.__version__, "trimesh": trimesh.__version__,
              "original_hashes_unchanged": len(manifest["original_inputs"]), "passed_files": len(records), "outputs": records,
              "limitations": ["Synthetic box surfaces and exact known transforms; no general repair/printability or native UI claim.", "Topology is verified per unique mesh; no global instance welding or healing."]}
    result = BASE / "output-verification.json"
    result.write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
    print(f"PASS {len(records)} limit-sized output files; 1002 object instances; 300000 triangles; original11 hashes unchanged")
    print("RESULT SHA256 "+hashlib.sha256(result.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
