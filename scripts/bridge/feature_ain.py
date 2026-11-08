"""Version-pinned conditional equivalents of the legacy three operand patches.

Continuations retain the caller's stack and locals. Disabled paths execute the
original instruction; no running CODE bytes are mutated by the trainer.
"""
import struct


def install_features(add_function, replace_code, instruction):
    def conditional(name, pc, original, patched, feature):
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
                struct.pack('<Hii', 0xF4, 0x435, 0x3E4B44),
                struct.pack('<Hii', 0xF4, 0x435, 0x3E4BA4), 7)
