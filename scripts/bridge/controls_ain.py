"""Version-pinned developer controls, dispatched through live SceneBattle callbacks.

No heap handles are retained by native code for dereferencing. The update delegate
owns its SceneBattle and is scoped to the same parts layer as the normal game UI.
"""


def control(asm, op, a=0, b=0, discard=False):
    asm.emit("PUSH", op)
    a(asm) if callable(a) else asm.emit("PUSH", a)
    b(asm) if callable(b) else asm.emit("PUSH", b)
    asm.emit("CALLHLL", 36, 2, -1)
    if discard:
        asm.emit("POP")


def install_load_control(add_function):
    def load_pump(asm):
        control(asm, 0); asm.emit("PUSH", 2); asm.emit("EQUALE"); asm.emit("IFZ", "done")
        control(asm, 1); asm.emit("CALLFUNC", 0x7027)  # LocalSave.IsExist
        asm.emit("IFZ", "reject")
        control(asm, 1)  # retain slot on stack before consuming the command
        control(asm, 3, 1, discard=True)
        asm.emit("CALLFUNC", 0x6708)  # ResetLoadManager.Run; may reset the VM
        asm.emit("RETURN")
        asm.label("reject"); control(asm, 3, 0, discard=True)
        asm.label("done"); asm.emit("RETURN")
    return add_function("R10Bridge_ControlPump", 0, [], load_pump)


def dispatch_battle_control(asm):
    control(asm, 0); asm.emit("PUSH", 3); asm.emit("EQUALE"); asm.emit("IFNZ", "end")
    control(asm, 0); asm.emit("PUSH", 4); asm.emit("EQUALE"); asm.emit("IFZ", "done")
    control(asm, 1); control(asm, 2); asm.emit("CALLFUNC", 0x6BA4)  # same availability helper as card buttons
    asm.emit("IFZ", "reject")
    asm.emit("PUSHSTRUCTPAGE"); asm.emit("PUSH", 0x6141)  # OnClickSkill schedules normal end task
    # Borrow the leader/string only after IsUsable validated the slot. A_REF copies the string argument.
    asm.global_ref(0x104)
    for index in (1, 0): asm.emit("PUSH", index); asm.emit("REF")
    control(asm, 1); asm.emit("REF"); asm.emit("PUSH", 0); asm.emit("REF"); asm.emit("A_REF")
    control(asm, 2)
    control(asm, 3, 1, discard=True)
    asm.emit("CALLMETHOD", 2); asm.emit("RETURN")
    asm.label("end")
    control(asm, 3, 1, discard=True)
    # Original ButtonTurnEnd callback invokes ProcessPostPlayerTurn.
    asm.this_get(0x6136); asm.emit("RETURN")
    asm.label("reject"); control(asm, 3, 0, discard=True); asm.emit("RETURN")
    asm.label("done"); asm.emit("RETURN")
