"""Offline checks for the bridge builder. Requires the original SHA-pinned sample."""
import argparse
from pathlib import Path
import struct
import sys
import unittest
import zlib

import build_instrumented_ain as builder
from battle_ain import AP_FULL, REPEAT_ACTIONS, STATUS_IMMUNITY, UNLIMITED_ACTIONS, UNLIMITED_SKILLS

SOURCE: Path


class WrapperVm:
    """Execute only newly appended integer/control-flow code with strict stack checks."""
    WIDTHS = {0x00: 1, 0x2C: 1, 0x2D: 1, 0x2E: 1, 0x30: 1, 0x41: 1, 0x5A: 3, 0x5C: 1, 0x61: 1, 0x7E: 1}

    def __init__(self, generated, report):
        plain = zlib.decompress(generated[16:])
        self.code = plain[16:16 + struct.unpack_from("<I", plain, 12)[0]]
        self.functions = {f["id"]: f for f in report["addedFunctions"]}
        self.by_name = {f["name"]: f for f in report["addedFunctions"]}
        self.globals = {0xF0: 0, 0x104: 200, 0x123: 300, 0x124: 400}
        self.objects = {0: {6: 1}, 100: {0: 0}, 200: {0: 201, 4: 600}, 201: {0: 1000}, 300: {0: 400, 10: 500}, 400: {}}
        self.events = []; self.protect = False; self.load_success = True

    def invoke(self, name, arguments=(), this=100):
        return self.run(self.by_name[name], arguments, this)

    def method(self, obj, function, args):
        assert obj != -1
        if function == 0x65E9: return self.objects[obj][6]
        if function == 0x6CA2: return self.objects[obj][4]
        if function == 0x6CA0: return self.objects[self.objects[obj][0]][0]
        if function == 0x6C42: return self.objects[obj][0]
        if function == 0x6C3C: return self.objects[obj][10]
        side = self.objects[obj][0]
        if function == 0x6810: return self.objects[200][4] if side == 0 else self.objects[300][0]
        if function == 0x6812:
            self.events.append(("setter", side, args[0]))
            if side == 0: self.objects[200][4] = max(0, min(args[0], 1000))
            else: self.objects[300][0] = args[0]
            return None
        if function in (0x580D, 0x581C):
            self.events.append(("init", function)); return None
        raise AssertionError(f"Unexpected method {function:x}")

    def battle(self, op, a, b):
        return 0

    def run(self, function, arguments, this, stack_values=()):
        pc, stack = function["entry"], list(stack_values)
        for _ in range(500):
            op = struct.unpack_from("<H", self.code, pc)[0]
            size = self.WIDTHS.get(op, 0)
            args = struct.unpack_from("<" + "i" * size, self.code, pc + 2)
            pc += 2 + size * 4
            if op == 0: stack.append(args[0])
            elif op == 1: stack.pop()
            elif op == 2:
                index, page = stack.pop(), stack.pop()
                assert page != -1
                stack.append(arguments[index] if page == "local" else self.globals[index] if page == "global" else self.objects[page][index])
            elif op == 4: stack.append("global")
            elif op == 5: stack.append("local")
            elif op == 9: stack.append(stack.pop() + stack.pop())
            elif op == 0x13:
                right, left = stack.pop(), stack.pop()
                stack.append(int(left < right))
            elif op == 0x5B: stack.append(this)
            elif op == 0x18: stack.append(int(stack.pop() == stack.pop()))
            elif op in (0x2D, 0x2E):
                condition = stack.pop()
                if bool(condition) == (op == 0x2E): pc = args[0]
            elif op == 0x2C:
                if args[0] < builder.CODE_SIZE:
                    return {"continuation": args[0], "stack": stack}
                pc = args[0]
            elif op == 0x41: stack.append(("string", args[0]))
            elif op == 0x79: pass  # A_REF copies the top value; abstract ownership is unchanged.
            elif op == 0x5A:
                assert args[0] == 36 and args[2] == -1
                values = stack[-3:]; del stack[-3:]
                if args[1] == 0:
                    self.events.append(("tick", *values)); stack.append(0)
                elif args[1] == 2:
                    stack.append(self.control(*values))
                elif args[1] == 7:
                    stack.append(self.battle(*values))
                else:
                    assert args[1] == 1
                    side, old, wanted = values
                    self.events.append(("filter", side, old, wanted))
                    stack.append(max(old, wanted) if self.protect and side == 0 else wanted)
            elif op == 0x5C:
                call_args = stack[-args[0]:] if args[0] else []
                if args[0]: del stack[-args[0]:]
                target, obj = stack.pop(), stack.pop()
                value = self.method(obj, target, call_args)
                if value is not None: stack.append(value)
            elif op == 0x30:
                target = args[0]
                if target == 0x7026:
                    self.events.append(("load", stack.pop())); stack.append(int(self.load_success))
                elif target == 0x7027:
                    self.events.append(("exists", stack.pop())); stack.append(int(self.exists))
                elif target == 0x6708: self.events.append(("reset_load", stack.pop()))
                elif target == 0x6BA4:
                    skill, party = stack.pop(), stack.pop()
                    self.events.append(("usable", party, skill)); stack.append(int(self.usable))
                elif target == 0x15D8: self.events.append(("cleanup",))
                else:
                    child = self.functions[target]
                    count = len(child["arguments"])
                    child_args = stack[-count:] if count else []
                    if count: del stack[-count:]
                    value = self.run(child, child_args, this)
                    if child["returnType"]: stack.append(value)
            elif op == 0x2F:
                if function["returnType"]:
                    assert len(stack) == 1; return stack.pop()
                assert not stack; return None
            elif op == 0x7A: stack.append(stack[-1])
            else: raise AssertionError(f"Unexpected opcode {op:x} at {pc:x}")
        raise AssertionError("Wrapper did not terminate")


