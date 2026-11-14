"""Execute emitted data pump; stub only original game methods and the native integer ABI."""
import argparse
from pathlib import Path
import struct
import unittest
import zlib
import build_instrumented_ain as builder
from data_ain import SETTERS, GETTERS


class DataVm:
    def __init__(self, code, entry, command=0, value=0, full=False, ready=True, cap=999, null=None):
        self.code, self.pc = code, entry
        self.command, self.value, self.full, self.ready, self.cap = command, value, full, ready, cap
        self.globals = {0xFE:100, 0x100:200, 0x101:300}
        if null is not None: self.globals[null] = -1
        self.values = [1,2,0,50]; self.sample = {}; self.writes = []; self.ack = None; self.data_ready = False

    def run(self):
        stack = []
        widths = {0:1,0x2C:1,0x2D:1,0x2E:1,0x30:1,0x5C:1,0x5A:3}
        for _ in range(2000):
            op=struct.unpack_from('<H',self.code,self.pc)[0]
            args=struct.unpack_from('<'+'i'*widths.get(op,0),self.code,self.pc+2); self.pc+=2+4*len(args)
            if op==0: stack.append(args[0])
            elif op==1: stack.pop()
            elif op==4: stack.append('globals')
            elif op==2:
                index,page=stack.pop(),stack.pop(); assert page=='globals'; stack.append(self.globals[index])
            elif op in (builder.OP["EQUALE"], builder.OP["LTE"], builder.OP["LT"]):
                right, left = stack.pop(), stack.pop()
                compare = {builder.OP["EQUALE"]: int.__eq__, builder.OP["LTE"]: int.__le__, builder.OP["LT"]: int.__lt__}
                stack.append(int(compare[op](left, right)))
            elif op in (0x2D,0x2E):
                if bool(stack.pop())==(op==0x2E): self.pc=args[0]
            elif op==0x2C: self.pc=args[0]
            elif op==0x30:
                assert args==(0x6D43,); stack.append(self.cap)
            elif op==0x5C:
                value=stack.pop() if args[0]==1 else None
                method,receiver=stack.pop(),stack.pop()
                if method==0x660A: assert receiver==100; stack.append(self.ready)
                elif method in GETTERS: assert receiver==200; stack.append(self.values[GETTERS.index(method)])
                elif method in SETTERS:
                    assert receiver==200; self.values[SETTERS.index(method)]=value; self.writes.append((method,value))
                elif method==0x6D4C: assert receiver==300; self.writes.append((method,value))
                else: raise AssertionError(hex(method))
            elif op==0x5A:
                assert args==(36,5,-1)
                b,a,action=stack.pop(),stack.pop(),stack.pop(); assert b==0; result=0
                if action==0: result=self.command
                elif action==1: result=self.value
                elif action==2:
                    if self.command:
                        self.ack=a
                        if self.command==6 and a: self.full=True
                        self.command=0
                elif action==6: self.data_ready=bool(a)
                elif action==7: result=int(self.full)
                elif action in range(10,14): self.sample[action-10]=a
                else: raise AssertionError(action)
                stack.append(result)
            elif op==0x2F: assert not stack; return self
            else: raise AssertionError(hex(op))
        raise AssertionError('Unbounded pump')


class DataTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        raw,report,_=builder.build(SOURCE.read_bytes(),'full')
        cls.code=zlib.decompress(raw[16:])[16:16+report['newCodeLength']]
        cls.entry=next(f['entry'] for f in report['addedFunctions'] if f['name']=='R10Bridge_DataPump')

    def vm(self, **kw): return DataVm(self.code,self.entry,**kw)

    def test_each_value_uses_matching_game_setter_and_readback(self):
        for kind,value in ((1,3),(2,0),(3,2),(4,999)):
            vm=self.vm(command=kind,value=value).run()
            self.assertEqual(vm.writes,[(SETTERS[kind-1],value)])
            self.assertEqual(vm.ack,1); self.assertTrue(vm.data_ready)
            self.assertEqual(vm.sample[kind-1],value)

    def test_star_delegates_all_four_targets_to_game_bulk_method(self):
        for value in (10,40,80,200):
            vm=self.vm(command=5,value=value).run()
            self.assertEqual(vm.writes,[(0x6D4C,value)]); self.assertEqual(vm.ack,1)

    def test_star_cap_preflight_prevents_partial_change(self):
        vm=self.vm(command=5,value=200,cap=100).run()
        self.assertEqual(vm.writes,[]); self.assertEqual(vm.ack,0)

    def test_no_game_or_null_globals_reject_without_touching_objects(self):
        for kw in ({'ready':False},*({'null':g} for g in (0xFE,0x100,0x101))):
            vm=self.vm(command=1,value=3,full=True,**kw).run()
            self.assertEqual(vm.writes,[]); self.assertEqual(vm.ack,0); self.assertFalse(vm.data_ready)

    def test_full_refills_only_missing_resources_and_never_unit(self):
        vm = self.vm(command=6)
        vm.values[1] = 0
        vm.run()
        self.assertEqual(vm.values, [3, 2, 3, 50])
        self.assertEqual(vm.ack, 1)
        vm.pc = self.entry
        vm.writes.clear()
        vm.run()
        self.assertEqual(vm.writes, [])

    def test_full_leaves_the_third_medal_for_the_game_to_convert(self):
        vm = self.vm(full=True)
        vm.values = [3, 3, 3, 50]
        vm.run()
        self.assertEqual(vm.writes, [])
        self.assertEqual(vm.sample[1], 3)
        vm.values[1] = 0  # AddHonor consumes the third medal after its dialog.
        vm.pc = self.entry
        vm.run()
        self.assertEqual(vm.writes, [(SETTERS[1], 2)])
        self.assertEqual(vm.sample[1], 2)

    def test_disabled_full_has_no_writes_and_rejects_manual_when_enabled(self):
        self.assertEqual(self.vm().run().writes,[])
        vm=self.vm(command=2,value=0,full=True).run()
        self.assertEqual(vm.writes,[]); self.assertEqual(vm.ack,0)


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--source',type=Path,required=True)
    args,remaining=parser.parse_known_args();SOURCE=args.source
    unittest.main(argv=['test_data_ain',*remaining],verbosity=2)
