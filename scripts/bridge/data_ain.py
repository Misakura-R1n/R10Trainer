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
        # Preserve normal consumption and events. Refill only changed values on the next update.
        for index, (setter, getter) in enumerate(zip(SETTERS[:3], GETTERS[:3])):
            asm.global_get(0x100, getter); asm.emit("PUSH", 3); asm.emit("EQUALE"); asm.emit("IFNZ", f"filled{index}")
            asm.global_ref(0x100); asm.emit("PUSH", setter); asm.emit("PUSH", 3); asm.emit("CALLMETHOD", 1)
            asm.label(f"filled{index}")
        asm.label("sample")
        for index, getter in enumerate(GETTERS):
            asm.emit("PUSH", 10+index); asm.global_get(0x100, getter); asm.emit("PUSH", 0)
            asm.emit("CALLHLL", 36, 5, -1); asm.emit("POP")
        data(asm, 6, 1, discard=True); asm.emit("RETURN")
        asm.label("invalid")
        data(asm, 2, 0, discard=True); asm.emit("RETURN")
    return add_function("R10Bridge_DataPump", 0, [], pump)
