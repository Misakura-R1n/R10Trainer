using R10.Core;
using R10.Core.Memory;
using R10.Core.Patching;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace R10.Tests;

public static class PatchTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AIN idle performs no game I/O", () =>
        {
            using var memory = new PatchFakeMemory();
            var fingerprintReads = 0;
            var engine = new R10PatchEngine(memory, () => { fingerprintReads++; return BugFixProfile.Sha256; });
            Drive(engine); engine.RestoreAll();
            Assert(memory.ReadCount == 0 && memory.WriteCount == 0 && memory.QueryCount == 0 && fingerprintReads == 0);
        });
        test("AIN food pair changes only operands and restores exact originals", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redraw, true); Drive(engine);
            Assert(engine.HasRecoveryDebt && engine.Observations[FeatureId.Redraw].Active);
            Assert(memory.Writes.Count == 2);
            Assert(memory.Writes[0].Address == memory.CodeBase + 0x3B085E && Convert.ToHexString(memory.Writes[0].Bytes) == "85600000");
            Assert(memory.Writes[1].Address == memory.CodeBase + 0x3B131A && Convert.ToHexString(memory.Writes[1].Bytes) == "00000000");
            engine.RestoreAll();
            Assert(!engine.HasRecoveryDebt && !engine.Observations[FeatureId.Redraw].Requested);
            Assert(memory.Hex(0x3B085C, 6) == "000080600000" && memory.Hex(0x3B1318, 6) == "0000D0070000");
            Assert(memory.Writes.All(w => w.Bytes.Length == 4));
        });
        test("AIN RETURN operand uses verified bytecode continuation", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redistribute, true); Drive(engine);
            Assert(memory.Writes.Count == 1 && memory.Writes[0].Address == memory.CodeBase + 0x3E4B3A);
            Assert(Convert.ToHexString(memory.Writes[0].Bytes) == "A44B3E00");
            engine.RestoreAll(); Assert(memory.Hex(0x3E4B34, 10) == "F40035040000444B3E00");
        });
        test("AIN external patches never become owned", () =>
        {
            using var memory = new PatchFakeMemory();
            memory.Poke(0x3B085C, Convert.FromHexString("000085600000"));
            memory.Poke(0x3B1318, Convert.FromHexString("000000000000"));
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true); Drive(engine);
            Assert(engine.Observations[FeatureId.Redraw].Active && !engine.HasRecoveryDebt && memory.WriteCount == 0);
            engine.RestoreAll(); Assert(memory.WriteCount == 0 && memory.Hex(0x3B085C, 6) == "000085600000");
        });
        test("AIN all members preflight before first write", () =>
        {
            using var memory = new PatchFakeMemory(); memory.Poke(0x3B1318, [0x99]);
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true);
            Throws(() => Drive(engine)); Assert(memory.WriteCount == 0 && !engine.HasRecoveryDebt);
        });
        test("AIN mixed external and owned food operands restore only owned member", () =>
        {
            using var memory = new PatchFakeMemory(); memory.Poke(0x3B085C, Convert.FromHexString("000085600000"));
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true); Drive(engine);
            Assert(memory.WriteCount == 1 && memory.Writes[0].Address == memory.CodeBase + 0x3B131A);
            engine.RestoreAll();
            Assert(memory.WriteCount == 2 && !engine.HasRecoveryDebt && memory.Hex(0x3B085C, 6) == "000085600000");
        });
        test("AIN final group verification catches concurrent change of earlier operand", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            memory.WriteFault = (address, _) =>
            {
                if (address == memory.CodeBase + 0x3B131A)
                    memory.Poke(0x3B085C, Convert.FromHexString("000080600000"));
            };
            engine.SetRequested(FeatureId.Redraw, true); Throws(() => Drive(engine));
            Assert(!engine.HasRecoveryDebt && !engine.Observations[FeatureId.Redraw].Active);
            Assert(memory.Hex(0x3B085C, 6) == "000080600000" && memory.Hex(0x3B1318, 6) == "0000D0070000");
        });
        test("AIN CODE changes during write retain responsibility without unsafe rollback", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            memory.WriteFault = (_, _) => memory.Poke(0x3B0A1C, [0x99]);
            engine.SetRequested(FeatureId.Redraw, true); Throws(() => Drive(engine));
            Assert(memory.WriteCount == 1 && engine.HasRecoveryDebt && !engine.Observations[FeatureId.Redraw].Active);
            memory.WriteFault = null;
            Throws(engine.RestoreAll); Assert(memory.WriteCount == 1 && engine.HasRecoveryDebt);
        });
        test("AIN successful write with failed read-back recovers instead of reporting success", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory); var readFailure = false;
            memory.WriteFault = (address, _) => { if (address == memory.CodeBase + 0x3B085E) readFailure = true; };
            memory.ReadFault = (address, _) =>
            {
                if (readFailure && address == memory.CodeBase + 0x3B085C)
                { readFailure = false; memory.WriteFault = null; throw new IOException("read-back unavailable"); }
            };
            engine.SetRequested(FeatureId.Redraw, true); Throws(() => Drive(engine));
            Assert(!engine.HasRecoveryDebt && memory.Hex(0x3B085C, 6) == "000080600000");
        });
        test("AIN failed second operand rolls back first operand", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory); var faulted = false;
            memory.WriteFault = (address, bytes) =>
            {
                if (address == memory.CodeBase + 0x3B131A && !faulted)
                { faulted = true; memory.WriteDirect(address, bytes.AsSpan(0, 1)); throw new IOException("injected partial write"); }
            };
            engine.SetRequested(FeatureId.Redraw, true); Throws(() => Drive(engine));
            Assert(!engine.HasRecoveryDebt && !engine.Observations[FeatureId.Redraw].Active);
            Assert(memory.Hex(0x3B085C, 6) == "000080600000" && memory.Hex(0x3B1318, 6) == "0000D0070000");
        });
        test("AIN partial write retains pending debt when rollback fails", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory); var faulted = false;
            memory.WriteFault = (address, bytes) =>
            {
                if (address != memory.CodeBase + 0x3B131A) return;
                if (!faulted) { faulted = true; memory.WriteDirect(address, bytes.AsSpan(0, 1)); }
                throw new IOException("injected persistent failure");
            };
            engine.SetRequested(FeatureId.Redraw, true); Throws(() => Drive(engine));
            Assert(engine.HasRecoveryDebt && memory.Hex(0x3B085C, 6) == "000080600000");
            memory.WriteFault = null; engine.RestoreAll();
            Assert(!engine.HasRecoveryDebt && memory.Hex(0x3B1318, 6) == "0000D0070000");
        });
        test("AIN third-party conflicts retain debt without overwriting", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redistribute, true); Drive(engine);
            memory.Poke(0x3E4B3A, [0x99, 0x88, 0x77, 0x66]); var writes = memory.WriteCount;
            Throws(engine.RestoreAll); Assert(engine.HasRecoveryDebt && memory.WriteCount == writes);
            memory.Poke(0x3E4B3A, Convert.FromHexString("A44B3E00")); engine.RestoreAll();
            Assert(!engine.HasRecoveryDebt);
        });
        test("AIN closing never re-patches successfully restored members", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redraw, true); engine.SetRequested(FeatureId.Redistribute, true); Drive(engine);
            memory.Poke(0x3B085E, [0x99, 0x88, 0x77, 0x66]);
            Throws(engine.RestoreAll);
            Assert(engine.HasRecoveryDebt && memory.Hex(0x3B1318, 6) == "0000D0070000" && memory.Hex(0x3E4B34, 10) == "F40035040000444B3E00");
            Assert(!engine.Observations[FeatureId.Redraw].Requested && !engine.Observations[FeatureId.Redistribute].Requested);
        });
        test("AIN unknown old mapping keeps ownership", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redraw, true); Drive(engine);
            memory.Poke(0x3B0A1C, [0x99]); var writes = memory.WriteCount;
            Throws(engine.RestoreAll); Assert(engine.HasRecoveryDebt && memory.WriteCount == writes);
        });
        test("AIN fully freed old CODE discharges vanished-address debt", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redraw, true); Drive(engine); var writes = memory.WriteCount;
            memory.Regions = [new(memory.CodeBase, BugFixProfile.CodeLength, 0x10000, 0, 0)];
            engine.RestoreAll(); Assert(!engine.HasRecoveryDebt && memory.WriteCount == writes);
        });
        test("AIN partially free or reserved old CODE cannot retire", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redraw, true); Drive(engine); var writes = memory.WriteCount;
            memory.Regions = [new(memory.CodeBase, 0x800000, 0x10000, 0, 0),
                new(memory.CodeBase + 0x800000, BugFixProfile.CodeLength - 0x800000, 0x2000, 0, 0)];
            Throws(engine.RestoreAll); Assert(engine.HasRecoveryDebt && memory.WriteCount == writes);
        });
        test("AIN duplicate locator refuses ambiguity", () =>
        {
            using var memory = new PatchFakeMemory(); memory.Poke(0x100, BugFixProfile.Anchors[0].Bytes);
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true);
            Throws(() => Drive(engine)); Assert(memory.WriteCount == 0 && !engine.HasRecoveryDebt);
        });
        test("AIN unsupported fingerprint never scans or writes", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = new R10PatchEngine(memory, () => new string('0', 64));
            engine.SetRequested(FeatureId.Redraw, true); Throws(() => Drive(engine));
            Assert(memory.ReadCount == 0 && memory.QueryCount == 0 && memory.WriteCount == 0 && !engine.Observations[FeatureId.Redraw].Supported);
        });
        test("AIN bad RETURN prevents auto-battle patch", () =>
        {
            using var memory = new PatchFakeMemory(); memory.Poke(0x3E4BA4, [0x99, 0]);
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redistribute, true);
            Throws(() => Drive(engine)); Assert(memory.WriteCount == 0);
        });
        test("AIN old PID debt cannot move to replacement PID", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redraw, true); Drive(engine); var writes = memory.WriteCount;
            memory.ProcessId++; Throws(engine.RestoreAll); Assert(engine.HasRecoveryDebt && memory.WriteCount == writes);
        });
        test("AIN unsigned high addresses remain exact", () =>
        {
            using var memory = new PatchFakeMemory(0xC0000000); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redistribute, true); Drive(engine);
            Assert(memory.Writes[0].Address == 0xC03E4B3A); engine.RestoreAll(); Assert(!engine.HasRecoveryDebt);
        });
        test("AIN known original after external recovery clears lease", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redistribute, true); Drive(engine);
            memory.Poke(0x3E4B34, Convert.FromHexString("F40035040000444B3E00")); var writes = memory.WriteCount;
            engine.RestoreAll(); Assert(!engine.HasRecoveryDebt && writes == memory.WriteCount);
        });
        test("AIN scan: bounded slices finish full uniqueness search before any write", () =>
        {
            using var memory = new PatchFakeMemory();
            const int budget = 65536;
            var engine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, budget, scanClock: () => 0);
            engine.SetRequested(FeatureId.Redraw, true);
            int slices = 0;
            do
            {
                long before = memory.ReadBytes; engine.Maintain(); slices++;
                Assert(memory.ReadBytes - before <= budget && engine.LastScanReadBytes <= budget);
                Assert(memory.WriteCount == 0 && !engine.HasRecoveryDebt);
            } while (engine.IsLocating && slices < 1000);
            Assert(slices > 2 && slices < 1000 && memory.ReadBytes >= BugFixProfile.CodeLength);
            engine.Maintain(); Assert(engine.Observations[FeatureId.Redraw].Active && memory.WriteCount == 2);
            engine.RestoreAll();
        });
        test("AIN scan: elapsed budget yields after one slow native read", () =>
        {
            using var memory = new PatchFakeMemory(); long ticks = 0;
            memory.ReadFault = (_, length) => { if (length > 1024) ticks += Stopwatch.Frequency / 100; };
            var engine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, scanClock: () => ticks);
            engine.SetRequested(FeatureId.Redraw, true); engine.Maintain();
            Assert(engine.IsLocating && memory.ReadCount == 1 && engine.LastScanReadBytes == 65536);
            engine.SetRequested(FeatureId.Redraw, false); Assert(!engine.IsLocating && !engine.HasRecoveryDebt);
        });
        test("AIN scan: cooperative yield and cancel need no remaining scan I/O", () =>
        {
            using var memory = new PatchFakeMemory(); int hashes = 0;
            var engine = new R10PatchEngine(memory, () => { hashes++; return BugFixProfile.Sha256; });
            engine.SetRequested(FeatureId.Redraw, true); engine.Maintain(() => true);
            Assert(hashes == 0 && memory.ReadCount == 0 && memory.QueryCount == 0);
            engine.Maintain(); Assert(engine.IsLocating && hashes == 1);
            int reads = memory.ReadCount, queries = memory.QueryCount;
            engine.SetRequested(FeatureId.Redraw, false); engine.Maintain(); engine.RestoreAll();
            Assert(!engine.IsLocating && memory.ReadCount == reads && memory.QueryCount == queries && memory.WriteCount == 0);
        });
        test("AIN scan: request changes preserve scan but never apply canceled feature", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redraw, true); engine.SetRequested(FeatureId.Redistribute, true); engine.Maintain();
            var progress = engine.ScanProgress; Assert(engine.IsLocating);
            engine.SetRequested(FeatureId.Redraw, false); Assert(engine.IsLocating && engine.ScanProgress == progress);
            Drive(engine);
            Assert(memory.WriteCount == 1 && memory.Writes[0].Address == memory.CodeBase + 0x3E4B3A);
            Assert(!engine.Observations[FeatureId.Redraw].Requested && engine.Observations[FeatureId.Redistribute].Active);
            engine.RestoreAll();
        });
        test("AIN scan: late duplicate prevents early candidate acceptance", () =>
        {
            using var memory = new PatchFakeMemory();
            memory.Poke(BugFixProfile.CodeLength - 1000, BugFixProfile.Anchors[0].Bytes);
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true);
            Throws(() => Drive(engine));
            Assert(memory.WriteCount == 0 && !engine.HasRecoveryDebt && !engine.IsLocating && memory.ReadBytes > BugFixProfile.CodeLength - 2000);
        });
        test("AIN scan: candidate anchors changing between slices abort binding", () =>
        {
            using var memory = new PatchFakeMemory(); var engine = Engine(memory);
            engine.SetRequested(FeatureId.Redraw, true);
            do { engine.Maintain(); } while (engine.ScanProgress < .35);
            memory.Poke(0x3E4A96, [0x99]); Throws(engine.Maintain);
            Assert(!engine.IsLocating && !engine.HasRecoveryDebt && memory.WriteCount == 0);
        });
        test("AIN scan: unrelated new region preserves earlier scanning progress", () =>
        {
            using var memory = new PatchFakeMemory();
            memory.Regions = [new(memory.CodeBase, 0xC00000, 0x1000, 4, 0x20000),
                new(memory.CodeBase + 0xC00000, BugFixProfile.CodeLength - 0xC00000, 0x2000, 0, 0)];
            var engine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, 65536, scanClock: () => 0);
            engine.SetRequested(FeatureId.Redraw, true); engine.Maintain();
            int before = memory.Reads.Count;
            memory.Regions[1] = new(memory.CodeBase + 0xC00000, BugFixProfile.CodeLength - 0xC00000, 0x1000, 4, 0x20000);
            engine.Maintain();
            Assert(memory.Reads.Skip(before).First(r => r.Length > 1024).Address > memory.CodeBase && engine.ScanLayoutChanges == 1);
            Drive(engine); Assert(engine.Observations[FeatureId.Redraw].Active); engine.RestoreAll();
        });
        test("AIN scan: newly readable regions participate in final uniqueness", () =>
        {
            using var memory = new PatchFakeMemory();
            memory.Regions = [new(memory.CodeBase, 0xC00000, 0x1000, 4, 0x20000),
                new(memory.CodeBase + 0xC00000, BugFixProfile.CodeLength - 0xC00000, 0x2000, 0, 0)];
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true);
            do { engine.Maintain(); } while (engine.ScanProgress < .4);
            memory.Poke(0xC00100, BugFixProfile.Anchors[0].Bytes);
            memory.Regions[1] = new(memory.CodeBase + 0xC00000, BugFixProfile.CodeLength - 0xC00000, 0x1000, 4, 0x20000);
            Throws(() => Drive(engine)); Assert(memory.WriteCount == 0 && !engine.HasRecoveryDebt);
        });
        test("AIN scan: changed scanned region discards only its own old evidence", () =>
        {
            using var memory = new PatchFakeMemory();
            memory.Regions = [new(memory.CodeBase, 0x200000, 0x1000, 4, 0x20000),
                new(memory.CodeBase + 0x200000, BugFixProfile.CodeLength - 0x200000, 0x1000, 4, 0x20000)];
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true);
            do { engine.Maintain(); } while (engine.ScanProgress < .2);
            int before = memory.Reads.Count;
            memory.Poke(0x100, BugFixProfile.Anchors[0].Bytes);
            memory.Regions[0] = new(memory.CodeBase, 0x200000, 0x1000, 2, 0x20000);
            engine.Maintain();
            Assert(memory.Reads.Skip(before).First(r => r.Length > 1024).Address > memory.CodeBase + 0x200000);
            Throws(() => Drive(engine)); Assert(memory.WriteCount == 0 && !engine.HasRecoveryDebt);
        });
        test("AIN scan: failed chunk discards partial locating evidence", () =>
        {
            using var memory = new PatchFakeMemory(); int chunks = 0;
            memory.ReadFault = (_, length) => { if (length > 1024 && ++chunks == 3) throw new IOException("scan chunk unavailable"); };
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true);
            Throws(() => Drive(engine)); Assert(!engine.IsLocating && memory.WriteCount == 0);
            memory.ReadFault = null; Drive(engine); Assert(engine.Observations[FeatureId.Redraw].Active); engine.RestoreAll();
        });
        test("AIN scan: duplicate crossing adjacent readable regions is rejected", () =>
        {
            using var memory = new PatchFakeMemory();
            memory.Regions = [new(memory.CodeBase, 0x200000, 0x1000, 4, 0x20000),
                new(memory.CodeBase + 0x200000, BugFixProfile.CodeLength - 0x200000, 0x1000, 2, 0x20000)];
            memory.Poke(0x1FFFF0, BugFixProfile.Anchors[0].Bytes);
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true);
            Throws(() => Drive(engine));
            Assert(memory.WriteCount == 0 && !engine.HasRecoveryDebt && !engine.IsLocating);
        });
        test("AIN scan: boundary windows count toward byte budget and split native reads", () =>
        {
            using var memory = new PatchFakeMemory();
            memory.Regions = [new(memory.CodeBase, 0x200000, 0x1000, 4, 0x20000),
                new(memory.CodeBase + 0x200000, BugFixProfile.CodeLength - 0x200000, 0x1000, 2, 0x20000)];
            const int budget = 65536;
            var engine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, budget, scanClock: () => 0);
            engine.SetRequested(FeatureId.Redraw, true);
            do
            {
                long before = memory.ReadBytes; engine.Maintain();
                Assert(memory.ReadBytes - before <= budget && memory.WriteCount == 0);
            } while (engine.IsLocating);
            int overlap = BugFixProfile.Anchors[0].Bytes.Length - 1;
            Assert(memory.Reads.Any(r => r.Address == memory.CodeBase + 0x200000 - (uint)overlap && r.Length == overlap));
            Assert(memory.Reads.Any(r => r.Address == memory.CodeBase + 0x200000 && r.Length == overlap));
            engine.Maintain(); Assert(engine.Observations[FeatureId.Redraw].Active); engine.RestoreAll();
        });
        test("AIN scan: layout change invalidates previously scanned boundary evidence", () =>
        {
            using var memory = new PatchFakeMemory();
            memory.Regions = [new(memory.CodeBase, 0x200000, 0x1000, 4, 0x20000),
                new(memory.CodeBase + 0x200000, BugFixProfile.CodeLength - 0x200000, 0x1000, 2, 0x20000)];
            bool sawBoundary = false, changed = false; int overlap = BugFixProfile.Anchors[0].Bytes.Length - 1;
            memory.ReadFault = (address, length) =>
            { if (address == memory.CodeBase + 0x200000 - (uint)overlap && length == overlap) sawBoundary = true; };
            memory.QueryFault = () =>
            {
                if (!sawBoundary || changed) return;
                changed = true; memory.Poke(0x1FFFF0, BugFixProfile.Anchors[0].Bytes);
                memory.Regions[0] = new(memory.CodeBase, 0x200000, 0x1000, 2, 0x20000);
            };
            var engine = Engine(memory); engine.SetRequested(FeatureId.Redraw, true);
            Throws(() => Drive(engine)); Assert(changed && memory.WriteCount == 0 && !engine.HasRecoveryDebt);
        });
        foreach (int instructionReadGate in new[] { 1, 4 })
        test($"runtime/patch: close during preflight read {instructionReadGate} prevents first patch write", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256); int discoveries = 0, reads = 0;
            patchEngine.SetRequested(FeatureId.Redraw, true);
            do { patchEngine.Maintain(); } while (patchEngine.IsLocating);
            Assert(memory.Code.WriteCount == 0); patchEngine.SetRequested(FeatureId.Redraw, false);
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null, (_, _) => patchEngine);
            try
            {
                Await(runtime.StartAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Ready);
                memory.Code.ReadFault = (address, length) =>
                {
                    if (address == memory.Code.CodeBase + 0x3B085C && length == 6 && Interlocked.Increment(ref reads) == instructionReadGate)
                    { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("preflight gate expired"); }
                };
                Assert(Await(runtime.SetToggleAsync(FeatureId.Redraw, true)).Success && entered.Wait(TimeSpan.FromSeconds(3)));
                var close = runtime.TryCloseAsync(); release.Set(); Assert(Await(close).Success);
                Assert(memory.Code.WriteCount == 0 && !patchEngine.HasRecoveryDebt && runtime.Current.Connection == ConnectionState.Closed);
            }
            finally { release.Set(); Await(runtime.TryCloseAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Closed); }
        });
        test("runtime/patch: close finishes current feature but does not start next feature", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var preflightEntered = new ManualResetEventSlim(); using var preflightRelease = new ManualResetEventSlim();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256); int discoveries = 0, writes = 0, preflight = 0;
            patchEngine.SetRequested(FeatureId.Redraw, true);
            do { patchEngine.Maintain(); } while (patchEngine.IsLocating);
            patchEngine.SetRequested(FeatureId.Redraw, false);
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null, (_, _) => patchEngine);
            try
            {
                Await(runtime.StartAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Ready);
                memory.Code.ReadFault = (address, length) =>
                {
                    if (address == memory.Code.CodeBase + 0x3B085C && length == 6 && Interlocked.Exchange(ref preflight, 1) == 0)
                    { preflightEntered.Set(); if (!preflightRelease.Wait(TimeSpan.FromSeconds(5))) throw new IOException("request gate expired"); }
                };
                memory.Code.WriteFault = (address, _) =>
                {
                    if (address == memory.Code.CodeBase + 0x3B085E && Interlocked.Increment(ref writes) == 1)
                    { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("write gate expired"); }
                };
                var food = runtime.SetToggleAsync(FeatureId.Redraw, true);
                Assert(Await(food).Success && preflightEntered.Wait(TimeSpan.FromSeconds(3)));
                var auto = runtime.SetToggleAsync(FeatureId.Redistribute, true);
                preflightRelease.Set(); // Queued auto request makes the first preflight yield before writing.
                Assert(Await(auto).Success && entered.Wait(TimeSpan.FromSeconds(3)));
                var close = runtime.TryCloseAsync(); release.Set(); Assert(Await(close).Success);
                Assert(!patchEngine.HasRecoveryDebt && memory.Code.WriteCount == 4);
                Assert(memory.Code.Writes.All(w => w.Address != memory.Code.CodeBase + 0x3E4B3A));
                Assert(memory.Code.Hex(0x3B085C, 6) == "000080600000" && memory.Code.Hex(0x3B1318, 6) == "0000D0070000");
            }
            finally { preflightRelease.Set(); release.Set(); Await(runtime.TryCloseAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Closed); }
        });
        test("runtime/data: close during one transaction prevents later maintenance transactions", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int discoveries = 0;
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null);
            try
            {
                Await(runtime.StartAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Ready);
                memory.Data.Memory.BeforeWrite = (address, _) =>
                {
                    if (address == memory.Data.CommonStatus + 0x34)
                    { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("data gate expired"); }
                };
                Assert(Await(runtime.SetToggleAsync(FeatureId.Resources, true)).Success);
                Assert(Await(runtime.SetToggleAsync(FeatureId.War, true)).Success);
                Assert(Await(runtime.SetToggleAsync(FeatureId.Treasure, true)).Success);
                Assert(entered.Wait(TimeSpan.FromSeconds(3)));
                memory.Data.Memory.Put(memory.Data.Resources, 1);
                for (uint offset = 0xBC; offset <= 0xC8; offset += 4) memory.Data.Memory.Put(memory.Data.Campaign + offset, 0);
                int writes = memory.Data.Memory.Writes.Count;
                var close = runtime.TryCloseAsync(); release.Set(); Assert(Await(close).Success);
                Assert(memory.Data.Memory.Writes.Count == writes && memory.Data.Memory.Int(memory.Data.CommonStatus + 0x34) == 100);
                Assert(memory.Data.Memory.Int(memory.Data.Resources) == 1 && memory.Data.Memory.Int(memory.Data.Campaign + 0xBC) == 0);
            }
            finally { release.Set(); Await(runtime.TryCloseAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Closed); }
        });
        test("runtime/scan: close bypasses queued commands and cancels at chunk boundary", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256); int discoveries = 0, blocked = 0;
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null, (_, _) => patchEngine);
            try
            {
                Await(runtime.StartAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Ready);
                memory.Code.ReadFault = (_, length) =>
                {
                    if (length > 1024 && Interlocked.Exchange(ref blocked, 1) == 0)
                    { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test did not release chunk"); }
                };
                Assert(Await(runtime.SetToggleAsync(FeatureId.Redraw, true)).Success);
                Assert(entered.Wait(TimeSpan.FromSeconds(3)));
                int dataWrites = memory.Data.Memory.Writes.Count;
                var queued = Enumerable.Range(0, 40).Select(i => runtime.SetValueAsync(FeatureId.Turn, i)).ToArray();
                var close = runtime.TryCloseAsync(); release.Set();
                Assert(Await(close).Success);
                Assert(Await(Task.WhenAll(queued)).All(r => !r.Success));
                Assert(memory.Data.Memory.Writes.Count == dataWrites && memory.Code.WriteCount == 0 && memory.Code.ReadCount == 1);
                Assert(!patchEngine.IsLocating && !patchEngine.HasRecoveryDebt && runtime.Current.Connection == ConnectionState.Closed);
            }
            finally { release.Set(); Await(runtime.TryCloseAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Closed); }
        });
        test("runtime/scan: battle and resource maintenance continue while locating", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var schedule = new PatchRuntimeSchedule();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, 65536, scanClock: () => 0); int discoveries = 0;
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null,
                (_, _) => patchEngine, () => schedule.Now, schedule.Wait);
            try
            {
                Await(runtime.StartAsync()); var step = schedule.Next();
                schedule.Resume(step, 500); step = schedule.Next(); Assert(runtime.Current.Connection == ConnectionState.Ready);
                var redraw = runtime.SetToggleAsync(FeatureId.Redraw, true);
                schedule.Resume(step, 500); step = schedule.Next(); Assert(Await(redraw).Success);
                Assert(runtime.Current.Features[FeatureId.Redraw].Detail?.StartsWith("正在定位", StringComparison.Ordinal) == true);
                var hp = runtime.SetToggleAsync(FeatureId.Hp, true); var resources = runtime.SetToggleAsync(FeatureId.Resources, true);
                schedule.Resume(step, 1000); step = schedule.Next(); Assert(Await(hp).Success && Await(resources).Success);
                schedule.Resume(step, 1075); step = schedule.Next();
                Assert(runtime.Current.Features[FeatureId.Hp].Active && runtime.Current.Features[FeatureId.Resources].Active);
                Assert(patchEngine.IsLocating && !patchEngine.HasRecoveryDebt && memory.Code.WriteCount == 0);
                Assert(memory.Data.Memory.Int(memory.Data.Party + 0x10) == 1000 && memory.Data.Memory.Int(memory.Data.Resources) == 3);
                var cancel = runtime.SetToggleAsync(FeatureId.Redraw, false);
                schedule.Resume(step, 1075); step = schedule.Next(); Assert(Await(cancel).Success);
                Assert(!patchEngine.IsLocating && !runtime.Current.Features[FeatureId.Redraw].Requested);
                var close = runtime.TryCloseAsync(); schedule.Resume(step, 1075); Assert(Await(close).Success);
            }
            finally { var close = runtime.TryCloseAsync(); schedule.ReleaseAll(); Await(close); }
        });
        test("runtime/scan: independent scan, data, and publication deadlines", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var schedule = new PatchRuntimeSchedule();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, 65536, scanClock: () => 0);
            int discoveries = 0, snapshots = 0;
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null,
                (_, _) => patchEngine, () => schedule.Now, schedule.Wait);
            runtime.SnapshotChanged += _ => Interlocked.Increment(ref snapshots);
            try
            {
                Await(runtime.StartAsync()); var step = schedule.Next(); Assert(step.Delay == 75);
                schedule.Resume(step, 500); step = schedule.Next(); Assert(runtime.Current.Connection == ConnectionState.Ready);
                var hp = runtime.SetToggleAsync(FeatureId.Hp, true);
                var resources = runtime.SetToggleAsync(FeatureId.Resources, true);
                var redraw = runtime.SetToggleAsync(FeatureId.Redraw, true);
                schedule.Resume(step, 500); step = schedule.Next();
                Assert(Await(hp).Success && Await(resources).Success && Await(redraw).Success && step.Delay == 0);
                Assert(runtime.Current.Features[FeatureId.Redraw].Detail?.StartsWith("正在定位", StringComparison.Ordinal) == true);
                schedule.Resume(step, 575); step = schedule.Next();
                Assert(memory.Data.Memory.Int(memory.Data.Party + 0x10) == 1000);
                memory.Data.Memory.Put(memory.Data.Party + 0x10, 500); memory.Data.Memory.Put(memory.Data.Resources, 1);
                int published = snapshots, queries = memory.Code.QueryCount, dataWrites = memory.Data.Memory.Writes.Count;
                // The clock is frozen while real production scanner slices continue. No elapsed-time
                // assertion is needed: the worker requests cooperative yield without a timer.
                for (int i = 0; i < 40; i++)
                {
                    Assert(step.Delay == 0); schedule.Resume(step, 575); step = schedule.Next();
                }
                Assert(memory.Code.QueryCount >= queries + 40 && patchEngine.IsLocating);
                Assert(snapshots == published && memory.Data.Memory.Writes.Count == dataWrites);
                Assert(memory.Data.Memory.Int(memory.Data.Party + 0x10) == 500 && memory.Data.Memory.Int(memory.Data.Resources) == 1);
                schedule.Resume(step, 649); step = schedule.Next();
                Assert(snapshots == published && memory.Data.Memory.Int(memory.Data.Party + 0x10) == 500);
                schedule.Resume(step, 650); step = schedule.Next();
                Assert(snapshots == published + 1 && memory.Data.Memory.Int(memory.Data.Party + 0x10) == 1000);
                Assert(memory.Data.Memory.Int(memory.Data.Resources) == 1);
                schedule.Resume(step, 1000); step = schedule.Next();
                Assert(memory.Data.Memory.Int(memory.Data.Resources) == 3);
                published = snapshots;
                for (int i = 0; patchEngine.IsLocating && i < 1000; i++)
                { Assert(step.Delay == 0); schedule.Resume(step, 1000); step = schedule.Next(); }
                // Binding completion and application each publish immediately at the same clock
                // time; ordinary progress did not enqueue a snapshot for every scanner slice.
                Assert(!patchEngine.IsLocating && memory.Code.WriteCount == 0 && snapshots == published + 1);
                Assert(runtime.Current.Features[FeatureId.Redraw].Detail == null && !runtime.Current.Features[FeatureId.Redraw].Active);
                Assert(step.Delay == 0); schedule.Resume(step, 1000); step = schedule.Next();
                Assert(step.Delay == 75 && runtime.Current.Features[FeatureId.Redraw].Active && snapshots == published + 2);
                var close = runtime.TryCloseAsync(); schedule.Resume(step, 1000);
                Assert(Await(close).Success && runtime.Current.Connection == ConnectionState.Closed && snapshots == published + 3);
            }
            finally { var close = runtime.TryCloseAsync(); schedule.ReleaseAll(); Await(close); }
        });
        test("runtime/scan: slow and fast transaction time cannot create battle-write bursts", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var schedule = new PatchRuntimeSchedule();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, 65536, scanClock: () => 0);
            int discoveries = 0, slowWrites = 0, fastWrites = 0;
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null,
                (_, _) => patchEngine, () => schedule.Now, schedule.Wait);
            try
            {
                Await(runtime.StartAsync()); var step = schedule.Next();
                schedule.Resume(step, 500); step = schedule.Next();
                var hp = runtime.SetToggleAsync(FeatureId.Hp, true);
                var resources = runtime.SetToggleAsync(FeatureId.Resources, true);
                var redraw = runtime.SetToggleAsync(FeatureId.Redraw, true);
                memory.Data.Memory.BeforeWrite = (address, _) =>
                {
                    if (address == memory.Data.Resources && ++slowWrites == 1) schedule.SetTime(1100);
                    if (address == memory.Data.Party + 0x10 && ++fastWrites == 1) schedule.SetTime(1200);
                };
                schedule.Resume(step, 1000); step = schedule.Next();
                Assert(Await(hp).Success && Await(resources).Success && Await(redraw).Success);
                Assert(schedule.Now == 1200 && step.Delay == 0 && memory.Data.Memory.Int(memory.Data.Party + 0x10) == 1000);
                memory.Data.Memory.Put(memory.Data.Party + 0x10, 500);
                int queries = memory.Code.QueryCount;
                schedule.Resume(step, 1200); step = schedule.Next();
                Assert(memory.Code.QueryCount > queries && memory.Data.Memory.Int(memory.Data.Party + 0x10) == 500);
                schedule.Resume(step, 1274); step = schedule.Next();
                Assert(memory.Data.Memory.Int(memory.Data.Party + 0x10) == 500 && fastWrites == 1);
                schedule.Resume(step, 1275); step = schedule.Next();
                Assert(memory.Data.Memory.Int(memory.Data.Party + 0x10) == 1000 && fastWrites == 2);
                var close = runtime.TryCloseAsync(); schedule.Resume(step, 1275); Assert(Await(close).Success);
            }
            finally { var close = runtime.TryCloseAsync(); schedule.ReleaseAll(); Await(close); }
        });
        test("runtime/scan: errors and command results bypass publication throttle", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var schedule = new PatchRuntimeSchedule();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, 65536, scanClock: () => 0);
            int discoveries = 0, snapshots = 0;
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null,
                (_, _) => patchEngine, () => schedule.Now, schedule.Wait);
            runtime.SnapshotChanged += _ => Interlocked.Increment(ref snapshots);
            try
            {
                Await(runtime.StartAsync()); var step = schedule.Next();
                schedule.Resume(step, 500); step = schedule.Next();
                var enable = runtime.SetToggleAsync(FeatureId.Redraw, true);
                schedule.Resume(step, 500); step = schedule.Next(); Assert(Await(enable).Success && patchEngine.IsLocating);
                int published = snapshots;
                memory.Code.ReadFault = (_, length) => { if (length > 1024) throw new IOException("script read failed"); };
                schedule.Resume(step, 500); step = schedule.Next();
                Assert(snapshots == published + 1 && runtime.Current.Features[FeatureId.Redraw].Error?.Contains("script read failed") == true);
                Assert(!patchEngine.IsLocating && memory.Code.WriteCount == 0);
                var disable = runtime.SetToggleAsync(FeatureId.Redraw, false);
                schedule.Resume(step, 500); step = schedule.Next(); Assert(Await(disable).Success);
                Assert(!runtime.Current.Features[FeatureId.Redraw].Requested && runtime.Current.Features[FeatureId.Redraw].Error == null);
                Assert(snapshots > published + 1);
                var close = runtime.TryCloseAsync(); schedule.Resume(step, 500); Assert(Await(close).Success);
            }
            finally { var close = runtime.TryCloseAsync(); schedule.ReleaseAll(); Await(close); }
        });
        test("runtime/scan: close cancels locating even when liveness query is unavailable", () =>
        {
            using var memory = new PatchRuntimeMemory(); using var schedule = new PatchRuntimeSchedule();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256, 65536, scanClock: () => 0); int discoveries = 0;
            var runtime = new TrainerRuntime(() => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "fixture.ain") : null,
                (_, _) => patchEngine, () => schedule.Now, schedule.Wait);
            try
            {
                Await(runtime.StartAsync()); var step = schedule.Next();
                schedule.Resume(step, 500); step = schedule.Next(); Assert(runtime.Current.Connection == ConnectionState.Ready);
                var redraw = runtime.SetToggleAsync(FeatureId.Redraw, true);
                schedule.Resume(step, 500); step = schedule.Next(); Assert(Await(redraw).Success);
                Assert(runtime.Current.Features[FeatureId.Redraw].Detail?.StartsWith("正在定位", StringComparison.Ordinal) == true);
                memory.AliveFault = () => throw new IOException("process query unavailable");
                var close = runtime.TryCloseAsync(); schedule.Resume(step, 500); step = schedule.Next(); Assert(!Await(close).Success);
                Assert(!patchEngine.IsLocating && !patchEngine.Observations[FeatureId.Redraw].Requested && !patchEngine.HasRecoveryDebt);
                Assert(memory.DisposeCount == 0 && runtime.Current.Connection == ConnectionState.RestorePending);
                memory.AliveFault = null; close = runtime.TryCloseAsync(); schedule.Resume(step, 500); Assert(Await(close).Success);
                Assert(runtime.Current.Connection == ConnectionState.Closed);
                Assert(memory.Code.WriteCount == 0 && memory.DisposeCount == 1);
            }
            finally { memory.AliveFault = null; var close = runtime.TryCloseAsync(); schedule.ReleaseAll(); Await(close); }
        });
        test("runtime/patch: failed close keeps owner, stops data, and retries restoration", () =>
        {
            using var memory = new PatchRuntimeMemory();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256);
            int discoveries = 0, blockRestore = 0;
            var runtime = new TrainerRuntime(
                () => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "test-only.ain") : null,
                (_, _) => patchEngine);
            try
            {
                Await(runtime.StartAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Ready);
                Assert(Await(runtime.SetToggleAsync(FeatureId.Resources, true)).Success);
                Program.WaitUntil(() => runtime.Current.Features[FeatureId.Resources].Active);
                Assert(Await(runtime.SetToggleAsync(FeatureId.Redraw, true)).Success);
                Program.WaitUntil(() => runtime.Current.Features[FeatureId.Redraw].Active);
                Assert(patchEngine.HasRecoveryDebt);
                memory.Code.WriteFault = (_, _) => { if (Volatile.Read(ref blockRestore) != 0) throw new IOException("restoration temporarily unavailable"); };
                Volatile.Write(ref blockRestore, 1);
                Assert(!Await(runtime.TryCloseAsync()).Success);
                var failedClose = runtime.Current;
                Assert(failedClose.Connection == ConnectionState.RestorePending && failedClose.ProcessId == memory.ProcessId);
                Assert(memory.DisposeCount == 0 && patchEngine.HasRecoveryDebt && Volatile.Read(ref discoveries) == 1);
                Assert(failedClose.Features.Values.All(f => !f.Requested && !f.Active && !f.LockRequested && !f.LockActive));
                int dataWrites = memory.Data.Memory.Writes.Count;
                memory.Data.Memory.Put(memory.Data.Resources, 1);
                Assert(!Await(runtime.SetValueAsync(FeatureId.Food, 2)).Success);
                Assert(!Await(runtime.SetToggleAsync(FeatureId.Resources, true)).Success);
                Assert(!Await(runtime.SetToggleAsync(FeatureId.Redraw, true)).Success);
                Assert(!Await(runtime.SetLockAsync(FeatureId.Turn, true)).Success);
                Assert(memory.Data.Memory.Int(memory.Data.Resources) == 1 && memory.Data.Memory.Writes.Count == dataWrites);
                Assert(patchEngine.HasRecoveryDebt && memory.DisposeCount == 0 && Volatile.Read(ref discoveries) == 1);
                Volatile.Write(ref blockRestore, 0);
                Await(runtime.TryCloseAsync()); // The worker may also complete its automatic retry first.
                Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Closed);
                Assert(Await(runtime.TryCloseAsync()).Success && memory.DisposeCount == 1 && !patchEngine.HasRecoveryDebt);
                Assert(runtime.Current.ProcessId == null && memory.Code.Hex(0x3B085C, 6) == "000080600000" && memory.Code.Hex(0x3B1318, 6) == "0000D0070000");
                Assert(memory.Data.Memory.Writes.Count == dataWrites && memory.Data.Memory.Int(memory.Data.Resources) == 1);
            }
            finally
            {
                Volatile.Write(ref blockRestore, 0);
                Await(runtime.TryCloseAsync());
                Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Closed);
            }
        });
        test("runtime/patch: confirmed process exit releases debt without stale writes", () =>
        {
            using var memory = new PatchRuntimeMemory();
            var patchEngine = new R10PatchEngine(memory, () => BugFixProfile.Sha256);
            int discoveries = 0;
            var runtime = new TrainerRuntime(
                () => Interlocked.Increment(ref discoveries) == 1 ? new GameConnection(memory, "test-only.ain") : null,
                (_, _) => patchEngine);
            try
            {
                Await(runtime.StartAsync()); Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Ready);
                Assert(Await(runtime.SetToggleAsync(FeatureId.Redraw, true)).Success);
                Program.WaitUntil(() => runtime.Current.Features[FeatureId.Redraw].Active);
                Assert(patchEngine.HasRecoveryDebt);
                memory.Code.WriteFault = (_, _) => throw new IOException("restore refused");
                Assert(!Await(runtime.TryCloseAsync()).Success);
                Assert(runtime.Current.ProcessId == memory.ProcessId && memory.DisposeCount == 0);
                memory.IsAlive = false;
                int writesAfterExit = memory.Code.WriteCount;
                Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Closed);
                Assert(memory.Code.WriteCount == writesAfterExit && memory.DisposeCount == 1);
                Assert(runtime.Current.ProcessId == null && runtime.Current.Features.Values.All(f => !f.Requested && !f.Active));
                Assert(Volatile.Read(ref discoveries) == 1 && Await(runtime.TryCloseAsync()).Success);
            }
            finally
            {
                memory.IsAlive = false; Await(runtime.TryCloseAsync());
                Program.WaitUntil(() => runtime.Current.Connection == ConnectionState.Closed);
            }
        });
    }

    private static void Drive(R10PatchEngine engine)
    {
        for (int i = 0; i < 20000; i++)
        {
            engine.Maintain();
            if (engine.IsLocating) continue;
            if (engine.LastScanReadBytes != 0) engine.Maintain();
            return;
        }
        throw new TimeoutException("Locator did not finish within bounded test slices.");
    }
    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static T Await<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    private static R10PatchEngine Engine(PatchFakeMemory memory) => new(memory, () => BugFixProfile.Sha256);
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Patch assertion failed."); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or AggregateException) { return; }
        throw new InvalidOperationException("Expected patch operation to fail.");
    }
}

