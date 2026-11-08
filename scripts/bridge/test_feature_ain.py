"""Execute generated continuations and verify original caller/delegate stack contracts."""
import argparse
from pathlib import Path
import struct
import unittest
import zlib
import build_instrumented_ain as builder


class FeatureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        raw, report, _ = builder.build(SOURCE.read_bytes(), 'full')
        cls.code = zlib.decompress(raw[16:])[16:16 + report['newCodeLength']]

    def run_site(self, pc, width, enabled, delegate_done=False):
        original_pc=pc; stack=['caller-context']; calls=[]
        for _ in range(30):
            if pc == original_pc + width or pc in (0x3E4B44, 0x3E4BA4):
                return pc,stack,calls
            op=struct.unpack_from('<H',self.code,pc)[0];pc+=2
            n={0:1,0x2C:1,0x2D:1,0x5A:3,0xF4:2}[op]
            args=struct.unpack_from('<'+'i'*n,self.code,pc);pc+=4*n
            if op==0:stack.append(args[0])
            elif op==0x2C:pc=args[0]
            elif op==0x2D:
                if not stack.pop():pc=args[0]
            elif op==0x5A:
                self.assertEqual(args,(36,4,-1))
                b,a,kind=stack.pop(),stack.pop(),stack.pop()
                self.assertEqual((a,b),(0,0));calls.append(kind);stack.append(enabled)
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
                self.assertEqual(self.run_site(0x3E4B34,10,enabled,done),
                                 (target,['caller-context'],[7,'delegate']))


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True)
    args,remaining=parser.parse_known_args();SOURCE=args.source
    unittest.main(argv=['test_feature_ain',*remaining],verbosity=2)
