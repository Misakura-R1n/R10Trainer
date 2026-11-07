using R10.Core;
using R10.Core.Memory;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace R10.Tests;

internal static class Program
{
    private static int _passed, _failed;
    public static int Main(string[] args)
    {
        if (args.Contains("--memory-helper")) return MemoryHelper();
        int bridgeAin = Array.IndexOf(args, "--bridge-ain");
        if (bridgeAin >= 0)
        {
            if (bridgeAin + 1 >= args.Length) { Console.Error.WriteLine("--bridge-ain requires an AIN path."); return 2; }
            BridgeAinTests.Run(Test, args[bridgeAin + 1]);
            Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed; bridge-AIN fixtures only; no game accessed");
            return _failed == 0 ? 0 : 1;
        }
        DataTests.Run(Test);
        PatchTests.Run(Test);
        Test("Catalog: 18 features, 3 locks, safe numeric boundaries", () =>
        {
            Require(FeatureCatalog.All.Count == 18 && FeatureCatalog.All.Count(f => f.HasLock) == 3);
            Require(FeatureCatalog.ValidValue(FeatureId.DeathRate, 12.5m));
            Require(!FeatureCatalog.ValidValue(FeatureId.DeathRate, 12.55m));
            Require(!FeatureCatalog.ValidValue(FeatureId.Star, 79));
            Require(!FeatureCatalog.ValidValue(FeatureId.Food, 4));
        });
        Test("Demo: explicit simulation, enemy exclusivity, resource lock", () =>
        {
            var demo = new SimulationRuntime();
            Require(demo.Current.IsSimulation && demo.Current.ProcessId == null);
            demo.SetToggleAsync(FeatureId.EnemyKill, true).GetAwaiter().GetResult();
            demo.SetToggleAsync(FeatureId.EnemyNear, true).GetAwaiter().GetResult();
            Require(!demo.Current.Features[FeatureId.EnemyKill].Requested && demo.Current.Features[FeatureId.EnemyNear].Requested);
            demo.SetToggleAsync(FeatureId.Resources, true).GetAwaiter().GetResult();
            Require(demo.Current.Features[FeatureId.Food].Value == 3);
            Require(!demo.SetValueAsync(FeatureId.Food, 1).GetAwaiter().GetResult().Success);
            Require(demo.SetLockAsync(FeatureId.DeathRate, true).GetAwaiter().GetResult().Success);
            Require(demo.Current.Features[FeatureId.DeathRate].LockActive);
            Require(demo.TryCloseAsync().GetAwaiter().GetResult().Success);
        });
        Test("Runtime: disconnected commands fail, orderly close", () =>
        {
            var runtime = new TrainerRuntime(() => null);
            runtime.StartAsync().GetAwaiter().GetResult();
            Require(!runtime.SetValueAsync(FeatureId.Turn, 9).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().Success);
            Require(!runtime.SetToggleAsync(FeatureId.Hp, true).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().Success);
            Require(runtime.TryCloseAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().Success);
        });
        Test("Runtime: unknown version cannot enable writes", () =>
        {
            var runtime = new TrainerRuntime(() => throw new NotSupportedException("Unknown fixture"));
            runtime.StartAsync().GetAwaiter().GetResult();
            WaitUntil(() => runtime.Current.Connection == ConnectionState.Unsupported);
            Require(runtime.Current.Features.Values.All(f => !f.CanEdit && !f.Active));
            Require(runtime.TryCloseAsync().GetAwaiter().GetResult().Success);
        });
        if (args.Contains("--native")) Test("Win32: independent x86 helper read/write, regions, exit", NativeHelperTest);
        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed; native={args.Contains("--native")}");
        return _failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    internal static void Require(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    internal static void WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(); Thread.Sleep(20); }
    }
    private static int MemoryHelper()
    {
        nint address = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(address, 1234567);
            Console.WriteLine($"{Environment.ProcessId} {unchecked((uint)address.ToInt64())}"); Console.Out.Flush();
            while (Console.ReadLine() is { } command)
            {
                if (command == "EXIT") break;
                if (command == "READ") { Console.WriteLine(Marshal.ReadInt32(address)); Console.Out.Flush(); }
            }
        }
        finally { Marshal.FreeHGlobal(address); }
        return 0;
    }
    private static void NativeHelperTest()
    {
        Require(IntPtr.Size == 4, "Publish tests as self-contained win-x86 before --native.");
        using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--memory-helper")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            string first = helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()!;
            var parts = first.Split(' '); int pid = int.Parse(parts[0]); uint address = uint.Parse(parts[1]);
            using var memory = new NativeProcessMemory(pid);
            Require(memory.IsAlive && memory.ReadInt32(address) == 1234567);
            memory.WriteInt32(address, -7654321);
            helper.StandardInput.WriteLine("READ"); helper.StandardInput.Flush();
            string observed = helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult()!;
            Require(observed == "-7654321", "Helper independently observed the wrong value.");
            Require(memory.QueryRegions().Any(r => r.IsReadable && r.BaseAddress <= address && (ulong)r.BaseAddress + r.Size > address));
            bool failed = false; try { memory.Read(1, 4); } catch { failed = true; } Require(failed, "Invalid read must fail.");
            helper.StandardInput.WriteLine("EXIT"); helper.StandardInput.Flush();
            Require(helper.WaitForExit(5000)); Require(!memory.IsAlive);
        }
        finally { if (!helper.HasExited) helper.Kill(); }
    }
}
