"""Пакет дополнения лаунчера optional_grass (1.9): пары xrRender_R1/R2.dll автора сборки с разной дальностью травы.
Источник: !!! БЗ - Adm-RAL АДАПТАЦИИ/ДАЛЬНОСТЬ ОТРИСОВКИ ТРАВЫ/<NNN>/bin. В zip: grass/<NNN>/xrRender_R1.dll,
grass/<NNN>/xrRender_R2.dll и MANIFEST.json (file/bytes/sha256). Usage: python3 make_grass_addon.py <out.zip>"""
import sys, re, json, hashlib, zipfile
from pathlib import Path
SRC = Path(__file__).resolve().parents[1] / '!!! БЗ - Adm-RAL АДАПТАЦИИ' / 'ДАЛЬНОСТЬ ОТРИСОВКИ ТРАВЫ'
out = Path(sys.argv[1])
rows, data = [], {}
for d in sorted(SRC.iterdir()):
    m = re.match(r'^(\d{3})', d.name)
    if not d.is_dir() or not m: continue
    for n in ('xrRender_R1.dll', 'xrRender_R2.dll'):
        b = (d / 'bin' / n).read_bytes()
        name = 'grass/%s/%s' % (m.group(1), n)
        data[name] = b
        rows.append({'file': name, 'bytes': len(b), 'sha256': hashlib.sha256(b).hexdigest()})
assert len(rows) == 20, len(rows)
out.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for r in rows: z.writestr(r['file'], data[r['file']])
    z.writestr('MANIFEST.json', json.dumps(rows, ensure_ascii=False, indent=1))
print(out, out.stat().st_size, hashlib.sha256(out.read_bytes()).hexdigest())