class ControlVm(WrapperVm):
    def __init__(self, generated, report):
        super().__init__(generated, report)
        self.action, self.arg, self.arg2 = 0, 1, 0
        self.active = self.enabled = self.usable = self.exists = True
        self.objects[100].update({17: 500, 18: 501, 20: 400})
        self.objects[200][1] = 600; self.objects[600] = {0: 601}
        self.objects[601] = {1: 602}; self.objects[602] = {0: "card-id"}
        self.globals[0x100] = 101
        self.objects.update({400: {3: 410, 14: 420}, 410: {0: 430, 1: 440},
                             430: {0: 0, 11: 450}, 440: {0: 1, 11: 460},
                             450: {0: 0}, 460: {0: 1}})
        self.rules, self.ap, self.ap_max, self.statuses = 0, 3, 6, []
        self.refresh, self.hp_side, self.hp_value = False, 0, 0
        self.ordinary = True

    def battle(self, op, a, b):
        if op == 0: return int(self.ordinary and b == 400 and (not a or self.rules & a))
        if op == 3: self.events.append(("ap", a, b)); return 0
        if op == 4: return self.hp_side
        if op == 5: return self.hp_value
        if op == 6:
            self.events.append(("hp_result", a)); self.hp_side = 0
            return 0
        if op == 8: return int(self.refresh)
        raise AssertionError(op)

    def control(self, op, a, b):
        if op == 0: return self.action
        if op == 1: return self.arg
        if op == 2: return self.arg2
        if op == 4: return int(self.enabled and a == 500)
        if op == 5: self.events.append(("ready", a)); return 0
        if op == 3:
            self.events.append(("ack", a)); self.action = self.arg = self.arg2 = 0
            return 0
        raise AssertionError(op)

    def method(self, obj, function, args):
        if function == 0x73DF: return int(self.active)
        if function == 0x6141: self.events.append(("skill_click", obj, *args)); return None
        if function == 0x6136: self.events.append(("end_turn", obj)); return None
        if function == 0x67B7: return self.ap
        if function == 0x66A6: return self.ap_max
        if function == 0x67B9:
            self.ap = args[0]; self.events.append(("set_ap", self.ap)); return None
        if function == 0x6254: self.events.append(("refresh_selector",)); return None
        if function == 0x6813: return 1000 if obj == 430 else 500
        if function == 0x681B: self.events.append(("clear_leaders", args[0])); return 0
        if function == 0x68FE: return len(self.statuses)
        if function == 0x68F8: self.events.append(("erase", self.statuses.pop(args[0]))); return None
        return super().method(obj, function, args)


class ControlBuildTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.generated, cls.report, _ = builder.build(SOURCE.read_bytes(), "controls")

    def vm(self, action):
        vm = ControlVm(self.generated, self.report); vm.action = action
        return vm

    def test_skill_arguments_survive_command_consumption(self):
        vm = self.vm(4); vm.arg2 = 1
        vm.invoke("SceneBattle@R10BridgeUpdate", (16,16))
        self.assertEqual(vm.events, [("ready",1),("ap",3,6),("usable",1,1),("ack",1),("skill_click",100,"card-id",1)])

    def test_skill_unavailable_does_not_call_handler(self):
        vm = self.vm(4); vm.usable = False
        vm.invoke("SceneBattle@R10BridgeUpdate", (16,16))
        self.assertEqual(vm.events[-1], ("ack",0))
        self.assertFalse(any(e[0] == "skill_click" for e in vm.events))

    def test_inactive_or_disabled_battle_never_consumes(self):
        for field in ("active","enabled"):
            vm = self.vm(3); setattr(vm,field,False)
            vm.invoke("SceneBattle@R10BridgeUpdate", (16,16))
            self.assertEqual(vm.events, [("ready",0)]); self.assertEqual(vm.action,3)

    def test_end_turn_acknowledged_before_original_handler(self):
        vm = self.vm(3); vm.invoke("SceneBattle@R10BridgeUpdate", (16,16))
        self.assertEqual(vm.events, [("ready",1),("ap",3,6),("ack",1),("end_turn",100)])

    def test_load_checks_existence_and_retains_slot_across_ack(self):
        vm = self.vm(2); vm.arg = 5005; vm.invoke("R10Bridge_ControlPump")
        self.assertEqual(vm.events, [("exists",5005),("ack",1),("reset_load",5005)])
        vm = self.vm(2); vm.exists = False; vm.invoke("R10Bridge_ControlPump")
        self.assertEqual(vm.events, [("exists",1),("ack",0)])


class BuildTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original = SOURCE.read_bytes()
        cls.generated, cls.report, cls.listing = builder.build(cls.original)
        cls.before = zlib.decompress(cls.original[16:])
        cls.after = zlib.decompress(cls.generated[16:])
        cls.old_code = cls.before[16:16 + builder.CODE_SIZE]
        cls.new_length = struct.unpack_from("<I", cls.after, 12)[0]
        cls.new_code = cls.after[16:16 + cls.new_length]
        cls.delta = cls.new_length - builder.CODE_SIZE

    def test_executable_bytes_preserved_and_null_moves(self):
        self.assertEqual(self.new_code[:builder.CODE_SIZE - 6], self.old_code[:-6])
        self.assertEqual(self.new_code[-6:], builder.instruction("FUNC", 0))
        self.assertEqual(self.delta, 112)
        old_start, old_end = next((s, e) for n, s, e in builder.SECTION_BOUNDS if n == "FUNC")
        before = builder.read_functions(self.before[old_start:old_end])
        after = builder.read_functions(self.after[old_start + self.delta:old_end + self.delta])
        changes = [(a["id"], a["address"], b["address"]) for a, b in zip(before, after) if a != b]
        self.assertEqual({x[0] for x in changes}, {0, 0x47DD, 0x2064})
        self.assertEqual(after[0]["address"], self.new_length)

    def test_unrelated_sections_and_existing_hll_bytes_preserved(self):
        hll_growth = len(self.after) - len(self.before) - self.delta
        for name, start, end in builder.SECTION_BOUNDS:
            if name in {"CODE", "FUNC", "HLL0"}: continue
            offset = (self.delta if start >= 0xDE5CFA else 0) + (hll_growth if start >= 0x1AFA1BD else 0)
            self.assertEqual(self.before[start:end], self.after[start + offset:end + offset], name)
        before = self.before[0x1AE658A:0x1AFA1BD]
        after = self.after[0x1AE658A + self.delta:0x1AFA1BD + self.delta + hll_growth]
        self.assertEqual(before[8:], after[8:len(before)])
        libraries = builder.read_libraries(after)
        self.assertEqual(libraries[-1], {"name": b"R10Bridge", "functions": [{"name": b"Tick", "returnType": 10,
            "arguments": [(b"marker", 10), (b"a", 10), (b"b", 10)]}]})

    def test_preexisting_function_locals_and_signatures_are_untouched(self):
        before = self.before[0xDE5CFA:0x1395C28]
        after = bytearray(self.after[0xDE5CFA + self.delta:0x1395C28 + self.delta])
        for function in builder.read_functions(before):
            offset = function["addressOffset"]
            after[offset:offset + 4] = before[offset:offset + 4]
        self.assertEqual(bytes(after), before)

    def test_unknown_and_already_instrumented_inputs_rejected(self):
        with self.assertRaisesRegex(ValueError, "Unsupported source"):
            builder.build(self.generated)
        modified = bytearray(self.original); modified[-10] ^= 1
        with self.assertRaisesRegex(ValueError, "Unsupported source"):
            builder.build(bytes(modified))

    def test_original_file_unchanged(self):
        self.assertEqual(SOURCE.read_bytes(), self.original)
        self.assertEqual(builder.sha(self.original), builder.SOURCE_SHA)

    def test_metadata_truncation_is_rejected(self):
        with self.assertRaises(ValueError):
            builder.read_functions(self.before[0xDE5CFA:0x1395C28 - 1])
        with self.assertRaises(ValueError):
            builder.read_libraries(self.before[0x1AE658A:0x1AFA1BD - 1])

    def test_output_is_confined_to_artifacts(self):
        with self.assertRaises(ValueError): builder.artifact_directory(builder.ROOT)
        with self.assertRaises(ValueError): builder.artifact_directory(builder.ARTIFACTS)
        with self.assertRaises(ValueError): builder.artifact_directory(builder.ARTIFACTS / ".." / "src")
        self.assertEqual(builder.artifact_directory(builder.ARTIFACTS / "bridge" / "test"),
                         (builder.ARTIFACTS / "bridge" / "test").resolve())

    def test_prefix_verifier_rejects_unconsumed_native_return(self):
        hook = builder.HOOKS[1]
        _, rows = builder.prelude(hook, builder.CODE_SIZE, 36)
        with self.assertRaisesRegex(ValueError, "stack"):
            builder.verify_prelude([row for row in rows if row["op"] != "POP"], hook, 36)


class FullBuildTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.raw = SOURCE.read_bytes()
        cls.generated, cls.report, _ = builder.build(cls.raw, "full")

    def vm(self): return WrapperVm(self.generated, self.report)

    def test_nine_damage_callers_and_five_exclusions(self):
        vm = self.vm(); target = vm.by_name["BattlePlayer@R10BridgeApplyDamage"]["id"]
        for pc in builder.DAMAGE_SITES: self.assertEqual(vm.code[pc:pc + 6], builder.instruction("PUSH", target))
        for pc in builder.EXCLUDED_HP_SITES: self.assertEqual(vm.code[pc:pc + 6], builder.instruction("PUSH", 0x6812))
        original = zlib.decompress(self.raw[16:])[16:16 + builder.CODE_SIZE]
        restored = bytearray(vm.code[:builder.CODE_SIZE - 6])
        for edit in self.report["codeEdits"]:
            value = bytes.fromhex(edit["originalHex"]); restored[edit["pc"]:edit["pc"] + len(value)] = value
        self.assertEqual(restored, original[:-6])

    def test_damage_filter_only_changes_protected_player_decrease(self):
        for side in (0, 1):
            for protect in (False, True):
                for requested in (0, 100, 700):
                    vm = self.vm(); vm.protect = protect; vm.objects[100][0] = side
                    old = 600 if side == 0 else 400
                    vm.invoke("BattlePlayer@R10BridgeApplyDamage", [requested])
                    expected = max(old, requested) if protect and side == 0 else requested
                    self.assertEqual([e for e in vm.events if e[0] == "setter"], [("setter", side, expected)])
                    self.assertIn(("filter", side, old, requested), vm.events)

    def test_snapshot_valid_and_heap_index_zero_is_not_null(self):
        vm = self.vm(); vm.invoke("R10Bridge_SampleBattleHp")
        self.assertEqual(vm.events, [("tick", 10, 600, 1000), ("tick", 11, 400, 500)])

    def test_snapshot_rejects_nulls_missing_party_status_and_hidden_battle(self):
        for field in (0xF0, 0x104, 0x123, 0x124, "status", "hidden"):
            vm = self.vm()
            if field == "status": vm.objects[200][0] = -1
            elif field == "hidden": vm.objects[0][6] = 0
            else: vm.globals[field] = -1
            vm.invoke("R10Bridge_SampleBattleHp")
            self.assertEqual(vm.events, [("tick", 12, 0, 0)])

    def test_local_load_failure_reports_after_result_and_preserves_boolean(self):
        for success in (False, True):
            vm = self.vm(); vm.load_success = success
            self.assertEqual(vm.invoke("R10Bridge_LoadLocalAttempt", [7]), int(success))
            self.assertEqual(vm.events, [("load", 7)] + ([] if success else [("tick", 4, 1, 0)]))

    def test_ready_markers_follow_init_or_cleanup(self):
        for name, before in (("GameChapter1@R10BridgeRestoreInit", ("init", 0x580D)),
                             ("GameChapter2@R10BridgeRestoreInit", ("init", 0x581C)),
                             ("R10Bridge_ResumeCleanup", ("cleanup",))):
            vm = self.vm(); vm.invoke(name)
            self.assertEqual(vm.events, [before, ("tick", 3, 2 if name.endswith("Cleanup") else 1, 0)])

    def test_resume_failure_keeps_original_error_string_and_continuation(self):
        vm = self.vm(); result = vm.invoke("R10Bridge_ResumeFailureContinuation")
        self.assertEqual(result, {"continuation": 0x172C58, "stack": [("string", 0x65C)]})
        self.assertEqual(vm.events, [("tick", 4, 2, 0)])

    def test_startup_capabilities_match_full_profile(self):
        self.assertEqual(self.report["capabilities"], 2031)
        startup = next(h for h in self.report["hooks"] if h["id"] == 0x47DD)
        prefix = bytes.fromhex(startup["prefixHex"])
        self.assertEqual(prefix[6:24], builder.instruction("PUSH", 0) + builder.instruction("PUSH", 1) + builder.instruction("PUSH", 2031))

    def test_card_management_uses_typed_locals_and_integer_native_abi(self):
        pump = next(f for f in self.report["addedFunctions"] if f["name"] == "R10Bridge_CardManagement")
        update = next(hook for hook in self.report["hooks"] if hook["id"] == 0x2064)
        self.assertEqual(update["cardManagement"], pump["id"])
        plain = zlib.decompress(self.generated[16:])
        func_start = 16 + self.report["newCodeLength"]
        function_bytes = sum(len(builder.new_function_record(f["entry"], f["name"], f["returnType"],
                                                              f["arguments"], f.get("locals", ())))
                             for f in self.report["addedFunctions"])
        func_end = func_start + 0x1395C28 - 0xDE5CFA + function_bytes
        record = builder.read_functions(plain[func_start:func_end])[pump["id"]]
        self.assertEqual(record["args"], 0)
        self.assertEqual([t[0] for t in record["types"]], [10] * 8 + [12] * 2)
        hll_start = 0x1AE658A + (func_end - 0x1395C28)
        hll_end = 0x1AFA1BD + len(plain) - builder.SECTION_BOUNDS[-1][2]
        exports = builder.read_libraries(plain[hll_start:hll_end])[-1]["functions"]
        export = next(e for e in exports if e["name"] == b"CardEdit")
        self.assertEqual(export, {"name": b"CardEdit", "returnType": 10,
                                 "arguments": [(b"op", 10), (b"a", 10), (b"b", 10)]})

    def test_player_battle_callback_refills_ap_and_applies_one_hp_setter(self):
        for maximum, side, hp in ((6, 0, 750), (10, 1, 250)):
            vm = ControlVm(self.generated, self.report)
            vm.rules, vm.ap, vm.ap_max = AP_FULL, 0, maximum
            vm.hp_side, vm.hp_value = side + 1, hp
            vm.invoke("SceneBattle@R10BridgeUpdate", (16, 16))
            self.assertIn(("ap", maximum, maximum), vm.events)
            self.assertIn(("setter", side, hp), vm.events)
            self.assertIn(("hp_result", hp), vm.events)
            self.assertNotIn(("filter", side, 600, hp), vm.events)
            vm.events.clear()
            vm.invoke("SceneBattle@R10BridgeUpdate", (16, 16))
            self.assertEqual(vm.events, [("ready", 1), ("ap", maximum, maximum)])

    def test_hp_above_the_current_limit_is_rejected_before_any_setter(self):
        vm = ControlVm(self.generated, self.report)
        vm.hp_side, vm.hp_value = 2, 501
        vm.invoke("SceneBattle@R10BridgeUpdate", (16, 16))
        self.assertIn(("hp_result", -1), vm.events)
        self.assertFalse(any(event[0] == "setter" for event in vm.events))

    def test_another_scene_cannot_consume_a_command_or_reset_selection(self):
        vm = ControlVm(self.generated, self.report)
        vm.objects[100][20] = 701
        vm.hp_side, vm.hp_value = 2, 20
        vm.invoke("SceneBattle@R10BridgeUpdate", (16, 16))
        self.assertEqual(vm.events, [])
        self.assertEqual(vm.hp_side, 2)

    def test_repeat_and_limited_skill_branches_are_independent(self):
        vm = ControlVm(self.generated, self.report)
        cases = [
            ("R10Bridge_AllowRepeatAction", 0x439882, 0x4398A0, REPEAT_ACTIONS),
            ("R10Bridge_AllowLimitedSkill", 0x439C5A, 0x439C90, UNLIMITED_SKILLS),
            ("R10Bridge_LimitedSkillCost", 0x438FC4, 0x438FCA, UNLIMITED_SKILLS),
        ]
        for name, allowed, blocked, flag in cases:
            for test_flag in (0, REPEAT_ACTIONS, UNLIMITED_SKILLS):
                vm.rules = test_flag
                for used in (0, 1):
                    result = vm.run(vm.by_name[name], (), 100, (used,))
                    self.assertEqual(result, {"continuation": blocked if used and not flag & test_flag else allowed, "stack": []})

    def test_immunity_clears_existing_conditions_through_game_methods(self):
        vm = ControlVm(self.generated, self.report)
        vm.rules = STATUS_IMMUNITY
        vm.statuses = ["poison", "sleep"]
        self.assertEqual(vm.invoke("R10Bridge_ClearBattleStatus"), 2)
        self.assertEqual(vm.events, [("clear_leaders", 0), ("erase", "sleep"), ("erase", "poison")])

    def test_only_party_status_and_player_action_cap_branches_are_bypassed(self):
        plain = zlib.decompress(self.generated[16:])
        start = 16 + self.report["newCodeLength"]
        extra = sum(len(builder.new_function_record(f["entry"], f["name"], f["returnType"],
                                                   f.get("arguments", ()), f.get("locals", ())))
                    for f in self.report["addedFunctions"])
        functions = builder.read_functions(plain[start:start + 0x1395C28 - 0xDE5CFA + extra])
        vm = ControlVm(self.generated, self.report)
        vm.rules = STATUS_IMMUNITY | UNLIMITED_ACTIONS
        for fid, args, receiver, want in (
            (0x6135, (1, 2), 100, 0), (0x6135, (0, 2), 100, 0x3BDD20),
            (0x68F5, (2,), 450, 0), (0x68F5, (2,), 460, 0x41992A),
            (0x6903, (2,), 450, 1), (0x6903, (2,), 460, 0x41A892),
            (0x6A44, (2,), 100, 0), (0x6A44, (3,), 100, 0), (0x6A44, (4,), 100, 0x42BAB6),
        ):
            result = vm.run({"entry": functions[fid]["address"], "returnType": 10}, args, receiver)
            self.assertEqual(result, {"continuation": want, "stack": []} if want > 1 else want)

    def test_card_management_grant_ignores_the_inheritance_marker(self):
        pump = next(f for f in self.report["addedFunctions"] if f["name"] == "R10Bridge_CardManagement")
        instructions = bytes.fromhex(pump["bytes"])
        self.assertNotIn(builder.instruction("S_PUSH", 0xFE), instructions)  # 削除
        self.assertIn(builder.instruction("CALLFUNC", 0x70C9), instructions)  # Consumable reward
        self.assertIn(builder.instruction("S_PUSH", 0x32B1), instructions)  # Test character
        self.assertIn(builder.instruction("S_PUSH", 0x32B2), instructions)  # Test item


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    args, remaining = parser.parse_known_args()
    SOURCE = args.source.resolve()
    unittest.main(argv=[sys.argv[0], *remaining], verbosity=2)
