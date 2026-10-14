"""Build release assets from the verified local ReFix release. No credentials required."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import zipfile
import re

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--release-dir', type=Path, default=Path(__file__).resolve().parent.parent / 'release_final')
    parser.add_argument('--version', default='2026.10.13.1')
    parser.add_argument('--tag', default='v2026.10.13.1')
    args = parser.parse_args()
    project = Path(__file__).resolve().parent
    output = project / 'artifacts' / args.tag
    output.mkdir(parents=True, exist_ok=True)
    base = 'https://github.com/SunChessss/Bolshaya-Zona-ReFix/releases/download/' + args.tag + '/'
    def asset(path, identity, title, group):
        return dict(id=identity, title=title, group=group, url=base+path.name, sha256=sha(path), bytes=path.stat().st_size)
    patch = output / 'Bolshaya_Zona_ReFix_Fixes.zip'
    shutil.copy2(args.release_dir / patch.name, patch)
    # Fail if any pre-existing release archive does not match its own manifest.
    with zipfile.ZipFile(patch) as z:
        rows = json.loads(z.read('MANIFEST.json'))
        assert set(z.namelist()) == {r['file'] for r in rows} | {'MANIFEST.json'}
        for row in rows:
            assert hashlib.sha256(z.read(row['file'])).hexdigest() == row['sha256'], row['file']
    addons = []
    titles = {'optional_season_spring': ('Весна', 'season'), 'optional_season_autumn': ('Осень', 'season'),
              'optional_minimal_hud': ('Минималистичный HUD', 'minimal_hud'), 'optional_new_hud': ('Adm-RAL NEW HUD', 'new_hud')}
    with zipfile.ZipFile(args.release_dir / 'Bolshaya_Zona_ReFix_Optional.zip') as src:
        source_manifest = {r['file']: r for r in json.loads(src.read('MANIFEST.json'))}
        for identity, (title, group) in titles.items():
            prefix = 'optional/' + identity + '/'
            path = output / (identity + '.zip')
            rows = []
            with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as dst:
                for info in src.infolist():
                    if not info.filename.startswith(prefix + 'gamedata/') or info.is_dir():
                        continue
                    data = src.read(info.filename)
                    assert hashlib.sha256(data).hexdigest() == source_manifest[info.filename]['sha256']
                    name = info.filename[len(prefix):]
                    dst.writestr(name, data)
                    rows.append(dict(file=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
                dst.writestr('MANIFEST.json', json.dumps(rows, ensure_ascii=False, indent=2).encode('utf-8'))
            assert rows
            addons.append(asset(path, identity, title, group))
    with zipfile.ZipFile(output / 'ReFixLauncher.zip', 'w', zipfile.ZIP_DEFLATED) as dst:
        for name in ['ReFixLauncher.exe', 'ReFixLauncher.exe.config']:
            dst.write(project / 'dist' / name, name)
        dst.write(project / 'README.md', 'ЛАУНЧЕР_README.md')
    catalog = dict(schema=1, version=args.version,
                   notes='Лаунчер ReFix 1.0\n\nКумулятивные исправления V20.2 Final, включая фикс статического освещения от 13.10.2026.\n\nИсправлены 36 назначений материалов на 11 локациях. Добавления по желанию: весна, осень, минималистичный HUD и Adm-RAL NEW HUD.',
                   patch=asset(patch, 'cumulative_patch', 'Исправления ReFix', 'core'), addons=addons)
    launcher_version = re.search(r'VersionString="([0-9.]+)"', (project / 'src/SelfUpdate.cs').read_text(encoding='utf-8')).group(1)
    catalog['launcher'] = dict(asset(output / 'ReFixLauncher.zip', 'launcher', 'Лаунчер ReFix', 'launcher'), version=launcher_version)
    (output / 'launcher-manifest.json').write_text(json.dumps(catalog, ensure_ascii=False, indent=2), encoding='utf-8')
    files = sorted(p for p in output.iterdir() if p.is_file() and p.name != 'SHA256SUMS.txt')
    (output / 'SHA256SUMS.txt').write_text(''.join(sha(p)+'  '+p.name+'\n' for p in files), encoding='ascii')
    print(output)
    for path in files:
        print(path.name, path.stat().st_size)

if __name__ == '__main__':
    main()
