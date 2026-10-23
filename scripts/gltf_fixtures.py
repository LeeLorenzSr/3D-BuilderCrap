"""Original, small glTF inputs; routed through the repository fixture commands."""
import base64
import copy
import hashlib
import json
import math
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / '.local/evidence/gltf-20261021/inputs'


def linear(rgb):
    return [v / 255 / 12.92 if v / 255 <= .04045 else ((v / 255 + .055) / 1.055) ** 2.4 for v in rgb]


class Asset:
    def __init__(self):
        self.data = bytearray()
        self.doc = dict(asset=dict(version='2.0', generator='Original Builder fixture'),
                        scene=0, scenes=[dict(nodes=[0])], nodes=[], meshes=[],
                        accessors=[], bufferViews=[], materials=[])

    def accessor(self, rows, kind, component=5126, normalized=False):
        while len(self.data) % 4:
            self.data.append(0)
        start = len(self.data)
        fmt = {5126: 'f', 5125: 'I', 5123: 'H', 5121: 'B'}[component]
        for row in rows:
            values = row if isinstance(row, (list, tuple)) else [row]
            self.data.extend(struct.pack('<' + fmt * len(values), *values))
        view = len(self.doc['bufferViews'])
        self.doc['bufferViews'].append(dict(buffer=0, byteOffset=start, byteLength=len(self.data)-start))
        a = dict(bufferView=view, componentType=component, count=len(rows), type=kind)
        if normalized:
            a['normalized'] = True
        if kind == 'VEC3' and component == 5126:
            a.update(min=[min(row[i] for row in rows) for i in range(3)],
                     max=[max(row[i] for row in rows) for i in range(3)])
        index = len(self.doc['accessors'])
        self.doc['accessors'].append(a)
        return index

    def material(self, name, rgb):
        index = len(self.doc['materials'])
        self.doc['materials'].append(dict(name=name, pbrMetallicRoughness=dict(
            baseColorFactor=linear(rgb)+[1], metallicFactor=0, roughnessFactor=1)))
        return index

    def mesh(self, name, vertices, face_materials, materials, divisor=1000):
        # Inverse of the successor's Y-up to Z-up conversion.
        positions = [(x/divisor, z/divisor, -y/divisor) for x, y, z in vertices]
        p = self.accessor(positions, 'VEC3')
        primitives = []
        for material in materials:
            faces = [f for f, m in face_materials if m == material]
            if faces:
                indices = self.accessor([i for face in faces for i in face], 'SCALAR', 5123)
                primitives.append(dict(attributes=dict(POSITION=p), indices=indices, material=material))
        index = len(self.doc['meshes'])
        self.doc['meshes'].append(dict(name=name, primitives=primitives))
        return index

    def encoded(self, binary=False, uri=None):
        doc = copy.deepcopy(self.doc)
        doc['buffers'] = [dict(byteLength=len(self.data))]
        if not binary:
            doc['buffers'][0]['uri'] = uri or 'data:application/octet-stream;base64,' + base64.b64encode(self.data).decode()
            return json.dumps(doc, indent=2).encode() + b'\n'
        text = json.dumps(doc, separators=(',', ':')).encode()
        text += b' ' * (-len(text) % 4)
        data = bytes(self.data) + b'\0' * (-len(self.data) % 4)
        return (struct.pack('<III', 0x46546c67, 2, 28+len(text)+len(data)) +
                struct.pack('<II', len(text), 0x4e4f534a) + text +
                struct.pack('<II', len(data), 0x004e4942) + data)


