"""Resolve, filter and draw random card-box outcomes on the game thread."""
from card_management_ain import CARD_STRINGS

ENABLED, BATCH_BEGIN, BATCH_END, BATCH_ARRAY, FILTERED, FALLBACK = range(6)
PROBE_BEGIN, PROBE_ROLL, PROBE_ACTIVE, PROBE_END = range(6, 10)
POOL_RESET, POOL_ADD, POOL_WEIGHT, POOL_PICK = range(10, 14)
WEIGHT_SCALE, VARIANT_RADIX = 30, 11
CARD_NAME_RESOLVER, ORIGINAL_ROLL, CREATE_CARD = 0x8BBF, 0x8BBB, 0x70F2
BATCH_SLOT = 2  # SceneGetCard.CreateCard's current index.
BOX_SELECTED, BOX_POOL, BOX_COUNT, BOX_INDEX = 2, 7, 8, 9  # q is idle by the box draw.
# Each branch has an initial draw, an optional game reroll and a creation call.
BOX_BRANCHES = (
    ("Chapter2", 0xD29A00, (0xD29A12, 0xD29A58), 0xD29B6E),
    ("Chapter1", 0xD42D82, (0xD42D94, 0xD42DE2), 0xD42E7E),
)
NAME_ROLLS = (
    0xCDC050, 0xCDC1FC, 0xCDC3A8, 0xCDC50C, 0xCDC63E, 0xCDC770,
    0xCDC8A2, 0xCDC9D4, 0xCDCB06, 0xCDCC38, 0xCDCD6A, 0xCDCE9C,
    0xCDCFCE, 0xCDD100, 0xCDD232, 0xCDD364, 0xCDD496, 0xCDD5C8,
    0xCDD6FA, 0xD473E2,  # Includes the alternate-outfit resolver.
)


