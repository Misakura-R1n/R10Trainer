"""Filter the two version-pinned card pools inside a synchronous reward batch.

The native side borrows an array handle only; it never dereferences VM memory.
The script owns the array throughout CreateCard, including all fixed reward IDs.
"""

def install_cards(add_function, replace_code, code, instruction):
    def native(a, op, value=0):
        a.emit("PUSH", op)
        value(a) if callable(value) else a.emit("PUSH", value)
        a.emit("PUSH", 0); a.emit("CALLHLL", 36, 3, -1)

    def splice(name, pc, expected, body):
        def continuation(a):
            body(a)
            a.code.extend(expected)
            a.emit("JUMP", pc + len(expected))
        fid = add_function(name, 0, [], continuation)
        # add_function returns consecutive IDs, but continuations need their entry.
        entry = splice.entries[fid]
        replace_code(pc, expected, instruction("JUMP", entry) + instruction("POP") * ((len(expected)-6)//2), name)

    # Entries are supplied by the builder without exposing or changing FUNC locals.
    splice.entries = add_function.entries
    def begin(a):
        native(a, 1, lambda x: x.local(0)); a.emit("POP")
    splice("R10Bridge_CardBatchBegin", 0x3ACE3A, instruction("PUSHLOCALPAGE") + instruction("PUSH", 0), begin)

    def remember(a):
        # Store the generated ID into this batch's existing cardIds argument.
        # Fixed slots remain fixed; future random slots are still empty strings.
        native(a, 0); a.emit("IFZ", "done")
        a.local(0); a.local(2); a.emit("REF")
        a.local(6); a.emit("PUSH", 0x6DB6); a.emit("CALLMETHOD", 0)
        a.emit("S_ASSIGN"); a.emit("DELETE")
        a.label("done")
    splice("R10Bridge_CardBatchRemember", 0x3AD0B2, code[0x3AD0B2:0x3AD0BA], remember)
    def end(a):
        native(a, 2); a.emit("POP")
    splice("R10Bridge_CardBatchEnd", 0x3AD0F2, code[0x3AD0F2:0x3AD0FA], end)

    def assign(a, index, value):
        a.emit("PUSHLOCALPAGE"); a.emit("PUSH", index)
        value(a) if callable(value) else a.emit("PUSH", value)
        a.emit("ASSIGN"); a.emit("POP")
    def increment(a, index):
        assign(a, index, lambda x: (x.local(index), x.emit("PUSH", 1), x.emit("ADD")))
    def candidate(a):
        a.local(7); a.local(3); a.emit("REF"); a.emit("A_REF")
    def filter_pool(a):
        native(a, 0); a.emit("IFZ", "original")
        assign(a, 9, 0); assign(a, 3, 1)
        a.label("loop")
        a.local(3); a.local(8); a.emit("LTE"); a.emit("IFZ", "finish")
        candidate(a); a.emit("CALLFUNC", 0x70C9); a.emit("IFNZ", "keep")  # isExtraCardId
        candidate(a); a.emit("CALLFUNC", 0x70FE); a.emit("IFNZ", "drop")  # exact owned ID, excludes stock
        assign(a, 2, 0)
        a.label("batch")
        a.local(2); native(a, 3); a.emit("CALLHLL", 5, 17, 12)
        a.emit("LT"); a.emit("IFZ", "keep")
        native(a, 3); a.local(2); a.emit("REF"); a.emit("A_REF")
        candidate(a); a.emit("S_EQUALE"); a.emit("IFNZ", "drop")
        increment(a, 2); a.emit("JUMP", "batch")
        a.label("keep")
        increment(a, 9)
        a.local(7); a.local(9); a.emit("REF"); candidate(a)
        a.emit("S_ASSIGN"); a.emit("DELETE"); a.emit("JUMP", "next")
        a.label("drop"); native(a, 4); a.emit("POP")
        a.label("next"); increment(a, 3); a.emit("JUMP", "loop")
        a.label("finish"); assign(a, 8, lambda x: x.local(9))
        a.local(8); a.emit("IFNZ", "original")
        native(a, 5); a.emit("POP")
        a.emit("PUSH", 0); a.emit("RETURN")  # existing caller generates the food-ticket fallback
        a.label("original")
    for suffix, pc in (("Chapter2", 0xD29A00), ("Chapter1", 0xD42D82)):
        expected = instruction("PUSHLOCALPAGE") + instruction("PUSH", 9)
        splice("R10Bridge_FilterCards" + suffix, pc, expected, filter_pool)
