"""Read card definitions and mutate the selected live save on the game thread."""
import struct

PAGE, INSPECT, GRANT, SET_STAR = range(10, 14)
SET_ITEM_STAR = 24
ITEM_STAR_CAPABILITY = 16384
CLAIM_ITEM_STAR, COMPLETE_ITEM_STAR = 16, 17
COMMON_PARAMS, PARTY = 0x100, 0x104
GET_ITEM_STAR, GET_EFFECTIVE_ITEM_STAR, ADD_ITEM_STAR = 0x66AE, 0x66B1, 0x66C9
PAGE_SIZE, ROW_LIMIT = 32, 4096
CARD_STRINGS = {
    "CP936": ("卡牌数据", "Id", "识别名", "種別", "测试角色", "测试物品"),
    "CP932": ("カードデータ", "Id", "識別名", "種別", "テストキャラ", "テストアイテム"),
}
PLAYER_CARDS, CHARACTERS = 0x103, 0x101
CARD_SCENE_SCOPE, CARD_RESUME_CHECK, CARD_READY = 12, 14, 15
CARD_SCENES = (
    ("Battle", 0x6559, 0x3FF994, "CALLFUNC", 0x6711),
    ("BattleResult", 0x655B, 0x3FFA7C, "CALLFUNC", 0x6711),
    ("SimpleBattle", 0x6560, 0x3FFD8E, "CALLFUNC", 0x670B),
    ("SimpleBattleResult", 0x6561, 0x3FFE14, "CALLMETHOD", 0),
    ("CardReward", 0x6713, 0x4097B0, "CALLFUNC", 0x6711),
)


def install_card_scene_guards(add_function, replace_code, instruction, relocate):
    def scope(asm, delta):
        for value in (CARD_SCENE_SCOPE, delta, 0):
            asm.emit("PUSH", value)
        asm.emit("CALLHLL", 36, 6, -1)
        asm.emit("POP")

    for scene, _, source_pc, call, operand in CARD_SCENES:
        pc = relocate(source_pc)
        original = instruction(call, operand)

        def body(asm):
            scope(asm, 1)
            asm.emit(call, operand)
            scope(asm, -1)
            asm.emit("JUMP", pc + len(original))

        name = f"R10Bridge_CardScene{scene}"
        function = add_function(name, 0, [], body)
        # Jump into the continuation with the original call arguments still on the stack.
        replace_code(pc, original, instruction("JUMP", add_function.entries[function]), name)


def install_card_resume_pump(add_function, section, functions, card_pump, require):
    name = "R10Bridge_CardResumePump"
    strings = section[8:-1].split(b"\0")
    count, = struct.unpack_from("<i", section, 4)
    require(count == len(strings) and section.endswith(b"\0"), "Invalid STR0 table")
    appended = []

    def intern(value):
        if value in strings:
            return strings.index(value)
        appended.append(value)
        return count + len(appended) - 1

    own_name = intern(name.encode("ascii"))
    empty = strings.index(b"")
    blocked = []
    for scene, caller, *_ in CARD_SCENES:
        blocked.append(intern(functions[caller]["name"]))
        blocked.append(intern(f"R10Bridge_CardScene{scene}".encode("ascii")))

    card_call = 0

    def pump(asm):
        nonlocal card_call
        index, frame = 0, 1

        def check_resume(verified):
            for value in (CARD_RESUME_CHECK, verified, 0):
                asm.emit("PUSH", value)
            asm.emit("CALLHLL", 36, 6, -1)

        def jump_if_name(string_index, target):
            asm.local(frame); asm.emit("A_REF"); asm.emit("S_PUSH", string_index)
            asm.emit("S_EQUALE"); asm.emit("IFNZ", target)

        check_resume(0)
        asm.emit("IFZ", "cards")
        # An empty or unavailable stack is insufficient. Index zero must see this frame.
        asm.emit("PUSH", 0); asm.emit("CALLHLL", 0, 22, -1)
        asm.emit("S_PUSH", own_name); asm.emit("S_EQUALE"); asm.emit("IFZ", "cards")
        asm.emit("PUSHLOCALPAGE"); asm.emit("PUSH", index); asm.emit("PUSH", 1)
        asm.emit("ASSIGN"); asm.emit("POP")
        asm.label("next")
        asm.local(index); asm.emit("PUSH", 256); asm.emit("LT"); asm.emit("IFZ", "cards")
        asm.local(frame); asm.local(index); asm.emit("CALLHLL", 0, 22, -1)
        asm.emit("S_ASSIGN"); asm.emit("DELETE")
        jump_if_name(empty, "safe")
        for string_index in blocked:
            jump_if_name(string_index, "cards")
        asm.emit("PUSHLOCALPAGE"); asm.emit("PUSH", index)
        asm.local(index); asm.emit("PUSH", 1); asm.emit("ADD"); asm.emit("ASSIGN"); asm.emit("POP")
        asm.emit("JUMP", "next")
        asm.label("safe"); check_resume(1); asm.emit("POP")
        asm.label("cards")
        card_call = asm.labels["cards"]
        asm.emit("CALLFUNC", card_pump); asm.emit("RETURN")

    new_section = section[:4] + struct.pack("<i", count + len(appended)) + section[8:]
    new_section += b"".join(value + b"\0" for value in appended)
    function = add_function(name, 0, [], pump, local_types=[("index", 10), ("frame", 12)])
    return function, new_section, card_call


