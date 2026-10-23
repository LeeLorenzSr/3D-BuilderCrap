"""Original subdivided boxes for visible surface-brush workflows."""
import hashlib
import io
import json
import platform
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT/'.local/fixtures/surface-brush'
URI = 'http://schemas.microsoft.com/3dmanufacturing/core/2015/02'
NS = '{'+URI+'}'


def cube_grid():
    """20mm cube, 2mm cells; each outward face has 200 triangles."""
    planes = [((0,0,0),(0,20,0),(20,0,0)), ((0,0,20),(20,0,0),(0,20,0)),
              ((0,0,0),(20,0,0),(0,0,20)), ((20,0,0),(0,20,0),(0,0,20)),
              ((20,20,0),(-20,0,0),(0,0,20)), ((0,20,0),(0,-20,0),(0,0,20))]
    vertices, triangles, lookup = [], [], {}

    def index(origin, u, v, x, y):
        point = tuple(origin[k]+u[k]*x/10+v[k]*y/10 for k in range(3))
        if point not in lookup:
            lookup[point] = len(vertices)
            vertices.append(point)
        return lookup[point]

    for origin, u, v in planes:
        for y in range(10):
            for x in range(10):
                a,b,c,d = [index(origin,u,v,i,j) for i,j in ((x,y),(x+1,y),(x+1,y+1),(x,y+1))]
                triangles.extend(((a,b,c),(a,c,d)))
    return vertices, triangles


def generate(template):
    ET.register_namespace('', URI)
    with zipfile.ZipFile(io.BytesIO(template('millimeter',1))) as archive:
        base_parts = {name: archive.read(name) for name in archive.namelist()}
    vertices, faces = cube_grid()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    cases = []
    for name in ('dense-cube','dense-pair','sealed-shell'):
        parts = dict(base_parts)
        model = ET.fromstring(parts['3D/3dmodel.model'])
        resources, build = model.find(NS+'resources'), model.find(NS+'build')
        resources.clear(); build.clear()
        palette = ET.SubElement(resources,NS+'basematerials',id='1')
        ET.SubElement(palette,NS+'base',name='White',displaycolor='#F3F3F0FF')
        ET.SubElement(palette,NS+'base',name='Blue',displaycolor='#267CB5FF')
        points, triangles = list(vertices), list(faces)
        if name == 'sealed-shell':
            offset = len(points)
            points.extend(tuple(2+.8*c for c in p) for p in vertices)
            triangles.extend((a+offset,c+offset,b+offset) for a,b,c in faces)
        for obj_id, label, color in [('2','Brush cube','0')]+([('3','Blue companion','1')] if name=='dense-pair' else []):
            obj = ET.SubElement(resources,NS+'object',id=obj_id,type='model',name=label,pid='1',pindex=color)
            mesh = ET.SubElement(obj,NS+'mesh')
            v = ET.SubElement(mesh,NS+'vertices')
            for point in points:
                ET.SubElement(v,NS+'vertex',**{axis:format(point[k],'.12g') for k,axis in enumerate('xyz')})
            t = ET.SubElement(mesh,NS+'triangles')
            for face in triangles:
                ET.SubElement(t,NS+'triangle',**{f'v{k+1}':str(index) for k,index in enumerate(face)})
        if name == 'dense-pair':
            group = ET.SubElement(resources,NS+'object',id='4',type='model',name='Brush pair')
            components = ET.SubElement(group,NS+'components')
            ET.SubElement(components,NS+'component',objectid='2')
            ET.SubElement(components,NS+'component',objectid='3',transform='1 0 0 0 1 0 0 0 1 30 0 0')
            ET.SubElement(build,NS+'item',objectid='4')
        else:
            ET.SubElement(build,NS+'item',objectid='2')
        parts['3D/3dmodel.model'] = ET.tostring(model,encoding='utf-8',xml_declaration=True)
        path = OUTPUT/(name+'.3mf')
        with zipfile.ZipFile(path,'w') as archive:
            for part,data in parts.items():
                archive.writestr(zipfile.ZipInfo(part,date_time=(2020,1,1,0,0,0)),data)
        cases.append(dict(file=path.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                          leaves=2 if name=='dense-pair' else 1,groups=1 if name=='dense-pair' else 0,
                          triangles_per_leaf=len(triangles),volume_per_leaf_mm3=3904 if name=='sealed-shell' else 8000))
    manifest = dict(provenance='Original subdivided cubes; scripts/generate_fixtures.py --surface-brush',
                    face_order=['bottom','top','front','right','back','left'],triangles_per_face=200,cases=cases)
    (OUTPUT/'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(generated=len(cases),directory=str(OUTPUT))))


def verify():
    import numpy as np
    import trimesh
    manifest = json.loads((OUTPUT/'manifest.json').read_text(encoding='utf-8'))
    results=[]
    for case in manifest['cases']:
        path = OUTPUT/case['file']
        assert hashlib.sha256(path.read_bytes()).hexdigest()==case['sha256']
        with zipfile.ZipFile(path) as archive:
            model=ET.fromstring(archive.read('3D/3dmodel.model'))
        assert model.get('unit')=='millimeter'
        objects=model.find(NS+'resources').findall(NS+'object')
        palette=model.find(NS+'resources').find(NS+'basematerials')
        assert [(m.get('name'),m.get('displaycolor')) for m in palette]==[('White','#F3F3F0FF'),('Blue','#267CB5FF')]
        leaves=[o for o in objects if o.find(NS+'mesh') is not None]
        assert len(leaves)==case['leaves']
        for obj in leaves:
            assert obj.get('pid')=='1' and obj.get('pindex')==('1' if obj.get('name')=='Blue companion' else '0')
            mesh=trimesh.Trimesh(vertices=[[float(p.get(a)) for a in 'xyz'] for p in obj.findall('.//'+NS+'vertex')],
                                 faces=[[int(t.get('v'+str(i))) for i in (1,2,3)] for t in obj.findall('.//'+NS+'triangle')],process=False)
            assert len(mesh.faces)==case['triangles_per_leaf'] and mesh.is_watertight and mesh.is_winding_consistent
            assert np.allclose(mesh.bounds,[[0,0,0],[20,20,20]],atol=.001)
            assert np.isclose(mesh.volume,case['volume_per_leaf_mm3'],atol=.001)
            components=mesh.split(only_watertight=False)
            assert len(components)==(2 if case['file']=='sealed-shell.3mf' else 1)
            if len(components)==2:
                assert sorted(round(c.volume) for c in components)==[-4096,8000]
        groups=[o for o in objects if o.find(NS+'components') is not None]
        assert len(groups)==case['groups']
        if groups:
            assert [c.get('objectid') for c in groups[0].find(NS+'components')]==['2','3']
            assert groups[0].find(NS+'components')[1].get('transform')=='1 0 0 0 1 0 0 0 1 30 0 0'
        results.append(case)
    report=dict(status='passed',python=platform.python_version(),numpy=np.__version__,trimesh=trimesh.__version__,cases=results,
                limitations='Analytic input dimensions, topology, shell ownership and material defaults only; no brush or original-app behavior claim.')
    (OUTPUT/'verification.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(status='passed',report=str(OUTPUT/'verification.json'))))
