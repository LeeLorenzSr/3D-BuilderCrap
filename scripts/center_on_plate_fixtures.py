"""Original elevated colored group for the Center on plate workflow."""
import hashlib
import json
import platform
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

from distribution_fixtures import BOXES, NS, URI, OUTPUT as SOURCE, generate as generate_source

OUTPUT = Path(__file__).resolve().parents[1] / '.local/fixtures/center-on-plate'
HEIGHTS = {'Blue member': 7, 'Green member': 11, 'Violet member': 15}


def generate(vertices, faces, template):
    generate_source(vertices, faces, template)
    with zipfile.ZipFile(SOURCE / 'distribution-assembly.3mf') as archive:
        parts = {name: archive.read(name) for name in archive.namelist()}
    ET.register_namespace('', URI)
    model = ET.fromstring(parts['3D/3dmodel.model'])
    objects = {o.get('id'): o for o in model.find(NS+'resources').findall(NS+'object')}
    for item in objects['7'].find(NS+'components'):
        transform = item.get('transform').split()
        transform[-1] = str(HEIGHTS[objects[item.get('objectid')].get('name')])
        item.set('transform', ' '.join(transform))
    parts['3D/3dmodel.model'] = ET.tostring(model, encoding='utf-8', xml_declaration=True)
    OUTPUT.mkdir(parents=True, exist_ok=True)
    path = OUTPUT / 'elevated-group.3mf'
    with zipfile.ZipFile(path, 'w') as archive:
        for name, data in parts.items():
            archive.writestr(zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0)), data)
    manifest = dict(provenance='Original elevated variant of distribution boxes; scripts/generate_fixtures.py --center-on-plate',
                    file=path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    units='millimeter', leaves=5, groups=1,
                    selected_group_bounds_mm=[[18, 0, 7], [34, 42, 23]],
                    center_translation_mm=[-26, -21, 0], center_and_bed_translation_mm=[-26, -21, -7])
    (OUTPUT/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(generated=str(path), sha256=manifest['sha256'])))


def inspect(path, centered=False, on_bed=False):
    """Independent XML/trimesh inspection; expected values come from analytic boxes."""
    import numpy as np
    import trimesh
    with zipfile.ZipFile(path) as archive:
        model = ET.fromstring(archive.read('3D/3dmodel.model'))
    resources, build = model.find(NS+'resources'), model.find(NS+'build')
    assert model.get('unit') == 'millimeter' and len(build) == 3
    objects = {o.get('id'): o for o in resources.findall(NS+'object')}
    palette = resources.find(NS+'basematerials')
    expected = {name: (size, [pos[0]-(26 if centered and name in HEIGHTS else 0),
                            pos[1]-(21 if centered and name in HEIGHTS else 0),
                            HEIGHTS.get(name, 0)-(7 if on_bed and name in HEIGHTS else 0)], color)
                for name, size, pos, color in BOXES}
    rows, groups = [], []

    def offset(item):
        values = list(map(float, item.get('transform', '1 0 0 0 1 0 0 0 1 0 0 0').split()))
        assert values[:9] == [1, 0, 0, 0, 1, 0, 0, 0, 1]
        return np.array(values[9:])

    def leaf(item, parent):
        obj = objects[item.get('objectid')]
        name = obj.get('name')
        size, position, color = expected[name]
        mesh = trimesh.Trimesh(
            vertices=[[float(v.get(a)) for a in 'xyz'] for v in obj.findall('.//'+NS+'vertex')],
            faces=[[int(f.get('v'+str(i))) for i in (1, 2, 3)] for f in obj.findall('.//'+NS+'triangle')], process=False)
        assert len(mesh.faces) == 12 and mesh.is_watertight and mesh.is_winding_consistent
        assert np.allclose(mesh.bounds[0], [0, 0, 0], atol=.001)
        assert np.allclose(mesh.extents, size, atol=.001) and np.isclose(mesh.volume, np.prod(size))
        assert np.allclose(offset(item)+parent, position, atol=.001)
        assert palette[int(obj.get('pindex'))].get('displaycolor').upper() == color
        rows.append(dict(name=name, position_mm=position, size_mm=size, color=color, volume_mm3=float(mesh.volume)))

    for item in build:
        obj = objects[item.get('objectid')]
        members = obj.find(NS+'components')
        if members is None:
            leaf(item, np.zeros(3))
        else:
            assert obj.get('name') == 'Color trio'
            names = [objects[m.get('objectid')].get('name') for m in members]
            assert names == list(HEIGHTS)
            groups.append(dict(name=obj.get('name'), members=names))
            for member in members:
                leaf(member, offset(item))
    assert len(rows) == 5 and len(groups) == 1 and {r['name'] for r in rows} == set(expected)
    return dict(status='passed', file=str(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                python=platform.python_version(), trimesh=trimesh.__version__, numpy=np.__version__,
                centered=centered, on_bed=on_bed, objects=rows, groups=groups, triangles=60,
                limitations='Analytic colored boxes only; no general format or original-app parity claim.')


def verify():
    manifest = json.loads((OUTPUT/'manifest.json').read_text(encoding='utf-8'))
    report = inspect(OUTPUT/manifest['file'])
    assert report['sha256'] == manifest['sha256']
    (OUTPUT/'verification.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(dict(status='passed', report=str(OUTPUT/'verification.json'))))
