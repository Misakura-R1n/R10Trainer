"""Version-pinned continuations for game-thread feature switches.

Continuations retain the caller's stack and locals. Disabled paths execute the
original instruction; no running CODE bytes are mutated by the trainer.
"""
import struct

EXTRA_TREASURE_PICK = 20
EXTRA_TREASURE_PICK_CAPABILITY = 8192
TREASURE_FIND = 25
TREASURE_FIND_CAPABILITY = 32768


def install_features(add_function, replace_code, instruction, relocate):
    def conditional(name, pc, original, patched, feature):
        pc = relocate(pc)
        def body(a):
            a.emit('PUSH', feature); a.emit('PUSH', 0); a.emit('PUSH', 0)
            a.emit('CALLHLL', 36, 4, -1)
            a.emit('IFZ', 'original')
            a.code.extend(patched); a.emit('JUMP', pc + len(original))
            a.label('original')
            a.code.extend(original); a.emit('JUMP', pc + len(original))
        fid = add_function(name, 0, [], body)
        replace_code(pc, original, instruction('JUMP', add_function.entries[fid]) +
                     instruction('POP') * ((len(original) - 6) // 2), name)

    conditional('R10Bridge_FoodTicketRedraw', 0x3B085C,
                instruction('PUSH', 0x6080), instruction('PUSH', 0x6085), 6)
    # 2000 is waitTime's delay, not a resource price. Consumption stays untouched.
    conditional('R10Bridge_FoodTicketDelay', 0x3B1318,
                instruction('PUSH', 2000), instruction('PUSH', 0), 6)
    conditional('R10Bridge_AutoBattleRepick', 0x3E4B34,
                struct.pack('<Hii', 0xF4, 0x435, relocate(0x3E4B44)),
                struct.pack('<Hii', 0xF4, 0x435, relocate(0x3E4BA4)), 7)


def install_extra_treasure_pick(add_function, replace_code, instruction, entry, has_game_bonus):
    # At either splice, m_cardCount is initialized. Only OpenTreasureBox passes two
    # candidates. The shared selector's other callers pass three.
    pc = entry + (0x84 if has_game_bonus else 0x8E)
    bonus = entry + 0x90  # Original tt[406] reward branch in the two patched sources.
    original = (instruction("IFNZ", bonus) if has_game_bonus else
                instruction("ASSIGN") + instruction("POP") + instruction("PUSHSTRUCTPAGE"))

    def body(asm):
        for value in (EXTRA_TREASURE_PICK, 0, 0):
            asm.emit("PUSH", value)
        asm.emit("CALLHLL", 36, 4, -1)
        if has_game_bonus:
            asm.emit("ADD")  # Combine the two booleans before the original branch.
        asm.emit("IFZ", "original")
        if not has_game_bonus:
            asm.emit("PUSH", 1); asm.emit("ADD")  # The normal/difficult pick limit is on the caller's stack.
        asm.member(2); asm.emit("PUSH", 2); asm.emit("EQUALE")
        asm.emit("IFZ", "chosen")
        asm.emit("PUSHSTRUCTPAGE"); asm.emit("PUSH", 2); asm.emit("PUSH", 3)
        asm.emit("ASSIGN"); asm.emit("POP")
        asm.label("chosen")
        if has_game_bonus:
            asm.emit("JUMP", bonus)
        asm.label("original")
        if not has_game_bonus:
            asm.code.extend(original)
        asm.emit("JUMP", pc + len(original))

    name = "R10Bridge_ExtraTreasurePick"
    function = add_function(name, 0, [], body)
    replace_code(pc, original, instruction("JUMP", add_function.entries[function]), name)


def install_treasure_find(add_function, replace_code, instruction, relocate):
    # CalcTreasure consumes the player's battle-status contribution here. Hooking
    # the shared getter instead would also change CommonStatus.Add and persist
    # the override during Party.RecalcStatus. Always read the natural value, then
    # replace only this reward input; disabling immediately uses the latest base.
    pc = relocate(0x3B8448)
    original = instruction("PUSH", 0x65A3) + instruction("CALLMETHOD", 0)

    def body(asm):
        asm.code.extend(original)
        for value in (TREASURE_FIND, 0, 0):
            asm.emit("PUSH", value)
        asm.emit("CALLHLL", 36, 4, -1)
        asm.emit("IFZ", "done")
        asm.emit("POP"); asm.emit("PUSH", 100)
        asm.label("done")
        asm.emit("JUMP", pc + len(original))

    name = "R10Bridge_TreasureFind"
    function = add_function(name, 0, [], body)
    replace_code(pc, original, instruction("JUMP", add_function.entries[function]) +
                 instruction("POP") * 3, name)
