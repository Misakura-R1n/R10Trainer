"""Export only the authored AIN changes, never the complete game script."""
from __future__ import annotations
import argparse
import base64
import json
from pathlib import Path
import struct
import zlib
from ain_profiles import LAYOUTS, VERSION_BY_SOURCE
from build_instrumented_ain import build, sha, artifact_directory, require


def export(source: bytes, dll: bytes, built: tuple[bytes, dict] | None = None) -> dict:
    source_hash = sha(source)
    version = VERSION_BY_SOURCE.get(source_hash)
    require(version is not None, 'Unsupported source AIN')
    if built is None:
        generated, report, _ = build(source, 'full')
    else:
        generated, report = built
    require(report['profile'] == 'full' and report['sourceSha256'] == source_hash and
            report['outputSha256'] == sha(generated), 'Generated AIN does not match its build report')
    dll_hash = sha(dll)
    if 'dllSha256' in report:
        require(report['dllSha256'] == dll_hash, 'Bridge DLL does not match the generated AIN build report')
    require(len(generated) >= 16 and generated[:8] == b'AI2\0\0\0\0\0', 'Invalid generated AIN header')
    length, compressed = struct.unpack_from('<II', generated, 8)
    require(length == version['targetPlainLength'] and compressed == len(generated) - 16,
            'Generated AIN sizes do not match the version registry')
    decoder = zlib.decompressobj()
    target = decoder.decompress(generated[16:], length + 1)
    require(len(target) == length and decoder.eof and not decoder.unused_data and
            sha(target) == version['targetPlainSha256'], 'Generated AIN does not match the version registry')
    original = zlib.decompress(source[16:])
    edits = []
    shift = 0
    for name, start, end in LAYOUTS[version['id']].sections:
        delta = report['sectionDeltas'].get(name, 0)
        old = original[start:end]
        new = target[start+shift:end+shift+delta]
        fixed = len(old) - 6 if name == 'CODE' else len(old)
        pos = 0
        while pos < fixed:
            if old[pos] == new[pos]:
                pos += 1
                continue
            last = pos + 1
            while last < fixed and old[last] != new[last]:
                last += 1
            edits.append({'offset': start+pos, 'remove': last-pos, 'expectedSha256': sha(old[pos:last]), 'data': base64.b64encode(new[pos:last]).decode()})
            pos = last
        if delta or fixed < len(old):
            edits.append({'offset': start+fixed, 'remove': len(old)-fixed, 'expectedSha256': sha(old[fixed:]), 'data': base64.b64encode(new[fixed:]).decode()})
        shift += delta
    rebuilt = bytearray()
    cursor = 0
    for edit in edits:
        rebuilt.extend(original[cursor:edit['offset']]); rebuilt.extend(base64.b64decode(edit['data']))
        cursor = edit['offset'] + edit['remove']
    rebuilt.extend(original[cursor:])
    require(bytes(rebuilt) == target, 'Recipe does not reconstruct the generated AIN')
    require(sum(len(base64.b64decode(e['data'])) for e in edits) <= 19 * 1024, 'Recipe exceeds the authored byte limit')
    return {'schema': 1, 'sourceSha256': version['sourceSha256'], 'sourcePlainSha256': version['sourcePlainSha256'],
            'targetPlainSha256': version['targetPlainSha256'], 'sourcePlainLength': len(original), 'targetPlainLength': len(target),
            'exeSha256': version['exeSha256'],
            'dllSha256': dll_hash, 'edits': edits}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, action='append', required=True)
    parser.add_argument('--generated-dir', type=Path, action='append',
                        help='Directory of the already-built full AIN and report, in --source order')
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, required=True)
    args = parser.parse_args()
    output = artifact_directory(args.output_dir)
    dll = args.dll.read_bytes()
    directories = args.generated_dir if args.generated_dir is not None else [None] * len(args.source)
    require(len(directories) == len(args.source), 'Provide one generated directory per source')
    patches = {}
    for source, directory in zip(args.source, directories):
        raw = source.read_bytes()
        source_hash = sha(raw)
        require(source_hash not in patches, f"Duplicate source AIN: {source}")
        built = None if directory is None else (
            (directory/'Rance10.ain').read_bytes(),
            json.loads((directory/'bridge-build.json').read_text(encoding='utf-8')),
        )
        patches[source_hash] = export(raw, dll, built)
    recipes = [patches[source_hash] for source_hash in VERSION_BY_SOURCE if source_hash in patches]
    patch = recipes[0] if len(recipes) == 1 else {'schema': 2, 'profiles': recipes}
    output.mkdir(parents=True, exist_ok=True)
    path = output/'hp-patch.json'
    serialized = json.dumps(patch, ensure_ascii=False, indent=2).encode('utf-8')
    require(len(serialized) <= 512 * 1024, 'Recipe bundle exceeds the installer size limit')
    temporary = path.with_suffix('.tmp')
    temporary.write_bytes(serialized)
    temporary.replace(path)
    print(json.dumps({'path': str(path), 'bytes': len(serialized), 'versions': [VERSION_BY_SOURCE[r['sourceSha256']]['id'] for r in recipes]}, ensure_ascii=False))


if __name__ == '__main__':
    main()
