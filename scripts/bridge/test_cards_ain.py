"""Execute emitted filter bytecode against owned/batch fixtures, not a second filter implementation."""
import argparse
from pathlib import Path
import struct
import unittest
import zlib
import build_instrumented_ain as builder


class CardVm:
    def __init__(self, code, entry, pool, owned=(), batch=(), enabled=True):
        self.code, self.pc = code, entry
        self.strings = {}
        def strings(values):
            result = []
            for text in values:
                key = len(self.strings) + 1000
                self.strings[key] = text; result.append(key)
            return result
        self.arrays = {100: strings([''] + list(pool)), 200: strings(batch)}
        self.locals = {7:100, 8:len(pool), 3:0, 9:0, 2:0}
        self.owned, self.enabled = set(owned), enabled
        self.filtered = self.fallback = 0

    def run(self):
        stack = []
        widths = {0:1, 0x2C:1, 0x2D:1, 0x2E:1, 0x30:1, 0x5A:3}
        for _ in range(100000):
            op = struct.unpack_from('<H',self.code,self.pc)[0]
            args = struct.unpack_from('<'+'i'*widths.get(op,0),self.code,self.pc+2)
            self.pc += 2+4*len(args)
            if op==0: stack.append(args[0])
            elif op==1: stack.pop()
            elif op==5: stack.append('local')
            elif op==2:
                index,page=stack.pop(),stack.pop()
                stack.append(self.locals[index] if page=='local' else self.arrays[page][index])
            elif op==0x19:
                value,index,page=stack.pop(),stack.pop(),stack.pop(); assert page=='local'
                self.locals[index]=value;stack.append(value)
            elif op==9: stack.append(stack.pop()+stack.pop())
            elif op in (0x13,0x15):
                right,left=stack.pop(),stack.pop();stack.append(int(left<right if op==0x13 else left<=right))
            elif op==0x79: stack.append(self.strings[stack.pop()])
            elif op==0x49: stack.append(int(stack.pop()==stack.pop()))
            elif op==0x44:
                value,key=stack.pop(),stack.pop();self.strings[key]=value;stack.append(key)
            elif op==0x77: stack.pop()
            elif op==0x30:
                text=stack.pop()
                assert args[0] in (0x70C9,0x70FE)
                stack.append(int(text.startswith('拟态／') if args[0]==0x70C9 else text in self.owned))
            elif op==0x5A:
                if args==(5,17,12): stack.append(len(self.arrays[stack.pop()]))
                else:
                    assert args==(36,3,-1)
                    b,a,action=stack.pop(),stack.pop(),stack.pop();assert (a,b)==(0,0)
                    if action==0:stack.append(int(self.enabled))
                    elif action==3:stack.append(200)
                    elif action==4:self.filtered+=1;stack.append(0)
                    elif action==5:self.fallback+=1;stack.append(0)
                    else:raise AssertionError(action)
            elif op in (0x2D,0x2E):
                if bool(stack.pop())==(op==0x2E):self.pc=args[0]
            elif op==0x2C:
                if args[0]<builder.CODE_SIZE:
                    assert stack==['local',9]
                    return [self.strings[k] for k in self.arrays[100][1:self.locals[8]+1]]
                self.pc=args[0]
            elif op==0x2F:
                assert stack==[0];return []
            else:raise AssertionError(hex(op))
        raise AssertionError('unbounded filter')


class CardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        generated,report,_=builder.build(SOURCE.read_bytes(),'full')
        plain=zlib.decompress(generated[16:]);cls.code=plain[16:16+report['newCodeLength']]
        cls.entries=[f['entry'] for f in report['addedFunctions'] if f['name'].startswith('R10Bridge_FilterCards')]

    def check_pool(self,pool,expected,owned=(),batch=(),enabled=True):
        for entry in self.entries:
            vm=CardVm(self.code,entry,pool,owned,batch,enabled)
            self.assertEqual(vm.run(),expected)
            self.assertEqual(vm.fallback,int(enabled and not expected))

    def test_owned_filter_and_weight_preservation(self):
        self.check_pool(['A','B','A','C','B'],['B','C','B'],owned=['A'])
    def test_exact_variant_not_character(self):
        self.check_pool(['A1','A2'],['A2'],owned=['A1'])
    def test_fixed_and_previous_random_slots_reserved(self):
        self.check_pool(['A','B','C'],['C'],batch=['B','','A'])
    def test_extras_can_repeat(self):
        self.check_pool(['A','拟态／餐券'],['拟态／餐券'],owned=['A','拟态／餐券'],batch=['拟态／餐券'])
    def test_all_owned_falls_back(self):
        self.check_pool(['A','A'],[],owned=['A'])
    def test_single_candidate(self):
        self.check_pool(['A','B'],['B'],owned=['A'])
    def test_disable_preserves_original_pool(self):
        self.check_pool(['A','B','B'],['A','B','B'],owned=['A'],batch=['B'],enabled=False)
    def test_next_batch_does_not_reserve_unselected_cards(self):
        self.check_pool(['A','B'],['B'],batch=['A'])
        self.check_pool(['A','B'],['A','B'])


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True)
    args,remaining=parser.parse_known_args();SOURCE=args.source
    unittest.main(argv=['test_cards_ain',*remaining],verbosity=2)
