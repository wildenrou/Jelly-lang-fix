#!/usr/bin/env python3
"""Package built bytes and a Jellyfin catalog; no network or credentials needed."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--legacy-dll', type=Path, help='Optional verified 1.0.0 DLL to include as a legacy catalog version')
parser.add_argument('--package-ref', default='main', help='Commit containing these package bytes; pin published catalog downloads to it')
args = parser.parse_args()
root = Path(__file__).resolve().parent
out = root / 'artifacts'
dist = root / 'dist'
out.mkdir(exist_ok=True)
dist.mkdir(exist_ok=True)
project = root / 'src/Jellyfin.Plugin.FrenchOriginals'
version = ET.parse(project / 'Jellyfin.Plugin.FrenchOriginals.csproj').findtext('.//Version')
assert version
short_version = version.removesuffix('.0')
dll = project / 'bin/Release/net10.0/Jellyfin.Plugin.FrenchOriginals.dll'
if not dll.is_file():
    raise SystemExit('Build the Release configuration first.')
logo = root / 'assets/french-originals-logo.png'
repo_url = 'https://raw.githubusercontent.com/wildenrou/Jelly-lang-fix/' + args.package_ref
description = ('French titles and summaries for French-original films and television. '
               'Diagnostic beta: a reported real-library read-back mismatch remains under investigation. '
               'Start in preview mode; verification failures stop the batch.')
manifest = {
    'category': 'Metadata', 'guid': '8a18225a-22b8-4e18-9921-b8f1b5900bbc',
    'name': 'French Originals', 'description': description, 'owner': 'wildenrou',
    'overview': 'French-original metadata updates with preview, clear reports, and audit logging.',
    'targetAbi': '12.0.0.0', 'version': version, 'status': 'Active', 'autoUpdate': False,
    'imagePath': logo.name, 'assemblies': [dll.name], 'timestamp': '2026-09-23T00:00:00Z'
}


def add(archive, name, data):
    info = zipfile.ZipInfo(name, (2026, 9, 23, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o100644 << 16
    archive.writestr(info, data)


def package(archive_path, dll_bytes, metadata, prefix=''):
    with zipfile.ZipFile(archive_path, 'w') as archive:
        add(archive, prefix + dll.name, dll_bytes)
        add(archive, prefix + 'meta.json', json.dumps(metadata, indent=2).encode())
        add(archive, prefix + logo.name, logo.read_bytes())
        for name in ['README.md', 'CHANGELOG.md', 'VALIDATION.md', 'LICENSE']:
            data = (root / name).read_bytes()
            if name == 'README.md':
                data = data.replace(f'(assets/{logo.name})'.encode(), f'({prefix}{logo.name})'.encode())
            add(archive, name, data)


catalog_zip = dist / f'FrenchOriginals-{short_version}.zip'
manual_zip = dist / f'FrenchOriginals-{short_version}-jellyfin12.zip'
package(catalog_zip, dll.read_bytes(), manifest)
package(manual_zip, dll.read_bytes(), manifest, f'FrenchOriginals_{version}/')

legacy_zip = dist / 'FrenchOriginals-1.0.0.zip'
if args.legacy_dll:
    legacy_bytes = args.legacy_dll.read_bytes()
    if hashlib.sha256(legacy_bytes).hexdigest() != '15dc35b2702fced2a05249b66dfd62e6360955be53115c7b184d9a1c931e7768':
        raise SystemExit('Legacy DLL does not match the verified original build.')
    legacy_manifest = dict(manifest, version='1.0.0.0', timestamp='2026-09-22T00:00:00Z')
    package(legacy_zip, legacy_bytes, legacy_manifest)


def catalog_version(number, path, changelog, timestamp):
    return {
        'version': number, 'targetAbi': '12.0.0.0', 'changelog': changelog,
        'sourceUrl': f'{repo_url}/dist/{path.name}',
        # Jellyfin's catalog installer requires MD5. SHA-256 is also published below.
        'checksum': hashlib.md5(path.read_bytes()).hexdigest(),
        'timestamp': timestamp
    }


versions = [catalog_version(version, catalog_zip,
    'Diagnostic beta. Hide unchanged report rows; identify title, summary, and language changes; '
    'journal exact expected/actual verification differences. The reported server mismatch is not yet confirmed fixed.',
    manifest['timestamp'])]
if legacy_zip.is_file():
    versions.append(catalog_version('1.0.0.0', legacy_zip,
        'Initial beta. A reported read-back mismatch remains under investigation. '
        'Use 1.0.1 or later for detailed verification diagnostics.', '2026-09-22T00:00:00Z'))
catalog = [{key: manifest[key] for key in ['guid', 'name', 'overview', 'description', 'owner', 'category']}]
catalog[0].update(imageUrl=f'{repo_url}/assets/{logo.name}', versions=versions)
(root / 'manifest.json').write_text(json.dumps(catalog, indent=2) + '\n')

packages = [catalog_zip, manual_zip] + ([legacy_zip] if legacy_zip.is_file() else [])
(dist / 'SHA256SUMS.txt').write_text(''.join(
    hashlib.sha256(path.read_bytes()).hexdigest() + '  ' + path.name + '\n' for path in packages))
source = out / f'FrenchOriginals-{short_version}-source.zip'
with zipfile.ZipFile(source, 'w') as archive:
    for path in sorted(root.rglob('*')):
        relative = path.relative_to(root)
        if path.is_file() and not any(part in {'bin', 'obj', 'artifacts', 'dist', '.git', '__pycache__', 'node_modules'} for part in relative.parts):
            add(archive, 'FrenchOriginals/' + relative.as_posix(), path.read_bytes())
(out / 'SOURCE-SHA256SUM.txt').write_text(hashlib.sha256(source.read_bytes()).hexdigest() + '  ' + source.name + '\n')
print('\n'.join(str(path) for path in [*packages, source, root / 'manifest.json']))
