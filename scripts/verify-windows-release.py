"""Verify the public app update feed, website download, and actual installer digest."""
import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request

ORIGIN = 'https://morphy.tpals0409dev.workers.dev'


def verify(expected):
    request = urllib.request.Request(ORIGIN + '/api/release/windows', headers={'Cache-Control': 'no-cache', 'User-Agent': 'Morphy/CI'})
    with urllib.request.urlopen(request, timeout=30) as response:
        actual = json.load(response)
        assert response.headers.get('Cache-Control') == 'no-store', 'Update feed must not be cached'
    for key in ('version', 'path', 'sha256', 'bytes'):
        assert actual[key] == expected[key], f'Public feed mismatch: {key}'
    request = urllib.request.Request(ORIGIN + '/download/windows', method='HEAD', headers={'User-Agent': 'Morphy/CI'})
    with urllib.request.urlopen(request, timeout=30) as response:
        assert response.url == ORIGIN + expected['path'], 'Website download targets a different release'
        assert int(response.headers['Content-Length']) == expected['bytes'], 'Installer is incomplete'
    digest, size = hashlib.sha256(), 0
    with urllib.request.urlopen(urllib.request.Request(ORIGIN + expected['path'], headers={'User-Agent': 'Morphy/CI'}), timeout=60) as response:
        while chunk := response.read(1024 * 1024):
            digest.update(chunk)
            size += len(chunk)
    assert size == expected['bytes'] and digest.hexdigest() == expected['sha256'], 'Downloaded installer digest mismatch'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifact', type=Path, required=True)
    args = parser.parse_args()
    manifests = list(args.artifact.rglob('windows-release.json'))
    if len(manifests) != 1:
        raise SystemExit('Expected exactly one Windows manifest')
    expected = json.loads(manifests[0].read_text(encoding='utf-8-sig'))
    for attempt in range(6):
        try:
            verify(expected)
            print(f"Verified public Windows {expected['version']}: feed, download link, size and SHA-256")
            return
        except Exception:
            if attempt == 5:
                raise
            time.sleep(5)


if __name__ == '__main__':
    main()