// Real worker iterations, controlled monotonic time, and explicit wait handshakes keep scheduler
// tests deterministic without requiring the host's native timer resolution or CPU speed.
internal sealed class PatchRuntimeSchedule : IDisposable
{
    internal sealed record Step(int Delay, TaskCompletionSource Completion);
    private readonly object _gate = new();
    private readonly BlockingCollection<Step> _steps = [];
    private long _now;
    private bool _released;
    private Step? _pending;
    internal long Now => Interlocked.Read(ref _now);
    internal Task Wait(int milliseconds)
    {
        lock (_gate)
        {
            if (_released) return Task.CompletedTask;
            var step = new Step(milliseconds, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending = step; _steps.Add(step); return step.Completion.Task;
        }
    }
    internal Step Next() => _steps.TryTake(out var step, TimeSpan.FromSeconds(5)) ? step : throw new TimeoutException("worker did not reach its scheduling wait");
    internal void SetTime(long now) => Interlocked.Exchange(ref _now, now);
    internal void Resume(Step step, long now) { SetTime(now); step.Completion.TrySetResult(); }
    internal void ReleaseAll()
    {
        lock (_gate)
        {
            _released = true; _pending?.Completion.TrySetResult();
            while (_steps.TryTake(out var step)) step.Completion.TrySetResult();
        }
    }
    public void Dispose() { ReleaseAll(); _steps.Dispose(); }
}

// A complete readable game-data fixture plus an independently mapped CODE image. The runtime's
// single worker executes real production engines; only the OS memory and file-fingerprint boundaries
// are substituted. The lock makes the explicit process-exit transition deterministic in these tests.
internal sealed class PatchRuntimeMemory : IProcessMemory
{
    private readonly object _gate = new();
    private bool _alive = true;
    private int _disposeCount;
    internal DataFixture Data { get; } = new();
    internal PatchFakeMemory Code { get; } = new(0x50000000);
    public int ProcessId => Data.Memory.ProcessId;
    public uint ModuleBase => Data.Memory.ModuleBase;
    internal Action? AliveFault { get; set; }
    public bool IsAlive { get { lock (_gate) { AliveFault?.Invoke(); return _alive; } } set { lock (_gate) _alive = value; } }
    internal int DisposeCount => Volatile.Read(ref _disposeCount);
    public byte[] Read(uint address, int length)
    {
        lock (_gate)
        {
            EnsureAvailable();
            return address >= Code.CodeBase && (ulong)address + (uint)length <= (ulong)Code.CodeBase + BugFixProfile.CodeLength
                ? Code.Read(address, length) : Data.Memory.Read(address, length);
        }
    }
    public void Write(uint address, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            EnsureAvailable();
            if (address >= Code.CodeBase && (ulong)address + (uint)bytes.Length <= (ulong)Code.CodeBase + BugFixProfile.CodeLength)
                Code.Write(address, bytes);
            else Data.Memory.Write(address, bytes);
        }
    }
    public IReadOnlyList<MemoryRegion> QueryRegions() { lock (_gate) { EnsureAvailable(); return Code.QueryRegions(); } }
    public void Dispose() { lock (_gate) { if (_disposeCount == 0) _disposeCount = 1; } }
    private void EnsureAvailable() { if (!_alive || _disposeCount != 0) throw new IOException("test process exited or handle released"); }
}