def install_cards(add_function, replace_code, instruction, relocate, functions, section, encoding, require):
    strings = section[8:-1].split(b"\0")
    table, id_field = (strings.index(name.encode(encoding)) for name in CARD_STRINGS[encoding][:2])
    converter = functions[CARD_NAME_RESOLVER]
    require(converter["address"] == relocate(0xCDBF90) and converter["args"] == 1
            and [t[0] for t in converter["types"]] == [12, 10, 10, 10, 12, 10, 12],
            "Unexpected reward name resolver signature")

    def native(a, op, first=0, second=0):
        a.emit("PUSH", op)
        for arg in (first, second):
            if callable(arg):
                arg(a)
            else:
                a.emit("PUSH", arg)
        a.emit("CALLHLL", 36, 3, -1)

    def assign(a, index, value):
        a.emit("PUSHLOCALPAGE"); a.emit("PUSH", index)
        if callable(value):
            value(a)
        else:
            a.emit("PUSH", value)
        a.emit("ASSIGN"); a.emit("POP")

    def increment(a, index):
        def next_value(b):
            b.local(index); b.emit("PUSH", 1); b.emit("ADD")
        assign(a, index, next_value)

    def text(a, index):
        a.local(index); a.emit("A_REF")

    def splice(name, pc, expected, body):
        pc = relocate(pc)
        function = add_function(name, 0, [], lambda a: body(a, pc + len(expected)))
        jump = instruction("JUMP", add_function.entries[function])
        replace_code(pc, expected, jump + instruction("POP") * ((len(expected) - len(jump)) // 2), name)
        return function

    def batch_hook(name, pc, expected, body):
        def continuation(a, resume):
            body(a)
            a.code.extend(expected)
            a.emit("JUMP", resume)
        splice(name, pc, expected, continuation)

    def begin(a):
        native(a, BATCH_BEGIN, lambda b: b.local(0)); a.emit("POP")

    def remember(a):
        # The original ID array already reserves fixed slots. Record each
        # generated ID in its slot before the next random draw.
        native(a, ENABLED); a.emit("IFZ", "done")
        a.local(0); a.local(BATCH_SLOT); a.emit("REF")
        a.local(6); a.emit("PUSH", 0x6DB6); a.emit("CALLMETHOD", 0)
        a.emit("S_ASSIGN"); a.emit("DELETE")
        a.label("done")

    def end(a):
        native(a, BATCH_END); a.emit("POP")

    push_local = instruction("PUSHLOCALPAGE")
    batch_hook("R10Bridge_CardBatchBegin", 0x3ACE3A, push_local + instruction("PUSH", 0), begin)
    batch_hook("R10Bridge_CardBatchRemember", 0x3AD0B2, push_local + instruction("PUSH", 1), remember)
    batch_hook("R10Bridge_CardBatchEnd", 0x3AD0F2, push_local + instruction("PUSH", 5), end)

    # Enter the game's converter using the same local layout. In probe mode its
    # terminal creation call returns the resolved string to this frame instead.
    # The converter and its outfit helper have at most one roll per name.
    resolver = add_function("R10Bridge_ResolveRewardId", 12, [("name", 12)],
                            lambda a: a.emit("JUMP", converter["address"]),
                            local_types=[("roll", 10), ("bound", 10), ("pending", 10),
                                         ("namePart", 12), ("index", 10), ("variant", 12)])

    def probe_roll(a):
        native(a, PROBE_ROLL, lambda b: b.local(0))
        a.emit("DUP"); a.emit("IFNZ", "return")
        a.emit("POP"); a.local(0); a.emit("CALLFUNC", ORIGINAL_ROLL)
        a.label("return"); a.emit("RETURN")
    roll = add_function("R10Bridge_RewardNameRoll", 10, ["bound"], probe_roll)
    for pc in NAME_ROLLS:
        replace_code(relocate(pc), instruction("CALLFUNC", ORIGINAL_ROLL), instruction("CALLFUNC", roll),
                     "reward name roll supports side-effect-free enumeration")

    def resolved(a, resume):
        native(a, PROBE_ACTIVE); a.emit("IFNZ", "return_name")
        a.emit("CALLFUNC", CREATE_CARD); a.emit("JUMP", resume)
        a.label("return_name"); a.emit("RETURN")
    splice("R10Bridge_RewardNameResolved", 0xCE0668, instruction("CALLFUNC", CREATE_CARD), resolved)

    def allowed(a):
        a.emit("S_PUSH", table); text(a, 0); a.emit("S_PUSH", id_field); a.emit("PUSH", 0)
        a.emit("CALLFUNC", 0x1573); a.emit("IFZ", "reject")  # Same ID lookup as card creation.
        text(a, 0); a.emit("CALLFUNC", 0x70C9); a.emit("IFNZ", "keep")  # Repeatable consumables.
        text(a, 0); a.emit("CALLFUNC", 0x70FE); a.emit("IFNZ", "duplicate")
        assign(a, 1, 0)
        a.label("slot")
        a.local(1); native(a, BATCH_ARRAY); a.emit("CALLHLL", 5, 17, 12)
        a.emit("LT"); a.emit("IFZ", "keep")
        native(a, BATCH_ARRAY); a.local(1); a.emit("REF"); a.emit("A_REF")
        text(a, 0); a.emit("S_EQUALE"); a.emit("IFNZ", "duplicate")
        increment(a, 1); a.emit("JUMP", "slot")
        a.label("keep"); a.emit("PUSH", 1); a.emit("RETURN")
        a.label("duplicate"); native(a, FILTERED); a.emit("POP")
        a.label("reject"); a.emit("PUSH", 0); a.emit("RETURN")
    is_allowed = add_function("R10Bridge_RewardAllowed", 47, [("id", 12)], allowed,
                              local_types=[("slot", 10)])

    def build_pool(a):
        pool, count, slot, ordinal, width, actual = range(6)

        def key(b):
            b.local(slot); b.emit("PUSH", VARIANT_RADIX); b.emit("MUL")
            b.local(ordinal); b.emit("ADD")

        def weight(b):
            b.emit("PUSH", WEIGHT_SCALE); b.local(width); b.emit("DIV")

        native(a, POOL_RESET); a.emit("IFZ", "unavailable")
        assign(a, slot, 1)
        a.label("entry")
        a.local(slot); a.local(count); a.emit("LTE"); a.emit("IFZ", "done")
        assign(a, ordinal, 1)
        a.label("variant")
        native(a, PROBE_BEGIN, lambda b: b.local(ordinal)); a.emit("IFZ", "next")
        a.local(actual)
        a.local(pool); a.local(slot); a.emit("REF"); a.emit("A_REF")
        a.emit("CALLFUNC", resolver); a.emit("S_ASSIGN"); a.emit("DELETE")
        assign(a, width, lambda b: native(b, PROBE_END))
        a.local(width); a.emit("IFZ", "next")
        text(a, actual); a.emit("CALLFUNC", is_allowed); a.emit("IFZ", "advance")
        native(a, POOL_ADD, key, weight); a.emit("POP")
        a.label("advance")
        increment(a, ordinal)
        a.local(ordinal); a.local(width); a.emit("LTE"); a.emit("IFNZ", "variant")
        a.label("next"); increment(a, slot); a.emit("JUMP", "entry")
        a.label("done"); native(a, POOL_WEIGHT); a.emit("RETURN")
        a.label("unavailable"); a.emit("PUSH", -1); a.emit("RETURN")
    build = add_function("R10Bridge_BuildRewardPool", 10, ["pool", "count"], build_pool,
                         local_types=[("slot", 10), ("ordinal", 10), ("width", 10), ("id", 12)])

    def generate(a):
        native(a, PROBE_BEGIN, lambda b: b.local(1)); a.emit("IFZ", "fallback")
        a.local(2); text(a, 0); a.emit("CALLFUNC", resolver)
        a.emit("S_ASSIGN"); a.emit("DELETE")
        native(a, PROBE_END); a.emit("IFZ", "fallback")
        text(a, 2); a.emit("CALLFUNC", is_allowed); a.emit("IFZ", "fallback")
        native(a, POOL_RESET); a.emit("POP")
        text(a, 2); a.emit("CALLFUNC", CREATE_CARD); a.emit("RETURN")
        a.label("fallback")
        native(a, POOL_RESET); a.emit("POP")
        native(a, FALLBACK); a.emit("POP"); a.emit("PUSH", 0); a.emit("RETURN")
    create = add_function("R10Bridge_CreateResolvedReward", 10, [("name", 12), "ordinal"], generate,
                          local_types=[("id", 12)])

    def prepare(a, resume):
        native(a, ENABLED); a.emit("IFZ", "original")
        a.local(BOX_POOL); a.local(BOX_COUNT); a.emit("CALLFUNC", build)
        a.emit("IFNZ", "original")
        native(a, FALLBACK); a.emit("POP"); a.emit("PUSH", 0); a.emit("RETURN")
        a.label("original")
        a.emit("PUSHLOCALPAGE"); a.emit("PUSH", BOX_INDEX); a.emit("JUMP", resume)

    def draw(a, resume):
        native(a, POOL_WEIGHT); a.emit("IFZ", "original")
        a.emit("POP")  # Replace the game's original pool size with total weight.

        def choose(b):
            def ticket(c):
                native(c, POOL_WEIGHT); c.emit("CALLFUNC", ORIGINAL_ROLL)
            native(b, POOL_PICK, ticket)

        assign(a, BOX_SELECTED, choose)
        a.local(BOX_SELECTED); a.emit("PUSH", VARIANT_RADIX); a.emit("DIV")
        a.emit("JUMP", resume)
        a.label("original"); a.emit("CALLFUNC", ORIGINAL_ROLL); a.emit("JUMP", resume)

    def finish(a, resume):
        native(a, POOL_WEIGHT); a.emit("IFZ", "original")
        a.local(BOX_SELECTED); a.emit("PUSH", VARIANT_RADIX); a.emit("MOD")
        a.emit("CALLFUNC", create); a.emit("JUMP", resume)
        a.label("original"); a.emit("CALLFUNC", CARD_NAME_RESOLVER); a.emit("JUMP", resume)

    for chapter, start, rolls, create_at in BOX_BRANCHES:
        splice(f"R10Bridge_FilterCards{chapter}", start, push_local + instruction("PUSH", BOX_INDEX), prepare)
        for attempt, pc in enumerate(rolls):
            splice(f"R10Bridge_RewardDraw{chapter}_{attempt}", pc, instruction("CALLFUNC", ORIGINAL_ROLL), draw)
        splice(f"R10Bridge_RewardCreate{chapter}", create_at, instruction("CALLFUNC", CARD_NAME_RESOLVER), finish)
