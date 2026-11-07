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


def install_controls(add_function, redirect):
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
    pump = add_function("R10Bridge_ControlPump", 0, [], load_pump)

    def battle_update(asm):
        asm.member(18); asm.emit("PUSH", 0x73DF); asm.emit("CALLMETHOD", 0)
        asm.emit("IFZ", "inactive")
        control(asm, 4, lambda a: a.member(17)); asm.emit("IFZ", "inactive")
        control(asm, 5, 1, discard=True)
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
        asm.label("inactive"); control(asm, 5, 0, discard=True)
        asm.label("done"); asm.emit("RETURN")
    callback = add_function("SceneBattle@R10BridgeUpdate", 0, ["passedTime", "scaledTime"], battle_update)

    def register(asm):
        asm.emit("PUSHSTRUCTPAGE"); asm.emit("PUSH", callback); asm.emit("DG_NEW_FROM_METHOD")
        asm.emit("CALLFUNC", 0x1B36)  # AFL_Parts_AddBeginUpdateEvent, current layer lifetime
    redirect(0x6132, register)

    def enabled(asm):
        asm.emit("PUSH", 20); asm.emit("PUSHSTRUCTPAGE"); asm.local(0)
        asm.emit("CALLHLL", 36, 0, -1); asm.emit("POP")
    redirect(0x6255, enabled)
    return pump
