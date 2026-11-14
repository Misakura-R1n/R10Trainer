"""Build an offline R10 bridge AIN inside artifacts; never modify the source/game.

The format reader is restricted to SHA-pinned AIN v12 builds. Format facts
were checked against libsys4's AIN reader and the bundled alice 0.13 CLI:
https://github.com/nunuhara/libsys4/blob/master/src/ain.c
https://github.com/nunuhara/alice-tools/blob/master/README-ain.md
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess
import sys
import zlib
from ain_profiles import LAYOUTS, ROOT, SECTION_BOUNDS, VERSIONS, VERSION_BY_SOURCE
from feature_ain import (EXTRA_TREASURE_PICK_CAPABILITY, TREASURE_FIND_CAPABILITY,
                         install_extra_treasure_pick, install_features, install_treasure_find)

ARTIFACTS = ROOT / "artifacts"
SOURCE_SHA = VERSIONS[0]["sourceSha256"]
DEFAULT_ALICE = Path(r"D:\code\deepseek\rc\文本文件\aindiff\vendor\alice\alice.exe")
CODE_SIZE = 0xDE5CEA
HOOKS = [
    {"id": 0x47DD, "name": "main", "entry": 0x2E68EC, "marker": 0, "arguments": 0},
    {"id": 0x2064, "name": "parts::detail::Update", "entry": 0x1F1B0E, "marker": 1, "arguments": 2},
]
OP = {"PUSH": 0x00, "POP": 0x01, "REF": 0x02, "PUSHGLOBALPAGE": 0x04, "PUSHLOCALPAGE": 0x05,
      "ASSIGN": 0x19, "LT": 0x13, "LTE": 0x15, "NOTE": 0x17, "AND": 0x0E, "S_ASSIGN": 0x44, "S_EQUALE": 0x49,
      "DELETE": 0x77, "ADD": 0x09, "SUB": 0x0A, "MUL": 0x0B, "DIV": 0x0C, "MOD": 0x0D,
      "EQUALE": 0x18, "JUMP": 0x2C, "IFZ": 0x2D, "IFNZ": 0x2E,
      "RETURN": 0x2F, "CALLFUNC": 0x30, "S_PUSH": 0x41, "C_REF": 0x57,
      "CALLHLL": 0x5A, "PUSHSTRUCTPAGE": 0x5B, "CALLMETHOD": 0x5C, "FUNC": 0x61, "SWAP": 0x66,
      "A_REF": 0x79, "DUP": 0x7A, "ENDFUNC": 0x7E, "DG_NEW_FROM_METHOD": 0xFD}
DAMAGE_SITES = [0x3C0D12, 0x3C2DF8, 0x3C306A, 0x3C313A, 0x3E49D0,
                0x42DCEA, 0x4311D4, 0x431EAA, 0x43879A]
EXCLUDED_HP_SITES = [0x3BE7F4, 0x3BEA04, 0x3C16EA, 0x3E4D58, 0x4124D2]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def i32(value: int) -> bytes:
    return struct.pack("<i", value)


class Reader:
    def __init__(self, data: bytes, offset: int = 0):
        self.data, self.pos = data, offset

    def integer(self) -> int:
        require(self.pos + 4 <= len(self.data), "Truncated integer")
        result = struct.unpack_from("<i", self.data, self.pos)[0]
        self.pos += 4
        return result

    def string(self) -> bytes:
        end = self.data.find(b"\0", self.pos)
        require(end >= self.pos, "Unterminated AIN string")
        result = self.data[self.pos:end]
        self.pos = end + 1
        return result

    def type(self, depth: int = 0) -> tuple:
        require(depth < 16, "Unsupported recursive AIN type")
        data_type, struct_type, nested = self.integer(), self.integer(), self.integer()
        require(nested in (0, 1), "Unsupported AIN v12 type rank")
        return data_type, struct_type, nested, self.type(depth + 1) if nested else None

    def variable(self) -> tuple:
        self.string(); self.string()
        result = self.type()
        has_value = self.integer()
        require(has_value in (0, 1), "Invalid variable initializer flag")
        if has_value:
            if result[0] == 12:
                self.string()
            elif result[0] not in {13, 18, 19, 20, 21, 22, 23, 24, 25, 31, 32, 51, 52, 59, 60, 63, 67, 69, 79, 80}:
                self.integer()
        return result


def read_functions(section: bytes) -> list[dict]:
    require(section[:4] == b"FUNC", "FUNC section missing")
    reader = Reader(section, 4)
    count = reader.integer()
    require(0 < count < 100000, "Invalid FUNC count")
    result = []
    for index in range(count):
        address_offset = reader.pos
        address, name = reader.integer(), reader.string()
        return_type = reader.type()
        args, variables, is_lambda, crc = (reader.integer() for _ in range(4))
        require(0 <= args <= variables < 10000 and is_lambda in (0, 1), "Invalid function signature")
        types = [reader.variable() for _ in range(variables)]
        result.append({"id": index, "addressOffset": address_offset, "address": address, "name": name,
                       "returnType": return_type, "args": args, "types": types, "crc": crc})
    require(reader.pos == len(section), "FUNC parse did not consume the section")
    return result


def read_libraries(section: bytes) -> list[dict]:
    require(section[:4] == b"HLL0", "HLL0 section missing")
    reader = Reader(section, 4)
    count = reader.integer()
    require(0 < count < 1000, "Invalid HLL count")
    result = []
    for _ in range(count):
        name, function_count = reader.string(), reader.integer()
        require(0 <= function_count < 10000, "Invalid HLL function count")
        functions = []
        for _ in range(function_count):
            function_name, return_type, arg_count = reader.string(), reader.integer(), reader.integer()
            require(0 <= arg_count <= 100, "Invalid HLL argument count")
            arguments = [(reader.string(), reader.integer()) for _ in range(arg_count)]
            functions.append({"name": function_name, "returnType": return_type, "arguments": arguments})
        result.append({"name": name, "functions": functions})
    require(reader.pos == len(section), "HLL0 parse did not consume the section")
    return result


def instruction(name: str, *args: int) -> bytes:
    return struct.pack("<H", OP[name]) + b"".join(i32(value) for value in args)


class Assembler:
    def __init__(self, start: int):
        self.start, self.code, self.rows, self.labels, self.fixups = start, bytearray(), [], {}, []

    def emit(self, name: str, *args: int | str) -> None:
        pc = self.start + len(self.code)
        self.rows.append({"pc": pc, "op": name, "args": args})
        self.code.extend(struct.pack("<H", OP[name]))
        for arg in args:
            if isinstance(arg, str):
                self.fixups.append((len(self.code), arg)); self.code.extend(i32(0))
            else:
                self.code.extend(i32(arg))

    def label(self, name: str) -> None:
        require(name not in self.labels, "Duplicate assembler label")
        self.labels[name] = self.start + len(self.code)

    def local(self, index: int) -> None:
        self.emit("PUSHLOCALPAGE"); self.emit("PUSH", index); self.emit("REF")

    def global_ref(self, index: int) -> None:
        self.emit("PUSHGLOBALPAGE"); self.emit("PUSH", index); self.emit("REF")

    def member(self, index: int) -> None:
        self.emit("PUSHSTRUCTPAGE"); self.emit("PUSH", index); self.emit("REF")

    def this_get(self, function: int) -> None:
        self.emit("PUSHSTRUCTPAGE"); self.emit("PUSH", function); self.emit("CALLMETHOD", 0)

    def global_get(self, global_id: int, function: int) -> None:
        self.global_ref(global_id); self.emit("PUSH", function); self.emit("CALLMETHOD", 0)

    def tick(self, marker: int, a: int, b: int) -> None:
        for value in (marker, a, b): self.emit("PUSH", value)
        self.emit("CALLHLL", 36, 0, -1); self.emit("POP")

    def finish(self) -> bytes:
        for offset, label in self.fixups:
            require(label in self.labels, "Missing assembler label")
            struct.pack_into("<i", self.code, offset, self.labels[label])
        for row in self.rows:
            row["args"] = tuple(self.labels[a] if isinstance(a, str) else a for a in row["args"])
        return bytes(self.code)


def new_function_record(entry: int, name: str, return_type: int, arguments: list[str | tuple[str, int]], local_types=()) -> bytes:
    result = i32(entry) + name.encode("ascii") + b"\0" + i32(return_type) + i32(-1) + i32(0)
    result += i32(len(arguments)) + i32(len(arguments) + len(local_types)) + i32(0) + i32(0)
    argument_types = [(arg, 10) if isinstance(arg, str) else arg for arg in arguments]
    for name, kind in [*argument_types, *local_types]:
        result += name.encode("ascii") + b"\0" + name.encode("ascii") + b"\0"
        result += i32(kind) + i32(-1) + i32(0) + i32(0)
    return result


def prelude(hook: dict, header_pc: int, library: int) -> tuple[bytes, list[dict]]:
    code, rows = bytearray(), []

    def emit(name: str, *args: int) -> None:
        rows.append({"pc": header_pc + len(code), "op": name, "args": args})
        code.extend(instruction(name, *args))

    emit("FUNC", hook["id"])
    emit("PUSH", hook["marker"])
    for kind, value in hook.get("tickArguments", [("local", 0), ("local", 1)] if hook["arguments"] else [("const", 1), ("const", 1)]):
        if kind == "local":
            emit("PUSHLOCALPAGE"); emit("PUSH", value); emit("REF")
        else: emit("PUSH", value)
    emit("CALLHLL", library, 0, -1)
    emit("POP")  # Tick returns int; original body must start with an empty evaluation stack.
    if hook.get("sampler") is not None: emit("CALLFUNC", hook["sampler"])
    if hook.get("data") is not None: emit("CALLFUNC", hook["data"])
    if hook.get("cardManagement") is not None: emit("CALLFUNC", hook["cardManagement"])
    if hook.get("controls") is not None: emit("CALLFUNC", hook["controls"])
    emit("JUMP", hook["entry"])
    emit("ENDFUNC", hook["id"])  # unreachable terminator for offline tools
    return bytes(code), rows


def verify_prelude(rows: list[dict], hook: dict, library: int) -> None:
    """Model only the new integer ABI prefix; original AIN code is never emulated here."""
    stack = []
    local_values = (17, 23)
    for row in rows[1:]:
        op, args = row["op"], row["args"]
        if op == "PUSH": stack.append(args[0])
        elif op == "PUSHLOCALPAGE": stack.append("local")
        elif op == "REF":
            index, page = stack.pop(), stack.pop()
            require(page == "local" and index in (0, 1), "Bad local access")
            stack.append(local_values[index])
        elif op == "CALLHLL":
            require(tuple(args) == (library, 0, -1), "Bad HLL call")
            specifications = hook.get("tickArguments", [("local", 0), ("local", 1)] if hook["arguments"] else [("const", 1), ("const", 1)])
            expected = [hook["marker"], *(local_values[value] if kind == "local" else value for kind, value in specifications)]
            require(stack == expected, "HLL argument order or evaluation stack mismatch")
            stack = [12345]
        elif op == "POP": stack.pop()
        elif op == "CALLFUNC": require(args[0] in (hook.get("sampler"), hook.get("controls"), hook.get("data"), hook.get("cardManagement")) and not stack, "Invalid helper call")
        elif op == "JUMP":
            require(args[0] == hook["entry"] and not stack, "Original entry stack is not empty")
            return
        else: raise ValueError("Unexpected prefix operation")
    raise ValueError("Prefix did not reach original body")


def build(raw: bytes, profile: str = "heartbeat") -> tuple[bytes, dict, str]:
    require(profile in {"heartbeat", "full", "controls"}, "Unknown bridge profile")
    source_hash = sha(raw)
    version = VERSION_BY_SOURCE.get(source_hash)
    require(version is not None, "Unsupported source AIN SHA-256; an already instrumented AIN is not an input")
    layout = LAYOUTS[version["id"]]
    at = layout.relocate
    encoding = f"CP{layout.code_page}"
    require(raw[:8] == b"AI2\0\0\0\0\0", "Unsupported AIN container")
    unpacked_size, compressed_size = struct.unpack_from("<II", raw, 8)
    require(compressed_size == len(raw) - 16, "Compressed length mismatch")
    plain = zlib.decompress(raw[16:])
    require(len(plain) == unpacked_size == layout.sections[-1][2], "Decompressed length mismatch")
    require(sha(plain) == version["sourcePlainSha256"], "Source AIN payload mismatch")
    sections = {}
    for name, start, end in layout.sections:
        sections[name] = plain[start:end]
        require(sections[name][:4] == name.encode("ascii"), "Source section boundary mismatch: " + name)
    require(sections["VERS"] == b"VERS" + i32(12), "Only AIN 12 is supported")
    code = sections["CODE"][8:]
    require(sections["CODE"][4:8] == i32(len(code)) and code[-6:] == instruction("FUNC", 0), "Invalid CODE length or terminal function")
    functions = read_functions(sections["FUNC"])
    libraries = read_libraries(sections["HLL0"])
    require(len(functions) == 37698 and len(libraries) == 36, "Unexpected metadata profile")
    require(all(item["name"] != b"R10Bridge" for item in libraries), "Bridge library already exists")
    require([f["id"] for f in functions if f["address"] >= len(code)] == [0], "Unexpected CODE end sentinel")
    func = bytearray(sections["FUNC"])
    extended = bytearray(code[:-6])  # move terminal FUNC 0 after the appended prefixes
    report_hooks, listing, added_functions, code_edits = [], [], [], []
    hooks = [dict(hook) for hook in HOOKS]
    capabilities = 2047 if profile == "controls" else 2031 if profile == "full" else 1
    if profile in {"full", "controls"}:
        from card_management_ain import ITEM_STAR_CAPABILITY
        capabilities |= EXTRA_TREASURE_PICK_CAPABILITY | ITEM_STAR_CAPABILITY | TREASURE_FIND_CAPABILITY
        if layout.code_page == 932:
            capabilities |= 2048
        if layout.patch_managed_rerolls:
            capabilities = (capabilities & ~(64 | 128)) | 4096
    hooks[0]["tickArguments"] = [("const", 1), ("const", capabilities)]
    redirected = set()

    def add_function(name: str, result: int, arguments: list[str | tuple[str, int]], body, *, local_types=()) -> int:
        function_id = len(functions) + len(added_functions)
        asm = Assembler(len(extended)); asm.emit("FUNC", function_id)
        body(asm); asm.emit("ENDFUNC", function_id)
        emitted = asm.finish(); extended.extend(emitted)
        entry = asm.start + 6
        added_functions.append({"id": function_id, "name": name, "entry": entry,
                                "returnType": result, "arguments": arguments, "bytes": emitted.hex().upper()})
        if local_types:
            added_functions[-1]["locals"] = local_types
        func.extend(new_function_record(entry, name, result, arguments, local_types))
        add_function.entries[function_id] = entry
        listing.append(f"; added {name} ({function_id:#x})")
        listing.extend(f"{row['pc']:08X} {row['op']} " + " ".join(str(a) for a in row["args"]) for row in asm.rows)
        return function_id

    add_function.entries = {}

    def replace_code(pc: int, expected: bytes, replacement: bytes, reason: str) -> None:
        require(len(expected) == len(replacement) and code[pc:pc + len(expected)] == expected, "Callsite bytes mismatch: " + reason)
        require(extended[pc:pc + len(expected)] == expected, "Overlapping callsite edits")
        extended[pc:pc + len(expected)] = replacement
        code_edits.append({"pc": pc, "originalHex": expected.hex().upper(), "replacementHex": replacement.hex().upper(), "reason": reason})

    def replace_added_target(function_id, pc, op, old_target, new_target, reason):
        record = added_functions[function_id - len(functions)]
        start = record["entry"] - 6
        end = start + len(record["bytes"]) // 2
        expected, replacement = instruction(op, old_target), instruction(op, new_target)
        require(start <= pc and pc + len(expected) <= end and
                extended[pc:pc + len(expected)] == expected, "Generated callsite mismatch: " + reason)
        extended[pc:pc + len(expected)] = replacement
        record["bytes"] = extended[start:end].hex().upper()
        listing.append(f"; {pc:08X} {op} {new_target} ({reason})")

    if profile in {"full", "controls"}:
        from battle_ain import flag, install_battle, install_battle_resume
        # Null is the AIN value -1, as used by SceneBattle.Run; heap index zero is valid.
        def sampler(asm):
            for global_id in (0xF0, 0x104, 0x123, 0x124):
                asm.global_ref(global_id); asm.emit("PUSH", -1); asm.emit("EQUALE"); asm.emit("IFNZ", "invalid")
            asm.global_ref(0x104); asm.emit("PUSH", 0); asm.emit("REF")
            asm.emit("PUSH", -1); asm.emit("EQUALE"); asm.emit("IFNZ", "invalid")
            flag(asm, 0); asm.emit("IFNZ", "sample")
            asm.global_get(0xF0, 0x65E9); asm.emit("IFZ", "invalid")
            asm.label("sample")
            for marker, global_id, hp, maximum in ((10, 0x104, 0x6CA2, 0x6CA0), (11, 0x123, 0x6C42, 0x6C3C)):
                asm.emit("PUSH", marker); asm.global_get(global_id, hp); asm.global_get(global_id, maximum)
                asm.emit("CALLHLL", 36, 0, -1); asm.emit("POP")
            asm.emit("RETURN")
            asm.label("invalid"); asm.tick(12, 0, 0); asm.emit("RETURN")
        sample_id = add_function("R10Bridge_SampleBattleHp", 0, [], sampler)
        hooks[1]["sampler"] = sample_id

        def damage(asm):
            asm.emit("PUSHSTRUCTPAGE"); asm.emit("PUSH", 0x6812)
            asm.member(0); asm.this_get(0x6810); asm.local(0)
            asm.emit("CALLHLL", 36, 1, -1); asm.emit("CALLMETHOD", 1)
            asm.emit("CALLFUNC", sample_id); asm.emit("RETURN")
        damage_id = add_function("BattlePlayer@R10BridgeApplyDamage", 0, ["value"], damage)
        for pc in map(at, DAMAGE_SITES):
            replace_code(pc + 2, i32(0x6812), i32(damage_id), "damage-only Hp setter target")
            require(code[pc:pc + 6] == instruction("PUSH", 0x6812), "Damage site is not a PUSH instruction")
        for pc in map(at, EXCLUDED_HP_SITES):
            require(extended[pc:pc + 6] == instruction("PUSH", 0x6812), "Healing/initialization/result site changed")

        for original_id, name, pc in ((0x580D, "GameChapter1@R10BridgeRestoreInit", 0x3402E0),
                                      (0x581C, "GameChapter2@R10BridgeRestoreInit", 0x340376)):
            pc = at(pc)
            def restore(asm, original_id=original_id):
                asm.this_get(original_id); asm.tick(3, 1, 0); asm.emit("RETURN")
            wrapper = add_function(name, 0, [], restore)
            require(code[pc:pc + 6] == instruction("PUSH", original_id), "Restore init target mismatch")
            replace_code(pc + 2, i32(original_id), i32(wrapper), "ordinary restore init completion")

        def load_local(asm):
            asm.local(0); asm.emit("CALLFUNC", 0x7026); asm.emit("IFNZ", "success")
            asm.tick(4, 1, 0); asm.emit("PUSH", 0); asm.emit("RETURN")
            asm.label("success"); asm.emit("PUSH", 1); asm.emit("RETURN")
        wrapper = add_function("R10Bridge_LoadLocalAttempt", 47, ["index"], load_local)
        pc = at(0x408F3A)
        require(code[pc:pc + 6] == instruction("CALLFUNC", 0x7026), "Local load caller mismatch")
        replace_code(pc + 2, i32(0x7026), i32(wrapper), "ordinary load failure only")

        def resume_cleanup(asm):
            asm.emit("CALLFUNC", 0x15D8); asm.tick(3, 2, 0); asm.emit("RETURN")
        wrapper = add_function("R10Bridge_ResumeCleanup", 0, [], resume_cleanup)
        pc = at(0x1723A2)
        require(code[pc:pc + 6] == instruction("CALLFUNC", 0x15D8), "Resume cleanup caller mismatch")
        replace_code(pc + 2, i32(0x15D8), i32(wrapper), "resume completion after cleanup; native pending-kind gate required")

        def resume_failure(asm):
            asm.tick(4, 2, 0)
            # The original S_PUSH is replayed without decoding/transcoding its string.
            asm.emit("S_PUSH", 0x65C); asm.emit("JUMP", at(0x172C58))
        wrapper = add_function("R10Bridge_ResumeFailureContinuation", 0, [], resume_failure)
        expected = instruction("S_PUSH", 0x65C)
        replace_code(at(0x172C52), expected, instruction("JUMP", add_function.entries[wrapper]), "ResumeLoad returned: preserve original Error continuation")

        hooks.extend([
            {"id": 0x6708, "name": "ResetLoadManager::Run", "entry": 0x408E72, "arguments": 1, "marker": 2,
             "tickArguments": [("const", 1), ("local", 0)]},
            {"id": 0x15DD, "name": "gamesave::detail::ロード実行", "entry": 0x172BD8, "arguments": 1, "marker": 2,
             "tickArguments": [("const", 2), ("const", -1)], "stringArgument": True},
        ])

    if profile in {"full", "controls"}:
        from cards_ain import install_cards
        install_cards(add_function, replace_code, instruction, at, functions, sections["STR0"], encoding, require)
        if not layout.patch_managed_rerolls:
            install_features(add_function, replace_code, instruction, at)
        selector = functions[0x6041]
        entry = selector["address"]
        require(selector["name"] == b"SceneGetCard@0" and selector["args"] == 4 and
                code[entry - 6:entry] == instruction("FUNC", 0x6041), "Unexpected card selector constructor")
        install_extra_treasure_pick(add_function, replace_code, instruction, entry, layout.has_extra_treasure_bonus)
        from data_ain import install_data
        hooks[1]["data"] = install_data(add_function)
        from card_management_ain import install_card_management, install_card_resume_pump, install_card_scene_guards, install_item_star
        install_card_scene_guards(add_function, replace_code, instruction, at)
        hooks[1]["cardManagement"] = install_card_management(add_function, sections["STR0"], encoding)

    if profile in {"full", "controls"}:
        def redirect(function_id, name, entry, arguments, body):
            entry = at(entry)
            original = functions[function_id]
            require(original["name"] == name.encode("ascii") and original["address"] == entry and
                    original["args"] == arguments and function_id not in redirected and
                    code[entry - 6:entry] == instruction("FUNC", function_id),
                    f"Redirect function metadata mismatch: {name}")
            asm = Assembler(len(extended)); asm.emit("FUNC", function_id)
            body(asm); asm.emit("JUMP", entry); asm.emit("ENDFUNC", function_id)
            extended.extend(asm.finish())
            struct.pack_into("<i", func, original["addressOffset"], asm.start + 6)
            redirected.add(function_id)
            listing.append(f"; battle prefix {name} ({function_id:#x})")
            listing.extend(f"{row['pc']:08X} {row['op']} {row['args']}" for row in asm.rows)
        battle_callback, battle_labels = install_battle(add_function, redirect, replace_code, instruction,
                                                        sample_id, profile == "controls", at)
    if profile == "controls":
        from controls_ain import install_load_control
        hooks[1]["controls"] = install_load_control(add_function)

    strings = sections["STR0"]
    if profile in {"full", "controls"}:
        require(libraries[0]["name"] == b"system" and libraries[0]["functions"][22] ==
                {"name": b"GetFuncStackName", "returnType": 12, "arguments": [(b"index", 10)]},
                "Unexpected system stack inspection ABI")
        # Append after the existing CODE so resume saves retain their call return addresses.
        card_pump = hooks[1]["cardManagement"]
        hooks[1]["cardManagement"] = len(functions) + len(added_functions)

    for hook in hooks:
        original = functions[hook["id"]]
        hook["entry"] = at(hook["entry"])
        require(original["name"] == hook["name"].encode(encoding) and original["address"] == hook["entry"] and
                original["args"] == hook["arguments"], "Hook function metadata mismatch")
        require(code[hook["entry"] - 6:hook["entry"]] == instruction("FUNC", hook["id"]), "Hook FUNC header mismatch")
        expected_type = (12 if hook.get("stringArgument") else 10, -1, 0, None)
        require(all(t == expected_type for t in original["types"][:hook["arguments"]]), "Hook parameters have unexpected types")
        header = len(extended)
        prefix, rows = prelude(hook, header, len(libraries))
        verify_prelude(rows, hook, len(libraries))
        extended.extend(prefix)
        struct.pack_into("<i", func, original["addressOffset"], header + 6)
        report_hooks.append({**hook, "originalEntry": hook["entry"], "newEntry": header + 6,
                             "prefixHex": prefix.hex().upper(), "prefixBytes": len(prefix)})
        listing.append(f"; {hook['name']} -> Tick marker {hook['marker']} -> original body")
        listing.extend(f"{row['pc']:08X} {row['op']} " + " ".join(str(a) for a in row["args"]) for row in rows)
    if profile in {"full", "controls"}:
        resume_pump, strings, card_call = install_card_resume_pump(add_function, strings, functions, card_pump, require)
        require(resume_pump == hooks[1]["cardManagement"], "Unexpected card resume pump function ID")
        # Reuse the restored delegate and keep every previous function and return PC stable.
        battle_resume = install_battle_resume(add_function, battle_labels["owned"])
        replace_added_target(battle_callback, battle_labels["ownership_check"], "IFZ", battle_labels["unowned"],
                             add_function.entries[battle_resume], "restore active battle")
        item_star = install_item_star(add_function, card_pump, functions, require)
        replace_added_target(resume_pump, card_call, "CALLFUNC", card_pump, item_star, "service card and shared item star requests")
        # Keep the previous helpers and saved return PCs stable across upgrades.
        install_treasure_find(add_function, replace_code, instruction, at)
    extended.extend(instruction("FUNC", 0))
    struct.pack_into("<i", func, functions[0]["addressOffset"], len(extended))
    struct.pack_into("<i", func, 4, len(functions) + len(added_functions))
    declaration = b"R10Bridge\0" + i32(8 if profile in {"full", "controls"} else 1) + b"Tick\0" + i32(10) + i32(3)
    declaration += b"marker\0" + i32(10) + b"a\0" + i32(10) + b"b\0" + i32(10)
    if profile in {"full", "controls"}:
        declaration += b"FilterHp\0" + i32(10) + i32(3)
        declaration += b"side\0" + i32(10) + b"oldHp\0" + i32(10) + b"requestedHp\0" + i32(10)
    if profile in {"full", "controls"}:
        declaration += b"Control\0" + i32(10) + i32(3) + b"op\0" + i32(10) + b"a\0" + i32(10) + b"b\0" + i32(10)
    if profile in {"full", "controls"}:
        declaration += b"Cards\0" + i32(10) + i32(3) + b"op\0" + i32(10) + b"a\0" + i32(10) + b"b\0" + i32(10)
        declaration += b"Feature\0" + i32(10) + i32(3) + b"kind\0" + i32(10) + b"a\0" + i32(10) + b"b\0" + i32(10)
        declaration += b"Data\0" + i32(10) + i32(3) + b"op\0" + i32(10) + b"a\0" + i32(10) + b"b\0" + i32(10)
        declaration += b"CardEdit\0" + i32(10) + i32(3) + b"op\0" + i32(10) + b"a\0" + i32(10) + b"b\0" + i32(10)
        declaration += b"Battle\0" + i32(10) + i32(3) + b"op\0" + i32(10) + b"a\0" + i32(10) + b"b\0" + i32(10)
    hll = b"HLL0" + i32(len(libraries) + 1) + sections["HLL0"][8:] + declaration
    updated = {**sections, "CODE": b"CODE" + i32(len(extended)) + extended, "FUNC": bytes(func), "HLL0": hll, "STR0": strings}
    # Verify narrow mutations independently of byte serialization.
    new_functions = read_functions(updated["FUNC"])
    targets = {0, *redirected, *(hook["id"] for hook in hooks)}
    for original, changed in zip(functions, new_functions):
        require({k:v for k,v in original.items() if k != "address"} ==
                {k:v for k,v in changed.items() if k != "address"}, "Non-address function metadata changed")
        require(original["id"] in targets or original == changed, "Unrelated function entry changed")
    new_libraries = read_libraries(hll)
    require(new_libraries[:-1] == libraries, "Existing HLL declarations changed")
    restored = bytearray(extended[:len(code) - 6])
    for edit in code_edits:
        expected = bytes.fromhex(edit["originalHex"])
        restored[edit["pc"]:edit["pc"] + len(expected)] = expected
    require(restored == code[:-6], "Original bytecode changed outside declared callsites")
    result_plain = b"".join(updated[name] for name, _, _ in layout.sections)
    compressed = zlib.compress(result_plain, 9)
    result = raw[:8] + struct.pack("<II", len(result_plain), len(compressed)) + compressed
    changed_sections = [name for name, _, _ in layout.sections if updated[name] != sections[name]]
    section_deltas = {name: len(updated[name]) - len(sections[name]) for name in changed_sections}
    report = {"schema": 1, "profile": profile, "scriptVersion": 1, "capabilities": capabilities, "controlRedirects": sorted(redirected),
              "sourceVersion": version["id"], "sourceSha256": source_hash, "outputSha256": sha(result),
              "outputPlainSha256": sha(result_plain), "outputPlainLength": len(result_plain), "ainVersion": 12,
              "originalCodeLength": len(code), "newCodeLength": len(extended), "hllLibraryIndex": len(libraries),
              "hll": {"name": "R10Bridge", "export": "Tick", "return": "int32", "arguments": ["int32 marker", "int32 a", "int32 b"]},
              "hooks": report_hooks, "addedFunctions": added_functions, "codeEdits": code_edits,
              "nullSentinelEntry": len(extended), "originalBytecodePreservedOutsideDeclaredCallsites": True,
              "changedSections": changed_sections, "sectionDeltas": section_deltas,
              "unchangedSections": [name for name, _, _ in layout.sections if name not in section_deltas],
              "prefixStackChecksPassed": len(hooks), "liveValidated": False}
    return result, report, "\n".join(listing) + "\n"


def artifact_directory(path: Path) -> Path:
    resolved = path.resolve()
    require(resolved.is_relative_to(ARTIFACTS.resolve()) and resolved != ARTIFACTS.resolve(), "Output must be a subdirectory of this repository's artifacts")
    return resolved


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--alice", type=Path, default=DEFAULT_ALICE)
    parser.add_argument("--profile", choices=["heartbeat", "full", "controls"], default="heartbeat")
    parser.add_argument("--dll-sha256", help="SHA-256 of the DLL verified by the component build")
    args = parser.parse_args()
    if args.dll_sha256 is not None:
        require(re.fullmatch(r"[0-9A-Fa-f]{64}", args.dll_sha256) is not None, "Invalid bridge DLL SHA-256")
    output = artifact_directory(args.output_dir)
    source = args.source.resolve()
    target = output / "Rance10.ain"
    require(source != target.resolve(), "Source and destination must differ")
    require(args.alice.is_file(), "alice executable not found")
    original = source.read_bytes()
    generated, report, listing = build(original, args.profile)
    encoding = f"CP{LAYOUTS[report['sourceVersion']].code_page}"
    output.mkdir(parents=True, exist_ok=True)
    temporary = output / "Rance10.ain.tmp"
    temporary.write_bytes(generated); temporary.replace(target)
    (output / "bridge-prefixes.asm").write_text(listing, encoding="utf-8")
    checks = [("--map", "output.map.txt"), ("--libraries", "output.libraries.txt")]
    for hook in report["hooks"]:
        checks.append((hook["name"], f"hook-{hook['id']:04x}.asm"))
    checks.extend((f["name"], f"added-{f['id']:04x}.asm") for f in report["addedFunctions"])
    for option, filename in checks:
        flags = [option] if option.startswith("--") else ["--function", option]
        command = [str(args.alice), "ain", "dump", *flags, "--input-encoding", encoding, "--output-encoding", "UTF-8", "-o", str(output / filename), str(target)]
        subprocess.run(command, check=True, capture_output=True)
    require("R10Bridge" in (output / "output.libraries.txt").read_text(encoding="utf-8"), "alice did not recognize the new HLL library")
    require(source.read_bytes() == original, "Source file changed during the offline build")
    report.update({"sourcePath": str(source), "outputPath": str(target), "alicePath": str(args.alice.resolve()),
                   "aliceSha256": sha(args.alice.read_bytes()), "aliceOfflineParsePassed": True, "sourceUnchanged": True})
    if args.dll_sha256 is not None:
        report["dllSha256"] = args.dll_sha256.upper()
    (output / "bridge-build.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(target), "sourceVersion": report["sourceVersion"], "sha256": report["outputSha256"],
                      "codeBytesAdded": report["newCodeLength"] - report["originalCodeLength"],
                      "hllIndex": report["hllLibraryIndex"], "sourceUnchanged": True, "aliceOfflineParsePassed": True}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    main()
