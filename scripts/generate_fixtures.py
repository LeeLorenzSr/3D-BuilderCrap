"""Generate deterministic, original meshes for behavioral comparison with 3D Builder.

Only the named files in fixtures/generated are replaced. No Microsoft assets are used.
STL stores no unit; its coordinates in this corpus are intended to be millimeters.
"""
from __future__ import annotations

import hashlib
import argparse
import json
import math
import struct
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "fixtures" / "generated"
VERTICES = [(0, 0, 0), (20, 0, 0), (20, 20, 0), (0, 20, 0),
            (0, 0, 20), (20, 0, 20), (20, 20, 20), (0, 20, 20)]
FACES = [(0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7),
         (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5),
         (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7)]


def triangles(faces=FACES, offset=(0, 0, 0)):
    return [tuple(tuple(VERTICES[i][j] + offset[j] for j in range(3)) for i in face)
            for face in faces]


def stl_bytes(mesh):
    result = bytearray(b"Original 3D Builder comparison fixture; coordinates intended as mm".ljust(80, b"\0"))
    result.extend(struct.pack("<I", len(mesh)))
    for a, b, c in mesh:
        u = tuple(b[i] - a[i] for i in range(3))
        v = tuple(c[i] - a[i] for i in range(3))
        n = (u[1]*v[2] - u[2]*v[1], u[2]*v[0] - u[0]*v[2], u[0]*v[1] - u[1]*v[0])
        length = math.sqrt(sum(x*x for x in n))
        normal = tuple(x / length for x in n) if length else (0, 0, 0)
        result.extend(struct.pack("<12fH", *normal, *a, *b, *c, 0))
    return bytes(result)


def three_mf_bytes(unit, units_per_mm):
    ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02"
    model = ET.Element("model", {"xmlns": ns, "unit": unit, "xml:lang": "en-US"})
    resources = ET.SubElement(model, "resources")
    obj = ET.SubElement(resources, "object", {"id": "1", "type": "model", "name": "20mm cube"})
    mesh = ET.SubElement(obj, "mesh")
    vertices = ET.SubElement(mesh, "vertices")
    for vertex in VERTICES:
        ET.SubElement(vertices, "vertex", {axis: format(value * units_per_mm, ".17g")
                                            for axis, value in zip("xyz", vertex)})
    faces = ET.SubElement(mesh, "triangles")
    for face in FACES:
        ET.SubElement(faces, "triangle", {f"v{i+1}": str(v) for i, v in enumerate(face)})
    build = ET.SubElement(model, "build")
    ET.SubElement(build, "item", {"objectid": "1"})
    ET.SubElement(build, "item", {"objectid": "1", "transform": f"1 0 0 0 1 0 0 0 1 {30*units_per_mm:.17g} 0 0"})
    import io
    buffer = io.BytesIO()
    parts = {
        "[Content_Types].xml": b'<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>',
        "_rels/.rels": b'<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>',
        "3D/3dmodel.model": ET.tostring(model, encoding="utf-8", xml_declaration=True),
    }
    with zipfile.ZipFile(buffer, "w") as archive:
        for name, data in parts.items():
            info = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
            # Stored members avoid compression-version differences in fixture digests.
            archive.writestr(info, data)
    return buffer.getvalue()


def box_grid(nx, ny, nz, size=100):
    """Indexed outward box surface with matching subdivisions on every shared edge."""
    counts = (nx, ny, nz)
    vertices, faces, indices = [], [], {}

    def vertex(key):
        key = tuple(key)
        if key not in indices:
            indices[key] = len(vertices)
            vertices.append(tuple(size * key[i] / counts[i] for i in range(3)))
        return indices[key]

    for axis, u, v, cross_sign in ((0, 1, 2, 1), (1, 0, 2, -1), (2, 0, 1, 1)):
        for side in (0, 1):
            reverse = cross_sign != (1 if side else -1)
            for j in range(counts[v]):
                for i in range(counts[u]):
                    cell = []
                    for du, dv in ((0, 0), (1, 0), (1, 1), (0, 1)):
                        key = [0, 0, 0]
                        key[axis], key[u], key[v] = side * counts[axis], i + du, j + dv
                        cell.append(vertex(key))
                    for a, b, c in ((cell[0], cell[1], cell[2]), (cell[0], cell[2], cell[3])):
                        faces.append((a, c, b) if reverse else (a, b, c))
    return vertices, faces


def performance_3mf(meshes, object_count=1000, grouped=False):
    """Shared mesh resources, one leaf per 15 mm-spaced grid position; optional standard components."""
    ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02"
    model = ET.Element("model", {"xmlns": ns, "unit": "millimeter", "xml:lang": "en-US"})
    resources = ET.SubElement(model, "resources")
    for index, (vertices, faces) in enumerate(meshes, 1):
        obj = ET.SubElement(resources, "object", {"id": str(index), "type": "model", "name": f"Grid box {index}"})
        mesh = ET.SubElement(obj, "mesh")
        vert = ET.SubElement(mesh, "vertices")
        for point in vertices:
            ET.SubElement(vert, "vertex", {a: format(x, ".17g") for a, x in zip("xyz", point)})
        tris = ET.SubElement(mesh, "triangles")
        for face in faces:
            ET.SubElement(tris, "triangle", {f"v{i+1}": str(x) for i, x in enumerate(face)})
    build = ET.SubElement(model, "build")
    if grouped:
        group = ET.SubElement(resources, "object", {"id": str(len(meshes)+1), "type": "model", "name": "Performance assembly"})
        destination = ET.SubElement(group, "components")
        ET.SubElement(build, "item", {"objectid": str(len(meshes)+1)})
    else:
        destination = build
    for i in range(object_count):
        # Combined fixture: 990*96 + 10*496 = exactly 100,000 triangles.
        mesh_id = 2 if len(meshes) == 2 and i >= 990 else 1
        x, y, z = (i % 10)*15, ((i // 10) % 10)*15, (i // 100)*15
        ET.SubElement(destination, "component" if grouped else "item", {
            "objectid": str(mesh_id), "transform": f"1 0 0 0 1 0 0 0 1 {x} {y} {z}"})
    import io
    buffer = io.BytesIO()
    parts = {
        "[Content_Types].xml": b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>',
        "_rels/.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>',
        "3D/3dmodel.model": ET.tostring(model, encoding="utf-8", xml_declaration=True),
    }
    with zipfile.ZipFile(buffer, "w") as archive:
        for name, data in parts.items():
            archive.writestr(zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0)), data)
    return buffer.getvalue()


def generate_performance():
    output = ROOT / ".local/evidence/performance-20261019/inputs"
    output.mkdir(parents=True, exist_ok=True)
    original = []
    for folder in (ROOT / "fixtures/generated", ROOT / "fixtures/failures"):
        manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
        for case in manifest["cases"]:
            path = folder / case["file"]
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            if digest != case["sha256"]:
                raise RuntimeError(f"Original fixture hash mismatch: {path}")
            original.append({"file": path.relative_to(ROOT).as_posix(), "sha256": digest})
    vertices, faces = box_grid(100, 100, 75)
    dense = [tuple(vertices[i] for i in face) for face in faces]
    cases = [
        ("dense-100000.stl", stl_bytes(dense), {"valid": True, "objects": 1, "triangles": 100000, "raw_vertices": 300000, "welded_vertices": 50002, "bounds_mm": [[0, 0, 0], [100, 100, 100]], "summed_volume_mm3": 1000000}),
        ("objects-1000.3mf", performance_3mf([box_grid(1, 1, 1, 10)]), {"valid": True, "objects": 1000, "groups": 0, "resources": 1, "triangles": 12000, "expanded_vertices": 8000, "bounds_mm": [[0, 0, 0], [145, 145, 145]], "summed_volume_mm3": 1000000}),
        ("combined-1000-100000.3mf", performance_3mf([box_grid(5, 2, 2, 10), box_grid(10, 4, 6, 10)], grouped=True), {"valid": True, "objects": 1000, "groups": 1, "group_name": "Performance assembly", "resources": 3, "triangles": 100000, "expanded_vertices": 52000, "bounds_mm": [[0, 0, 0], [145, 145, 145]], "summed_volume_mm3": 1000000}),
        ("over-triangles-100001.stl", stl_bytes(dense + [dense[0]]), {"valid": False, "triangles": 100001, "rejection": "triangle limit exceeded by one; duplicate final face is intentional"}),
        ("over-objects-1001.3mf", performance_3mf([box_grid(1, 1, 1, 10)], 1001), {"valid": False, "objects": 1001, "triangles": 12012, "rejection": "object limit exceeded by one"}),
        ("truncated-dense.stl", stl_bytes(dense)[:-50], {"valid": False, "declared_triangles": 100000, "stored_triangles": 99999, "rejection": "last binary triangle record is missing"}),
    ]
    records = []
    for name, data, expected in cases:
        (output / name).write_bytes(data)
        records.append({"file": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), **expected})
    manifest = {"schema_version": 1, "provenance": "Original deterministic performance inputs; generated only with --performance", "limits": {"objects": 1000, "triangles": 100000, "vertices": 300000}, "original_inputs": original, "cases": records}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(records)} performance fixtures in {output}; original {len(original)} hashes unchanged")


