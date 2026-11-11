"""Ordinary battle changes executed by the supported game's own thread."""
from controls_ain import control, dispatch_battle_control


CONTEXT, ENEMY, PARTY, PLAYER_PARAMETERS = 0x124, 0x123, 0x104, 0x100
AP_FULL, UNLIMITED_SKILLS, STATUS_IMMUNITY, REPEAT_ACTIONS, UNLIMITED_ACTIONS = (1, 2, 4, 8, 16)
FLAG, ENTER, LEAVE, REPORT_AP, CLAIM_HP, HP_VALUE, FINISH_HP, TARGET_CHANGED, REFRESH_SELECTOR = range(9)


def battle(asm, op, a=0, b=0, discard=False):
    asm.emit("PUSH", op)
    a(asm) if callable(a) else asm.emit("PUSH", a)
    b(asm) if callable(b) else asm.emit("PUSH", b)
    asm.emit("CALLHLL", 36, 7, -1)
    if discard:
        asm.emit("POP")


def flag(asm, bit):
    battle(asm, FLAG, bit, lambda a: a.global_ref(CONTEXT))


def member_of_value(asm, index):
    asm.emit("PUSH", index); asm.emit("REF")


def player(asm, side):
    asm.global_ref(CONTEXT); member_of_value(asm, 3)  # BattleContext.m_players
    member_of_value(asm, side)


def player_hp(asm, side, method=0x6810):
    player(asm, side); asm.emit("PUSH", method); asm.emit("CALLMETHOD", 0)


def ap_object(asm):
    asm.global_ref(CONTEXT); member_of_value(asm, 14)  # BattleContext.Ap


def current_ap(asm):
    ap_object(asm); asm.emit("PUSH", 0x67B7); asm.emit("CALLMETHOD", 0)


def max_ap(asm):
    asm.global_get(PLAYER_PARAMETERS, 0x66A6)


