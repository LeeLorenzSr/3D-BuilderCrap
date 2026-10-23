"""Check fixture hashes and analytic geometry using an independent mesh reader.

This verifies our input corpus, not 3D Builder behavior or general printability.
Install tools/verification-requirements.txt in an isolated Python environment.
"""
import hashlib
import argparse
import json
import platform
import struct
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

import numpy as np
import trimesh

ROOT = Path(__file__).resolve().parents[1]
FIXTURES = ROOT / "fixtures" / "generated"


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def component_count(faces, vertex_count):
    """Union-find on indexed triangle edges; no optional SciPy/network dependency."""
    parent = list(range(vertex_count))

    def find(index):
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    for a, b, c in faces:
        root = find(int(a))
        for index in (int(b), int(c)):
            other = find(index)
            if root != other:
                parent[other] = root
    return len({find(i) for i in range(vertex_count)})


def verify_performance():
    """Verify unique mesh topology once, then measure every transformed shared instance."""
    directory = ROOT / ".local/evidence/performance-20261019/inputs"
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    originals = []
    for case in manifest["original_inputs"]:
        digest = hashlib.sha256((ROOT / case["file"]).read_bytes()).hexdigest()
        require(digest == case["sha256"], f"Original input changed: {case['file']}")
        originals.append({"file": case["file"], "sha256": digest})
    require(len(originals) == 11, "Expected the unchanged nine ordinary and two failure inputs")
    ns = "{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}"
    records = []

    def topology(mesh, side):
        require(np.count_nonzero(mesh.area_faces == 0) == 0, "Zero-area triangle in valid grid box")
        mesh.merge_vertices()
        require(mesh.is_watertight and mesh.is_winding_consistent, "Grid box is not closed with consistent indexed winding")
        require(mesh.euler_number == 2 and mesh.volume > 0 and component_count(mesh.faces, len(mesh.vertices)) == 1, "Expected one outward genus-zero shell")
        require(np.allclose(mesh.bounds, [[0, 0, 0], [side, side, side]], atol=1e-7), "Wrong analytic box bounds")
        require(np.isclose(mesh.volume, side**3, rtol=1e-9), "Wrong analytic box volume")
        # Every triangle must lie in one analytic box plane and point outward there.
        points = mesh.vertices[mesh.faces]
        normals = np.cross(points[:, 1]-points[:, 0], points[:, 2]-points[:, 0])
        covered = np.zeros(len(mesh.faces), dtype=bool)
        for axis in range(3):
            for value, direction in ((0, -1), (side, 1)):
                on_plane = np.all(np.isclose(points[:, :, axis], value, atol=1e-7), axis=1)
                require(np.all(normals[on_plane, axis]*direction > 0), "Inward face on an analytic box plane")
                covered |= on_plane
        require(np.all(covered), "A triangle leaves the analytic box surface")
        return {"vertices_welded": len(mesh.vertices), "triangles": len(mesh.faces), "components": 1, "watertight": True,
                "winding_consistent": True, "euler_number": int(mesh.euler_number), "signed_volume_mm3": float(mesh.volume)}

    for case in manifest["cases"]:
        path = directory / case["file"]
        data = path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        require(digest == case["sha256"] and len(data) == case["bytes"], f"Performance input changed: {path}")
        record = {"file": path.name, "sha256": digest, "expected_core_acceptance": case["valid"]}
        if path.suffix == ".stl":
            declared = struct.unpack_from("<I", data, 80)[0]
            stored = (len(data)-84)//50
            if not case["valid"]:
                require((declared == 100001 and stored == 100001) if "over-triangles" in path.name
                        else (declared == 100000 and stored == 99999 and len(data) == 84+99999*50), "Wrong bounded negative STL fixture")
                record.update(declared_triangles=declared, stored_triangles=stored, rejection=case["rejection"])
            else:
                mesh = trimesh.load_mesh(path, process=False)
                raw_vertices = len(mesh.vertices)
                require(declared == stored == len(mesh.faces) == case["triangles"] and raw_vertices == case["raw_vertices"], "Dense STL raw counts differ")
                record.update(topology(mesh, 100))
                require(record["vertices_welded"] == case["welded_vertices"], "Dense welded vertex count differs")
                record.update(objects=1, raw_vertices=raw_vertices, bounds_mm=mesh.bounds.tolist())
        else:
            with zipfile.ZipFile(path) as archive:
                model = ET.fromstring(archive.read("3D/3dmodel.model"))
            require(model.attrib.get("unit") == "millimeter", "Performance 3MF unit differs")
            resources = {o.attrib["id"]: o for o in model.find(ns+"resources")}
            unique = {}
            for resource_id, resource in resources.items():
                element = resource.find(ns+"mesh")
                if element is None:
                    continue
                vertices = np.array([[float(v.attrib[a]) for a in "xyz"] for v in element.find(ns+"vertices")])
                faces = np.array([[int(f.attrib[a]) for a in ("v1", "v2", "v3")] for f in element.find(ns+"triangles")])
                mesh = trimesh.Trimesh(vertices=vertices, faces=faces, process=False)
                unique[resource_id] = (mesh, topology(mesh, 10))
            instances = []

            def transform(text):
                result = np.eye(4)
                if text:
                    values = np.array([float(v) for v in text.split()])
                    require(len(values) == 12 and np.all(np.isfinite(values)), "Malformed 3MF transform")
                    result[:3, :3], result[3, :3] = values[:9].reshape(3, 3), values[9:]
                return result

            def expand(resource_id, matrix, ancestors):
                require(resource_id not in ancestors, "Cyclic performance components")
                if resource_id in unique:
                    instances.append((resource_id, matrix))
                    return
                for component in resources[resource_id].find(ns+"components"):
                    expand(component.attrib["objectid"], transform(component.attrib.get("transform")) @ matrix, ancestors+(resource_id,))

            build = model.find(ns+"build")
            for item in build:
                expand(item.attrib["objectid"], transform(item.attrib.get("transform")), ())
            require(len(instances) == case["objects"], "Wrong expanded object count")
            faces_total = sum(len(unique[r][0].faces) for r, _ in instances)
            require(faces_total == case["triangles"], "Wrong expanded triangle count")
            record.update(objects=len(instances), triangles=faces_total, resources=len(resources), unique_meshes={k: value[1] for k, value in unique.items()})
            if case["valid"]:
                require(len(resources) == case["resources"], "Wrong shared resource count")
                vertices_total = sum(len(unique[r][0].vertices) for r, _ in instances)
                require(vertices_total == case["expanded_vertices"], "Wrong expanded vertex count")
                minimum, maximum = np.full(3, np.inf), np.full(3, -np.inf)
                volume = 0
                for i, (resource_id, matrix) in enumerate(instances):
                    require(np.allclose(matrix[:3, :3], np.eye(3)), "Unexpected fixture rotation/scale")
                    require(np.array_equal(matrix[3, :3], [(i % 10)*15, ((i//10) % 10)*15, (i//100)*15]), "Instance order or placement differs")
                    mesh = unique[resource_id][0]
                    points = mesh.vertices @ matrix[:3, :3] + matrix[3, :3]
                    minimum, maximum = np.minimum(minimum, points.min(axis=0)), np.maximum(maximum, points.max(axis=0))
                    volume += mesh.volume*np.linalg.det(matrix[:3, :3])
                require(np.allclose([minimum, maximum], case["bounds_mm"]), "Wrong combined world bounds")
                require(np.isclose(volume, case["summed_volume_mm3"], rtol=1e-9), "Wrong summed instance volume")
                groups = sum(resources[item.attrib["objectid"]].find(ns+"components") is not None for item in build)
                require(groups == case["groups"], "Wrong group count")
                if groups:
                    require(resources[build[0].attrib["objectid"]].attrib["name"] == case["group_name"], "Wrong component group name")
                record.update(expanded_vertices=vertices_total, bounds_mm=[minimum.tolist(), maximum.tolist()], groups=groups, signed_volume_mm3=float(volume))
            else:
                require(len(instances) == 1001, "Wrong bounded object-limit negative")
                record["rejection"] = case["rejection"]
        records.append(record)
    report = {"status": "passed", "observation": "Independent synthetic input verification; no application timing or UI claim", "python": platform.python_version(), "numpy": np.__version__, "trimesh": trimesh.__version__, "original_inputs": originals, "cases": records,
              "limitations": ["Unique meshes are checked once; shared instances are measured analytically rather than globally welded.", "No formal 3MF conformance validation or general printability claim."]}
    destination = directory.parent / "fixture-verification.json"
    destination.write_text(json.dumps(report, indent=2)+"\n", encoding="utf-8")
    print(f"PASS {len(records)} performance fixtures; {len(originals)} original hashes unchanged. Evidence: {destination}")


def main():
    manifest = json.loads((FIXTURES / "manifest.json").read_text(encoding="utf-8"))
    results = []
    loaded = {}
    for case in manifest["cases"]:
        path = FIXTURES / case["file"]
        require(hashlib.sha256(path.read_bytes()).hexdigest() == case["sha256"], f"Digest mismatch: {path}")
        if path.suffix == ".stl":
            # process=False preserves the deliberately duplicated/degenerate faces.
            mesh = trimesh.load_mesh(path, process=False)
            require(len(mesh.faces) == case["triangle_count"], f"Face count mismatch: {path}")
            mesh.merge_vertices()
            loaded[path.name] = mesh
            results.append({"file": path.name, "triangles": len(mesh.faces),
                            "extents_mm": mesh.extents.tolist(), "watertight": bool(mesh.is_watertight),
                            "winding_consistent": bool(mesh.is_winding_consistent),
                            "signed_volume_mm3": float(mesh.volume)})
        else:
            scene = trimesh.load_scene(path, process=False)
            require(len(scene.graph.nodes_geometry) == 2, f"Expected two build items: {path}")
            unit = next(iter(scene.geometry.values())).units
            require(unit in ("millimeter", "inch"), f"Unexpected unit: {unit}")
            scale = 25.4 if unit == "inch" else 1.0
            dimensions = scene.extents * scale
            require(np.allclose(dimensions, [50, 20, 20]), f"Wrong world-space dimensions: {path}: {dimensions}")
            results.append({"file": path.name, "build_items": 2, "unit": unit,
                            "extents_mm": dimensions.tolist()})
    cube = loaded["cube-20mm.stl"]
    require(cube.is_watertight and cube.is_winding_consistent, "Baseline must be closed and consistently wound")
    require(np.allclose(cube.extents, [20, 20, 20]) and np.isclose(cube.volume, 8000), "Wrong baseline dimensions/volume")
    hole = loaded["missing-top.stl"]
    counts = np.bincount(hole.edges_unique_inverse)
    require(not hole.is_watertight and np.count_nonzero(counts == 1) == 4, "Expected four open boundary edges")
    inverted = loaded["inverted-cube.stl"]
    require(inverted.is_watertight and np.isclose(inverted.volume, -8000), "Expected inward cube")
    duplicate = loaded["duplicate-face.stl"]
    require(len(np.unique(np.sort(duplicate.faces, axis=1), axis=0)) == 12, "Expected one duplicate face")
    degenerate = loaded["degenerate-face.stl"]
    require(np.count_nonzero(degenerate.area_faces == 0) == 1, "Expected one zero-area face")
    for name, extent in [("overlapping-cubes.stl", 30), ("separate-cubes.stl", 50)]:
        mesh = loaded[name]
        require(mesh.is_watertight and np.allclose(mesh.extents, [extent, 20, 20]), f"Wrong two-shell fixture: {name}")
        # 16000 is the sum of oriented shell volumes, not a union volume.
        require(np.isclose(mesh.volume, 16000), f"Wrong summed shell volume: {name}")
    print(json.dumps({"status": "passed", "trimesh": trimesh.__version__, "numpy": np.__version__,
                      "cases": results,
                      "limitations": ["No 3D Builder execution or output comparison.",
                                      "No general self-intersection, wall-thickness, or printability test.",
                                      "3MF import checked with trimesh; formal 3MF conformance validation remains pending."]}, indent=2))


def verify_survey_image():
    """Validate just the small UI-survey raster; no mesh or application tests."""
    import zlib
    path = ROOT / ".local/evidence/feature-survey-20261019/inputs/silhouette-ramp.png"
    data = path.read_bytes()
    require(data[:8] == b"\x89PNG\r\n\x1a\n", "Invalid PNG signature")
    position, chunks = 8, {}
    while position < len(data):
        size = struct.unpack_from(">I", data, position)[0]
        kind = data[position + 4:position + 8]
        body = data[position + 8:position + 8 + size]
        checksum = struct.unpack_from(">I", data, position + 8 + size)[0]
        require(zlib.crc32(kind + body) == checksum, "PNG chunk CRC mismatch")
        chunks[kind] = chunks.get(kind, b"") + body
        position += size + 12
    require(position == len(data) and b"IEND" in chunks, "PNG termination mismatch")
    require(struct.unpack(">IIBBBBB", chunks[b"IHDR"]) == (64, 64, 8, 0, 0, 0, 0),
            "Expected 64x64 non-interlaced grayscale PNG")
    rows = zlib.decompress(chunks[b"IDAT"])
    require(len(rows) == 64 * 65 and all(rows[y * 65] == 0 for y in range(64)),
            "Unexpected scanline data")
    pixel = lambda x, y: rows[y * 65 + x + 1]
    require(pixel(0, 0) == 255 and pixel(10, 10) == 0 and pixel(30, 50) == 0,
            "Expected white frame and black L")
    ramp = [pixel(x, 15) for x in range(30, 56)]
    require(ramp[::5] == [0, 51, 102, 153, 204, 255]
            and all(a < b for a, b in zip(ramp, ramp[1:])), "Expected grayscale ramp")
    print(json.dumps({"status": "passed", "python": platform.python_version(),
                      "file": path.relative_to(ROOT).as_posix(),
                      "sha256": hashlib.sha256(data).hexdigest(),
                      "dimensions_pixels": [64, 64],
                      "limit": "Input raster validation only; no accepted 3D Builder mesh measured."}, indent=2))


def verify_mirror_measurement():
    """Read and measure the three analytic mirror/ruler inputs, without application code."""
    directory = ROOT / ".local/evidence/mirror-measurement-20261020/inputs"
    manifest = json.loads((directory / "manifest.json").read_text())
    ns = "{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}"
    reports = []
    for case in manifest["cases"]:
        path = directory / case["file"]
        require(hashlib.sha256(path.read_bytes()).hexdigest() == case["sha256"], "Input hash mismatch")
        if path.suffix == ".stl":
            mesh = trimesh.load_mesh(path, process=True)
        else:
            with zipfile.ZipFile(path) as archive:
                model = ET.fromstring(archive.read("3D/3dmodel.model"))
            vertices = np.array([[float(v.get(a)) for a in "xyz"]
                                 for v in model.findall(f".//{ns}vertex")])
            faces = np.array([[int(t.get(a)) for a in ("v1", "v2", "v3")]
                              for t in model.findall(f".//{ns}triangle")])
            item = model.find(f"{ns}build/{ns}item")
            transform = np.array([float(n) for n in item.get("transform", "1 0 0 0 1 0 0 0 1 0 0 0").split()]).reshape(4, 3)
            determinant = float(np.linalg.det(transform[:3]))
            require(np.isclose(determinant, case["source_transform_determinant"]), "Wrong affine determinant")
            vertices = vertices @ transform[:3] + transform[3]
            if determinant < 0:
                faces = faces[:, [0, 2, 1]]
            mesh = trimesh.Trimesh(vertices=vertices, faces=faces, process=False)
        expected = np.array(case["world_vertices_mm"], dtype=float)
        residual = np.max(np.min(np.linalg.norm(mesh.vertices[:, None] - expected[None], axis=2), axis=1))
        require(len(mesh.vertices) == 4 and len(mesh.faces) == 4 and residual < 1e-9, "Wrong analytic vertices/counts")
        require(mesh.is_watertight and mesh.is_winding_consistent and mesh.euler_number == 2, "Wrong closed topology")
        require(component_count(mesh.faces, len(mesh.vertices)) == 1 and np.all(mesh.area_faces > 0), "Wrong component/face area")
        require(np.isclose(mesh.volume, case["canonical_volume_mm3"], rtol=1e-12), "Wrong outward analytic volume")
        require(np.allclose(mesh.bounds, [expected.min(axis=0), expected.max(axis=0)]), "Wrong bounds")
        reports.append({"file": case["file"], "sha256": case["sha256"], "bounds_mm": mesh.bounds.tolist(),
                        "volume_mm3": float(mesh.volume), "maximum_vertex_deviation_mm": float(residual),
                        "vertices": 4, "triangles": 4, "components": 1, "euler": 2})
    require(np.linalg.norm(np.array(manifest["vertices_mm"][3]) - manifest["vertices_mm"][2]) == 39, "Wrong reference ruler edge")
    report = {"status": "passed", "python": platform.python_version(), "numpy": np.__version__,
              "trimesh": trimesh.__version__, "reference_CD_distance_mm": 39, "cases": reports,
              "limitations": ["Input geometry only; no application behavior or output parity claim.",
                              "Reflected 3MF orientation measured after canonical determinant correction."]}
    (directory / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


def verify_text_emboss():
    """Measure the notched plate without using the replacement application."""
    directory = ROOT / ".local/evidence/text-emboss-20261020/inputs"
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    ns = "{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}"
    reports = []
    for case in manifest["cases"]:
        path = directory / case["file"]
        require(hashlib.sha256(path.read_bytes()).hexdigest() == case["sha256"], "Input hash mismatch")
        if path.suffix == ".stl":
            mesh = trimesh.load_mesh(path, process=True)
        else:
            with zipfile.ZipFile(path) as archive:
                model = ET.fromstring(archive.read("3D/3dmodel.model"))
            vertices = np.array([[float(v.get(a)) for a in "xyz"] for v in model.findall(f".//{ns}vertex")])
            faces = np.array([[int(t.get(a)) for a in ("v1", "v2", "v3")] for t in model.findall(f".//{ns}triangle")])
            mesh = trimesh.Trimesh(vertices=vertices, faces=faces, process=False)
        require(len(mesh.vertices) == 12 and len(mesh.faces) == 20, "Wrong plate counts")
        require(mesh.is_watertight and mesh.is_winding_consistent and mesh.euler_number == 2, "Wrong plate topology")
        require(component_count(mesh.faces, len(mesh.vertices)) == 1 and np.all(mesh.area_faces > 0), "Wrong plate component/area")
        require(np.allclose(mesh.bounds, [[0, 0, 0], [60, 32, 6]], atol=1e-12, rtol=0), "Wrong plate bounds")
        require(np.isclose(mesh.volume, (60 * 32 - 10 * 8) * 6, rtol=1e-12), "Wrong notched plate volume")
        expected = np.array(manifest["vertices_mm"])
        deviation = float(np.max(np.min(np.linalg.norm(mesh.vertices[:, None] - expected[None], axis=2), axis=1)))
        require(deviation == 0, "Wrong plate vertex positions")
        reports.append({"file": case["file"], "sha256": case["sha256"], "bounds_mm": mesh.bounds.tolist(),
                        "volume_mm3": float(mesh.volume), "triangles": len(mesh.faces), "components": 1,
                        "euler": 2, "maximum_vertex_deviation_mm": deviation})
    report = {"status": "passed", "python": platform.python_version(), "numpy": np.__version__,
              "trimesh": trimesh.__version__, "cases": reports, "limitations": ["Input geometry only."]}
    (directory / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


def verify_image_relief():
    """Independently parse the simple PNG/BMP encodings and check source semantics."""
    import zlib
    d = ROOT / ".local/evidence/image-relief-20261020/inputs"
    manifest = json.loads((d/"manifest.json").read_text(encoding="utf-8"))
    pixels = json.loads((d/"pixels.json").read_text(encoding="utf-8"))
    reports=[]
    for case in manifest["cases"]:
        data=(d/case["file"]).read_bytes()
        require(hashlib.sha256(data).hexdigest()==case["sha256"],"Raster hash changed")
        if case["expected"]=="reject malformed":
            require(data.startswith(b"\x89PNG\r\n\x1a\n") and len(data)==39,"Malformed fixture changed")
            reports.append(dict(case))
            continue
        if case["file"].endswith(".png"):
            require(data[:8]==b"\x89PNG\r\n\x1a\n","PNG signature")
            chunks={};position=8
            while position<len(data):
                n=struct.unpack_from(">I",data,position)[0];kind=data[position+4:position+8]
                body=data[position+8:position+8+n]
                require(zlib.crc32(kind+body)==struct.unpack_from(">I",data,position+8+n)[0],"PNG CRC")
                chunks[kind]=chunks.get(kind,b"")+body;position+=12+n
            w,h,depth,color,compression,filtering,interlace=struct.unpack(">IIBBBBB",chunks[b"IHDR"])
            require((depth,color,compression,filtering,interlace)==(8,6,0,0,0),"RGBA8 encoding")
            rows=zlib.decompress(chunks[b"IDAT"]);stride=w*4+1
            require(len(rows)==stride*h and all(rows[y*stride]==0 for y in range(h)),"PNG rows")
            rgba=[c for y in range(h) for c in rows[y*stride+1:(y+1)*stride]]
        else:
            require(data[:2]==b"BM","BMP signature")
            w,h=struct.unpack_from("<ii",data,18);bits=struct.unpack_from("<H",data,28)[0]
            require(bits==24 and struct.unpack_from("<I",data,30)[0]==0,"BMP24 encoding")
            offset=struct.unpack_from("<I",data,10)[0];stride=(w*3+3)//4*4;rgba=[]
            for y in reversed(range(h)):
                for x in range(w):
                    b,g,r=data[offset+y*stride+x*3:offset+y*stride+x*3+3];rgba.extend((r,g,b,255))
        oracle=pixels[case["source"]]
        require((w,h)==(oracle["width"],oracle["height"]) and rgba==oracle["rgba"],"Raster pixels differ")
        reports.append(dict(case,rgba_sha256=hashlib.sha256(bytes(rgba)).hexdigest()))
    def pixel(name,x,y):
        p=pixels[name];start=(y*p["width"]+x)*4;return p["rgba"][start:start+4]
    require(pixel("marker",6,6)==[255,255,255,255] and pixel("marker",6,44)==[0,0,0,255],"Asymmetric F")
    require(pixel("marker",36,38)==[255,0,0,255] and pixel("marker",45,38)==[0,255,0,255],"Color patches")
    require(pixel("alpha",0,0)==[255,255,255,0] and pixel("alpha",4,0)==[255,255,255,255],"Alpha endpoints")
    require(pixel("ramp",0,0)==[0,0,0,255] and pixel("ramp",8,4)==[255,255,255,255],"Ramp endpoints")
    report={"status":"passed","python":platform.python_version(),"cases":reports,
            "limitations":["Synthetic raster correctness only; production codec and geometry verified separately."]}
    (d/"verification.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"status":"passed","cases":len(reports),"report":str(d/"verification.json")}))


def verify_hollow():
    directory=ROOT/".local/evidence/hollow-20261020/inputs"
    manifest=json.loads((directory/"manifest.json").read_text(encoding="utf-8"))
    expected={"box-20":([20,20,20],8000),"concave-l":([30,30,20],10000),
              "narrow-bridge":([30,12,12],3480),"thin-plate":([20,20,2],800),"sphere-20":([20,20,20],None)}
    records=[]
    for item in manifest["cases"]:
        path=directory/item["file"]; digest=hashlib.sha256(path.read_bytes()).hexdigest()
        require(digest==item["sha256"],f"Hash changed: {path.name}")
        if path.suffix==".stl":
            mesh=trimesh.load_mesh(path,process=True)
        else:
            with zipfile.ZipFile(path) as archive: model=ET.fromstring(archive.read("3D/3dmodel.model"))
            ns={"m":"http://schemas.microsoft.com/3dmanufacturing/core/2015/02"}
            vv=[[float(v.attrib[a]) for a in "xyz"] for v in model.findall(".//m:vertices/m:vertex",ns)]
            ff=[[int(t.attrib[k]) for k in ("v1","v2","v3")] for t in model.findall(".//m:triangles/m:triangle",ns)]
            mesh=trimesh.Trimesh(vv,ff,process=False)
        require(mesh.is_watertight and mesh.is_winding_consistent and mesh.volume>0,"Expected a closed outward input")
        require(component_count(mesh.faces,len(mesh.vertices))==1 and mesh.euler_number==2,"Expected one genus-zero outer shell")
        require(len(mesh.faces)==item["triangles"] and len(mesh.vertices)==item["vertices"],"Unexpected input counts")
        require(np.all(mesh.area_faces>0),"Degenerate input triangle")
        size,volume=expected[path.stem]
        require(np.allclose(mesh.bounds,[[0,0,0],size],rtol=0,atol=2e-6),"Input physical bounds")
        if volume is not None: require(np.isclose(mesh.volume,volume,rtol=1e-9),"Input analytic material volume")
        else:
            require(np.max(np.abs(np.linalg.norm(mesh.vertices-10,axis=1)-10))<2e-6,"Sphere vertices on 10 mm radius")
            require(.96<mesh.volume/(4*np.pi*1000/3)<1,"Inscribed faceted sphere volume")
            require(np.all(np.einsum('ij,ij->i',mesh.face_normals,mesh.triangles_center-10)>0),"Sphere outward normals")
        records.append({**item,"sha256":digest,"bounds_mm":mesh.bounds.tolist(),"volume_mm3":float(mesh.volume),"components":1})
    report={"status":"passed","python":platform.python_version(),"numpy":np.__version__,"trimesh":trimesh.__version__,"cases":records,
            "limitations":["Original synthetic inputs only; no hollowing output or original-app behavior is established.","Sphere is a 320-face inscribed approximation, not an analytic sphere."]}
    (directory/"verification.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"status":"passed","cases":len(records),"report":str(directory/"verification.json")}))


def verify_simplify():
    directory=ROOT/".local/evidence/simplify-20261020/inputs"
    manifest=json.loads((directory/"manifest.json").read_text(encoding="utf-8"))
    expected={"dense-box":([20,20,20],[8000]),"dense-sphere":([20,20,20],None),
              "dense-torus":([28,28,8],None),"narrow-torus":([39.6,39.6,19.6],None),
              "hollow-box":([20,20,20],[8000,-4096]),"thin-hollow-box":([20,20,20],[8000,-19.6**3]),
              "hollow-sphere":([20,20,20],None),"thin-plate":([20,20,.5],[200]),
              "separate-spheres":([40.2,20,20],None),"small-secondary":([21.25,20,20],None)}
    records=[]
    for item in manifest["cases"]:
        path=directory/item["file"]; digest=hashlib.sha256(path.read_bytes()).hexdigest()
        require(digest==item["sha256"],f"Hash changed: {path.name}")
        if path.suffix==".stl":
            mesh=trimesh.load_mesh(path,process=True)
        else:
            with zipfile.ZipFile(path) as archive: model=ET.fromstring(archive.read("3D/3dmodel.model"))
            ns={"m":"http://schemas.microsoft.com/3dmanufacturing/core/2015/02"}
            vv=[[float(v.attrib[a]) for a in "xyz"] for v in model.findall(".//m:vertices/m:vertex",ns)]
            ff=[[int(t.attrib[k]) for k in ("v1","v2","v3")] for t in model.findall(".//m:triangles/m:triangle",ns)]
            mesh=trimesh.Trimesh(vv,ff,process=False)
        require(mesh.is_watertight and mesh.is_winding_consistent and mesh.volume>0,"Expected closed oriented material")
        require(len(mesh.faces)==item["triangles"] and len(mesh.vertices)==item["vertices"],"Input counts")
        require(np.all(mesh.area_faces>0),"Degenerate input triangle")
        size,volumes=expected[path.stem]
        require(np.allclose(mesh.bounds,[[0,0,0],size],rtol=0,atol=3e-6),"Input physical bounds")
        shells=mesh.split(only_watertight=False)
        count=2 if path.stem in ("hollow-box","thin-hollow-box","hollow-sphere","separate-spheres","small-secondary") else 1
        require(len(shells)==count,"Boundary component count")
        require(all(s.euler_number==(0 if "torus" in path.stem else 2) for s in shells),"Input genus")
        actual=sorted([float(s.volume) for s in shells],reverse=True)
        if volumes is not None: require(np.allclose(actual,volumes,rtol=2e-6,atol=2e-4),"Analytic signed boundary volumes")
        require(sum(v<0 for v in actual)==(1 if "hollow" in path.stem else 0),"Inward cavity boundary retained")
        records.append({**item,"sha256":digest,"bounds_mm":mesh.bounds.tolist(),"volume_mm3":float(mesh.volume),
                        "boundary_components":count,"signed_shell_volumes_mm3":actual,"shell_euler_numbers":[s.euler_number for s in shells]})
    report={"status":"passed","python":platform.python_version(),"numpy":np.__version__,"trimesh":trimesh.__version__,"cases":records,
            "limitations":["Synthetic input verification only; no simplifier or original-app behavior established.","Curved shapes are inscribed faceted surfaces; their saved triangles define the reference geometry."]}
    (directory/"verification.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"status":"passed","cases":len(records),"report":str(directory/"verification.json")}))


def verify_smooth():
    directory=ROOT/'.local/evidence/smooth-20261020/inputs'
    manifest=json.loads((directory/'manifest.json').read_text(encoding='utf-8'))
    records=[]
    for item in manifest['cases']:
        path=directory/item['file'];digest=hashlib.sha256(path.read_bytes()).hexdigest()
        require(digest==item['sha256'],f'Hash changed: {path.name}')
        if path.suffix=='.stl':mesh=trimesh.load_mesh(path,process=True)
        else:
            with zipfile.ZipFile(path) as archive:model=ET.fromstring(archive.read('3D/3dmodel.model'))
            ns={'m':'http://schemas.microsoft.com/3dmanufacturing/core/2015/02'}
            vv=[[float(v.attrib[a]) for a in 'xyz'] for v in model.findall('.//m:vertices/m:vertex',ns)]
            ff=[[int(t.attrib[k]) for k in ('v1','v2','v3')] for t in model.findall('.//m:triangles/m:triangle',ns)]
            mesh=trimesh.Trimesh(vv,ff,process=False)
        require(mesh.is_watertight and mesh.is_winding_consistent and mesh.volume>0,'Expected oriented closed material')
        require(len(mesh.faces)==item['triangles'] and len(mesh.vertices)==item['vertices'],'Unexpected input counts')
        require(np.all(mesh.area_faces>0),'Degenerate input')
        shells=mesh.split(only_watertight=False);signed=sorted([float(s.volume) for s in shells],reverse=True)
        expected_shells=2 if path.stem in ('noisy-hollow-sphere','thin-hollow-sphere','separate-noisy-solids') else 1
        require(len(shells)==expected_shells,'Boundary count')
        require(sum(v<0 for v in signed)==(1 if 'hollow' in path.stem else 0),'Cavity orientation')
        require(all(s.euler_number==(0 if 'torus' in path.stem else 2) for s in shells),'Boundary genus')
        params=manifest['analytic'][path.stem];quality=[]
        if params['kind'] in ('sphere','separate_spheres'):
            ordered=sorted(shells,key=lambda s:abs(s.volume),reverse=True)
            for i,shell in enumerate(ordered):
                center=params['center'] if params['kind']=='sphere' else params['centers'][i]
                residual=np.linalg.norm(shell.vertices-center,axis=1)-params['radii'][i]
                require(np.max(np.abs(residual))<=params['noise'][i]+3e-6,'Radial noise amplitude')
                quality.append(dict(radial_residual_rms_mm=float(np.sqrt(np.mean(residual**2))),
                                    radial_residual_std_mm=float(np.std(residual)),mean_radius_error_mm=float(np.mean(residual))))
        elif params['kind']=='torus':
            p=mesh.vertices-np.array(params['center'])
            residual=np.sqrt((np.linalg.norm(p[:,:2],axis=1)-params['major'])**2+p[:,2]**2)-params['tube']
            require(np.max(np.abs(residual))<=params['noise']+3e-6,'Tube noise amplitude')
            quality.append(dict(tube_residual_rms_mm=float(np.sqrt(np.mean(residual**2))),tube_residual_std_mm=float(np.std(residual))))
        else:
            require(np.allclose(mesh.bounds[:,0:2],[[0,0],[20,20]],rtol=0,atol=1e-6),'Box XY dimensions')
            require(mesh.bounds[0,2]>=-params['noise']-1e-6 and mesh.bounds[1,2]<=params['size'][2]+params['noise']+1e-6,'Box Z noise amplitude')
            if params['noise']==0:require(np.isclose(mesh.volume,8000),'Sharp box volume')
        records.append({**item,'sha256':digest,'bounds_mm':mesh.bounds.tolist(),'volume_mm3':float(mesh.volume),
                        'boundary_components':len(shells),'signed_shell_volumes_mm3':signed,'roughness_reference':quality})
    report=dict(status='passed',python=platform.python_version(),numpy=np.__version__,trimesh=trimesh.__version__,cases=records,
                limitations=['Synthetic inputs only. Radial/tube noise statistics are fixture-specific references, not a smoothing result.',
                             'Closed oriented boundary checks are not a general self-intersection proof.'])
    (directory/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(status='passed',cases=len(records),report=str(directory/'verification.json'))))


def verify_separate():
    directory = ROOT / '.local/evidence/separate-20261020/inputs'
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    expected = json.loads((directory / 'geometry.json').read_text(encoding='utf-8'))
    records = []
    for item in manifest['cases']:
        path = directory / item['file']
        require(hashlib.sha256(path.read_bytes()).hexdigest() == item['sha256'], f'Changed fixture: {path.name}')
        if path.suffix == '.stl':
            mesh = trimesh.load_mesh(path, process=True)
        else:
            with zipfile.ZipFile(path) as archive:
                model = ET.fromstring(archive.read('3D/3dmodel.model'))
            ns = {'m': 'http://schemas.microsoft.com/3dmanufacturing/core/2015/02'}
            vv = [[float(v.attrib[a]) for a in 'xyz'] for v in model.findall('.//m:vertices/m:vertex', ns)]
            ff = [[int(t.attrib[k]) for k in ('v1','v2','v3')] for t in model.findall('.//m:triangles/m:triangle', ns)]
            mesh = trimesh.Trimesh(vv, ff, process=False)
        spec = expected[path.stem]
        require(len(mesh.faces) == item['triangles'], 'Input face count')
        require(mesh.is_watertight and mesh.is_winding_consistent, 'Expected individually closed oriented boundaries')
        require(np.all(mesh.area_faces > 0), 'No degenerate input faces')
        reference = np.asarray(spec['vertices'])[np.asarray(spec['triangles'])]
        require(np.max(np.abs(mesh.triangles - reference)) < .001, 'Input physical coordinates')
        signed = []
        for ids in spec['shellTriangleIndices']:
            shell = trimesh.Trimesh(mesh.vertices, mesh.faces[ids], process=True)
            signed.append(float(shell.volume))
        pieces = []
        for ids in spec['expectedPieceTriangleIndices']:
            piece = trimesh.Trimesh(mesh.vertices, mesh.faces[ids], process=True)
            require(piece.is_watertight and piece.volume > 0, 'Expected closed positive material piece')
            pieces.append(dict(triangles=len(ids), volume_mm3=float(piece.volume), bounds_mm=piece.bounds.tolist()))
        records.append(dict(file=path.name, expectedOutcome=spec['expectedOutcome'],
                            signed_boundary_volumes_mm3=signed, pieces=pieces))
    report = dict(status='passed', cases=records, toleranceMillimeters=.001,
                  tools=dict(python=platform.python_version(), numpy=np.__version__, trimesh=trimesh.__version__),
                  limitations='Synthetic input check. Contact/orientation-invalid specimens are deliberately closed; closure alone is not material validity.')
    (directory / 'verification.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(status='passed', cases=len(records))))


def verify_extrude_down():
    directory = ROOT / '.local/evidence/extrude-down-20261020/inputs'
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    records = []
    ns = {'m': 'http://schemas.microsoft.com/3dmanufacturing/core/2015/02'}
    for item in manifest['cases']:
        path = directory / item['file']
        require(hashlib.sha256(path.read_bytes()).hexdigest() == item['sha256'], 'Fixture hash')
        with zipfile.ZipFile(path) as archive:
            model = ET.fromstring(archive.read('3D/3dmodel.model'))
        vv = [[float(v.attrib[a]) for a in 'xyz'] for v in model.findall('.//m:vertices/m:vertex', ns)]
        ff = [[int(t.attrib[k]) for k in ('v1', 'v2', 'v3')] for t in model.findall('.//m:triangles/m:triangle', ns)]
        mesh = trimesh.Trimesh(vv, ff, process=False)
        require(mesh.is_watertight and mesh.is_winding_consistent, 'Closed oriented boundaries')
        require(len(mesh.faces) == item['triangles'] and np.all(mesh.area_faces > 0), 'Face count and area')
        require(np.allclose(mesh.bounds, item['bounds_mm'], atol=.001, rtol=0), 'Physical bounds')
        require(abs(mesh.volume-item['volume_mm3']) < .01, 'Material volume including cavity')
        records.append(dict(file=path.name, bounds_mm=mesh.bounds.tolist(), volume_mm3=float(mesh.volume)))
    report = dict(status='passed', cases=records,
                  tools=dict(python=platform.python_version(), numpy=np.__version__, trimesh=trimesh.__version__),
                  limitations='Synthetic input validation; not an original-app behavior or repair-parity claim.')
    (directory / 'verification.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(status='passed', cases=len(records))))


def verify_materials():
    directory = ROOT / '.local/evidence/paint-materials-20261021/inputs'
    manifest = json.loads((directory/'manifest.json').read_text(encoding='utf-8'))
    ns = '{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}'
    records = []
    for item in manifest['cases']:
        path = directory/item['file']
        require(hashlib.sha256(path.read_bytes()).hexdigest() == item['sha256'], 'Fixture hash')
        with zipfile.ZipFile(path) as archive: model = ET.fromstring(archive.read('3D/3dmodel.model'))
        resources = model.find(ns+'resources')
        definitions = {e.get('id'): e for e in resources.findall(ns+'object')}
        meshes = []
        def expand(identifier, outer):
            definition = definitions[identifier]; mesh = definition.find(ns+'mesh')
            if mesh is not None:
                vv = [[float(v.get(a)) for a in 'xyz'] for v in mesh.findall(ns+'vertices/'+ns+'vertex')]
                ff = [[int(t.get(k)) for k in ('v1','v2','v3')] for t in mesh.findall(ns+'triangles/'+ns+'triangle')]
                shape = trimesh.Trimesh(vv, ff, process=False); shape.apply_transform(outer)
                require(shape.is_watertight and shape.is_winding_consistent, 'Analytic box topology')
                require(abs(shape.volume-8000) < .001, 'Analytic box volume')
                meshes.append(shape)
            else:
                for child in definition.findall(ns+'components/'+ns+'component'):
                    expand(child.get('objectid'), outer @ transform(child.get('transform')))
        def transform(value):
            result = np.eye(4)
            if value:
                numbers = np.array(list(map(float, value.split()))).reshape(4,3)
                result[:3, :3] = numbers[:3].T; result[:3,3] = numbers[3]
            return result
        for build in model.findall(ns+'build/'+ns+'item'):
            expand(build.get('objectid'), transform(build.get('transform')))
        combined = trimesh.util.concatenate(meshes)
        require(len(meshes) == 3 and len(combined.faces) == 36, 'Three box assembly')
        require(np.allclose(combined.bounds, manifest['bounds_mm'], atol=.001, rtol=0), 'Physical dimensions')
        require(abs(combined.volume-manifest['volume_mm3']) < .001, 'Assembly volume')
        materials = resources.find(ns+'basematerials')
        first = definitions['2']; name = path.stem
        if item['expected'] == 'accept':
            require(len(set(e.get('id') for e in resources)) == len(resources), 'Unique resource IDs')
            for identifier, expected in zip(('2','3','4'), manifest['leafMaterials']):
                obj = definitions[identifier]
                if expected is None: require(obj.get('pid') is None, 'Unassigned material')
                else:
                    entry = materials[int(obj.get('pindex'))]
                    require(obj.get('pid') == materials.get('id') and entry.get('name') == expected[0]
                            and entry.get('displaycolor')[:7] == expected[1], 'Named object color')
        elif name == 'transparent-material': require(materials[0].get('displaycolor').endswith('80'), 'Alpha refusal specimen')
        elif name == 'triangle-material': require(first.find('.//'+ns+'triangle').get('p1') == '1', 'Face property refusal specimen')
        elif name == 'missing-material': require(first.get('pid') == '99', 'Missing material specimen')
        elif name == 'duplicate-resource': require(materials.get('id') == first.get('id'), 'Resource collision specimen')
        records.append(dict(**item, triangles=36, bounds_mm=combined.bounds.tolist(), volume_mm3=float(combined.volume)))
    report = dict(status='passed', cases=records,
                  tools=dict(python=platform.python_version(), numpy=np.__version__, trimesh=trimesh.__version__),
                  limitations='Independent fixture validation; rejection labels are intended product behavior, not observed import results.')
    (directory/'verification.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(status='passed', cases=len(records))))


def verify_surface_paint():
    """Independently inspect explicit fixture colors and analytic box geometry."""
    directory = ROOT / '.local/evidence/surface-paint-20261021/inputs'
    manifest = json.loads((directory/'manifest.json').read_text(encoding='utf-8'))
    ns = '{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}'
    records = []
    for case in manifest['cases']:
        path = directory/case['file']
        require(hashlib.sha256(path.read_bytes()).hexdigest() == case['sha256'], 'Input hash changed')
        with zipfile.ZipFile(path) as archive: model = ET.fromstring(archive.read('3D/3dmodel.model'))
        res = model.find(ns+'resources'); objs = res.findall(ns+'object')
        palette = res.find(ns+'basematerials')
        require([e.get('displaycolor') for e in palette] == ['#F3F3F0FF', '#D65248FF', '#267CB5FF'], 'Palette')
        name = path.stem
        for index, obj in enumerate(objs):
            mesh = obj.find(ns+'mesh')
            if mesh is None: continue
            vertices = [[float(v.get(a)) for a in 'xyz'] for v in mesh.findall(ns+'vertices/'+ns+'vertex')]
            triangles = mesh.findall(ns+'triangles/'+ns+'triangle')
            faces = [[int(t.get(a)) for a in ('v1', 'v2', 'v3')] for t in triangles]
            shape = trimesh.Trimesh(vertices, faces, process=False)
            require(shape.is_watertight and shape.is_winding_consistent and abs(shape.volume-8000) < .001, 'Box shape')
            require(np.allclose(shape.bounds, [[0,0,0], [20,20,20]], atol=.001, rtol=0), 'Box dimensions')
            if name == 'missing-default':
                require(obj.get('pid') is None and obj.get('pindex') is None and triangles[6].get('pid') == '1', 'Missing-default specimen')
            elif name == 'gradient':
                require(triangles[2].get('p1') == '1' and triangles[2].get('p2') == '2' and triangles[2].get('p3') == '1', 'Gradient specimen')
            else:
                require(obj.get('pid') == '1' and obj.get('pindex') == '0', 'Explicit default')
                actual = [int(t.get('p1', obj.get('pindex'))) for t in triangles]
                expected = [0]*12
                for face in ((2,3) if index == 0 else ()): expected[face] = 1
                for face in ((6,7) if index == 0 else (4,)): expected[face] = 2
                require(actual == expected, 'Triangle colors')
                require(all(t.get('p2', t.get('p1')) == t.get('p1') and t.get('p3', t.get('p1')) == t.get('p1') for t in triangles), 'Uniform base materials')
        if name == 'surface-group':
            group = objs[-1]; members = group.findall(ns+'components/'+ns+'component')
            require(len(members) == 2 and group.get('pid') is None and members[1].get('transform') == '1 0 0 0 1 0 0 0 1 30 0 0', 'Group ownership and spacing')
        records.append(dict(**case, passed=True))
    report = dict(status='passed', tools=dict(python=platform.python_version(), numpy=np.__version__, trimesh=trimesh.__version__), cases=records,
                  limitations='Synthetic inputs only. Import, paint transactions and native rendering are verified separately.')
    (directory/'verification.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(status='passed', cases=len(records))))


def verify_image_contour():
    """Independent PNG decoding and semantic checks for original contour inputs."""
    import zlib
    directory = ROOT / ".local/evidence/image-contour-20261021/inputs"
    manifest = json.loads((directory/"manifest.json").read_text(encoding="utf-8"))
    pixels = json.loads((directory/"pixels.json").read_text(encoding="utf-8"))
    reports = []
    for case in manifest["cases"]:
        data = (directory/case["file"]).read_bytes()
        require(hashlib.sha256(data).hexdigest()==case["sha256"],"Contour input hash changed")
        if case["expected"]=="reject malformed":
            require(data==b"not an image\n","Malformed input changed")
            reports.append(dict(case))
            continue
        require(data[:8]==b"\x89PNG\r\n\x1a\n","PNG signature")
        chunks = {}; position = 8
        while position < len(data):
            size = struct.unpack_from(">I",data,position)[0]; kind = data[position+4:position+8]
            body = data[position+8:position+8+size]
            require(zlib.crc32(kind+body)==struct.unpack_from(">I",data,position+8+size)[0],"PNG CRC")
            chunks[kind] = chunks.get(kind,b"")+body; position += size+12
        w,h,depth,color,compression,filtering,interlace = struct.unpack(">IIBBBBB",chunks[b"IHDR"])
        require((depth,color,compression,filtering,interlace)==(8,6,0,0,0),"RGBA8 encoding")
        rows = zlib.decompress(chunks[b"IDAT"]); stride = 4*w+1
        require(len(rows)==stride*h and all(rows[y*stride]==0 for y in range(h)),"PNG rows")
        rgba = [v for y in range(h) for v in rows[y*stride+1:(y+1)*stride]]
        oracle = pixels[case["source"]]
        require((w,h)==(oracle["width"],oracle["height"]) and rgba==oracle["rgba"],"Decoded pixels differ")
        reports.append(dict(case,rgba_sha256=hashlib.sha256(bytes(rgba)).hexdigest()))
    def p(name,x,y):
        item=pixels[name]; i=4*(y*item["width"]+x); return item["rgba"][i:i+4]
    require(sum(pixels["ring-island"]["rgba"][::4])//255==69,"Ring foreground count")
    require(p("ring-island",1,1)==[255]*4 and p("ring-island",5,5)==[0,0,0,255],"Ring/hole")
    require(p("ring-island",13,2)==[255]*4 and p("ring-island",13,9)==[0,0,0,255],"Asymmetric island")
    require([p("levels",x,0)[0] for x in (0,2,4,6)]==[0,64,128,192],"Threshold levels")
    require([p("alpha",x,0)[3] for x in (0,2,4,6)]==[255,128,0,0],"Alpha levels")
    require(p("diagonal",0,0)==p("diagonal",1,1)==[255]*4 and p("diagonal",0,1)==[0,0,0,255],"Point-contact input")
    report = dict(status="passed",python=platform.python_version(),cases=reports,
                  limitations="Input pixels only; contour geometry, application decoding and native interaction verified separately.")
    (directory/"verification.json").write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
    print(json.dumps(dict(status="passed",cases=len(reports),report=str(directory/"verification.json"))))


def verify_obj():
    directory = ROOT / ".local/evidence/obj-20261021/inputs"
    manifest = json.loads((directory/'manifest.json').read_text(encoding='utf-8'))
    for case in manifest['cases']:
        require(hashlib.sha256((directory/case['file']).read_bytes()).hexdigest() == case['sha256'],case['file']+' hash')
    reports = []
    for name in ('assembly-mm.obj','assembly-in.obj','hollow-mm.obj'):
        expected = manifest['expectations'][name]
        mesh = trimesh.load(directory/name,force='mesh',process=False)
        if expected['units'] == 'Inches': mesh.apply_scale(25.4)
        mesh.merge_vertices()
        require(len(mesh.faces)==expected['triangles'],name+' triangles')
        require(np.allclose(mesh.bounds,expected['bounds_mm'],atol=0.001,rtol=0),name+' bounds')
        require(abs(mesh.volume-expected['volume_mm3'])<0.001,name+' signed material volume')
        require(mesh.is_watertight and mesh.is_winding_consistent,name+' closed oriented shells')
        reports.append(dict(file=name,bounds_mm=mesh.bounds.tolist(),triangles=len(mesh.faces),volume_mm3=float(mesh.volume),
                            shells=component_count(mesh.faces,len(mesh.vertices))))
    require(reports[2]['shells']==2,'hollow outer and inner shells')
    lines = (directory/'concave-mm.obj').read_text().splitlines()
    vertices = np.array([[float(x) for x in line.split()[1:]] for line in lines if line.startswith('v ')])
    refs = next(line.split()[1:] for line in lines if line.startswith('f '))
    require(all(len(ref.split('/'))==3 for ref in refs),'concave UV/normal reference form')
    polygon = vertices[[int(ref.split('/')[0])-1 for ref in refs]]
    area = float(np.sum(polygon[:,0]*np.roll(polygon[:,1],-1)-polygon[:,1]*np.roll(polygon[:,0],-1))/2)
    require(area==5 and np.all(polygon[:,2]==0),'analytic concave input area/planarity')
    reports.append(dict(file='concave-mm.obj',polygon_vertices=6,area_mm2=area,expected_triangles=4))
    require('f 1 2 99' in (directory/'bad-index.obj').read_text(),'invalid face fixture')
    require('map_Kd unavailable.png' in (directory/'texture.mtl').read_text(),'texture refusal fixture')
    report = dict(status='passed',python=platform.python_version(),trimesh=trimesh.__version__,numpy=np.__version__,cases=reports,
        limitations='Independent fixture geometry/hash check. Application object grouping and diffuse color ownership are verified separately.')
    (directory/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(status='passed',cases=len(reports),report=str(directory/'verification.json'))))


def verify_ply():
    directory = ROOT / ".local/evidence/ply-20261021/inputs"
    manifest = json.loads((directory/'manifest.json').read_text(encoding='utf-8'))
    for case in manifest['cases']:
        require(hashlib.sha256((directory/case['file']).read_bytes()).hexdigest()==case['sha256'],case['file']+' hash')
    reports = []
    for name in ('assembly-mm.ply','assembly-in-le.ply','assembly-in-be.ply','hollow-mm.ply'):
        expected = manifest['expectations'][name]
        mesh = trimesh.load(directory/name,process=False)
        raw = mesh.metadata['_ply_raw']['face']['data']
        colors = np.column_stack([np.asarray(raw[c]).ravel() for c in ('red','green','blue')])
        # Input faces are all quads for assemblies and triangles for the hollow mesh.
        per_polygon = 2 if name.startswith('assembly') else 1
        actual = {','.join(map(str,c)): int(np.sum(np.all(colors==c,axis=1)))*per_polygon for c in np.unique(colors,axis=0)}
        require(actual==expected['face_rgb_counts'],name+' declared face colors')
        if expected['units']=='Inches': mesh.apply_scale(25.4)
        mesh.merge_vertices()
        require(len(mesh.faces)==expected['triangles'],name+' triangles')
        require(np.allclose(mesh.bounds,expected['bounds_mm'],atol=.001,rtol=0),name+' bounds')
        require(abs(mesh.volume-expected['volume_mm3'])<.001,name+' material volume')
        require(mesh.is_watertight and mesh.is_winding_consistent,name+' closed oriented shells')
        require(component_count(mesh.faces,len(mesh.vertices))==expected['shells'],name+' shell count')
        reports.append(dict(file=name,bounds_mm=mesh.bounds.tolist(),triangles=len(mesh.faces),
            volume_mm3=float(mesh.volume),face_rgb_counts=actual,shells=expected['shells']))
    mesh = trimesh.load(directory/'vertex-colors.ply',process=False)
    colors = mesh.visual.vertex_colors[:,:3].astype(float)
    require(np.array_equal(np.floor(colors.mean(axis=0)+.5),[85,85,85]),'vertex color arithmetic mean')
    require(abs(mesh.area-4.5)<.001,'vertex triangle area')
    reports.append(dict(file='vertex-colors.ply',average_rgb=[85,85,85],area_mm2=float(mesh.area)))
    mesh = trimesh.load(directory/'concave-mm.ply',process=False)
    raw = mesh.metadata['_ply_raw']
    vv = np.column_stack([np.asarray(raw['vertex']['data'][c]).ravel() for c in ('x','y','z')])
    indices = np.asarray(raw['face']['data']['vertex_indices'])[0]
    polygon = vv[indices]
    area = float(np.sum(polygon[:,0]*np.roll(polygon[:,1],-1)-polygon[:,1]*np.roll(polygon[:,0],-1))/2)
    require(area==5 and np.all(polygon[:,2]==0),'concave polygon source area/planarity')
    reports.append(dict(file='concave-mm.ply',area_mm2=area,expected_triangles=4))
    report = dict(status='passed',python=platform.python_version(),trimesh=trimesh.__version__,numpy=np.__version__,cases=reports,
        limitations='Independent input geometry and declared color checks. Trimesh chooses vertex color over face color when both exist; this check reads declared face RGB directly. Successor precedence, triangulation and transaction behavior are verified separately.')
    (directory/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(status='passed',cases=len(reports),report=str(directory/'verification.json'))))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--performance", action="store_true", help="Verify opt-in .local limit inputs without altering the ordinary corpus")
    mode.add_argument("--survey-image", action="store_true", help="Verify only the original small UI-survey PNG")
    mode.add_argument("--mirror-measurement", action="store_true", help="Verify only the three analytic mirror/ruler inputs")
    mode.add_argument("--text-emboss", action="store_true", help="Verify only the analytic notched label plate")
    mode.add_argument("--image-relief", action="store_true", help="Verify only original image relief raster fixtures")
    mode.add_argument("--hollow", action="store_true", help="Verify only original hollowing solids under .local")
    mode.add_argument("--simplify", action="store_true", help="Verify only original simplification surfaces under .local")
    mode.add_argument("--smooth", action="store_true", help="Verify only original noisy smoothing surfaces under .local")
    mode.add_argument("--separate", action="store_true", help="Verify only disconnected material/cavity inputs under .local")
    mode.add_argument("--extrude-down", action="store_true", help="Verify the three small downward-extension solids")
    mode.add_argument("--materials", action="store_true", help="Verify the colored assembly and explicit unsupported-material specimens")
    mode.add_argument("--image-contour", action="store_true", help="Verify original contour silhouette, threshold and alpha rasters")
    mode.add_argument("--surface-paint", action="store_true", help="Verify per-triangle color boxes and refusal inputs")
    mode.add_argument("--obj", action="store_true", help="Verify the original OBJ/MTL unit and cavity inputs")
    mode.add_argument("--ply", action="store_true", help="Verify the original ASCII/binary PLY unit, color and cavity inputs")
    mode.add_argument("--gltf", action="store_true", help="Verify the original glTF/GLB unit, transform and cavity inputs")
    mode.add_argument("--distribution", action="store_true", help="Verify the original colored group/unequal-box distribution input")
    mode.add_argument("--center-on-plate", action="store_true", help="Verify the elevated colored group used for centering and bed placement")
    mode.add_argument("--surface-brush", action="store_true", help="Verify subdivided boxes and sealed brush-painting cavity ownership")
    args = parser.parse_args()
    if args.surface_brush:
        from surface_brush_fixtures import verify
        verify()
    elif args.center_on_plate:
        from center_on_plate_fixtures import verify
        verify()
    elif args.distribution:
        from distribution_fixtures import verify
        verify()
    elif args.gltf:
        from gltf_fixtures import verify
        verify()
    elif args.ply:
        verify_ply()
    elif args.obj:
        verify_obj()
    elif args.surface_paint:
        verify_surface_paint()
    elif args.image_contour:
        verify_image_contour()
    elif args.materials:
        verify_materials()
    elif args.extrude_down:
        verify_extrude_down()
    elif args.separate:
        verify_separate()
    elif args.smooth:
        verify_smooth()
    elif args.simplify:
        verify_simplify()
    elif args.hollow:
        verify_hollow()
    elif args.image_relief:
        verify_image_relief()
    elif args.text_emboss:
        verify_text_emboss()
    elif args.mirror_measurement:
        verify_mirror_measurement()
    elif args.survey_image:
        verify_survey_image()
    elif args.performance:
        verify_performance()
    else:
        main()
