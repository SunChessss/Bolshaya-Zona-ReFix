"""Build GitHub release assets from the verified local ReFix release. No credentials required.

Since launcher 1.4 the catalog (launcher-manifest.json) carries "profiles": the reference file lists of the
builds (src/InstallProfiles.json). A new patch therefore needs NO new launcher: pass --launcher-from with the tag
of the release that already holds ReFixLauncher.zip (it is copied into the new release too: README links to
.../releases/latest/download/ReFixLauncher.zip), and --addons-from to reuse unchanged add-on packages.

  python package.py --version 2026.10.14.3 --tag v2026.10.14.3 --notes-file notes.txt --addons-from v2026.10.13.1
  (new launcher: omit --launcher-from; dist/ReFixLauncher.exe must be freshly built by build.ps1)
"""
from pathlib import Path
import argparse, hashlib, json, re, shutil, zipfile

def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for b in iter(lambda: f.read(4 << 20), b''): h.update(b)
    return h.hexdigest()

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--release-dir', type=Path, default=Path(__file__).resolve().parent.parent / 'release_final')
    ap.add_argument('--version', required=True)
    ap.add_argument('--tag', required=True)
    ap.add_argument('--notes-file', type=Path, required=True)
    ap.add_argument('--addons-from', help='tag whose add-on packages are reused (unchanged add-ons)')
    ap.add_argument('--launcher-from', help='tag whose ReFixLauncher.zip is reused (no new launcher)')
    a = ap.parse_args()
    project = Path(__file__).resolve().parent
    out = project / 'artifacts' / a.tag
    out.mkdir(parents=True, exist_ok=True)
    base = 'https://github.com/SunChessss/Bolshaya-Zona-ReFix/releases/download/'
    def asset(path, ident, title, group):
        return dict(id=ident, title=title, group=group, url=base + a.tag + '/' + path.name, sha256=sha(path), bytes=path.stat().st_size)
    def previous(tag):
        return json.loads((project / 'artifacts' / tag / 'launcher-manifest.json').read_text(encoding='utf-8'))
    patch = out / 'Bolshaya_Zona_ReFix_Fixes.zip'
    shutil.copy2(a.release_dir / patch.name, patch)
    with zipfile.ZipFile(patch) as z:                     # the archive must match its own manifest
        rows = json.loads(z.read('MANIFEST.json'))
        assert set(z.namelist()) == {r['file'] for r in rows} | {'MANIFEST.json'}
        for r in rows: assert hashlib.sha256(z.read(r['file'])).hexdigest() == r['sha256'], r['file']
    profiles = json.loads((project / 'src/InstallProfiles.json').read_text(encoding='utf-8-sig'))
    patch_sha = sha(patch)
    assert profiles[0]['patchHash'] == patch_sha and profiles[0]['version'] == a.version, 'add the build to src/InstallProfiles.json first'
    game = {r['file']: r['sha256'] for r in rows if (r['file'].startswith(('gamedata/', 'bin/'))) and r['file'] != 'gamedata/config/bz_lord.ltx'}
    assert game == {f['file']: f['sha256'] for f in profiles[0]['files']}, 'profile differs from the Fixes archive'
    if a.addons_from:
        addons = previous(a.addons_from)['addons']
    else:
        raise SystemExit('rebuilding add-ons is not supported here; pass --addons-from')
    if a.launcher_from:
        launcher = previous(a.launcher_from)['launcher']
        # Every release carries the same ReFixLauncher.zip, so the permanent link
        # .../releases/latest/download/ReFixLauncher.zip always works. The catalog keeps the original URL.
        src = project / 'artifacts' / a.launcher_from / 'ReFixLauncher.zip'
        assert sha(src) == launcher['sha256'] and src.stat().st_size == launcher['bytes'], 'ReFixLauncher.zip differs from ' + a.launcher_from
        shutil.copy2(src, out / src.name)
    else:
        lz = out / 'ReFixLauncher.zip'
        with zipfile.ZipFile(lz, 'w', zipfile.ZIP_DEFLATED) as z:
            for n in ['ReFixLauncher.exe', 'ReFixLauncher.exe.config']: z.write(project / 'dist' / n, n)
            z.write(project / 'README.md', 'ЛАУНЧЕР_README.md')
        version = re.search(r'VersionString="([0-9.]+)"', (project / 'src/SelfUpdate.cs').read_text(encoding='utf-8')).group(1)
        launcher = dict(asset(lz, 'launcher', 'Лаунчер ReFix', 'launcher'), version=version)
    catalog = dict(schema=1, version=a.version, notes=a.notes_file.read_text(encoding='utf-8').strip(),
                   patch=asset(patch, 'cumulative_patch', 'Исправления ReFix', 'core'), addons=addons,
                   launcher=launcher, profiles=profiles)
    (out / 'launcher-manifest.json').write_text(json.dumps(catalog, ensure_ascii=False, indent=2), encoding='utf-8')
    files = sorted(p for p in out.iterdir() if p.is_file() and p.name != 'SHA256SUMS.txt')
    (out / 'SHA256SUMS.txt').write_text(''.join(sha(p) + '  ' + p.name + '\n' for p in files), encoding='ascii')
    print(out)
    for p in files: print(p.name, p.stat().st_size)

if __name__ == '__main__':
    main()