internal sealed class PatchFakeMemory : IProcessMemory
{
    private readonly byte[] _image = new byte[BugFixProfile.CodeLength];
    internal PatchFakeMemory(uint codeBase = 0x10000000)
    {
        CodeBase = codeBase;
        Regions = [new(codeBase, BugFixProfile.CodeLength, 0x1000, 4, 0x20000)];
        foreach (var anchor in BugFixProfile.Anchors) Poke(anchor.Pc, anchor.Bytes);
        foreach (var definition in BugFixProfile.Definitions) Poke(definition.WindowPc, definition.OriginalWindow);
    }
    public int ProcessId { get; set; } = 42;
    public uint ModuleBase => 0x400000;
    public bool IsAlive { get; set; } = true;
    internal uint CodeBase { get; }
    internal List<MemoryRegion> Regions { get; set; }
    internal int ReadCount { get; private set; }
    internal long ReadBytes { get; private set; }
    internal ConcurrentQueue<(uint Address, int Length)> Reads { get; } = [];
    internal int WriteCount { get; private set; }
    internal int QueryCount { get; private set; }
    internal List<(uint Address, byte[] Bytes)> Writes { get; } = [];
    internal Action<uint, byte[]>? WriteFault { get; set; }
    internal Action<uint, int>? ReadFault { get; set; }
    internal Action? QueryFault { get; set; }
    internal void Poke(uint pc, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_image.AsSpan(checked((int)pc)));
    internal string Hex(uint pc, int count) => Convert.ToHexString(_image.AsSpan(checked((int)pc), count));
    public byte[] Read(uint address, int length)
    {
        ReadCount++;
        ReadBytes += length; Reads.Enqueue((address, length));
        ReadFault?.Invoke(address, length);
        if (!Regions.Any(r => r.IsReadable && address >= r.BaseAddress && (ulong)address + (uint)length <= (ulong)r.BaseAddress + r.Size))
            throw new IOException("unmapped read");
        return _image.AsSpan(checked((int)(address - CodeBase)), length).ToArray();
    }
    public void Write(uint address, ReadOnlySpan<byte> bytes)
    {
        WriteCount++; var copy = bytes.ToArray(); Writes.Add((address, copy));
        WriteFault?.Invoke(address, copy);
        WriteDirect(address, copy);
    }
    internal void WriteDirect(uint address, ReadOnlySpan<byte> bytes) => Poke(checked(address - CodeBase), bytes);
    public IReadOnlyList<MemoryRegion> QueryRegions() { QueryCount++; QueryFault?.Invoke(); return Regions.ToArray(); }
    public void Dispose() { }
}
