"""Prepare a new, independent game/save copy. Never launch or change the source.

Use ../prepare-bridge-sandbox.ps1 on Windows so save_root is the actual redirected
MyDocuments/AliceSoft folder. Partial failures retain the new directories, but the
main executable is published only after isolation and file verification succeed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts"
# Match the legacy package name and build.ps1's versioned player directories.
TRAINER_PACKAGE_NAME = re.compile(r"R10Trainer-(?:v\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?-)?win-x86", re.IGNORECASE)
CORE_SHA = {
    "Rance10.ain": "7ED22B2854DCB93B6E447198E7CB682CD05B90CF6AB3226091D80003D52DD48A",
    "Rance10.exe": "39A508C05B13AFC5427F0B722FCE5C4EDECED126587E037D585CF0E70D279297",
    "AliceStart.ini": "26A9C641DB8A3A8C0BD1AC89505663BF8D261EF6100AB1D3ADB81B59E2136AE4",
    "PCInfo.dll": "293C7D89D2790E4DA6CCF003019152C3231BE054863E16206BF3741040DCDD10",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def file_sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def reject_reparse(path: Path) -> None:
    for candidate in (path, *path.parents):
        if candidate.exists() or candidate.is_symlink():
            info = candidate.lstat()
            require(not candidate.is_symlink() and not (getattr(info, "st_file_attributes", 0) & 0x400),
                    f"Reparse points/symlinks are not accepted: {candidate}")


def inventory(directory: Path) -> list[Path]:
    require(directory.is_dir(), f"Directory does not exist: {directory}")
    reject_reparse(directory)
    result = []
    for current, directories, files in os.walk(directory, followlinks=False):
        for name in directories + files:
            child = Path(current) / name
            reject_reparse(child)
            if name in files:
                require(child.is_file(), f"Unsupported filesystem entry: {child}")
                result.append(child.relative_to(directory))
    return sorted(result)


def hashes(directory: Path) -> dict[str, str]:
    return {str(path): file_sha(directory / path) for path in inventory(directory)}


def isolated_ini(raw: bytes, game_name: str) -> tuple[bytes, str]:
    text = raw.decode("latin1")
    names = re.findall(r'^\s*GameName\s*=\s*"([^"\r\n]+)"\s*$', text, re.M)
    require(len(names) == 1, "Expected exactly one GameName assignment")
    source_name = names[0].encode("latin1").decode("cp936")
    require(source_name != game_name, "Sandbox GameName must differ from the source")
    require(re.findall(r'^\s*SaveFolder\s*=\s*"([^"\r\n]+)"\s*$', text, re.M) == ["SaveData"],
            "Only the original relative SaveFolder=SaveData is supported")
    require(re.findall(r'^\s*RegName\s*=\s*"([^"\r\n]*)"\s*$', text, re.M) == [""],
            "Only the original empty RegName is supported")
    require(len(re.findall(r'^\s*LoadDLL\s*=\s*\{', text, re.M)) == 1 and "R10Bridge" not in text,
            "Expected a single unmodified LoadDLL block")
    text, count = re.subn(r'(^[ \t]*GameName[ \t]*=[ \t]*)"[^"\r\n]+"',
                         lambda match: match[1] + '"' + game_name + '"', text, count=1, flags=re.M)
    require(count == 1, "Could not replace GameName")
    text, count = re.subn(r'(^[ \t]*LoadDLL[ \t]*=[ \t]*\{)(\r?\n)',
                         lambda match: match[1] + match[2] + '"R10Bridge.dll",' + match[2],
                         text, count=1, flags=re.M)
    require(count == 1, "Could not add bridge DLL")
    return text.encode("latin1"), source_name


def prepare(source: Path, target: Path, save_root: Path, game_name: str,
            bridge_directory: Path, bridge_dll: Path, save_source: Path | None = None) -> dict:
    require(re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]{2,63}", game_name) is not None,
            "SandboxName must be 3-64 ASCII letters/digits/underscore/hyphen and start with a letter")
    for path in (source, target, save_root, bridge_directory, bridge_dll):
        reject_reparse(path.absolute())
    source, target, save_root = source.resolve(), target.resolve(), save_root.resolve()
    artifacts = ARTIFACTS.resolve()
    require(target.is_relative_to(artifacts) and target != artifacts,
            "Target must be a new subdirectory of this repository's artifacts")
    # resolve() can keep these suffixes for missing directories, which Win32 may strip on creation.
    require(not any(TRAINER_PACKAGE_NAME.fullmatch(part.rstrip(" .")) for part in target.relative_to(artifacts).parts),
            "A game copy must never be prepared inside the trainer package")
    require(not target.exists(), f"Target already exists; choose a new directory: {target}")
    save_game = save_root / game_name
    save_target = save_game / "SaveData"
    require(not save_game.exists(), f"Sandbox save root already exists; choose a new SandboxName: {save_game}")
    source_files = inventory(source)
    source_hashes = {name: file_sha(source / name) for name in CORE_SHA}
    require(source_hashes == CORE_SHA, "Source game core hashes do not match the supported original profile")
    ini, original_name = isolated_ini((source / "AliceStart.ini").read_bytes(), game_name)
    require(original_name not in (".", "..") and "/" not in original_name and "\\" not in original_name,
            "Unexpected source GameName")
    save_source = (save_source or save_root / original_name / "SaveData").absolute()
    reject_reparse(save_source)
    save_source = save_source.resolve()
    for old in (source, save_source):
        for new in (target, save_game):
            require(not old.is_relative_to(new) and not new.is_relative_to(old),
                    "Source and destination directories must not overlap")
    require(not target.is_relative_to(save_game) and not save_game.is_relative_to(target),
            "Game and save destinations must not overlap")
    save_hashes = hashes(save_source)
    manifest = json.loads((bridge_directory / "bridge-build.json").read_text(encoding="utf-8-sig"))
    bridge_ain = bridge_directory / "Rance10.ain"
    supported_profiles = {"full": {15, 47, 239, 495, 1007, 2031, 10223, 26607, 59375},
                          "controls": {31, 63, 255, 511, 1023, 2047, 10239, 26623, 59391}}
    require(manifest["sourceSha256"] == CORE_SHA["Rance10.ain"] and
            manifest["capabilities"] in supported_profiles.get(manifest["profile"], ()) and
            manifest["aliceOfflineParsePassed"] is True and
            file_sha(bridge_ain) == manifest["outputSha256"], "Bridge AIN and its full-profile build manifest do not match")
    dll = bridge_dll.read_bytes()
    require(len(dll) >= 64 and dll[:2] == b"MZ", "Bridge DLL is not a PE file")
    pe = struct.unpack_from("<I", dll, 60)[0]
    require(pe + 24 <= len(dll) and dll[pe:pe + 4] == b"PE\0\0" and
            struct.unpack_from("<H", dll, pe + 4)[0] == 0x14C and
            struct.unpack_from("<H", dll, pe + 22)[0] & 0x2000, "Bridge DLL must be an x86 PE DLL")
    dll_hash = hashlib.sha256(dll).hexdigest().upper()

    # Exclusive creation prevents reuse of previous experiments. No cleanup/move of existing paths.
    target.parent.mkdir(parents=True, exist_ok=True)
    target.mkdir()
    try:
        save_root.mkdir(parents=True, exist_ok=True)
        save_game.mkdir()
        save_target.mkdir()
        (target / "AliceStart.ini").write_bytes(ini)  # never put the original GameName in the copy
        shutil.copy2(bridge_ain, target / "Rance10.ain")
        (target / "R10Bridge.dll").write_bytes(dll)
        for current, directories, _ in os.walk(source, followlinks=False):
            for directory in directories:
                relative = (Path(current) / directory).relative_to(source)
                (target / relative).mkdir(parents=True, exist_ok=True)
        for relative in source_files:
            if len(relative.parts) == 1 and relative.name.casefold() in ("rance10.exe", "rance10.ain", "alicestart.ini", "r10bridge.dll"):
                continue
            destination = target / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source / relative, destination)
        for relative in save_hashes:
            destination = save_target / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(save_source / relative, destination)
        require(hashes(save_source) == save_hashes == hashes(save_target),
                "Source saves changed during the copy or copied saves differ; no ready executable was published")
        require({name: file_sha(source / name) for name in CORE_SHA} == source_hashes,
                "Source game core files changed during the copy")
        require((target / "AliceStart.ini").read_bytes() == ini and file_sha(target / "Rance10.ain") == manifest["outputSha256"] and
                file_sha(target / "R10Bridge.dll") == dll_hash, "Copied bridge/INI verification failed")
        # Publish the main executable last, after a complete independent copy and isolation checks.
        executable_temp = target / "Rance10.exe.preparing"
        shutil.copy2(source / "Rance10.exe", executable_temp)
        require(file_sha(executable_temp) == source_hashes["Rance10.exe"], "Copied executable hash mismatch")
        report = {"source": str(source), "target": str(target), "independentCopies": True,
                  "gameName": game_name, "saveSource": str(save_source), "saveTarget": str(save_target),
                  "originalSaveHashes": save_hashes, "sourceCoreHashes": source_hashes,
                  "bridgeAinSha256": manifest["outputSha256"], "bridgeDllSha256": dll_hash,
                  "sourceFileCount": len(source_files), "saveFileCount": len(save_hashes), "launched": False}
        (target / "bridge-sandbox.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        executable_temp.rename(target / "Rance10.exe")
        return report
    except Exception as error:
        raise RuntimeError(f"Preparation failed; new partial directories were retained at {target} and {save_game}. {error}") from error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--target", required=True, type=Path)
    parser.add_argument("--save-root", required=True, type=Path)
    parser.add_argument("--game-name", required=True)
    parser.add_argument("--save-source", type=Path)
    parser.add_argument("--bridge-directory", type=Path, default=ARTIFACTS / "bridge/full")
    parser.add_argument("--bridge-dll", type=Path, default=ARTIFACTS / "bridge-native/R10Bridge.dll")
    args = parser.parse_args()
    report = prepare(args.source, args.target, args.save_root, args.game_name, args.bridge_directory, args.bridge_dll, args.save_source)
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    main()