def generate_survey_image():
    """Small original raster for opening the image-to-model UI, not a mesh oracle."""
    import zlib
    output = ROOT / ".local/evidence/feature-survey-20261019/inputs"
    output.mkdir(parents=True, exist_ok=True)
    width = height = 64
    # A white frame, black L and grayscale ramp expose silhouette/height modes.
    rows = bytearray()
    for y in range(height):
        rows.append(0)  # PNG scanline filter: none
        for x in range(width):
            value = 255
            if 8 <= x < 20 and 8 <= y < 54 or 8 <= x < 42 and 42 <= y < 54:
                value = 0
            elif 30 <= x < 56 and 8 <= y < 34:
                value = (x - 30) * 255 // 25
            rows.append(value)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    data = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(rows)))
            + chunk(b"IEND", b""))
    path = output / "silhouette-ramp.png"
    path.write_bytes(data)
    print(json.dumps({"file": path.relative_to(ROOT).as_posix(),
                      "sha256": hashlib.sha256(data).hexdigest(),
                      "width": width, "height": height, "purpose": "Original image-import UI probe"}))


def generate_mirror_measurement():
    """Asymmetric tetrahedron and a reflected/sheared instance for the mirror/ruler slice."""
    import io
    output = ROOT / ".local/evidence/mirror-measurement-20261020/inputs"
    output.mkdir(parents=True, exist_ok=True)
    vertices = [(0, 0, 0), (18, 0, 0), (3, 26, 0), (5, 7, 34)]
    faces = [(0, 2, 1), (0, 1, 3), (1, 2, 3), (2, 0, 3)]

    def package(transform=None):
        ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02"
        model = ET.Element("model", {"xmlns": ns, "unit": "millimeter"})
        resource = ET.SubElement(ET.SubElement(model, "resources"), "object",
                                 {"id": "1", "type": "model", "name": "Asymmetric tetrahedron"})
        mesh = ET.SubElement(resource, "mesh")
        v = ET.SubElement(mesh, "vertices")
        for point in vertices:
            ET.SubElement(v, "vertex", {axis: str(value) for axis, value in zip("xyz", point)})
        t = ET.SubElement(mesh, "triangles")
        for face in faces:
            ET.SubElement(t, "triangle", {f"v{i+1}": str(value) for i, value in enumerate(face)})
        attributes = {"objectid": "1"}
        if transform:
            attributes["transform"] = transform
        ET.SubElement(ET.SubElement(model, "build"), "item", attributes)
        parts = {
            "[Content_Types].xml": b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>',
            "_rels/.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>',
            "3D/3dmodel.model": ET.tostring(model, encoding="utf-8", xml_declaration=True),
        }
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            for name, data in parts.items():
                archive.writestr(zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0)), data)
        return buffer.getvalue()

    negative = "-1 0.5 0 0 2 0 0.25 0 1 40 -10 6"
    transformed = [(40, -10, 6), (22, -1, 6), (37, 43.5, 6), (43.5, 6.5, 40)]
    data_sets = [
        ("asymmetric-tetrahedron.stl", stl_bytes([tuple(vertices[i] for i in f) for f in faces]), vertices, 2652, 1),
        ("asymmetric-tetrahedron.3mf", package(), vertices, 2652, 1),
        ("reflected-affine-tetrahedron.3mf", package(negative), transformed, 5304, -2),
    ]
    cases = []
    for name, data, world, volume, determinant in data_sets:
        (output / name).write_bytes(data)
        cases.append({"file": name, "sha256": hashlib.sha256(data).hexdigest(),
                      "world_vertices_mm": world, "canonical_volume_mm3": volume,
                      "source_transform_determinant": determinant, "triangles": 4, "components": 1})
    manifest = {"provenance": "Original analytic inputs, independent of the replacement generators.",
                "vertices_mm": vertices, "outward_faces": faces, "edge_CD_mm": 39,
                "negative_transform_3mf": negative, "cases": cases}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(cases)} mirror/measurement inputs in {output}")


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    cube = triangles()
    cases = [
        ("cube-20mm.stl", cube, "Closed, outward cube; 20 mm sides; 8000 mm^3. Import as millimeters."),
        ("missing-top.stl", triangles(FACES[:2] + FACES[4:]), "Top two triangles removed; four boundary edges. Record repair and dimensional change."),
        ("inverted-cube.stl", triangles([(a, c, b) for a, b, c in FACES]), "Closed cube with all faces inward; signed volume -8000 mm^3."),
        ("duplicate-face.stl", cube + [cube[0]], "One extra copy of a triangle; test duplicate-face diagnosis and repair."),
        ("degenerate-face.stl", cube + [((0, 0, 0), (0, 0, 0), (20, 0, 0))], "One zero-area triangle added; record whether import removes it silently."),
        ("overlapping-cubes.stl", cube + triangles(offset=(10, 0, 0)), "Two closed intersecting shells; x extent 30 mm. Their union is 12000 mm^3; repair must be observed, not assumed to union them."),
        ("separate-cubes.stl", cube + triangles(offset=(30, 0, 0)), "Two disjoint shells; x extent 50 mm, combined volume 16000 mm^3. Record object and shell handling."),
    ]
    records = []
    for name, mesh, purpose in cases:
        data = stl_bytes(mesh)
        (OUTPUT / name).write_bytes(data)
        records.append({"file": name, "sha256": hashlib.sha256(data).hexdigest(),
                        "triangle_count": len(mesh), "purpose": purpose})
    for name, unit, scale in [("assembly-mm.3mf", "millimeter", 1.0),
                              ("assembly-inch.3mf", "inch", 1.0/25.4)]:
        data = three_mf_bytes(unit, scale)
        (OUTPUT / name).write_bytes(data)
        records.append({"file": name, "sha256": hashlib.sha256(data).hexdigest(),
                        "purpose": "Two instances of the same 20 mm cube, second translated 30 mm in x; total bounds 50 x 20 x 20 mm.",
                        "declared_unit": unit, "build_items": 2})
    manifest = {"schema_version": 1, "provenance": "Original synthetic geometry generated by scripts/generate_fixtures.py",
                "note": "These are test inputs and analytic expectations, not observed 3D Builder outputs.", "cases": records}
    (OUTPUT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Generated {len(records)} fixtures in {OUTPUT}")


def generate_text_emboss():
    """A notched planar plate, independent of replacement text/boolean generators."""
    import io
    output = ROOT / ".local/evidence/text-emboss-20261020/inputs"
    output.mkdir(parents=True, exist_ok=True)
    polygon = [(0, 0), (60, 0), (60, 24), (50, 24), (50, 32), (0, 32)]
    vertices = [(x, y, z) for z in (0, 6) for x, y in polygon]
    faces = []
    for i in range(1, 5):
        faces.extend([(0, i + 1, i), (6, i + 6, i + 7)])
    for i in range(6):
        j = (i + 1) % 6
        faces.extend([(i, j, j + 6), (i, j + 6, i + 6)])
    ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02"
    model = ET.Element("model", {"xmlns": ns, "unit": "millimeter"})
    obj = ET.SubElement(ET.SubElement(model, "resources"), "object",
                        {"id": "1", "type": "model", "name": "Notched label plate"})
    mesh = ET.SubElement(obj, "mesh")
    v = ET.SubElement(mesh, "vertices")
    for point in vertices:
        ET.SubElement(v, "vertex", {axis: str(value) for axis, value in zip("xyz", point)})
    t = ET.SubElement(mesh, "triangles")
    for face in faces:
        ET.SubElement(t, "triangle", {f"v{i+1}": str(value) for i, value in enumerate(face)})
    ET.SubElement(ET.SubElement(model, "build"), "item", {"objectid": "1"})
    parts = {
        "[Content_Types].xml": b'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>',
        "_rels/.rels": b'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>',
        "3D/3dmodel.model": ET.tostring(model, encoding="utf-8", xml_declaration=True),
    }
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        for name, data in parts.items():
            archive.writestr(zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0)), data)
    files = {
        "notched-plate.3mf": buffer.getvalue(),
        "notched-plate.stl": stl_bytes([tuple(vertices[i] for i in f) for f in faces]),
    }
    cases = []
    for name, data in files.items():
        (output / name).write_bytes(data)
        cases.append({"file": name, "sha256": hashlib.sha256(data).hexdigest()})
    manifest = {"provenance": "Original analytic notched plate; no application implementation used.",
                "bounds_mm": [[0, 0, 0], [60, 32, 6]], "area_mm2": 1840, "volume_mm3": 11040,
                "vertices_mm": vertices, "faces": faces, "triangles": 20, "cases": cases}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output), "cases": cases}, indent=2))


