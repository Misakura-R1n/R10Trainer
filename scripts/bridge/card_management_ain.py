"""Read card definitions and mutate the selected live save on the game thread."""

PAGE, INSPECT, GRANT, SET_STAR = range(10, 14)
PAGE_SIZE, ROW_LIMIT = 32, 4096
TABLE, ID, CHARACTER, TYPE, DELETED = 0xF3E, 0xFEE, 0xF21, 0xFD1, 0xFE
PLAYER_CARDS, CHARACTERS = 0x103, 0x101


def install_card_management(add_function):
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
            asm.emit("S_PUSH", TABLE); value(local(row)); asm.emit("S_PUSH", field)
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

        # Avoid an incomplete save, combat, a reward awaiting selection, or a generator callback.
        for index in (0xFE, PLAYER_CARDS, CHARACTERS):
            asm.global_ref(index); asm.emit("PUSH", -1); asm.emit("EQUALE"); asm.emit("IFNZ", "unavailable")
        asm.global_get(0xFE, 0x660A); asm.emit("IFZ", "unavailable")
        asm.global_ref(0x124); asm.emit("PUSH", -1); asm.emit("NOTE"); asm.emit("IFNZ", "unavailable")
        asm.global_ref(PLAYER_CARDS); asm.emit("PUSH", 1); asm.emit("REF")
        asm.emit("CALLHLL", 5, 19, 13); asm.emit("IFZ", "unavailable")  # Array.Empty(m_stock)
        asm.global_ref(0x132); asm.emit("CALLHLL", 11, 3, -1); asm.emit("IFZ", "unavailable")
        native(0, 1, discard=True); asm.emit("JUMP", "claim")
        asm.label("unavailable"); native(0, discard=True)
        asm.label("claim")
        assign(kind, lambda: native(1)); value(local(kind)); asm.emit("IFZ", "return")
        assign(row, lambda: native(2))
        def height():
            asm.emit("S_PUSH", TABLE); asm.emit("PUSH", 0); asm.emit("CALLFUNC", 0x155D)  # EX_Height
        assign(total, height)
        value(local(total)); asm.emit("PUSH", ROW_LIMIT); asm.emit("LTE"); asm.emit("IFZ", "reject")
        value(local(row)); value(local(total)); asm.emit("LT"); asm.emit("IFZ", "reject")
        def page_end():
            value(local(row)); asm.emit("PUSH", PAGE_SIZE); asm.emit("ADD")
        assign(end, page_end)

        asm.label("row")
        load_text(card_id, ID); load_text(character_id, CHARACTER)
        assign(flags, owned)
        text(card_id); asm.emit("CALLFUNC", 0x70C9); asm.emit("IFNZ", "publish")  # Consumable reward
        table_field(DELETED, 0x156C); asm.emit("IFNZ", "publish")  # EX_RA2Int
        for test_id in (0x32B1, 0x32B2):
            text(card_id); asm.emit("S_PUSH", test_id); asm.emit("S_EQUALE"); asm.emit("IFNZ", "publish")
        table_field(TYPE, 0x156C); asm.emit("PUSH", 88); asm.emit("EQUALE"); asm.emit("IFNZ", "publish")
        def add_flag(flag):
            value(local(flags)); asm.emit("PUSH", flag); asm.emit("ADD")
        assign(flags, lambda: add_flag(2))
        value(local(character_id)); asm.emit("CALLHLL", 30, 4, -1); asm.emit("IFNZ", "publish")
        table_field(TYPE, 0x156C); asm.emit("PUSH", 10); asm.emit("EQUALE"); asm.emit("IFNZ", "publish")
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