def install_item_star(add_function, card_pump, functions, require):
    for index, name, arguments in ((GET_ITEM_STAR, b"PlayerCommonParam@ItemStar::get", 0),
                                   (GET_EFFECTIVE_ITEM_STAR, b"PlayerCommonParam@GetAlteredItemStar", 0),
                                   (ADD_ITEM_STAR, b"PlayerCommonParamEvent::AddItemStar", 2)):
        function = functions[index]
        require(function["name"] == name and function["args"] == arguments, "Unexpected item star function signature")

    def pump(asm):
        def card_edit(op, a=0, b=0):
            for value in (op, a, b):
                if callable(value):
                    value()
                else:
                    asm.emit("PUSH", value)
            asm.emit("CALLHLL", 36, 6, -1)

        asm.emit("CALLFUNC", card_pump)  # Refreshes the scene guard before either kind of request.
        asm.emit("PUSHLOCALPAGE"); asm.emit("PUSH", 0); card_edit(CLAIM_ITEM_STAR)
        asm.emit("ASSIGN"); asm.emit("POP")
        asm.local(0); asm.emit("IFZ", "return")
        for index in (COMMON_PARAMS, PARTY):
            asm.global_ref(index); asm.emit("PUSH", -1); asm.emit("EQUALE"); asm.emit("IFNZ", "reject")
        asm.local(0); asm.emit("PUSH", SET_ITEM_STAR); asm.emit("EQUALE"); asm.emit("IFZ", "read")
        card_edit(3); asm.global_get(COMMON_PARAMS, GET_ITEM_STAR); asm.emit("SUB")
        asm.emit("PUSH", 0); asm.emit("CALLFUNC", ADD_ITEM_STAR)  # Signed delta, no reward dialog. Recalculates party stats.
        asm.label("read")
        card_edit(COMPLETE_ITEM_STAR, lambda: asm.global_get(COMMON_PARAMS, GET_ITEM_STAR),
                  lambda: asm.global_get(COMMON_PARAMS, GET_EFFECTIVE_ITEM_STAR))
        asm.emit("POP"); asm.emit("RETURN")
        asm.label("reject"); card_edit(COMPLETE_ITEM_STAR, -1, -1); asm.emit("POP")
        asm.label("return"); asm.emit("RETURN")

    return add_function("R10Bridge_ItemStar", 0, [], pump, local_types=[("kind", 10)])