def generate_image_relief():
    """Small original RGBA images; no application codec or geometry code."""
    import zlib
    output = ROOT / ".local/evidence/image-relief-20261020/inputs"
    output.mkdir(parents=True, exist_ok=True)

    def png(w, h, rgba):
        def chunk(kind, body):
            return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
        rows = b"".join(b"\0" + bytes(rgba[y*w*4:(y+1)*w*4]) for y in range(h))
        return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w,h,8,6,0,0,0))
                + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))

    def bmp(w, h, rgba):
        stride = (w*3+3)//4*4
        rows = bytearray()
        for y in reversed(range(h)):
            for x in range(w):
                r,g,b,a = rgba[(y*w+x)*4:(y*w+x+1)*4]
                rows.extend((b,g,r))
            rows.extend(b"\0"*(stride-w*3))
        return (b"BM" + struct.pack("<IHHI",54+len(rows),0,0,54)
                + struct.pack("<IiiHHIIiiII",40,w,h,1,24,0,len(rows),2835,2835,0,0) + rows)

    def marker(x,y):
        # Broad white F at top-left; gray ramp at right and separate red/green/blue patches.
        if 5 <= x < 11 and 5 <= y < 36 or 5 <= x < 29 and 5 <= y < 11 or 5 <= x < 24 and 18 <= y < 24:
            return (255,255,255,255)
        if 35 <= x < 60 and 4 <= y < 28:
            n=(x-35)*255//24
            return (n,n,n,255)
        if 36 <= y < 44 and 34 <= x < 42: return (255,0,0,255)
        if 36 <= y < 44 and 43 <= x < 51: return (0,255,0,255)
        if 36 <= y < 44 and 52 <= x < 60: return (0,0,255,255)
        return (0,0,0,255)

    definitions = [
        ("black",2,2,lambda x,y:(0,0,0,255)),
        ("white",4,2,lambda x,y:(255,255,255,255)),
        ("ramp",9,5,lambda x,y:(x*255//8,)*3+(255,)),
        ("marker",64,48,marker),
        ("alpha",5,3,lambda x,y:((255,255,255),(255,0,0),(0,0,255))[y]+(x*255//4,)),
        ("single-pixel",1,1,lambda x,y:(128,64,32,128)),
        ("oversized",2049,1,lambda x,y:(0,0,0,255)),
    ]
    cases, pixels = [], {}
    for name,w,h,fn in definitions:
        rgba = [v for y in range(h) for x in range(w) for v in fn(x,y)]
        pixels[name] = {"width":w,"height":h,"rgba":rgba}
        files = {name+".png":png(w,h,rgba)}
        if name in ("marker","ramp"): files[name+".bmp"]=bmp(w,h,rgba)
        for filename,data in files.items():
            (output/filename).write_bytes(data)
            cases.append({"file":filename,"sha256":hashlib.sha256(data).hexdigest(),
                          "source":name,"width":w,"height":h,
                          "expected":"reject dimensions" if name=="oversized" else "accept"})
    broken = png(2,2,[255]*16)[:39]
    (output/"truncated.png").write_bytes(broken)
    cases.append({"file":"truncated.png","sha256":hashlib.sha256(broken).hexdigest(),"expected":"reject malformed"})
    (output/"pixels.json").write_text(json.dumps(pixels,separators=(",",":"))+"\n",encoding="utf-8")
    manifest = {"provenance":"Original synthetic RGBA fixtures from scripts/generate_fixtures.py --image-relief",
                "pixelContract":"Top-down unpremultiplied RGBA8; marker white F at upper left; alpha varies along +X.",
                "cases":cases}
    (output/"manifest.json").write_text(json.dumps(manifest,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"generated":len(cases),"output":str(output)}))


def generate_hollow():
    """Original closed solids for physical cavity/thickness measurements; isolated from old corpus."""
    output = ROOT / ".local/evidence/hollow-20261020/inputs"
    output.mkdir(parents=True, exist_ok=True)
    models = {"box-20": (VERTICES, FACES)}
    # An outward icosahedron, twice subdivided and projected onto a 10 mm sphere.
    phi = (1 + math.sqrt(5)) / 2
    sphere = [(-1,phi,0),(1,phi,0),(-1,-phi,0),(1,-phi,0),(0,-1,phi),(0,1,phi),
              (0,-1,-phi),(0,1,-phi),(phi,0,-1),(phi,0,1),(-phi,0,-1),(-phi,0,1)]
    sf = [(0,11,5),(0,5,1),(0,1,7),(0,7,10),(0,10,11),(1,5,9),(5,11,4),(11,10,2),
          (10,7,6),(7,1,8),(3,9,4),(3,4,2),(3,2,6),(3,6,8),(3,8,9),(4,9,5),(2,4,11),
          (6,2,10),(8,6,7),(9,8,1)]
    def unit(p):
        length=math.sqrt(sum(x*x for x in p)); return tuple(x/length for x in p)
    sphere=list(map(unit,sphere))
    for _ in range(2):
        cache={}; next_faces=[]
        def midpoint(a,b):
            key=tuple(sorted((a,b)))
            if key not in cache:
                cache[key]=len(sphere); sphere.append(unit(tuple((x+y)/2 for x,y in zip(sphere[a],sphere[b]))))
            return cache[key]
        for a,b,c in sf:
            ab,bc,ca=midpoint(a,b),midpoint(b,c),midpoint(c,a)
            next_faces.extend([(a,ab,ca),(b,bc,ab),(c,ca,bc),(ab,bc,ca)])
        sf=next_faces
    models["sphere-20"] = ([tuple(10+10*x for x in p) for p in sphere],sf)
    polygon=[(0,0),(30,0),(30,10),(10,10),(10,30),(0,30)]
    lv=[(x,y,z) for z in (0,20) for x,y in polygon]
    cap=[(0,1,2),(0,2,3),(0,3,5),(3,4,5)]
    lf=[(a,c,b) for a,b,c in cap]+[(a+6,b+6,c+6) for a,b,c in cap]
    for a in range(6):
        b=(a+1)%6; lf.extend([(a,b,b+6),(a,b+6,a+6)])
    models["concave-l"]=(lv,lf)
    # Two 12 mm blocks joined by a 6 x 2 x 2 mm bridge, triangulated without T-junctions.
    axes=([0,12,18,30],[0,5,7,12],[0,5,7,12]); cells={(x,y,z) for x in range(3) for y in range(3) for z in range(3) if x in (0,2) or (y==1 and z==1)}
    dv=[]; df=[]; indices={}
    def vi(key):
        key=tuple(key)
        if key not in indices: indices[key]=len(dv); dv.append(tuple(axes[i][key[i]] for i in range(3)))
        return indices[key]
    for cell in sorted(cells):
        for axis,u,v,cross in ((0,1,2,1),(1,0,2,-1),(2,0,1,1)):
            for side in (0,1):
                neighbor=list(cell); neighbor[axis]+=1 if side else -1
                if tuple(neighbor) in cells: continue
                corners=[]
                for du,dw in ((0,0),(1,0),(1,1),(0,1)):
                    key=list(cell); key[axis]+=side; key[u]+=du; key[v]+=dw; corners.append(vi(key))
                for a,b,c in ((corners[0],corners[1],corners[2]),(corners[0],corners[2],corners[3])):
                    df.append((a,b,c) if cross==(1 if side else -1) else (a,c,b))
    models["narrow-bridge"]=(dv,df)
    models["thin-plate"] = ([(x,y,z/10) for x,y,z in VERTICES],FACES)
    cases=[]
    for name,(vertices,faces) in models.items():
        for extension,data in (("3mf",performance_3mf([(vertices,faces)],object_count=1)),
                               ("stl",stl_bytes([tuple(vertices[i] for i in face) for face in faces]))):
            file=name+"."+extension; (output/file).write_bytes(data)
            cases.append({"file":file,"sha256":hashlib.sha256(data).hexdigest(),"vertices":len(vertices),"triangles":len(faces)})
    (output/"geometry.json").write_text(json.dumps({n:{"vertices":v,"triangles":f} for n,(v,f) in models.items()},indent=2)+"\n",encoding="utf-8")
    (output/"manifest.json").write_text(json.dumps({"provenance":"Original synthetic geometry from scripts/generate_fixtures.py --hollow","cases":cases},indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"generated":len(cases),"output":str(output)}))


def generate_simplify():
    """Dense original surfaces, including inward cavity boundaries; never repair these inputs."""
    output = ROOT / ".local/evidence/simplify-20261020/inputs"
    output.mkdir(parents=True, exist_ok=True)
    def scaled(mesh, scale, shift=(0, 0, 0)):
        vertices, faces = mesh
        return ([tuple(p[i]*scale[i]+shift[i] for i in range(3)) for p in vertices], list(faces))
    def joined(*meshes):
        vertices, faces = [], []
        for vv, ff in meshes:
            offset = len(vertices)
            vertices.extend(vv); faces.extend(tuple(i+offset for i in f) for f in ff)
        return vertices, faces
    def reversed_shell(mesh):
        vv, ff = mesh
        return vv, [(a,c,b) for a,b,c in ff]
    phi = (1+math.sqrt(5))/2
    vv = [(-1,phi,0),(1,phi,0),(-1,-phi,0),(1,-phi,0),(0,-1,phi),(0,1,phi),
          (0,-1,-phi),(0,1,-phi),(phi,0,-1),(phi,0,1),(-phi,0,-1),(-phi,0,1)]
    ff = [(0,11,5),(0,5,1),(0,1,7),(0,7,10),(0,10,11),(1,5,9),(5,11,4),(11,10,2),
          (10,7,6),(7,1,8),(3,9,4),(3,4,2),(3,2,6),(3,6,8),(3,8,9),(4,9,5),(2,4,11),
          (6,2,10),(8,6,7),(9,8,1)]
    def unit(p):
        norm=math.sqrt(sum(x*x for x in p)); return tuple(x/norm for x in p)
    vv=list(map(unit,vv))
    for _ in range(3):
        edges={}; faces=[]
        def mid(a,b):
            key=tuple(sorted((a,b)))
            if key not in edges:
                edges[key]=len(vv); vv.append(unit(tuple((x+y)/2 for x,y in zip(vv[a],vv[b]))))
            return edges[key]
        for a,b,c in ff:
            ab,bc,ca=mid(a,b),mid(b,c),mid(c,a)
            faces.extend([(a,ab,ca),(b,bc,ab),(c,ca,bc),(ab,bc,ca)])
        ff=faces
    sphere=scaled((vv,ff),(10,10,10),(10,10,10))
    box=box_grid(8,8,8,size=20)
    models={"dense-box":box,"dense-sphere":sphere}
    tv=[]; tf=[]; segments,rings=64,24
    for i in range(segments):
        u=2*math.pi*i/segments
        for j in range(rings):
            v=2*math.pi*j/rings
            tv.append((14+(10+4*math.cos(v))*math.cos(u),14+(10+4*math.cos(v))*math.sin(u),4+4*math.sin(v)))
    for i in range(segments):
        for j in range(rings):
            a=i*rings+j; b=((i+1)%segments)*rings+j
            c=((i+1)%segments)*rings+(j+1)%rings; d=i*rings+(j+1)%rings
            tf.extend([(a,b,c),(a,c,d)])
    models["dense-torus"]=(tv,tf)
    models["hollow-box"]=joined(box,reversed_shell(scaled(box,(.8,.8,.8),(2,2,2))))
    models["thin-hollow-box"]=joined(box,reversed_shell(scaled(box,(.98,.98,.98),(.2,.2,.2))))
    models["hollow-sphere"]=joined(sphere,reversed_shell(scaled((vv,ff),(8,8,8),(10,10,10))))
    models["thin-plate"]=scaled(box,(1,1,.025))
    models["separate-spheres"]=joined(sphere,scaled(sphere,(1,1,1),(20.2,0,0)))
    models["small-secondary"]=joined(sphere,scaled((vv,ff),(.25,.25,.25),(21,10,10)))
    narrow=[]
    for i in range(segments):
        u=2*math.pi*i/segments
        for j in range(rings):
            v=2*math.pi*j/rings
            narrow.append((19.8+(10+9.8*math.cos(v))*math.cos(u),19.8+(10+9.8*math.cos(v))*math.sin(u),9.8+9.8*math.sin(v)))
    models["narrow-torus"]=(narrow,tf)
    cases=[]
    for name,(vertices,faces) in models.items():
        for extension,data in (("3mf",performance_3mf([(vertices,faces)],object_count=1)),
                               ("stl",stl_bytes([tuple(vertices[i] for i in face) for face in faces]))):
            file=name+"."+extension; (output/file).write_bytes(data)
            cases.append({"file":file,"sha256":hashlib.sha256(data).hexdigest(),"vertices":len(vertices),"triangles":len(faces)})
    (output/"geometry.json").write_text(json.dumps({n:{"vertices":v,"triangles":f} for n,(v,f) in models.items()},indent=2)+"\n",encoding="utf-8")
    (output/"manifest.json").write_text(json.dumps({"provenance":"Original synthetic geometry from scripts/generate_fixtures.py --simplify","cases":cases},indent=2)+"\n",encoding="utf-8")
    print(json.dumps({"generated":len(cases),"output":str(output)}))


def generate_smooth():
    """Deterministic original rough surfaces, retaining sharp edges and signed cavities."""
    output=ROOT/".local/evidence/smooth-20261020/inputs"
    output.mkdir(parents=True,exist_ok=True)
    phi=(1+math.sqrt(5))/2
    units=[(-1,phi,0),(1,phi,0),(-1,-phi,0),(1,-phi,0),(0,-1,phi),(0,1,phi),
           (0,-1,-phi),(0,1,-phi),(phi,0,-1),(phi,0,1),(-phi,0,-1),(-phi,0,1)]
    faces=[(0,11,5),(0,5,1),(0,1,7),(0,7,10),(0,10,11),(1,5,9),(5,11,4),(11,10,2),
           (10,7,6),(7,1,8),(3,9,4),(3,4,2),(3,2,6),(3,6,8),(3,8,9),(4,9,5),(2,4,11),
           (6,2,10),(8,6,7),(9,8,1)]
    def unit(p):
        length=math.sqrt(sum(x*x for x in p));return tuple(x/length for x in p)
    units=list(map(unit,units))
    for _ in range(3):
        edges={};new_faces=[]
        def mid(a,b):
            key=tuple(sorted((a,b)))
            if key not in edges:
                edges[key]=len(units);units.append(unit(tuple((x+y)/2 for x,y in zip(units[a],units[b]))))
            return edges[key]
        for a,b,c in faces:
            ab,bc,ca=mid(a,b),mid(b,c),mid(c,a)
            new_faces.extend([(a,ab,ca),(b,bc,ab),(c,ca,bc),(ab,bc,ca)])
        faces=new_faces
    def noise(i,seed):
        # Integer hash, so no global random state or random-library version changes.
        n=(i*747796405+seed*2891336453+277803737)&0xffffffff
        n=((n>>((n>>28)+4))^n)*277803737&0xffffffff
        n=(n>>22)^n
        return 2*n/4294967295-1
    def sphere(radius,amplitude,seed=1,center=(10,10,10),inward=False):
        vv=[tuple(center[k]+(radius+amplitude*noise(i,seed))*p[k] for k in range(3)) for i,p in enumerate(units)]
        return vv,[(a,c,b) for a,b,c in faces] if inward else list(faces)
    def join(*meshes):
        vv=[];ff=[]
        for vertices,triangles in meshes:
            offset=len(vv);vv.extend(vertices);ff.extend(tuple(i+offset for i in f) for f in triangles)
        return vv,ff
    def torus(tube,amplitude,seed):
        vv=[];ff=[];segments,rings=64,24;center=(10+tube,10+tube,tube)
        for i in range(segments):
            u=2*math.pi*i/segments
            for j in range(rings):
                v=2*math.pi*j/rings;r=tube+amplitude*noise(i*rings+j,seed)
                vv.append((center[0]+(10+r*math.cos(v))*math.cos(u),center[1]+(10+r*math.cos(v))*math.sin(u),center[2]+r*math.sin(v)))
        for i in range(segments):
            for j in range(rings):
                a=i*rings+j;b=((i+1)%segments)*rings+j;c=((i+1)%segments)*rings+(j+1)%rings;d=i*rings+(j+1)%rings
                ff.extend([(a,b,c),(a,c,d)])
        return vv,ff
    box=box_grid(8,8,8,size=20)
    def rough_box(height,amplitude):
        vv=[]
        for i,(x,y,z) in enumerate(box[0]):
            zz=z*height/20
            if 0<x<20 and 0<y<20 and z in (0,20):zz+=amplitude*noise(i,7)
            vv.append((x,y,zz))
        return vv,list(box[1])
    models={
      'noisy-sphere':sphere(10,.18),
      'noisy-hollow-sphere':join(sphere(10,.18),sphere(8,.14,2,inward=True)),
      'thin-hollow-sphere':join(sphere(10,.05),sphere(9.6,.05,2,inward=True)),
      'noisy-torus':torus(4,.08,3),
      'narrow-torus':torus(9.5,.025,4),
      'sharp-box':box,
      'noisy-box':rough_box(20,.12),
      'noisy-thin-plate':rough_box(.5,.03),
      'separate-noisy-solids':join(sphere(10,.18),sphere(1,.018,5,(22,10,10))),
    }
    analytic={
      'noisy-sphere':dict(kind='sphere',center=[10,10,10],radii=[10],noise=[.18]),
      'noisy-hollow-sphere':dict(kind='sphere',center=[10,10,10],radii=[10,8],noise=[.18,.14]),
      'thin-hollow-sphere':dict(kind='sphere',center=[10,10,10],radii=[10,9.6],noise=[.05,.05]),
      'noisy-torus':dict(kind='torus',center=[14,14,4],major=10,tube=4,noise=.08),
      'narrow-torus':dict(kind='torus',center=[19.5,19.5,9.5],major=10,tube=9.5,noise=.025),
      'sharp-box':dict(kind='box',size=[20,20,20],noise=0),
      'noisy-box':dict(kind='box',size=[20,20,20],noise=.12),
      'noisy-thin-plate':dict(kind='box',size=[20,20,.5],noise=.03),
      'separate-noisy-solids':dict(kind='separate_spheres',centers=[[10,10,10],[22,10,10]],radii=[10,1],noise=[.18,.018]),
    }
    cases=[]
    for name,(vertices,triangles) in models.items():
        for extension,data in (("3mf",performance_3mf([(vertices,triangles)],object_count=1)),
                               ("stl",stl_bytes([tuple(vertices[i] for i in face) for face in triangles]))):
            file=name+'.'+extension;(output/file).write_bytes(data)
            cases.append(dict(file=file,sha256=hashlib.sha256(data).hexdigest(),vertices=len(vertices),triangles=len(triangles)))
    (output/'geometry.json').write_text(json.dumps({n:dict(vertices=v,triangles=f,analytic=analytic[n]) for n,(v,f) in models.items()},indent=2)+'\n',encoding='utf-8')
    (output/'manifest.json').write_text(json.dumps(dict(provenance='Original deterministic rough geometry from scripts/generate_fixtures.py --smooth',cases=cases,analytic=analytic),indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(generated=len(cases),output=str(output))))


def generate_separate():
    """Small material-ownership corpus: disconnected solids, cavities and nested islands."""
    output = ROOT / '.local/evidence/separate-20261020/inputs'
    output.mkdir(parents=True, exist_ok=True)

    def box(size, origin=(0, 0, 0), inward=False):
        vv = [tuple(origin[k] + p[k] * size / 20 for k in range(3)) for p in VERTICES]
        return vv, [(a, c, b) for a, b, c in FACES] if inward else list(FACES)

    def torus(tube, inward=False):
        vv, ff = [], []
        segments, rings = 32, 12
        for i in range(segments):
            u = 2 * math.pi * i / segments
            for j in range(rings):
                v = 2 * math.pi * j / rings
                vv.append((13 + (10 + tube * math.cos(v)) * math.cos(u),
                           13 + (10 + tube * math.cos(v)) * math.sin(u), 3 + tube * math.sin(v)))
        for i in range(segments):
            for j in range(rings):
                a, b = i*rings+j, ((i+1)%segments)*rings+j
                c, d = ((i+1)%segments)*rings+(j+1)%rings, i*rings+(j+1)%rings
                ff.extend([(a, b, c), (a, c, d)])
        return vv, [(a, c, b) for a, b, c in ff] if inward else ff

    # Explicit expected ownership is synthetic intent, independent of the product algorithm.
    cases = {
        'two-boxes': ([box(20), box(8, (30, 0, 0))], [[0], [1]], 'separate'),
        'two-hollow-boxes': ([box(20), box(16, (2, 2, 2), True),
                              box(12, (30, 0, 0)), box(8, (32, 2, 2), True)], [[0, 1], [2, 3]], 'separate'),
        # Deliberately put cavities before owners and interleave the face stream.
        'nested-island': ([box(24, (3, 3, 3), True), box(6, (12, 12, 12), True),
                           box(30), box(10, (10, 10, 10))], [[0, 2], [1, 3]], 'separate'),
        'torus-and-center': ([torus(3), torus(1.5, True), box(2, (12, 12, 2))], [[0, 1], [2]], 'separate'),
        'one-hollow-box': ([box(20), box(16, (2, 2, 2), True)], [[0, 1]], 'no-op'),
        'touching-boxes': ([box(20), box(10, (20, 5, 5))], [], 'reject-contact'),
        'nested-positive': ([box(20), box(10, (5, 5, 5))], [], 'reject-orientation'),
    }
    manifest, geometry = [], {}
    for name, (shells, owners, expected) in cases.items():
        vv, shell_faces = [], []
        for vertices, faces in shells:
            offset = len(vv)
            vv.extend(vertices)
            shell_faces.append([tuple(i + offset for i in f) for f in faces])
        ff, shell_indices = [], [[] for _ in shells]
        for index in range(max(map(len, shell_faces))):
            for s, faces in enumerate(shell_faces):
                if index < len(faces):
                    shell_indices[s].append(len(ff)); ff.append(faces[index])
        expected_pieces = [sorted(i for s in owner for i in shell_indices[s]) for owner in owners]
        expected_pieces.sort(key=min)
        geometry[name] = dict(vertices=vv, triangles=ff, shellTriangleIndices=shell_indices,
                              expectedPieceTriangleIndices=expected_pieces, expectedOutcome=expected)
        for extension, data in [('3mf', performance_3mf([(vv, ff)], object_count=1)),
                                ('stl', stl_bytes([tuple(vv[i] for i in f) for f in ff]))]:
            filename = name + '.' + extension
            (output / filename).write_bytes(data)
            manifest.append(dict(file=filename, sha256=hashlib.sha256(data).hexdigest(),
                                 vertices=len(vv), triangles=len(ff), expectedOutcome=expected))
    (output / 'geometry.json').write_text(json.dumps(geometry, indent=2)+'\n', encoding='utf-8')
    (output / 'manifest.json').write_text(json.dumps(dict(provenance='Original synthetic material ownership fixtures; --separate', cases=manifest), indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(generated=len(manifest), output=str(output))))


def generate_extrude_down():
    """Three small original solids for the downward-extension workflow."""
    output = ROOT / '.local/evidence/extrude-down-20261020/inputs'
    output.mkdir(parents=True, exist_ok=True)
    box = [(x, y, z + 10) for x, y, z in VERTICES]
    inner = [(2 + x * .8, 2 + y * .8, 12 + z * .8) for x, y, z in VERTICES]
    hollow = box + inner
    hollow_faces = list(FACES) + [(a + 8, c + 8, b + 8) for a, b, c in FACES]
    torus, torus_faces = [], []
    segments, rings = 32, 12
    for i in range(segments):
        u = i * 2 * math.pi / segments
        for j in range(rings):
            v = j * 2 * math.pi / rings
            torus.append((13 + (10 + 3 * math.cos(v)) * math.cos(u),
                          13 + (10 + 3 * math.cos(v)) * math.sin(u), 8 + 3 * math.sin(v)))
    for i in range(segments):
        for j in range(rings):
            a, b = i*rings+j, ((i+1)%segments)*rings+j
            c, d = ((i+1)%segments)*rings+(j+1)%rings, i*rings+(j+1)%rings
            torus_faces.extend([(a, b, c), (a, c, d)])
    cases = [('raised-box', box, FACES, [[0, 0, 10], [20, 20, 30]], 8000),
             ('raised-hollow-box', hollow, hollow_faces, [[0, 0, 10], [20, 20, 30]], 3904),
             ('raised-torus', torus, torus_faces, [[0, 0, 5], [26, 26, 11]],
              segments*rings/2 * 10 * 9 * math.sin(2*math.pi/segments) * math.sin(2*math.pi/rings))]
    manifest = []
    for name, vertices, faces, bounds, volume in cases:
        data = performance_3mf([(vertices, faces)], object_count=1)
        filename = name + '.3mf'
        (output / filename).write_bytes(data)
        manifest.append(dict(file=filename, sha256=hashlib.sha256(data).hexdigest(),
                             triangles=len(faces), bounds_mm=bounds, volume_mm3=volume))
    (output / 'manifest.json').write_text(json.dumps(dict(cases=manifest), indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(output=str(output), cases=len(manifest))))


def generate_materials():
    """Small original 3MF assembly and explicit unsupported-material specimens."""
    import copy
    import io
    output = ROOT / '.local/evidence/paint-materials-20261021/inputs'
    output.mkdir(parents=True, exist_ok=True)
    uri = 'http://schemas.microsoft.com/3dmanufacturing/core/2015/02'
    ns = '{' + uri + '}'
    ET.register_namespace('', uri)
    with zipfile.ZipFile(io.BytesIO(three_mf_bytes('millimeter', 1))) as archive:
        parts = {name: archive.read(name) for name in archive.namelist()}
    model = ET.fromstring(parts['3D/3dmodel.model'])
    resources, build = model.find(ns+'resources'), model.find(ns+'build')
    source = copy.deepcopy(resources.find(ns+'object'))
    resources.clear(); build.clear()
    palette = ET.SubElement(resources, ns+'basematerials', id='1')
    ET.SubElement(palette, ns+'base', name='Warm amber', displaycolor='#E6A23CFF')
    ET.SubElement(palette, ns+'base', name='Ocean blue', displaycolor='#267CB5FF')
    for identifier, index in [(2, 0), (3, 1), (4, None)]:
        obj = copy.deepcopy(source)
        obj.set('id', str(identifier)); obj.set('name', 'Block')
        if index is not None:
            obj.set('pid', '1'); obj.set('pindex', str(index))
        resources.append(obj)
    group = ET.SubElement(resources, ns+'object', id='5', name='Color pair', type='model')
    components = ET.SubElement(group, ns+'components')
    ET.SubElement(components, ns+'component', objectid='2')
    ET.SubElement(components, ns+'component', objectid='3', transform='1 0 0 0 1 0 0 0 1 30 0 0')
    ET.SubElement(build, ns+'item', objectid='5')
    ET.SubElement(build, ns+'item', objectid='4', transform='1 0 0 0 1 0 0 0 1 60 0 0')
    cases = []
    for name, expected in [('colored-assembly', 'accept'), ('six-digit-color', 'accept'),
                           ('transparent-material', 'reject'), ('triangle-material', 'reject'),
                           ('missing-material', 'reject'), ('duplicate-resource', 'reject')]:
        candidate = copy.deepcopy(model)
        mats = candidate.find(ns+'resources/'+ns+'basematerials')
        obj = candidate.find(ns+'resources/'+ns+'object')
        if name == 'six-digit-color':
            for item in mats: item.set('displaycolor', item.get('displaycolor')[:7])
        elif name == 'transparent-material': mats[0].set('displaycolor', '#E6A23C80')
        elif name == 'triangle-material':
            face = obj.find('.//'+ns+'triangle'); face.set('pid', '1'); face.set('p1', '1')
        elif name == 'missing-material': obj.set('pid', '99')
        elif name == 'duplicate-resource': mats.set('id', '2')
        package = io.BytesIO()
        with zipfile.ZipFile(package, 'w') as archive:
            for part, data in parts.items():
                if part == '3D/3dmodel.model': data = ET.tostring(candidate, encoding='utf-8', xml_declaration=True)
                archive.writestr(zipfile.ZipInfo(part, date_time=(2020, 1, 1, 0, 0, 0)), data)
        data = package.getvalue(); filename = name+'.3mf'
        (output/filename).write_bytes(data)
        cases.append(dict(file=filename, sha256=hashlib.sha256(data).hexdigest(), expected=expected))
    manifest = dict(provenance='Original synthetic colored boxes; scripts/generate_fixtures.py --materials',
                    bounds_mm=[[0, 0, 0], [80, 20, 20]], volume_mm3=24000,
                    leafMaterials=[['Warm amber', '#E6A23C'], ['Ocean blue', '#267CB5'], None], cases=cases)
    (output/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(output=str(output), cases=len(cases))))


def generate_surface_paint():
    """Original boxes with uniform per-triangle base materials and two refusal inputs."""
    import copy
    import io
    output = ROOT / '.local/evidence/surface-paint-20261021/inputs'
    output.mkdir(parents=True, exist_ok=True)
    uri = 'http://schemas.microsoft.com/3dmanufacturing/core/2015/02'
    ns = '{' + uri + '}'
    ET.register_namespace('', uri)
    with zipfile.ZipFile(io.BytesIO(three_mf_bytes('millimeter', 1))) as archive:
        parts = {name: archive.read(name) for name in archive.namelist()}
    model = ET.fromstring(parts['3D/3dmodel.model'])
    resources, build = model.find(ns+'resources'), model.find(ns+'build')
    source = copy.deepcopy(resources.find(ns+'object'))
    resources.clear(); build.clear()
    palette = ET.SubElement(resources, ns+'basematerials', id='1')
    for name, color in [('White', '#F3F3F0FF'), ('Red', '#D65248FF'), ('Blue', '#267CB5FF')]:
        ET.SubElement(palette, ns+'base', name=name, displaycolor=color)
    source.attrib.update(id='2', name='Surface cube', pid='1', pindex='0')
    faces = source.findall('.//'+ns+'triangle')
    for index in (2, 3): faces[index].set('p1', '1')  # Inherit pid; omitted p2/p3 use p1.
    for index in (6, 7): faces[index].attrib.update(pid='1', p1='2', p2='2', p3='2')
    resources.append(source)
    ET.SubElement(build, ns+'item', objectid='2')
    cases = []
    for name in ('surface-cube', 'surface-group', 'gradient', 'missing-default'):
        candidate = copy.deepcopy(model)
        res, build = candidate.find(ns+'resources'), candidate.find(ns+'build')
        obj = res.find(ns+'object')
        if name == 'surface-group':
            other = copy.deepcopy(obj); other.set('id', '3'); other.set('name', 'Surface cube')
            for face in other.findall('.//'+ns+'triangle'):
                for key in ('pid', 'p1', 'p2', 'p3'): face.attrib.pop(key, None)
            other.findall('.//'+ns+'triangle')[4].set('p1', '2')
            res.append(other)
            group = ET.SubElement(res, ns+'object', id='4', name='Surface pair', type='model')
            members = ET.SubElement(group, ns+'components')
            ET.SubElement(members, ns+'component', objectid='2')
            ET.SubElement(members, ns+'component', objectid='3', transform='1 0 0 0 1 0 0 0 1 30 0 0')
            build.clear(); ET.SubElement(build, ns+'item', objectid='4')
        elif name == 'gradient':
            obj.findall('.//'+ns+'triangle')[2].attrib.update(p2='2', p3='1')
        elif name == 'missing-default':
            obj.attrib.pop('pid'); obj.attrib.pop('pindex')
        package = io.BytesIO()
        with zipfile.ZipFile(package, 'w') as archive:
            for part, data in parts.items():
                if part == '3D/3dmodel.model': data = ET.tostring(candidate, encoding='utf-8', xml_declaration=True)
                archive.writestr(zipfile.ZipInfo(part, date_time=(2020, 1, 1, 0, 0, 0)), data)
        data = package.getvalue(); filename = name+'.3mf'
        (output/filename).write_bytes(data)
        cases.append(dict(file=filename, sha256=hashlib.sha256(data).hexdigest(), expected='accept' if name.startswith('surface-') else 'reject'))
    manifest = dict(provenance='Original synthetic boxes; scripts/generate_fixtures.py --surface-paint', cases=cases,
                    description='20 mm white cube, red top (triangles 2/3), blue +X side (6/7); grouped counterpart has a blue triangle 4. Gradient and missing default are invalid.')
    (output/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(generated=len(cases), output=str(output))))


