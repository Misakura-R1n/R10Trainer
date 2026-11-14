"""Typed game-thread operations. Never retain VM object references in native code."""

SETTERS = (0x6699, 0x669C, 0x66A2, 0x66A5)
GETTERS = (0x6697, 0x669A, 0x66A0, 0x66A3)


def data(asm, op, a=0, b=0, discard=False):
    for value in (op, a, b): asm.emit("PUSH", value)
    asm.emit("CALLHLL", 36, 5, -1)
    if discard: asm.emit("POP")


def install_data(add_function):
    def pump(asm):
        data(asm, 6, 0, discard=True)
        for global_id in (0xFE, 0x100, 0x101):
            asm.global_ref(global_id); asm.emit("PUSH", -1); asm.emit("EQUALE"); asm.emit("IFNZ", "invalid")
        asm.global_get(0xFE, 0x660A); asm.emit("IFZ", "invalid")  # GameContext.Phase
        for index, (setter, getter) in enumerate(zip(SETTERS, GETTERS), 1):
            data(asm, 0); asm.emit("PUSH", index); asm.emit("EQUALE"); asm.emit("IFZ", f"next{index}")
            if index <= 3:
                data(asm, 7); asm.emit("IFNZ", "invalid")
            asm.global_ref(0x100); asm.emit("PUSH", setter); data(asm, 1); asm.emit("CALLMETHOD", 1)
            asm.global_get(0x100, getter); data(asm, 1); asm.emit("EQUALE"); asm.emit("IFZ", "invalid")
            data(asm, 2, 1, discard=True); asm.emit("JUMP", "maintain")
            asm.label(f"next{index}")
        data(asm, 0); asm.emit("PUSH", 5); asm.emit("EQUALE"); asm.emit("IFZ", "full")
        # Preflight the game's configured cap before the bulk call can change any character.
        data(asm, 1); asm.emit("CALLFUNC", 0x6D43); asm.emit("LTE"); asm.emit("IFZ", "invalid")
        asm.global_ref(0x101); asm.emit("PUSH", 0x6D4C); data(asm, 1); asm.emit("CALLMETHOD", 1)
        data(asm, 2, 1, discard=True); asm.emit("JUMP", "maintain")
        asm.label("full")
        data(asm, 0); asm.emit("PUSH", 6); asm.emit("EQUALE"); asm.emit("IFZ", "maintain")
        data(asm, 2, 1, discard=True)
        asm.label("maintain")
        data(asm, 7); asm.emit("IFZ", "sample")
        # A third medal is earned and converted to a rank by AddHonor. Its reward dialog can
        # yield after setting the count to three, so leave that in-flight value alone.
        for index, (setter, getter, target) in enumerate(zip(SETTERS[:3], GETTERS[:3], (3, 2, 3))):
            asm.global_get(0x100, getter); asm.emit("PUSH", target); asm.emit("LT"); asm.emit("IFZ", f"filled{index}")
            asm.global_ref(0x100); asm.emit("PUSH", setter); asm.emit("PUSH", target); asm.emit("CALLMETHOD", 1)
            asm.label(f"filled{index}")
        asm.label("sample")
        for index, getter in enumerate(GETTERS):
            asm.emit("PUSH", 10+index); asm.global_get(0x100, getter); asm.emit("PUSH", 0)
            asm.emit("CALLHLL", 36, 5, -1); asm.emit("POP")
        data(asm, 6, 1, discard=True); asm.emit("RETURN")
        asm.label("invalid")
        data(asm, 2, 0, discard=True); asm.emit("RETURN")
    return add_function("R10Bridge_DataPump", 0, [], pump)


def install_resource_hud(add_function, replace_code, instruction, functions):
    # CommonFramePoint owns the pip sprites on the mission and quest HUD. Register with
    # the game layer's update list, using VM delegates like the native battle scene.
    add_update, erase_update, add_deleted = 0x1B36, 0x1B37, 0x1B26
    update_value, get_value = 0x58C9, 0x58CB
    is_show, get_part_number, is_initialized = 0x7339, 0x733A, 0x733F
    point_parts, point_parent, point_type = 0, 1, 2

    def parent_call(asm, method):
        asm.member(point_parent); asm.emit("PUSH", method); asm.emit("CALLMETHOD", 0)

    def update(asm):
        parent_call(asm, is_initialized); asm.emit("IFZ", "done")
        asm.global_ref(0x100); asm.emit("PUSH", -1); asm.emit("EQUALE"); asm.emit("IFNZ", "done")
        asm.emit("PUSHLOCALPAGE"); asm.emit("PUSH", 2); asm.this_get(get_value); asm.emit("ASSIGN"); asm.emit("POP")
        for index in range(3):
            asm.member(point_parts); asm.emit("PUSH", index); asm.emit("REF")
            asm.emit("PUSH", is_show); asm.emit("CALLMETHOD", 0)
            asm.emit("PUSH", index); asm.local(2); asm.emit("LT"); asm.emit("EQUALE")
            asm.emit("IFZ", "refresh")
        asm.emit("RETURN")
        asm.label("refresh"); asm.this_get(update_value)
        asm.label("done"); asm.emit("RETURN")

    callback = add_function("CommonFramePoint@R10BridgeUpdate", 0, ["passedTime", "scaledTime"], update,
                            local_types=[("count", 10)])

    def unregister(asm):
        asm.emit("PUSHSTRUCTPAGE"); asm.emit("PUSH", callback); asm.emit("DG_NEW_FROM_METHOD")
        asm.emit("CALLFUNC", erase_update); asm.emit("RETURN")

    deleted = add_function("CommonFramePoint@R10BridgeDeleted", 0, ["partNumber"], unregister)

    def initialize(asm):
        asm.this_get(update_value)
        asm.member(point_type); asm.emit("IFZ", "done")  # War results use the game's own refresh.
        asm.emit("PUSHSTRUCTPAGE"); asm.emit("PUSH", callback); asm.emit("DG_NEW_FROM_METHOD")
        asm.emit("CALLFUNC", add_update)
        parent_call(asm, get_part_number)
        asm.emit("PUSHSTRUCTPAGE"); asm.emit("PUSH", deleted); asm.emit("DG_NEW_FROM_METHOD")
        asm.emit("CALLFUNC", add_deleted)
        asm.label("done"); asm.emit("RETURN")

    wrapper = add_function("CommonFramePoint@R10BridgeInit", 0, [], initialize)
    init = functions[0x58C3]
    def init_tail(method):
        return (instruction("PUSHSTRUCTPAGE") + instruction("PUSH", method) +
                instruction("CALLMETHOD", 0) + instruction("RETURN"))

    if init["name"] != b"CommonFramePoint@Init" or init["args"] != 1:
        raise ValueError("Unexpected CommonFramePoint initializer")
    replace_code(init["address"] + 36, init_tail(update_value), init_tail(wrapper),
                 "register resource HUD refresh at point initialization")
