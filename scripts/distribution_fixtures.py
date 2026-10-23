"""Small original distribution assembly, routed through the fixture commands."""
import hashlib
import io
import json
import platform
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / '.local/fixtures/distribution'
URI = 'http://schemas.microsoft.com/3dmanufacturing/core/2015/02'
NS = '{' + URI + '}'
# Name, dimensions, world translation, opaque color. Three middle leaves form one unit.
BOXES = [
    ('Left anchor', (10, 10, 12), (0, 0, 0), '#E6A23CFF'),
    ('Blue member', (6, 10, 14), (18, 0, 0), '#267CB5FF'),
    ('Green member', (10, 10, 10), (21, 16, 0), '#46A56CFF'),
    ('Violet member', (16, 10, 8), (18, 32, 0), '#9367B4FF'),
    ('Right anchor', (30, 10, 16), (90, 0, 0), '#D65E4AFF'),
]


def generate(vertices, faces, template):
    ET.register_namespace('', URI)
    with zipfile.ZipFile(io.BytesIO(template('millimeter', 1))) as archive:
        parts = {name: archive.read(name) for name in archive.namelist()}
    model = ET.fromstring(parts['3D/3dmodel.model'])
    resources, build = model.find(NS+'resources'), model.find(NS+'build')
    resources.clear()
    build.clear()
    palette = ET.SubElement(resources, NS+'basematerials', id='1')
    for i, (name, size, _, color) in enumerate(BOXES):
        ET.SubElement(palette, NS+'base', name=name, displaycolor=color)
        obj = ET.SubElement(resources, NS+'object', id=str(i+2), type='model', name=name,
                            pid='1', pindex=str(i))
        mesh = ET.SubElement(obj, NS+'mesh')
        points = ET.SubElement(mesh, NS+'vertices')
        for point in vertices:
            ET.SubElement(points, NS+'vertex', **{axis: str(point[j]*size[j]/20)
                                                 for j, axis in enumerate('xyz')})
        triangles = ET.SubElement(mesh, NS+'triangles')
        for face in faces:
            ET.SubElement(triangles, NS+'triangle', **{f'v{j+1}': str(v) for j, v in enumerate(face)})
    group = ET.SubElement(resources, NS+'object', id='7', type='model', name='Color trio')
    members = ET.SubElement(group, NS+'components')
    for i, (_, _, position, _) in enumerate(BOXES):
        destination = members if 1 <= i <= 3 else build
        tag = 'component' if 1 <= i <= 3 else 'item'
        ET.SubElement(destination, NS+tag, objectid=str(i+2),
                      transform='1 0 0 0 1 0 0 0 1 ' + ' '.join(map(str, position)))
    ET.SubElement(build, NS+'item', objectid='7')
    parts['3D/3dmodel.model'] = ET.tostring(model, encoding='utf-8', xml_declaration=True)
    package = io.BytesIO()
    with zipfile.ZipFile(package, 'w') as archive:
        for name, data in parts.items():
            archive.writestr(zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0)), data)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    path = OUTPUT / 'distribution-assembly.3mf'
    path.write_bytes(package.getvalue())
    manifest = dict(provenance='Original boxes from scripts/generate_fixtures.py --distribution',
                    file=path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    units='millimeter', leaves=5, groups=1, root_units=3,
                    x_centers_mm=[5, 26, 105], x_widths_mm=[10, 16, 30],
                    expected_center_group_translation_mm=29, expected_gap_group_translation_mm=24,
                    expected_equal_gap_mm=32)
    (OUTPUT/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(generated=str(path), sha256=manifest['sha256'])))


def verify():
    import numpy as np
    import trimesh
    manifest = json.loads((OUTPUT/'manifest.json').read_text(encoding='utf-8'))
    path = OUTPUT / manifest['file']
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    assert digest == manifest['sha256'], 'Fixture digest changed'
    with zipfile.ZipFile(path) as archive:
        model = ET.fromstring(archive.read('3D/3dmodel.model'))
    resources, build = model.find(NS+'resources'), model.find(NS+'build')
    assert model.get('unit') == 'millimeter' and len(build) == 3
    objects = {obj.get('id'): obj for obj in resources.findall(NS+'object')}
    group = objects['7'].find(NS+'components')
    assert [item.get('objectid') for item in group] == ['3', '4', '5']
    transforms = {item.get('objectid'): item.get('transform') for item in [*build, *group]}
    palette = resources.find(NS+'basematerials')
    results = []
    for i, (name, size, position, color) in enumerate(BOXES):
        obj = objects[str(i+2)]
        points = [[float(v.get(axis)) for axis in 'xyz'] for v in obj.findall('.//'+NS+'vertex')]
        faces = [[int(f.get('v'+str(j))) for j in (1, 2, 3)] for f in obj.findall('.//'+NS+'triangle')]
        mesh = trimesh.Trimesh(vertices=points, faces=faces, process=False)
        assert mesh.is_watertight and mesh.is_winding_consistent
        assert np.allclose(mesh.extents, size, atol=.001) and np.isclose(mesh.volume, np.prod(size))
        transform = list(map(float, transforms[str(i+2)].split()))
        assert transform[:9] == [1, 0, 0, 0, 1, 0, 0, 0, 1] and transform[9:] == list(position)
        assert obj.get('pid') == '1' and palette[int(obj.get('pindex'))].get('displaycolor') == color
        results.append(dict(name=name, size_mm=size, position_mm=position,
                            volume_mm3=float(mesh.volume), watertight=True, color=color))
    report = dict(status='passed', sha256=digest, python=platform.python_version(),
                  trimesh=trimesh.__version__, numpy=np.__version__, boxes=results,
                  groups=1, root_units=3,
                  limitations='Independent input topology, dimensions, transforms and colors only; no original-app or successor UI claim.')
    (OUTPUT/'verification.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(status='passed', report=str(OUTPUT/'verification.json'))))
