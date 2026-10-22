"""Verify a Windows Actions artifact; optionally upload to R2 from the Mac Worker project."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--artifact', type=Path, required=True)
parser.add_argument('--bucket', help='Existing Morphy R2 bucket name (required with --publish)')
parser.add_argument('--publish', action='store_true', help='Upload files, then switch Windows latest metadata')
args = parser.parse_args()
manifests = list(args.artifact.resolve().rglob('windows-release.json'))
if len(manifests) != 1:
    raise SystemExit('Artifact must contain exactly one windows-release.json')
manifest = manifests[0]
release = json.loads(manifest.read_text(encoding='utf-8-sig'))
version = release.get('version', '')
if not re.fullmatch(r'\d+\.\d+\.\d+', version):
    raise SystemExit('Invalid version')
filename = f'Morphy-{version}-win-x64-Setup.exe'
if release.get('path') != f'/releases/{filename}':
    raise SystemExit('Unexpected installer path')
installer = manifest.parent / filename
with installer.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
if digest != release.get('sha256') or installer.stat().st_size != release.get('bytes'):
    raise SystemExit('Installer size or SHA-256 does not match metadata')
source = manifest.parent / 'WicHeic-v1.1.16-corresponding-source.zip'
if not source.is_file():
    raise SystemExit('Corresponding codec sources are missing')
print(f'Verified {filename}: {digest}')
if not args.publish:
    print('Verification only. Nothing uploaded. Use --publish --bucket NAME to publish.')
    raise SystemExit(0)
if not args.bucket:
    parser.error('--bucket is required with --publish')
# Run from the existing Worker project, using its installed Wrangler and login.
# Upload the versioned files first. The small manifest is the final publish step.
for file, key, content_type, cache in [
    (installer, f'releases/{filename}', 'application/octet-stream', 'public, max-age=31536000, immutable'),
    (source, f'releases/{source.name}', 'application/zip', 'public, max-age=31536000, immutable'),
    (manifest, 'windows/latest.json', 'application/json', 'no-store'),
]:
    subprocess.run(['npx', '--no-install', 'wrangler', 'r2', 'object', 'put', f'{args.bucket}/{key}',
                    '--file', str(file), '--content-type', content_type, '--cache-control', cache, '--remote'], check=True)
print('Windows release published. Mac release metadata was not modified.')
