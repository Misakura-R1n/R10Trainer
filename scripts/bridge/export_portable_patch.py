"""Export only the authored AIN changes, never the complete game script."""
from __future__ import annotations
import argparse
import base64
import json
from pathlib import Path
import struct
import zlib
from build_instrumented_ain import build, SECTION_BOUNDS, new_function_record, sha, SOURCE_SHA, artifact_directory


def export(source: bytes, dll: bytes) -> dict:
    generated, report, _ = build(source, 'full')
    original = zlib.decompress(source[16:])
    target = zlib.decompress(generated[16:])
    code_delta = report['newCodeLength'] - report['originalCodeLength']
    func_delta = sum(len(new_function_record(f['entry'], f['name'], f['returnType'], f['arguments'])) for f in report['addedFunctions'])
    hll_delta = len(target) - len(original) - code_delta - func_delta
    edits = []
    shift = 0
    for name, start, end in SECTION_BOUNDS:
        delta = {'CODE': code_delta, 'FUNC': func_delta, 'HLL0': hll_delta}.get(name, 0)
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
    assert bytes(rebuilt) == target
    assert sum(len(base64.b64decode(e['data'])) for e in edits) < 8192
    return {'schema': 1, 'sourceSha256': SOURCE_SHA, 'sourcePlainSha256': sha(original),
            'targetPlainSha256': sha(target), 'sourcePlainLength': len(original), 'targetPlainLength': len(target),
            'exeSha256': '39A508C05B13AFC5427F0B722FCE5C4EDECED126587E037D585CF0E70D279297',
            'dllSha256': sha(dll), 'edits': edits}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, required=True)
    args = parser.parse_args()
    output = artifact_directory(args.output_dir)
    patch = export(args.source.read_bytes(), args.dll.read_bytes())
    output.mkdir(parents=True, exist_ok=True)
    path = output/'hp-patch.json'
    path.write_text(json.dumps(patch, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'path': str(path), 'bytes': path.stat().st_size, 'authoredBytes': sum(len(base64.b64decode(e['data'])) for e in patch['edits']), 'targetPlainSha256': patch['targetPlainSha256']}, ensure_ascii=False))


if __name__ == '__main__':
    main()