def generate_image_contour():
    """Small original silhouette, threshold and alpha inputs; no application implementation."""
    import zlib
    output = ROOT / ".local/evidence/image-contour-20261021/inputs"
    output.mkdir(parents=True, exist_ok=True)

    def png(w, h, rgba):
        def chunk(kind, body):
            return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
        rows = b"".join(b"\0" + bytes(rgba[y*w*4:(y+1)*w*4]) for y in range(h))
        return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w,h,8,6,0,0,0))
                + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))

    def silhouette(x, y):
        inside = (1 <= x <= 9 and 1 <= y <= 10 and not (3 <= x <= 7 and 3 <= y <= 8))
        inside |= 12 <= x <= 14 and 1 <= y <= 3
        return (255 if inside else 0,)*3 + (255,)

    definitions = [
        ("ring-island",16,12,silhouette),
        ("levels",8,4,lambda x,y:(64*(x//2),)*3+(255,)),
        ("alpha",8,4,lambda x,y:((255,255,255,255),(255,255,255,128),(255,255,255,0),(0,0,0,0))[x//2]),
        ("white",4,2,lambda x,y:(255,255,255,255)),
        ("black",4,2,lambda x,y:(0,0,0,255)),
        ("diagonal",2,2,lambda x,y:(255 if x==y else 0,)*3+(255,)),
    ]
    cases, pixels = [], {}
    for name,w,h,fn in definitions:
        rgba = [v for y in range(h) for x in range(w) for v in fn(x,y)]
        data = png(w,h,rgba)
        (output/(name+".png")).write_bytes(data)
        pixels[name] = dict(width=w,height=h,rgba=rgba)
        cases.append(dict(file=name+".png",source=name,width=w,height=h,sha256=hashlib.sha256(data).hexdigest(),expected="valid raster"))
    data = b"not an image\n"
    (output/"invalid.png").write_bytes(data)
    cases.append(dict(file="invalid.png",sha256=hashlib.sha256(data).hexdigest(),expected="reject malformed"))
    (output/"pixels.json").write_text(json.dumps(pixels,separators=(",",":"))+"\n",encoding="utf-8")
    manifest = dict(provenance="Original synthetic RGBA images from scripts/generate_fixtures.py --image-contour",
                    description="Top-down ring with through-hole and separate upper-right island; grayscale/alpha stripes; full/empty; diagonal point contact.",
                    source_ring_foreground_pixels=69,cases=cases)
    (output/"manifest.json").write_text(json.dumps(manifest,indent=2)+"\n",encoding="utf-8")
    print(json.dumps(dict(generated=len(cases),output=str(output))))


def generate_obj():
    """Original unit-equivalent colored boxes and a closed cavity, plus two refusal inputs."""
    output = ROOT / ".local/evidence/obj-20261021/inputs"
    output.mkdir(parents=True, exist_ok=True)
    quads = [(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)]
    colors = {"White": "0.9529411764705882 0.9529411764705882 0.9411764705882353",
              "Red": "0.8392156862745098 0.3215686274509804 0.2823529411764706",
              "Blue": "0.14901960784313725 0.48627450980392156 0.7098039215686275"}
    materials = "# Original opaque diffuse materials\n" + "".join(
        f"newmtl {name}\nKd {rgb}\nd 1\nillum 1\n\n" for name, rgb in colors.items())
    files = {"palette.mtl": materials}
    for units, factor in (("mm",1), ("in",1/25.4)):
        lines = ["# Original two-box fixture; source units: "+units, "mtllib palette.mtl", "g Assembly"]
        for part, offset in enumerate((0,30)):
            lines.append("o "+("Painted_box" if part == 0 else "Blue_box"))
            lines += ["v "+" ".join(format((value+(offset if axis==0 else 0))*factor,".17g")
                                     for axis,value in enumerate(vertex)) for vertex in VERTICES]
            for face_index, face in enumerate(quads):
                material = ("Red" if face_index == 1 else "Blue" if face_index == 3 else "White") if part == 0 else "Blue"
                lines.append("usemtl "+material)
                lines.append("f "+" ".join(str(i+1) if part == 0 else str(i-8) for i in face))
        files["assembly-"+units+".obj"] = "\n".join(lines)+"\n"
    lines = ["# One editable hollow solid, outer 20 mm and inner 16 mm", "mtllib palette.mtl", "o Hollow_box", "usemtl Red"]
    for inner in (False,True):
        vertices = [tuple(2+v*0.8 for v in vertex) if inner else vertex for vertex in VERTICES]
        lines += ["v "+" ".join(map(str,vertex)) for vertex in vertices]
        for face in FACES:
            face = tuple(reversed(face)) if inner else face
            lines.append("f "+" ".join(str(i+1+(8 if inner else 0)) for i in face))
    files["hollow-mm.obj"] = "\n".join(lines)+"\n"
    files["concave-mm.obj"] = ("# Planar L, area 5 mm2; begins at a vertex unsuitable for a simple triangle fan\n"
        "o Concave_L\nv 0 0 0\nv 3 0 0\nv 3 1 0\nv 1 1 0\nv 1 3 0\nv 0 3 0\n"
        "vt 0 0\nvt 1 0\nvt 1 0.3333333333\nvt 0.3333333333 0.3333333333\nvt 0.3333333333 1\nvt 0 1\n"
        "vn 0 0 1\ns 1\nf 2/2/1 3/3/1 4/4/1 5/5/1 6/6/1 1/1/1\n")
    files["bad-index.obj"] = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 99\n"
    files["texture.mtl"] = "newmtl Texture\nKd 1 1 1\nmap_Kd unavailable.png\n"
    files["unsupported-texture.obj"] = "mtllib texture.mtl\no Textured\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Texture\nf 1 2 3\n"
    cases = []
    for name, text in files.items():
        data = text.encode("utf-8"); (output/name).write_bytes(data)
        cases.append(dict(file=name,sha256=hashlib.sha256(data).hexdigest()))
    manifest = dict(provenance="Original synthetic inputs from scripts/generate_fixtures.py --obj",cases=cases,
        expectations={"assembly-mm.obj":dict(units="Millimeters",bounds_mm=[[0,0,0],[50,20,20]],triangles=24,volume_mm3=16000,objects=2,group="Assembly"),
                      "assembly-in.obj":dict(units="Inches",bounds_mm=[[0,0,0],[50,20,20]],triangles=24,volume_mm3=16000,objects=2,group="Assembly"),
                      "hollow-mm.obj":dict(units="Millimeters",bounds_mm=[[0,0,0],[20,20,20]],triangles=24,volume_mm3=3904,objects=1,shells=2),
                      "concave-mm.obj":dict(units="Millimeters",bounds_mm=[[0,0,0],[3,3,0]],triangles=4,area_mm2=5,objects=1),
                      "bad-index.obj":"reject invalid reference", "unsupported-texture.obj":"reject unsupported texture without reading image"})
    (output/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(generated=len(cases),output=str(output))))


def generate_ply():
    """Original PLY mesh/color/unit inputs; each file represents one mesh."""
    output = ROOT / ".local/evidence/ply-20261021/inputs"
    output.mkdir(parents=True, exist_ok=True)
    quads = [(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)]
    white, red, blue = (243,243,240), (214,82,72), (38,124,181)

    def encode(vertices, faces, encoding="ascii", vertex_colors=None, face_colors=None, alpha=False):
        header = ["ply", "format "+encoding+" 1.0", "comment Original synthetic mesh; caller chooses units",
                  "element vertex "+str(len(vertices)), "property double x", "property double y", "property double z"]
        if vertex_colors is not None:
            header += ["property uchar "+c for c in ("red","green","blue")]
            if alpha: header += ["property uchar alpha"]
        header += ["element face "+str(len(faces)), "property list uchar int vertex_indices"]
        if face_colors is not None: header += ["property uchar "+c for c in ("red","green","blue")]
        data = ("\n".join(header)+"\nend_header\n").encode("ascii")
        rows = [("ddd"+("BBB" if vertex_colors is not None else "")+("B" if alpha else ""),
                 list(v)+(list(vertex_colors[i]) if vertex_colors is not None else [])) for i,v in enumerate(vertices)]
        rows += [("B"+"i"*len(f)+("BBB" if face_colors is not None else ""),
                  [len(f),*f]+(list(face_colors[i]) if face_colors is not None else [])) for i,f in enumerate(faces)]
        if encoding == "ascii":
            return data + ("\n".join(" ".join(format(x,".17g") if isinstance(x,float) else str(x) for x in row) for _,row in rows)+"\n").encode("ascii")
        endian = "<" if encoding == "binary_little_endian" else ">"
        return data + b"".join(struct.pack(endian+fmt,*row) for fmt,row in rows)

    vertices = list(VERTICES)+[(x+30,y,z) for x,y,z in VERTICES]
    faces = quads+[tuple(i+8 for i in f) for f in quads]
    colors = [red if i==1 else blue if i==3 else white for i in range(6)]+[blue]*6
    files, expectations = {}, {}
    for name,encoding,factor,units in (("assembly-mm.ply","ascii",1,"Millimeters"),
            ("assembly-in-le.ply","binary_little_endian",1/25.4,"Inches"),
            ("assembly-in-be.ply","binary_big_endian",1/25.4,"Inches")):
        vv = [tuple(value*factor for value in v) for v in vertices]
        files[name] = encode(vv,faces,encoding,[(0,255,0)]*16,colors)
        expectations[name] = dict(units=units,bounds_mm=[[0,0,0],[50,20,20]],triangles=24,
            volume_mm3=16000,shells=2,objects=1,face_rgb_counts={"243,243,240":8,"214,82,72":2,"38,124,181":14})
    vv = list(VERTICES)+[tuple(2+value*.8 for value in v) for v in VERTICES]
    ff = list(FACES)+[tuple(i+8 for i in reversed(f)) for f in FACES]
    files['hollow-mm.ply'] = encode(vv,ff,"binary_little_endian",face_colors=[red]*24)
    expectations['hollow-mm.ply'] = dict(units="Millimeters",bounds_mm=[[0,0,0],[20,20,20]],
        triangles=24,volume_mm3=3904,shells=2,objects=1,face_rgb_counts={"214,82,72":24})
    vv = [(0,0,0),(3,0,0),(0,3,0)]
    files['vertex-colors.ply'] = encode(vv,[(0,1,2)],vertex_colors=[(255,0,0,255),(0,255,0,255),(0,0,255,255)],alpha=True)
    expectations['vertex-colors.ply'] = dict(units="Millimeters",triangles=1,area_mm2=4.5,average_rgb=[85,85,85])
    files['concave-mm.ply'] = encode([(0,0,0),(3,0,0),(3,1,0),(1,1,0),(1,3,0),(0,3,0)],
        [(1,2,3,4,5,0)],face_colors=[blue])
    expectations['concave-mm.ply'] = dict(units="Millimeters",triangles=4,area_mm2=5)
    files['bad-index.ply'] = encode(vv,[(0,1,99)])
    files['truncated.ply'] = files['assembly-in-le.ply'][:-2]
    files['transparent.ply'] = encode(vv,[(0,1,2)],vertex_colors=[(255,0,0,128)]*3,alpha=True)
    files['partial-rgb.ply'] = files['vertex-colors.ply'].replace(b'property uchar blue\n',b'')
    for name in ('bad-index.ply','truncated.ply','transparent.ply','partial-rgb.ply'):
        expectations[name] = dict(reject=True)
    cases = []
    for name,data in files.items():
        (output/name).write_bytes(data)
        cases.append(dict(file=name,sha256=hashlib.sha256(data).hexdigest()))
    (output/'manifest.json').write_text(json.dumps(dict(provenance="Original synthetic inputs from scripts/generate_fixtures.py --ply",
        cases=cases,expectations=expectations),indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(generated=len(cases),output=str(output))))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--performance", action="store_true", help="Generate opt-in limit fixtures under .local; leave the original corpus untouched")
    mode.add_argument("--survey-image", action="store_true", help="Generate only a small original PNG under the local UI survey inputs")
    mode.add_argument("--mirror-measurement", action="store_true", help="Generate only the asymmetric mirror/ruler inputs under .local")
    mode.add_argument("--text-emboss", action="store_true", help="Generate only the analytic notched label plate under .local")
    mode.add_argument("--image-relief", action="store_true", help="Generate only original image relief rasters under .local")
    mode.add_argument("--hollow", action="store_true", help="Generate only original hollowing solids under .local")
    mode.add_argument("--simplify", action="store_true", help="Generate only original simplification surfaces under .local")
    mode.add_argument("--smooth", action="store_true", help="Generate only original noisy smoothing surfaces under .local")
    mode.add_argument("--separate", action="store_true", help="Generate only disconnected material/cavity inputs under .local")
    mode.add_argument("--extrude-down", action="store_true", help="Generate only three small downward-extension solids under .local")
    mode.add_argument("--materials", action="store_true", help="Generate only the small colored assembly and material refusal inputs")
    mode.add_argument("--image-contour", action="store_true", help="Generate only contour silhouette, threshold, alpha and refusal rasters")
    mode.add_argument("--surface-paint", action="store_true", help="Generate only per-triangle color boxes and refusal inputs")
    mode.add_argument("--obj", action="store_true", help="Generate only original colored OBJ/MTL unit, cavity and refusal inputs")
    mode.add_argument("--ply", action="store_true", help="Generate only original ASCII/binary PLY unit, color, cavity and refusal inputs")
    mode.add_argument("--gltf", action="store_true", help="Generate only original glTF/GLB units, colors, hierarchy, cavity and refusal inputs")
    mode.add_argument("--distribution", action="store_true", help="Generate only a small colored group/unequal-box distribution input")
    mode.add_argument("--center-on-plate", action="store_true", help="Generate an elevated colored group for centering and bed placement")
    mode.add_argument("--surface-brush", action="store_true", help="Generate subdivided colored boxes and a sealed cavity for brush painting")
    args = parser.parse_args()
    if args.surface_brush:
        from surface_brush_fixtures import generate
        generate(three_mf_bytes)
    elif args.center_on_plate:
        from center_on_plate_fixtures import generate
        generate(VERTICES, FACES, three_mf_bytes)
    elif args.distribution:
        from distribution_fixtures import generate
        generate(VERTICES, FACES, three_mf_bytes)
    elif args.gltf:
        from gltf_fixtures import generate
        generate(VERTICES, FACES)
    elif args.ply:
        generate_ply()
    elif args.obj:
        generate_obj()
    elif args.surface_paint:
        generate_surface_paint()
    elif args.image_contour:
        generate_image_contour()
    elif args.materials:
        generate_materials()
    elif args.extrude_down:
        generate_extrude_down()
    elif args.separate:
        generate_separate()
    elif args.smooth:
        generate_smooth()
    elif args.simplify:
        generate_simplify()
    elif args.hollow:
        generate_hollow()
    elif args.image_relief:
        generate_image_relief()
    elif args.text_emboss:
        generate_text_emboss()
    elif args.mirror_measurement:
        generate_mirror_measurement()
    elif args.survey_image:
        generate_survey_image()
    elif args.performance:
        generate_performance()
    else:
        main()