def install_battle(add_function, redirect, replace_code, instruction, sample_hp, controls, relocate):
    def refill(asm):
        flag(asm, AP_FULL); asm.emit("IFZ", "done")
        current_ap(asm); max_ap(asm); asm.emit("LT"); asm.emit("IFZ", "done")
        ap_object(asm); asm.emit("PUSH", 0x67B9); max_ap(asm); asm.emit("CALLMETHOD", 1)
        asm.emit("PUSH", 1); asm.emit("RETURN")
        asm.label("done"); asm.emit("PUSH", 0); asm.emit("RETURN")
    refill_ap = add_function("R10Bridge_RefillAp", 10, [], refill)

    def bad_group(asm):
        player(asm, 0); member_of_value(asm, 11)  # BattlePlayer.BadConditions

    def bad_count(asm):
        bad_group(asm); asm.emit("PUSH", 0x68FE); asm.emit("CALLMETHOD", 0)

    def cleanse(asm):
        flag(asm, STATUS_IMMUNITY); asm.emit("IFZ", "disabled")
        # EraseBadState(0) clears leader stun/stone. The group erase method updates its view.
        player(asm, 0); asm.emit("PUSH", 0x681B); asm.emit("PUSH", 0); asm.emit("CALLMETHOD", 1)
        bad_count(asm); asm.emit("ADD")
        asm.label("next")
        bad_count(asm); asm.emit("IFZ", "done")
        bad_group(asm); asm.emit("PUSH", 0x68F8)
        bad_count(asm); asm.emit("PUSH", -1); asm.emit("ADD"); asm.emit("CALLMETHOD", 1)
        asm.emit("JUMP", "next")
        asm.label("done"); asm.emit("RETURN")
        asm.label("disabled"); asm.emit("PUSH", 0); asm.emit("RETURN")
    clear_status = add_function("R10Bridge_ClearBattleStatus", 10, [], cleanse)

    def before_action_check(asm):
        # StartRound and skill/defence completion all check here before enabling selection.
        for function in (refill_ap, clear_status):
            asm.emit("CALLFUNC", function); asm.emit("POP")
    redirect(0x6134, "SceneBattle@isActionAvailable", 0x3BDA76, 0, before_action_check)

    def hp_command(asm):
        battle(asm, CLAIM_HP); asm.emit("DUP"); asm.emit("IFZ", "empty")
        asm.emit("PUSH", 1); asm.emit("EQUALE"); asm.emit("IFNZ", "player")
        for side in (1, 0):
            if side == 0:
                asm.label("player")
            player_hp(asm, side, 0x6813); battle(asm, HP_VALUE)
            asm.emit("LT"); asm.emit("IFNZ", "reject")
            player(asm, side); asm.emit("PUSH", 0x6812); battle(asm, HP_VALUE)
            asm.emit("CALLMETHOD", 1)
            battle(asm, HP_VALUE); asm.emit("IFZ", "reject")  # A setter event can change the battle.
            battle(asm, FINISH_HP, lambda a: player_hp(a, side), discard=True)
            asm.emit("CALLFUNC", sample_hp); asm.emit("RETURN")
        asm.label("reject"); battle(asm, FINISH_HP, -1, discard=True); asm.emit("RETURN")
        asm.label("empty"); asm.emit("POP"); asm.emit("RETURN")
    apply_hp = add_function("R10Bridge_ApplyBattleHp", 0, [], hp_command)

    def update(asm):
        battle(asm, FLAG, 0, lambda a: a.member(20)); asm.emit("IFZ", "unowned")
        asm.member(20); asm.global_ref(CONTEXT); asm.emit("EQUALE"); asm.emit("IFZ", "inactive")
        asm.member(18); asm.emit("PUSH", 0x73DF); asm.emit("CALLMETHOD", 0)
        asm.emit("IFZ", "inactive")
        control(asm, 4, lambda a: a.member(17)); asm.emit("IFZ", "inactive")
        for global_id in (PARTY, ENEMY):
            asm.global_ref(global_id); asm.emit("PUSH", -1); asm.emit("EQUALE")
            asm.emit("IFNZ", "inactive")
        for side in (0, 1):
            player_hp(asm, side); asm.emit("PUSH", 1); asm.emit("LT"); asm.emit("IFNZ", "inactive")
        control(asm, 5, 1, lambda a: a.global_ref(ENEMY), discard=True)
        asm.emit("CALLFUNC", refill_ap)
        battle(asm, REFRESH_SELECTOR); asm.emit("IFZ", "refresh")
        asm.emit("CALLFUNC", clear_status); asm.emit("ADD")
        asm.emit("PUSH", 1); asm.emit("ADD")
        asm.label("refresh"); asm.emit("IFZ", "snapshot")
        asm.member(17); asm.emit("PUSH", 0x6254); asm.emit("CALLMETHOD", 0)
        asm.label("snapshot"); battle(asm, REPORT_AP, current_ap, max_ap, discard=True)
        asm.emit("CALLFUNC", apply_hp)
        if controls:
            dispatch_battle_control(asm)
        else:
            asm.emit("RETURN")
        asm.label("inactive"); control(asm, 5, 0, discard=True); asm.emit("RETURN")
        asm.label("unowned"); asm.emit("RETURN")
    callback = add_function("SceneBattle@R10BridgeUpdate", 0, ["passedTime", "scaledTime"], update)

    def register(asm):
        asm.global_ref(ENEMY); asm.emit("PUSH", -1); asm.emit("EQUALE"); asm.emit("IFNZ", "skip")
        battle(asm, ENTER, lambda a: a.member(20), discard=True)
        asm.emit("PUSHSTRUCTPAGE"); asm.emit("PUSH", callback); asm.emit("DG_NEW_FROM_METHOD")
        asm.emit("CALLFUNC", 0x1B36)  # AFL_Parts_AddBeginUpdateEvent, current layer lifetime
        asm.label("skip")
    redirect(0x6132, "SceneBattle@Run", 0x3BD330, 0, register)

    def selector_changed(asm):
        asm.emit("PUSH", 20); asm.emit("PUSHSTRUCTPAGE"); asm.local(0)
        asm.emit("CALLHLL", 36, 0, -1); asm.emit("POP")
    redirect(0x6255, "BattleSkillSelector@SetControlEnable", 0x3D4E8E, 1, selector_changed)

    def leave(asm):
        battle(asm, LEAVE, discard=True)
    redirect(0x6370, "SceneSimpleBattle@Run", 0x3E3ECC, 0, leave)
    redirect(0x655D, "BattleScenes::RunWinLoseView", 0x3FFAFC, 0, leave)

    def target_changed(asm):
        battle(asm, TARGET_CHANGED, lambda a: a.global_ref(CONTEXT), discard=True)
    redirect(0x6117, "EnemyCreator::Change", 0x3BA6C6, 3, target_changed)

    def team_limit(asm):
        # The player-side call checks effects on side 1. The enemy-side caller checks side 0.
        asm.local(0); asm.emit("PUSH", 1); asm.emit("EQUALE"); asm.emit("IFZ", "original")
        flag(asm, UNLIMITED_ACTIONS); asm.emit("IFZ", "original")
        asm.emit("PUSH", 0); asm.emit("RETURN")
        asm.label("original")
    redirect(0x6135, "SceneBattle@CheckActionCountLimit", 0x3BDD20, 2, team_limit)

    def condition_guard(result):
        def prefix(asm):
            asm.member(0); asm.emit("IFNZ", "original")  # BadConditionGroup.m_side, 0 is the party
            flag(asm, STATUS_IMMUNITY); asm.emit("IFZ", "original")
            asm.emit("PUSH", result); asm.emit("RETURN")
            asm.label("original")
        return prefix
    redirect(0x68F5, "BadConditionGroup@IsEnableAddTo", 0x41992A, 1, condition_guard(0))
    redirect(0x6903, "BadConditionGroup@IsDisableType", 0x41A892, 1, condition_guard(1))

    def leader_guard(asm):
        asm.local(0); asm.emit("PUSH", 2); asm.emit("EQUALE"); asm.emit("IFNZ", "check")
        asm.local(0); asm.emit("PUSH", 3); asm.emit("EQUALE"); asm.emit("IFZ", "original")
        asm.label("check"); flag(asm, STATUS_IMMUNITY); asm.emit("IFZ", "original")
        asm.emit("PUSH", 0); asm.emit("RETURN")
        asm.label("original")
    redirect(0x6A44, "LeaderState@IsSettable", 0x42BAB6, 1, leader_guard)

    def allow_used(name, pc, blocked, bit):
        pc, blocked = relocate(pc), relocate(blocked)
        def body(asm):
            asm.emit("IFZ", "allowed")
            flag(asm, bit); asm.emit("IFNZ", "allowed")
            asm.emit("JUMP", blocked)
            asm.label("allowed"); asm.emit("JUMP", pc + 6)
        function = add_function(name, 0, [], body)
        replace_code(pc, instruction("IFNZ", blocked), instruction("JUMP", add_function.entries[function]), name)

    allow_used("R10Bridge_AllowRepeatAction", 0x43987C, 0x4398A0, REPEAT_ACTIONS)
    allow_used("R10Bridge_AllowLimitedSkill", 0x439C54, 0x439C90, UNLIMITED_SKILLS)
    allow_used("R10Bridge_LimitedSkillCost", 0x438FBE, 0x438FCA, UNLIMITED_SKILLS)