def generate(vertices, faces):
    OUTPUT.mkdir(parents=True, exist_ok=True)
    files, expected = {}, {}
    asset = Asset()
    white = asset.material('White', (243, 243, 240))
    red = asset.material('Red', (214, 82, 72))
    blue = asset.material('Blue', (38, 124, 181))
    painted = [(f, red if i in (2, 3) else blue if i in (6, 7) else white) for i, f in enumerate(faces)]
    asset.mesh('Painted box', vertices, painted, [white, red, blue])
    asset.mesh('Blue box', vertices, [(f, blue) for f in faces], [blue])
    asset.doc['nodes'] = [dict(name='Assembly', translation=[.005, .002, -.003], children=[1, 2]),
                         dict(name='Painted box', mesh=0, translation=[-.005, -.002, .003]),
                         dict(name='Blue box', mesh=1, matrix=[1,0,0,0,0,1,0,0,0,0,1,0,.025,-.002,.003,1])]
    for name, data in [('assembly.gltf', asset.encoded()), ('assembly.glb', asset.encoded(True)),
                       ('assembly-external.gltf', asset.encoded(uri='assembly.bin'))]:
        files[name] = data
        expected[name] = dict(units='Meters', bounds_mm=[[0,0,0],[50,20,20]], triangles=24,
                              objects=2, group='Assembly', volume_mm3=16000,
                              face_rgb_counts={'243,243,240':8, '214,82,72':2, '38,124,181':14})
    files['assembly.bin'] = bytes(asset.data)
    hollow = Asset()
    red = hollow.material('Red', (214,82,72))
    vv = list(vertices)+[tuple(2+v*.8 for v in p) for p in vertices]
    ff = list(faces)+[tuple(i+8 for i in reversed(f)) for f in faces]
    hollow.mesh('Hollow box', vv, [(f, red) for f in ff], [red])
    hollow.doc['nodes'] = [dict(name='Hollow box', mesh=0)]
    files['hollow.glb'] = hollow.encoded(True)
    expected['hollow.glb'] = dict(units='Meters', bounds_mm=[[0,0,0],[20,20,20]],
                                 objects=1, triangles=24, shells=2, volume_mm3=3904)
    raw = Asset()
    red = raw.material('Red', (214,82,72))
    raw.mesh('Raw millimeter box', vertices, [(f,red) for f in faces], [red], divisor=1)
    raw.doc['nodes'] = [dict(name='Raw millimeter box', mesh=0)]
    files['raw-millimeters.gltf'] = raw.encoded()
    expected['raw-millimeters.gltf'] = dict(units='Millimeters', bounds_mm=[[0,0,0],[20,20,20]], objects=1, triangles=12, volume_mm3=8000)

    colors = Asset()
    positions = colors.accessor([(0,0,0),(.003,0,0),(0,0,-.003)], 'VEC3')
    byte_colors = colors.accessor([(255,0,0,255),(0,255,0,255),(0,0,255,255)], 'VEC4', 5121, True)
    float_colors = colors.accessor([(.5,.5,.5,1)]*3, 'VEC4')
    colors.doc['materials'] = [dict(name='Linear factor', pbrMetallicRoughness=dict(baseColorFactor=[.25,.5,1,1])),
                               dict(name='Second factor', pbrMetallicRoughness=dict(baseColorFactor=[.5,.25,.125,1]))]
    colors.doc['meshes'] = [dict(primitives=[dict(attributes=dict(POSITION=positions,COLOR_0=c),material=i)]) for i,c in enumerate((byte_colors,float_colors))]
    colors.doc['nodes'] = [dict(name='Color tests',children=[1,2]), dict(name='Byte colors',mesh=0),dict(name='Float colors',mesh=1,translation=[.005,0,0])]
    files['vertex-colors.gltf'] = colors.encoded()
    expected['vertex-colors.gltf'] = dict(units='Meters', objects=2, triangles=2, area_mm2=9,
                                         linear_face_colors=[[1/12,1/6,1/3],[.25,.125,.0625]])

    # Use meter vertices and asymmetric tetrahedron; two instances, one mirrored.
    transforms = Asset()
    red = transforms.material('Red',(214,82,72))
    tetra = [(0,0,0),(12,0,0),(0,17,0),(0,0,26)]
    tf = [(0,2,1),(0,1,3),(1,2,3),(2,0,3)]
    transforms.mesh('Tetra',tetra,[(f,red) for f in tf],[red])
    transforms.doc['nodes'] = [dict(name='Instances',translation=[.005,.002,-.003],children=[1,2]),
                               dict(name='Rotated',mesh=0,rotation=[0,math.sqrt(.5),0,math.sqrt(.5)]),
                               dict(name='Mirrored',mesh=0,translation=[.05,0,0],scale=[-1,1,1])]
    files['instances.glb'] = transforms.encoded(True)
    expected['instances.glb'] = dict(units='Meters',objects=2,triangles=8,volume_mm3=1768,
                                    bounds_mm=[[-12,3,2],[55,20,28]])

    topology = Asset()
    p = topology.accessor([(0,0,0),(.003,0,0),(.003,0,-.003),(0,0,-.003)],'VEC3')
    strip = topology.accessor([0,1,3,2],'SCALAR',5123)
    fan = topology.accessor([0,1,2,3],'SCALAR',5123)
    topology.doc['meshes'] = [dict(primitives=[dict(attributes=dict(POSITION=p),indices=strip,mode=5)]),
                              dict(primitives=[dict(attributes=dict(POSITION=p),indices=fan,mode=6)])]
    topology.doc['nodes'] = [dict(name='Topology',children=[1,2]),dict(name='Strip',mesh=0),dict(name='Fan',mesh=1,translation=[.005,0,0])]
    files['strip-fan.gltf'] = topology.encoded()
    expected['strip-fan.gltf'] = dict(units='Meters',objects=2,triangles=4,area_mm2=18)

    for name, mutate in [
        ('bad-accessor.gltf', lambda d: d['accessors'][0].update(count=999)),
        ('unsafe-companion.gltf', lambda d: d['buffers'][0].update(uri='../outside.bin')),
        ('transparent.gltf', lambda d: d['materials'][0].update(alphaMode='BLEND')),
        ('texture.gltf', lambda d: d['materials'][0]['pbrMetallicRoughness'].update(baseColorTexture=dict(index=0))),
        ('animated.gltf', lambda d: d.update(animations=[dict(channels=[],samplers=[])]))]:
        doc = json.loads(asset.encoded())
        mutate(doc)
        files[name] = json.dumps(doc).encode()
        expected[name] = dict(reject=True)
    files['truncated.glb'] = files['assembly.glb'][:-7]
    expected['truncated.glb'] = dict(reject=True)
    records = []
    for name,data in files.items():
        (OUTPUT/name).write_bytes(data)
        records.append(dict(file=name,sha256=hashlib.sha256(data).hexdigest()))
    (OUTPUT/'manifest.json').write_text(json.dumps(dict(provenance='Original synthetic inputs from scripts/generate_fixtures.py --gltf',
        cases=records,expectations=expected),indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(generated=len(records),output=str(OUTPUT))))


