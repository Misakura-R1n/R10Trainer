"""Execute the emitted reward wrappers with controlled game resolver fixtures."""
import argparse
from dataclasses import dataclass
from pathlib import Path
import struct
import unittest
import zlib

import build_instrumented_ain as builder
import cards_ain as cards


@dataclass
class Text:
    value: str = ""


class CardVm:
    WIDTHS = {0: 1, 0x2C: 1, 0x2D: 1, 0x2E: 1, 0x30: 1, 0x41: 1, 0x5A: 3}

    def __init__(self, code, report, strings, pool, owned=(), batch=(), variants=None, enabled=True, new_abi=True):
        self.code = code
        self.functions = {f["id"]: f for f in report["addedFunctions"]}
        self.by_name = {f["name"].removeprefix("R10Bridge_"): f for f in self.functions.values()}
        self.strings = strings
        self.arrays = {100: [Text(value) for value in ["", *pool]], 200: [Text(value) for value in batch]}
        self.box = [0] * 12
        self.box[cards.BOX_POOL], self.box[cards.BOX_COUNT] = 100, len(pool)
        self.owned = set(owned)
        self.variants = variants or {}
        self.enabled, self.new_abi = enabled, new_abi
        self.filtered = self.fallback = self.ordinal = self.width = 0
        self.pool = []
        self.roll_bounds, self.tickets, self.created = [], [], []

    def call(self, name, args=()):
        return self.run(self.by_name[name], args)

    def prepare(self, chapter):
        return self.run(self.by_name["FilterCards" + chapter], locals_=self.box)

    def draw(self, chapter, attempt=0):
        _, stack = self.run(self.by_name[f"RewardDraw{chapter}_{attempt}"], locals_=self.box,
                            stack_values=["local", cards.BOX_INDEX, self.box[cards.BOX_COUNT]])
        assert stack[:2] == ["local", cards.BOX_INDEX] and len(stack) == 3
        return stack[2]

    def create(self, chapter, slot):
        name = self.arrays[100][slot].value
        _, stack = self.run(self.by_name["RewardCreate" + chapter], locals_=self.box, stack_values=[name])
        assert stack == [1]
        return self.created[-1]

    def game_roll(self, width):
        self.roll_bounds.append(width)
        ticket = self.tickets.pop(0) if self.tickets else 1
        assert 1 <= ticket <= width
        return ticket

    def native(self, op, a, b):
        if op == cards.ENABLED: return int(self.enabled)
        if op == cards.BATCH_ARRAY: return 200
        if op == cards.FILTERED:
            self.filtered += 1
        elif op == cards.FALLBACK:
            self.fallback += 1
        elif not self.new_abi or not self.enabled:
            return 0
        elif op == cards.POOL_RESET:
            self.pool.clear()
            return 1
        elif op == cards.POOL_ADD:
            assert a >= cards.VARIANT_RADIX and 0 < b <= cards.WEIGHT_SCALE
            self.pool.append((a, b))
        elif op == cards.POOL_WEIGHT:
            return sum(weight for _, weight in self.pool)
        elif op == cards.POOL_PICK:
            for key, weight in self.pool:
                a -= weight
                if a <= 0:
                    return key
            raise AssertionError("Weighted ticket outside the pool")
        elif op == cards.PROBE_BEGIN:
            assert self.ordinal == self.width == 0 and a >= 1
            self.ordinal = a
            return 1
        elif op == cards.PROBE_ROLL:
            if not self.ordinal:
                return 0
            assert self.width == 0 and self.ordinal <= a
            self.width = a
            return self.ordinal
        elif op == cards.PROBE_ACTIVE:
            return int(self.ordinal != 0)
        elif op == cards.PROBE_END:
            width = self.width or 1
            assert 1 <= self.ordinal <= width
            self.ordinal = self.width = 0
            return width
        else:
            raise AssertionError(op)
        return 0

    def original(self, function, stack):
        if function == 0x1573:
            zero, field, card_id, table = (stack.pop() for _ in range(4))
            assert (table, field, zero) == ("卡牌数据", "Id", 0)
            return int(bool(card_id))
        if function == 0x70C9:
            return int(stack.pop().startswith("拟态／"))
        if function == 0x70FE:
            return int(stack.pop() in self.owned)
        if function == cards.ORIGINAL_ROLL:
            return self.game_roll(stack.pop())
        if function == cards.CARD_NAME_RESOLVER:
            name = stack.pop()
            options = self.variants.get(name, (name,))
            stack.append(options[self.call("RewardNameRoll", (len(options),)) - 1] if len(options) > 1 else options[0])
        else:
            assert function == cards.CREATE_CARD
        assert not self.ordinal, "Resolving a name must not create a card"
        self.created.append(stack.pop())
        return 1

    def run(self, function, args=(), locals_=None, stack_values=()):
        pc, stack = function["entry"], list(stack_values)
        if locals_ is None:
            locals_ = [Text(value) if isinstance(value, str) else value for value in args]
            locals_.extend(Text() if kind == 12 else 0 for _, kind in function.get("locals", ()))
        arithmetic = {9: int.__add__, 0x0B: int.__mul__, 0x0C: int.__floordiv__, 0x0D: int.__mod__,
                      0x13: int.__lt__, 0x15: int.__le__}
        for _ in range(100000):
            op, = struct.unpack_from("<H", self.code, pc)
            width = self.WIDTHS.get(op, 0)
            operands = struct.unpack_from("<" + "i" * width, self.code, pc + 2)
            pc += 2 + 4 * width
            if op == 0: stack.append(operands[0])
            elif op in (1, 0x77): stack.pop()
            elif op == 5: stack.append("local")
            elif op == 2:
                index, page = stack.pop(), stack.pop()
                stack.append(locals_[index] if page == "local" else self.arrays[page][index])
            elif op == 0x19:
                value, index, page = stack.pop(), stack.pop(), stack.pop()
                assert page == "local"
                locals_[index] = value
                stack.append(value)
            elif op in arithmetic:
                right, left = stack.pop(), stack.pop()
                stack.append(int(arithmetic[op](left, right)))
            elif op == 0x41: stack.append(self.strings[operands[0]])
            elif op == 0x79:
                assert isinstance(stack[-1], Text)
                stack[-1] = stack[-1].value
            elif op == 0x49: stack.append(int(stack.pop() == stack.pop()))
            elif op == 0x44:
                value, destination = stack.pop(), stack.pop()
                assert isinstance(value, str)
                destination.value = value
                stack.append(destination)
            elif op == 0x7A: stack.append(stack[-1])
            elif op == 0x30:
                target = operands[0]
                if target in self.functions:
                    child = self.functions[target]
                    count = len(child["arguments"])
                    child_args = [stack.pop() for _ in range(count)][::-1]
                    stack.append(self.run(child, child_args))
                else:
                    stack.append(self.original(target, stack))
            elif op == 0x5A:
                if operands == (5, 17, 12):
                    stack.append(len(self.arrays[stack.pop()]))
                else:
                    assert operands == (36, 3, -1)
                    b, a, action = stack.pop(), stack.pop(), stack.pop()
                    stack.append(self.native(action, a, b))
            elif op in (0x2D, 0x2E):
                if bool(stack.pop()) == (op == 0x2E):
                    pc = operands[0]
            elif op == 0x2C:
                target = operands[0]
                if target == 0xCDBF90:
                    # Supply the game's deterministic mapping or the outcomes
                    # of its one bounded roll, then execute the actual epilogue.
                    name = locals_[0].value
                    outcomes = self.variants.get(name, (name,))
                    ordinal = self.call("RewardNameRoll", (len(outcomes),)) if len(outcomes) > 1 else 1
                    stack.append(outcomes[ordinal - 1])
                    pc = self.by_name["RewardNameResolved"]["entry"]
                elif target < builder.CODE_SIZE:
                    return target, stack
                else:
                    pc = target
            elif op == 0x2F:
                assert len(stack) == 1
                return stack.pop()
            else:
                raise AssertionError(hex(op))
        raise AssertionError("Unbounded reward wrapper")


class CardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        generated, cls.report, _ = builder.build(SOURCE.read_bytes(), "full")
        plain = zlib.decompress(generated[16:])
        cls.code = plain[16:16 + cls.report["newCodeLength"]]
        start = next(s for name, s, _ in builder.SECTION_BOUNDS if name == "STR0")
        start += sum(cls.report["sectionDeltas"][name] for name in ("CODE", "FUNC", "HLL0"))
        reader = builder.Reader(plain, start + 4)
        cls.strings = [reader.string().decode("gbk", errors="replace") for _ in range(reader.integer())]

    def vm(self, pool, **options):
        return CardVm(self.code, self.report, self.strings, pool, **options)

    def test_owned_variants_are_removed_with_original_weights(self):
        cara = ("Lv30 卡拉", "Lv33 卡拉", "Lv35 卡拉", "Lv40 卡拉", "Lv45 卡拉")
        knight = ("Lv30 赫尔曼骑士", "Lv33 赫尔曼骑士", "Lv35 赫尔曼骑士", "Lv40 赫尔曼骑士", "Lv45 赫尔曼骑士")
        for chapter in ("Chapter1", "Chapter2"):
            vm = self.vm(["通用卡拉", "通用赫尔曼骑士", "Lv40 弓兵"], owned=[cara[3], knight[0]],
                         variants={"通用卡拉": cara, "通用赫尔曼骑士": knight})
            _, stack = vm.prepare(chapter)
            self.assertEqual(stack, ["local", cards.BOX_INDEX])
            self.assertEqual(vm.pool, [(12, 6), (13, 6), (14, 6), (16, 6),
                                       (24, 6), (25, 6), (26, 6), (27, 6), (34, 30)])
            self.assertEqual(vm.filtered, 2)
            self.assertEqual(vm.roll_bounds, [])
            self.assertEqual(vm.created, [])
            vm.tickets = [25, 13]  # Initial choice and the original game's reroll.
            self.assertEqual(vm.draw(chapter), 2)
            slot = vm.draw(chapter, attempt=1)
            self.assertEqual(slot, 1)
            self.assertEqual(vm.create(chapter, slot), "Lv35 卡拉")
            self.assertEqual(vm.roll_bounds, [78, 78])

    def test_fixed_and_previous_random_ids_reserve_exact_variants(self):
        for chapter in ("Chapter1", "Chapter2"):
            vm = self.vm(["generic", "A", "B", "C"], variants={"generic": ("A", "B", "C")},
                         batch=["A", "B", ""])
            vm.prepare(chapter)
            self.assertEqual(vm.pool, [(14, 10), (45, 30)])
            self.assertEqual(vm.create(chapter, vm.draw(chapter)), "C")

    def test_repeatable_consumables_and_empty_filtered_pool(self):
        for chapter in ("Chapter1", "Chapter2"):
            vm = self.vm(["generic", "拟态／餐券"], variants={"generic": ("A", "B", "C")},
                         owned=["A", "B", "C", "拟态／餐券"], batch=["拟态／餐券"])
            vm.prepare(chapter)
            self.assertEqual(vm.create(chapter, vm.draw(chapter)), "拟态／餐券")
            self.assertEqual(vm.fallback, 0)
            vm = self.vm(["generic", "A"], variants={"generic": ("A", "B", "C")}, owned=["A", "B", "C"])
            self.assertEqual(vm.prepare(chapter), 0)
            self.assertEqual((vm.filtered, vm.fallback), (4, 1))
            self.assertEqual(vm.roll_bounds, [])

    def test_disabled_or_older_dll_uses_the_original_draw(self):
        for options in ({"enabled": False}, {"new_abi": False}):
            vm = self.vm(["generic"], owned=["A"], variants={"generic": ("A", "B", "C")}, **options)
            vm.prepare("Chapter1")
            slot = vm.draw("Chapter1")
            self.assertEqual(vm.create("Chapter1", slot), "A")
            self.assertEqual(vm.roll_bounds, [1, 3])
            self.assertEqual((vm.filtered, vm.fallback), (0, 0))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    args, remaining = parser.parse_known_args()
    SOURCE = args.source
    unittest.main(argv=["test_cards_ain", *remaining], verbosity=2)
