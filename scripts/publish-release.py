"""Verify a Mac DMG or a Windows Actions artifact and, with --publish, put it on the website.

  python3 scripts/publish-release.py --dmg build/Morphy-0.2.0.dmg [--publish]
  python3 scripts/publish-release.py --artifact ./windows-artifact [--publish]

Publishing uploads the files to R2 first and inserts the D1 row last, so /download never points
at a file that is still uploading. A published version is never overwritten: bump the version.
Run from the repository root, logged in with `npx wrangler login`.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

BUCKET, DATABASE = 'morphy-releases', 'morphy'
WRANGLER = ['npx', '-y', 'wrangler@4']

def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def mac(dmg):
    m = re.fullmatch(r'Morphy-(\d+\.\d+\.\d+)\.dmg', dmg.name)
    if not m or not dmg.is_file():
        raise SystemExit('Expected an existing file named Morphy-X.Y.Z.dmg')
    release = {'version': m[1], 'path': f'/releases/{dmg.name}', 'sha256': sha256(dmg), 'bytes': dmg.stat().st_size}
    return 'mac', release, [(dmg, 'application/x-apple-diskimage')]

def windows(artifact):
    manifests = list(artifact.resolve().rglob('windows-release.json'))
    if len(manifests) != 1:
        raise SystemExit('Artifact must contain exactly one windows-release.json')
    folder = manifests[0].parent
    release = json.loads(manifests[0].read_text(encoding='utf-8-sig'))
    version = release.get('version', '')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise SystemExit('Invalid version')
    name = f'Morphy-{version}-win-x64-Setup.exe'
    installer, source = folder / name, folder / 'WicHeic-v1.1.16-corresponding-source.zip'
    if release.get('path') != f'/releases/{name}':
        raise SystemExit('Unexpected installer path')
    if sha256(installer) != release.get('sha256') or installer.stat().st_size != release.get('bytes'):
        raise SystemExit('Installer size or SHA-256 does not match windows-release.json')
    if not source.is_file():
        raise SystemExit('Corresponding codec sources are missing')
    release = {k: release[k] for k in ('version', 'path', 'sha256', 'bytes')}
    return 'windows', release, [(source, 'application/zip'), (installer, 'application/octet-stream')]

def d1(sql):
    out = subprocess.run([*WRANGLER, 'd1', 'execute', DATABASE, '--remote', '--json', '--command', sql],
                         check=True, capture_output=True, text=True).stdout
    return json.loads(out)[0]['results']

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
source = parser.add_mutually_exclusive_group(required=True)
source.add_argument('--dmg', type=Path)
source.add_argument('--artifact', type=Path)
parser.add_argument('--publish', action='store_true', help='Upload to R2 and make it the current download')
args = parser.parse_args()
platform, release, files = mac(args.dmg) if args.dmg else windows(args.artifact)
print(f"Verified {platform} {release['version']}: {release['sha256']} ({release['bytes']} bytes)")
if not args.publish:
    print('Verification only. Nothing uploaded. Add --publish to publish.')
    raise SystemExit(0)

# Values below are regex- or hash-checked above, so building SQL from them is safe.
if d1(f"SELECT 1 FROM releases WHERE platform = '{platform}' AND version = '{release['version']}'"):
    raise SystemExit(f"{platform} {release['version']} is already published. Bump the version instead.")
for file, content_type in files:
    subprocess.run([*WRANGLER, 'r2', 'object', 'put', f'{BUCKET}/releases/{file.name}', '--file', str(file),
                    '--content-type', content_type, '--cache-control', 'public, max-age=31536000, immutable', '--remote'], check=True)
d1("INSERT INTO releases (platform, version, path, sha256, bytes) VALUES "
   f"('{platform}', '{release['version']}', '{release['path']}', '{release['sha256']}', {release['bytes']})")
print(f"Published. /download{'' if platform == 'mac' else '/windows'} now serves {release['path']}")