def verify():
    import platform
    import numpy as np
    import trimesh
    manifest = json.loads((OUTPUT/'manifest.json').read_text())
    for case in manifest['cases']:
        assert hashlib.sha256((OUTPUT/case['file']).read_bytes()).hexdigest() == case['sha256'], case['file']
    reports = []
    for name, expected in manifest['expectations'].items():
        if expected.get('reject') or name in ('vertex-colors.gltf','strip-fan.gltf'):
            continue  # Their color/topology contract is exercised through Core.
        scene = trimesh.load(OUTPUT/name,force='scene',process=False)
        mesh = scene.to_mesh()
        # glTF always means meters to this independent reader; override raw source as specified.
        factor = 1 if expected['units']=='Millimeters' else 1000
        basis = np.array([[factor,0,0,0],[0,0,-factor,0],[0,factor,0,0],[0,0,0,1]])
        mesh.apply_transform(basis)
        mesh.merge_vertices()
        assert len(mesh.faces) == expected['triangles'], name+' triangles'
        assert np.allclose(mesh.bounds,expected['bounds_mm'],atol=.001,rtol=0), (name,mesh.bounds)
        assert abs(mesh.volume-expected['volume_mm3']) < .01, (name,mesh.volume)
        assert mesh.is_watertight and mesh.is_winding_consistent, name+' shell orientation'
        reports.append(dict(file=name,bounds_mm=mesh.bounds.tolist(),triangles=len(mesh.faces),volume_mm3=float(mesh.volume)))
    (OUTPUT/'verification.json').write_text(json.dumps(dict(status='PASS',python=platform.python_version(),numpy=np.__version__,trimesh=trimesh.__version__,
        method='Independent trimesh scene transforms and mesh geometry; color/primitive refusal contracts checked separately in Core',cases=reports),indent=2)+'\n')
    print(json.dumps(dict(status='PASS',cases=len(reports),output=str(OUTPUT/'verification.json'))))
