"""Build a portable preview from the application's canonical SVG assets."""
from pathlib import Path
import argparse
import html
import json
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=root / '.local/evidence/icons-branding-20261020/icon-gallery.html')
args = parser.parse_args()
assets = root / 'prototypes/Builder/Builder.Desktop/Assets/Icons'
catalog = json.loads((assets / 'catalog.json').read_text(encoding='utf-8'))
sections = {}
for entry in catalog:
    svg = (assets / (entry['key'] + '.svg')).read_text(encoding='utf-8')
    parsed = ET.fromstring(svg)
    assert parsed.get('viewBox') == '0 0 24 24'
    assert all(e.tag.rsplit('}', 1)[-1] in ('path', 'title') for e in parsed)
    assert all(not any(a.startswith('on') or a.endswith('href') for a in e.attrib) for e in parsed.iter())
    label = html.escape(entry['label'])
    key = html.escape(entry['key'])
    sections.setdefault(entry['group'], []).append(f'<div class="icon"><div class="samples">{svg}{svg}</div><span>{label}</span><code>{key}.svg</code></div>')
body = ''.join(f'<section><h2>{html.escape(group)}</h2><div class="grid">{"".join(items)}</div></section>' for group, items in sections.items())
page = f'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>3D BuilderCrap — SVG icons</title><style>
*{{box-sizing:border-box}}body{{font:14px "Segoe UI",sans-serif;background:#f6f7f8;color:#25333d;margin:40px auto;padding:0 24px;max-width:1180px}}
h1{{font-size:28px;margin:0 0 8px}}p{{color:#596975}}h2{{font-size:17px;margin:32px 0 14px}}.grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));border-top:1px solid #d6dde2;border-left:1px solid #d6dde2}}
.icon{{background:white;padding:18px 12px;border-right:1px solid #d6dde2;border-bottom:1px solid #d6dde2}}.samples{{display:flex;gap:18px;align-items:center;height:40px;color:#28658c;margin-bottom:12px}}.samples svg:first-child{{width:32px;height:32px}}.samples svg:last-child{{width:16px;height:16px}}span,code{{display:block}}code{{color:#596975;font-size:10px;margin-top:5px}}
</style><h1>3D BuilderCrap</h1><p>{len(catalog)} original SVG icons · 24-unit grid · 1.6-unit rounded strokes · shown at 32 and 16 px</p>{body}
<p>Appearance icons are prepared for future tools; their presence does not imply those features are implemented.</p></html>'''
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(page, encoding='utf-8')
print(f'Validated {len(catalog)} SVGs; wrote {args.output}')
