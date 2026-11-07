"""Filesystem fixtures only: no real game, saves, DLL loading, or process access."""
import hashlib
import json
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

import prepare_sandbox as sandbox


class SandboxTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.artifacts = self.root / "artifacts"
        self.source = self.root / "original"
        self.target = self.artifacts / "game-copy"
        self.saves = self.root / "Documents/AliceSoft"
        self.save_source = self.saves / "Original/SaveData"
        self.bridge = self.artifacts / "bridge/full"
        for path in (self.source, self.save_source, self.bridge):
            path.mkdir(parents=True)
        (self.source / "EmptyDataDirectory").mkdir()
        ini = b'; original comment \xff\r\nGameName = "Original"\r\nSaveFolder = "SaveData"\r\nRegName = ""\r\nLoadDLL = {\r\n"PCInfo.dll",\r\n}\r\n'
        for name, data in {"Rance10.ain": b"original-ain", "Rance10.exe": b"original-exe",
                           "AliceStart.ini": ini, "PCInfo.dll": b"original-pcinfo", "Data.afa": b"game-asset"}.items():
            (self.source / name).write_bytes(data)
        (self.save_source / "slot.asd").write_bytes(b"original-save")
        self.core = {name: sandbox.file_sha(self.source / name) for name in sandbox.CORE_SHA}
        (self.bridge / "Rance10.ain").write_bytes(b"instrumented-ain")
        self.manifest = {"sourceSha256": self.core["Rance10.ain"], "profile": "full", "capabilities": 15,
                         "aliceOfflineParsePassed": True, "outputSha256": sandbox.file_sha(self.bridge / "Rance10.ain")}
        (self.bridge / "bridge-build.json").write_text(json.dumps(self.manifest), encoding="utf-8")
        self.dll = self.bridge / "R10Bridge.dll"
        dll = bytearray(128); dll[:2] = b"MZ"; struct.pack_into("<I", dll, 60, 64)
        dll[64:68] = b"PE\0\0"; struct.pack_into("<H", dll, 68, 0x14C); struct.pack_into("<H", dll, 86, 0x2000)
        self.dll.write_bytes(dll)
        self.enterContext(patch.object(sandbox, "ARTIFACTS", self.artifacts))
        self.enterContext(patch.object(sandbox, "CORE_SHA", self.core))

    def prepare(self, **kwargs):
        arguments = dict(source=self.source, target=self.target, save_root=self.saves, game_name="R10SandboxTest",
                         bridge_directory=self.bridge, bridge_dll=self.dll)
        arguments.update(kwargs)
        return sandbox.prepare(**arguments)

    def test_copies_are_independent_and_original_bytes_are_unchanged(self):
        originals, saves = sandbox.hashes(self.source), sandbox.hashes(self.save_source)
        result = self.prepare()
        self.assertEqual(sandbox.hashes(self.source), originals)
        self.assertEqual(sandbox.hashes(self.save_source), saves)
        save_target = Path(result["saveTarget"])
        self.assertEqual(save_target, self.saves / "R10SandboxTest/SaveData")
        self.assertFalse((self.target / "Data.afa").samefile(self.source / "Data.afa"))
        self.assertTrue((self.target / "EmptyDataDirectory").is_dir())
        self.assertFalse((save_target / "slot.asd").samefile(self.save_source / "slot.asd"))
        (save_target / "slot.asd").write_bytes(b"changed-copy")
        self.assertEqual(sandbox.hashes(self.save_source), saves)
        ini = (self.target / "AliceStart.ini").read_bytes()
        self.assertIn(b'GameName = "R10SandboxTest"', ini)
        self.assertIn(b'"R10Bridge.dll",\r\n"PCInfo.dll",', ini)
        self.assertIn(b'comment \xff', ini)
        self.assertFalse(result["launched"])

    def test_existing_game_target_is_rejected_before_save_creation(self):
        self.target.mkdir(parents=True)
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.prepare()
        self.assertFalse((self.saves / "R10SandboxTest").exists())

    def test_existing_save_namespace_is_rejected_before_game_creation(self):
        (self.saves / "R10SandboxTest").mkdir()
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.prepare()
        self.assertFalse(self.target.exists())

    def test_unsafe_game_names_are_rejected(self):
        for name in ("../Original", 'bad"name', "Original\\child", "name\nother", "兰斯"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.prepare(game_name=name)
        self.assertFalse(self.target.exists())

    def test_unknown_source_is_rejected(self):
        (self.source / "Rance10.ain").write_bytes(b"unknown")
        with self.assertRaisesRegex(ValueError, "core hashes"):
            self.prepare()
        self.assertFalse(self.target.exists())

    def test_source_destination_overlap_is_rejected(self):
        nested_source = self.artifacts / "original"
        shutil.copytree(self.source, nested_source)
        with self.assertRaisesRegex(ValueError, "overlap"):
            self.prepare(source=nested_source, target=nested_source / "nested")
        self.assertFalse((nested_source / "nested").exists())

    def test_game_cannot_be_copied_into_trainer_package(self):
        with self.assertRaisesRegex(ValueError, "trainer package"):
            self.prepare(target=self.artifacts / "R10Trainer-win-x86/game")
        self.assertFalse((self.artifacts / "R10Trainer-win-x86").exists())

    def test_mismatched_bridge_manifest_is_rejected(self):
        (self.bridge / "Rance10.ain").write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "manifest"):
            self.prepare()
        self.assertFalse(self.target.exists())

    def test_source_symlink_is_rejected(self):
        link = self.source / "linked-data"
        try:
            link.symlink_to(self.save_source, target_is_directory=True)
        except OSError as error:
            self.skipTest(f"Symlink creation is unavailable: {error}")
        with self.assertRaisesRegex(ValueError, "Reparse"):
            self.prepare()
        self.assertFalse(self.target.exists())

    def test_changed_save_aborts_before_publishing_executable(self):
        copy = shutil.copy2
        def changing_copy(source, destination, *args, **kwargs):
            result = copy(source, destination, *args, **kwargs)
            if Path(source) == self.save_source / "slot.asd":
                Path(source).write_bytes(b"save-raced")
            return result
        with patch.object(sandbox.shutil, "copy2", side_effect=changing_copy), self.assertRaisesRegex(RuntimeError, "saves changed"):
            self.prepare()
        self.assertFalse((self.target / "Rance10.exe").exists())
        self.assertIn(b'"R10SandboxTest"', (self.target / "AliceStart.ini").read_bytes())

    def test_copy_failure_leaves_only_isolated_unlaunchable_partial_copy(self):
        copy = shutil.copy2
        def failing_copy(source, destination, *args, **kwargs):
            if Path(source).name == "Data.afa":
                raise OSError("injected disk failure")
            return copy(source, destination, *args, **kwargs)
        with patch.object(sandbox.shutil, "copy2", side_effect=failing_copy), self.assertRaisesRegex(RuntimeError, "partial directories"):
            self.prepare()
        self.assertFalse((self.target / "Rance10.exe").exists())
        self.assertIn(b'"R10SandboxTest"', (self.target / "AliceStart.ini").read_bytes())
        self.assertEqual((self.source / "AliceStart.ini").read_bytes().count(b'"Original"'), 1)

    @unittest.skipUnless(sys.platform == "win32", "Case-insensitive Windows source lookup")
    def test_core_filename_case_cannot_bypass_deferred_executable_or_isolated_ini(self):
        for name in ("Rance10.exe", "AliceStart.ini", "Rance10.ain"):
            (self.source / name).rename(self.source / name.upper())
        copy = shutil.copy2
        def failing_copy(source, destination, *args, **kwargs):
            if Path(source) == self.save_source / "slot.asd":
                raise OSError("injected after all source assets")
            return copy(source, destination, *args, **kwargs)
        with patch.object(sandbox.shutil, "copy2", side_effect=failing_copy), self.assertRaises(RuntimeError):
            self.prepare()
        self.assertFalse(any(path.name.casefold() == "rance10.exe" for path in self.target.iterdir()))
        self.assertIn(b'"R10SandboxTest"', (self.target / "AliceStart.ini").read_bytes())


if __name__ == "__main__":
    unittest.main(verbosity=2)
