"""Choose the next Windows patch version from the source floor and published release."""
import argparse
import json
import os
from pathlib import Path
import re
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

ORIGIN = 'https://morphy.tpals0409dev.workers.dev'


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r'\d+\.\d+\.\d+', value):
        raise ValueError(f'Invalid three-part version: {value!r}')
    parts = tuple(map(int, value.split('.')))
    if any(part > 65534 for part in parts):
        raise ValueError('Version exceeds .NET assembly version limits')
    return parts


def choose(floor, published=None, requested=None):
    current = max(version(floor), version(published) if published else (0, 0, 0))
    result = version(requested) if requested else version('.'.join(map(str, (*current[:2], current[2] + 1))))
    if result <= current:
        raise ValueError('Requested version must be newer than both source and published versions')
    return '.'.join(map(str, result))


def published_version():
    try:
        request = urllib.request.Request(ORIGIN + '/api/release/windows', headers={'Cache-Control': 'no-cache', 'User-Agent': 'Morphy/CI'})
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)['version']
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise  # Never guess a version when the release service is unavailable.


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--requested', default='')
    args = parser.parse_args()
    project = Path(__file__).resolve().parent.parent / 'windows/Morphy.Windows/Morphy.Windows.csproj'
    floor = ET.parse(project).findtext('./PropertyGroup/Version')
    selected = choose(floor, published_version(), args.requested or None)
    print(selected)
    if output := os.environ.get('GITHUB_OUTPUT'):
        with open(output, 'a', encoding='utf-8') as stream:
            stream.write(f'version={selected}\n')


if __name__ == '__main__':
    main()
