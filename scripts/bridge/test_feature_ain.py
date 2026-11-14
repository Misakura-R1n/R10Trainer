"""Execute generated continuations and verify original caller/delegate stack contracts."""
import argparse
from pathlib import Path
import struct
import unittest
import zlib
import build_instrumented_ain as builder
from ain_profiles import LAYOUTS, VERSION_BY_SOURCE
from feature_ain import TREASURE_FIND, TREASURE_FIND_CAPABILITY


class FeatureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        raw, report, _ = builder.build(SOURCE.read_bytes(), 'full')
        cls.code = zlib.decompress(raw[16:])[16:16 + report['newCodeLength']]

    def run_site(self, pc, width, enabled, delegate_done=False, *, code=None, stack=None, members=None, continuations=()):
        code = self.code if code is None else code
        stack = ['caller-context'] if stack is None else stack
        members = {} if members is None else members
        ends = {pc + width, *continuations}
        calls = []
        for _ in range(30):
            if pc in ends:
                return pc,stack,calls
            op=struct.unpack_from('<H',code,pc)[0];pc+=2
            n={0:1,0x01:0,0x02:0,0x09:0,0x18:0,0x19:0,0x2C:1,0x2D:1,0x5A:3,0x5B:0,0x5C:1,0xF4:2}[op]
            args=struct.unpack_from('<'+'i'*n,code,pc);pc+=4*n
            if op==0:stack.append(args[0])
            elif op==0x01: stack.pop()
            elif op==0x02:
                field=stack.pop()
                self.assertEqual(stack.pop(),'selector')
                stack.append(members[field])
            elif op==0x09: stack.append(stack.pop()+stack.pop())
            elif op==0x18: stack.append(stack.pop()==stack.pop())
            elif op==0x19:
                value, field = stack.pop(), stack.pop()
                self.assertEqual(stack.pop(),'selector')
                members[field] = value
                stack.append(value)
            elif op==0x2C:pc=args[0]
            elif op==0x2D:
                if not stack.pop():pc=args[0]
            elif op==0x5A:
                self.assertEqual(args,(36,4,-1))
                b,a,kind=stack.pop(),stack.pop(),stack.pop()
                self.assertEqual((a,b),(0,0));calls.append(kind);stack.append(enabled)
            elif op==0x5B: stack.append('selector')
            elif op==0x5C:
                self.assertEqual(args,(0,))
                self.assertEqual(stack.pop(),0x65A3)
                self.assertEqual(stack.pop(),'status')
                calls.append('natural-treasure')
                stack.append(members['treasure'])
            elif op==0xF4:
                self.assertEqual(args[0],0x435);calls.append('delegate')
                if delegate_done:pc=args[1]
        self.fail('Continuation did not reach original code')

    def test_food_callback_preserves_receiver(self):
        for enabled in (False,True):
            self.assertEqual(self.run_site(0x3B085C,6,enabled),
                             (0x3B0862,['caller-context',0x6085 if enabled else 0x6080],[6]))

    def test_food_delay_does_not_touch_consumption(self):
        for enabled in (False,True):
            self.assertEqual(self.run_site(0x3B1318,6,enabled),
                             (0x3B131E,['caller-context',0 if enabled else 2000],[6]))

    def test_delegate_body_still_runs_and_only_exit_changes(self):
        for enabled in (False,True):
            for done in (False,True):
                target=(0x3E4BA4 if enabled else 0x3E4B44) if done else 0x3E4B3E
                self.assertEqual(self.run_site(0x3E4B34,10,enabled,done,continuations=(0x3E4B44, 0x3E4BA4)),
                                 (target,['caller-context'],[7,'delegate']))

    def test_extra_pick_updates_both_counts_with_one_switch_read(self):
        for enabled in (False, True):
            for initial_limit in (1, 2):
                for candidate_count in (2, 3):
                    with self.subTest(enabled=enabled, initial_limit=initial_limit, candidate_count=candidate_count):
                        members = {2: candidate_count}
                        result = self.run_site(0x3ACB0A, 6, enabled,
                            stack=['caller-context', 'selector', 6, initial_limit], members=members)
                        self.assertEqual(result, (0x3ACB10, ['caller-context', 'selector'], [20]))
                        self.assertEqual(members, {2: 3 if enabled else candidate_count, 6: initial_limit + enabled})

    def test_extra_pick_preserves_existing_game_bonus_branch(self):
        checked = 0
        for path in COMPATIBILITY_SOURCES:
            raw = path.read_bytes()
            version = VERSION_BY_SOURCE[builder.sha(raw)]
            if not LAYOUTS[version['id']].has_extra_treasure_bonus:
                continue
            checked += 1
            generated, report, _ = builder.build(raw, 'full')
            self.assertTrue(report['capabilities'] & 8192)
            code = zlib.decompress(generated[16:])[16:16 + report['newCodeLength']]
            for game_bonus in (False, True):
                for enabled in (False, True):
                    with self.subTest(version=version['id'], game_bonus=game_bonus, enabled=enabled):
                        effective = game_bonus or enabled
                        members = {2: 2}
                        result = self.run_site(0x3ACB00, 6, enabled, code=code, continuations=(0x3ACB0C,),
                            stack=['caller-context', 'selector', 6, 'game-context', game_bonus], members=members)
                        self.assertEqual(result, (0x3ACB0C if effective else 0x3ACB06,
                            ['caller-context', 'selector', 6, 'game-context'], [20]))
                        self.assertEqual(members, {2: 3 if effective else 2})
        if not checked:
            self.skipTest('No input with the original treasure pick bonus')

    def test_treasure_find_uses_current_base_without_mutating_status_in_all_profiles(self):
        # Recalculation may replace the natural contribution between rewards. A
        # toggle affects only the consumed value and must never become save data.
        paths = list(dict.fromkeys([SOURCE, *COMPATIBILITY_SOURCES]))
        for path in paths:
            raw = path.read_bytes()
            version = VERSION_BY_SOURCE[builder.sha(raw)]
            layout = LAYOUTS[version['id']]
            original = zlib.decompress(raw[16:])
            original_code = original[16:16 + struct.unpack_from('<I', original, 12)[0]]
            pc = layout.relocate(0x3B8448)
            getter = layout.relocate(0x4009EC)
            for profile in ('full', 'controls'):
                generated, report, _ = builder.build(raw, profile)
                self.assertTrue(report['capabilities'] & TREASURE_FIND_CAPABILITY)
                code = zlib.decompress(generated[16:])[16:16 + report['newCodeLength']]
                self.assertEqual(code[getter:getter+26], original_code[getter:getter+26],
                                 'Shared getter must retain its natural aggregation/read/save behavior')
                members = {}
                for enabled, base in ((False, 22), (True, 22), (True, 0), (True, 150), (False, 7)):
                    with self.subTest(version=version['id'], profile=profile, enabled=enabled, base=base):
                        members['treasure'] = base
                        result = self.run_site(pc, 12, enabled, code=code,
                            stack=['caller-context', 'reward-bonus', 5, 'status'], members=members)
                        self.assertEqual(result, (pc + 12,
                            ['caller-context', 'reward-bonus', 5, 100 if enabled else base],
                            ['natural-treasure', TREASURE_FIND]))
                        self.assertEqual(members, {'treasure': base})

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True)
    parser.add_argument('--compatibility-source',type=Path,action='append',default=[])
    args,remaining=parser.parse_known_args();SOURCE=args.source;COMPATIBILITY_SOURCES=args.compatibility_source
    unittest.main(argv=['test_feature_ain',*remaining],verbosity=2)