def install_card_management(add_function, section, encoding):
    strings = section[8:-1].split(b"\0")
    table, id_field, character_field, type_field, *test_ids = (
        strings.index(name.encode(encoding)) for name in CARD_STRINGS[encoding])
    kind, row, total, end, flags, char_index, limit, target, card_id, character_id = range(10)
    local_types = [(name, 10) for name in ("kind", "row", "total", "end", "flags", "charIndex", "limit", "target")]
    local_types += [("cardId", 12), ("characterId", 12)]

    def pump(asm):
        def value(v):
            if callable(v):
                v()
            else:
                asm.emit("PUSH", v)

        def local(index):
            return lambda: asm.local(index)

        def assign(index, v):
            asm.emit("PUSHLOCALPAGE"); asm.emit("PUSH", index); value(v)
            asm.emit("ASSIGN"); asm.emit("POP")

        def increment(index):
            def next_value():
                value(local(index)); asm.emit("PUSH", 1); asm.emit("ADD")
            assign(index, next_value)

        def native(op, a=0, b=0, discard=False):
            asm.emit("PUSH", op); value(a); value(b); asm.emit("CALLHLL", 36, 6, -1)
            if discard: asm.emit("POP")

        def text(index):
            value(local(index)); asm.emit("A_REF")

        def table_field(field, function):
            asm.emit("S_PUSH", table); value(local(row)); asm.emit("S_PUSH", field)
            asm.emit("S_PUSH" if function == 0x156E else "PUSH", 0)
            asm.emit("PUSH", 0); asm.emit("CALLFUNC", function)

        def load_text(index, field):
            value(local(index)); table_field(field, 0x156E)  # EX_RA2String
            asm.emit("S_ASSIGN"); asm.emit("DELETE")

        def copy_text(index, op):
            assign(char_index, 0)
            def length():
                value(local(index)); asm.emit("CALLHLL", 30, 2, -1)  # String.Length
            assign(limit, length)
            asm.label(f"text{op}")
            value(local(char_index)); value(local(limit)); asm.emit("LT"); asm.emit("IFZ", f"text{op}Done")
            asm.emit("PUSH", op)
            value(local(index)); value(local(char_index)); asm.emit("C_REF")
            asm.emit("PUSH", 0); asm.emit("CALLHLL", 36, 6, -1); asm.emit("POP")
            increment(char_index); asm.emit("JUMP", f"text{op}")
            asm.label(f"text{op}Done")

        def method(global_id, function, argument):
            asm.global_ref(global_id); asm.emit("PUSH", function); value(argument); asm.emit("CALLMETHOD", 1)

        def character():
            method(CHARACTERS, 0x6D48, lambda: text(character_id))  # Get, only after IsExist

        def character_value(function):
            character(); asm.emit("DUP"); asm.emit("PUSH", function); asm.emit("CALLMETHOD", 0)
            asm.emit("SWAP"); asm.emit("DELETE")

        def owned():
            text(card_id); asm.emit("CALLFUNC", 0x6761)  # Excludes temporary reward stock.
            asm.emit("DUP"); asm.emit("PUSH", -1); asm.emit("NOTE")
            asm.emit("SWAP"); asm.emit("DELETE")

        def is_kind(command, otherwise):
            value(local(kind)); asm.emit("PUSH", command); asm.emit("EQUALE"); asm.emit("IFZ", otherwise)

        # Scene lifetime is tracked by the bridge. The game retains the previous battle context.
        for index in (0xFE, PLAYER_CARDS, CHARACTERS):
            asm.global_ref(index); asm.emit("PUSH", -1); asm.emit("EQUALE"); asm.emit("IFNZ", "unavailable")
        asm.global_get(0xFE, 0x660A); asm.emit("IFZ", "unavailable")
        asm.global_ref(PLAYER_CARDS); asm.emit("PUSH", 1); asm.emit("REF")
        asm.emit("CALLHLL", 5, 19, 13); asm.emit("IFZ", "unavailable")  # Array.Empty(m_stock)
        asm.global_ref(0x132); asm.emit("CALLHLL", 11, 3, -1); asm.emit("IFZ", "unavailable")
        native(CARD_READY, 1, discard=True); asm.emit("JUMP", "claim")
        asm.label("unavailable"); native(CARD_READY, discard=True)
        asm.label("claim")
        assign(kind, lambda: native(1)); value(local(kind)); asm.emit("IFZ", "return")
        assign(row, lambda: native(2))
        def height():
            asm.emit("S_PUSH", table); asm.emit("PUSH", 0); asm.emit("CALLFUNC", 0x155D)  # EX_Height
        assign(total, height)
        value(local(total)); asm.emit("PUSH", ROW_LIMIT); asm.emit("LTE"); asm.emit("IFZ", "reject")
        value(local(row)); value(local(total)); asm.emit("LT"); asm.emit("IFZ", "reject")
        def page_end():
            value(local(row)); asm.emit("PUSH", PAGE_SIZE); asm.emit("ADD")
        assign(end, page_end)

        asm.label("row")
        load_text(card_id, id_field); load_text(character_id, character_field)
        assign(flags, owned)
        text(card_id); asm.emit("CALLFUNC", 0x70C9); asm.emit("IFNZ", "publish")  # Consumable reward
        # The table's integration marker affects inheritance, while the game can grant these cards.
        for test_id in test_ids:
            text(card_id); asm.emit("S_PUSH", test_id); asm.emit("S_EQUALE"); asm.emit("IFNZ", "publish")
        table_field(type_field, 0x156C); asm.emit("PUSH", 88); asm.emit("EQUALE"); asm.emit("IFNZ", "publish")
        def add_flag(flag):
            value(local(flags)); asm.emit("PUSH", flag); asm.emit("ADD")
        assign(flags, lambda: add_flag(2))
        value(local(character_id)); asm.emit("CALLHLL", 30, 4, -1); asm.emit("IFNZ", "publish")
        table_field(type_field, 0x156C); asm.emit("PUSH", 10); asm.emit("EQUALE"); asm.emit("IFNZ", "publish")
        assign(flags, lambda: add_flag(4))

        asm.label("publish")
        native(4, local(row), local(flags), discard=True)
        copy_text(card_id, 5); copy_text(character_id, 6)
        is_kind(PAGE, "selected")
        increment(row)
        value(local(row)); value(local(total)); asm.emit("LT"); asm.emit("IFZ", "done")
        value(local(row)); value(local(end)); asm.emit("LT"); asm.emit("IFNZ", "row")
        asm.emit("JUMP", "done")

        asm.label("selected")
        native(7); asm.emit("IFZ", "reject")
        is_kind(GRANT, "details")
        value(local(flags)); asm.emit("PUSH", 2); asm.emit("AND"); asm.emit("IFZ", "reject")
        value(local(flags)); asm.emit("PUSH", 1); asm.emit("AND"); asm.emit("IFNZ", "details")
        text(card_id); asm.emit("CALLFUNC", 0x70F2); asm.emit("POP")  # Ｐ友方卡牌追加
        native(7); asm.emit("IFZ", "reject")
        owned(); asm.emit("IFZ", "reject")
        assign(flags, lambda: add_flag(1)); native(8, discard=True)

        asm.label("details")
        value(local(flags)); asm.emit("PUSH", 5); asm.emit("AND"); asm.emit("PUSH", 5); asm.emit("EQUALE")
        asm.emit("IFZ", "noStar")
        method(CHARACTERS, 0x6D47, lambda: text(character_id)); asm.emit("IFZ", "noStar")
        assign(limit, lambda: asm.emit("CALLFUNC", 0x6D43))  # Character::GetMaxStar
        is_kind(SET_STAR, "readStar")
        assign(target, lambda: native(3))
        asm.emit("PUSH", 0); value(local(target)); asm.emit("LTE"); asm.emit("IFZ", "reject")
        value(local(target)); value(local(limit)); asm.emit("LTE"); asm.emit("IFZ", "reject")
        character_value(0x6D1E); value(local(target)); asm.emit("EQUALE"); asm.emit("IFNZ", "readStar")
        character(); asm.emit("DUP"); asm.emit("PUSH", 0x6D3E); value(local(target)); asm.emit("CALLMETHOD", 1)
        asm.emit("DELETE")  # Character@ForceSetStar resets EXP and recalculates NextExp.
        asm.global_get(PLAYER_CARDS, 0x6E2C)  # Invalidate organization status caches.
        character_value(0x6D1E); value(local(target)); asm.emit("EQUALE"); asm.emit("IFZ", "reject")

        asm.label("readStar")
        native(9, lambda: character_value(0x6D1E), lambda: character_value(0x6D25), discard=True)
        native(10, lambda: character_value(0x6D28), local(limit), discard=True)
        asm.emit("JUMP", "done")
        asm.label("noStar")
        is_kind(SET_STAR, "done"); asm.emit("JUMP", "reject")
        asm.label("done"); native(11, 1, local(total), discard=True); asm.emit("RETURN")
        asm.label("reject"); native(11, discard=True)
        asm.label("return"); asm.emit("RETURN")

    return add_function("R10Bridge_CardManagement", 0, [], pump, local_types=local_types)
