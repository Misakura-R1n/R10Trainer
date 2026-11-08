using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Call(int a, int b, int c);
    private static Call tick = null!, filter = null!, control = null!;
    private static int checks, failed;
    private static int Main(string[] args)
    {
        if (IntPtr.Size != 4 || args.Length != 1) throw new InvalidOperationException("Use the x86 test executable with a DLL path.");
        var dll = NativeLibrary.Load(Path.GetFullPath(args[0]));
        tick = Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(dll, "Tick"));
        filter = Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(dll, "FilterHp"));
        control = Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(dll, "Control"));
        tick(0, 1, 15); tick(1, 16, 16);
        var initial = Request("{\"op\":\"get_state\"}");
        Check(initial.GetProperty("pid").GetInt32() == Environment.ProcessId, "native pipe belongs to helper process");
        Check(initial.GetProperty("phase").GetInt32() == 1, "game thread pump becomes ready");
        Check(filter(0, 100, 25) == 25, "guard disabled leaves HP unchanged");
        var queued = Request("{\"op\":\"set_feature\",\"feature\":\"hp_guard\",\"enabled\":true,\"generation\":0}");
        int id = queued.GetProperty("id").GetInt32();
        Check(queued.GetProperty("state").GetString() == "queued" && filter(0,100,25)==25, "queued request cannot mutate game state");
        tick(1,16,16);
        Check(Request($"{{\"op\":\"get_command_result\",\"id\":{id}}}").GetProperty("state").GetString() == "applied", "pump acknowledges command");
        Check(filter(0,100,25)==100 && filter(0,100,0)==100, "guard rejects damage and fatal HP in helper model");
        Check(filter(1,100,25)==25 && filter(0,100,150)==150 && filter(0,0,-1)==-1, "enemy, healing and already-zero HP pass through");
        tick(10,100,200); tick(11,50,500);
        var snapshot=Request("{\"op\":\"get_state\"}");
        Check(snapshot.GetProperty("playerHp").GetInt32()==100 && snapshot.GetProperty("enemyHp").GetInt32()==50, "typed snapshot fields");
        var pending=Request("{\"op\":\"set_feature\",\"feature\":\"hp_guard\",\"enabled\":false,\"generation\":0}").GetProperty("id").GetInt32();
        tick(2,1,0);
        Check(filter(0,100,25)==25, "loading suspends guard");
        tick(1,16,16);
        Check(Request($"{{\"op\":\"get_command_result\",\"id\":{pending}}}").GetProperty("state").GetString()=="rejected", "load invalidates queued generation");
        Check(Request("{\"op\":\"set_feature\",\"feature\":\"hp_guard\",\"enabled\":true,\"generation\":0}").GetProperty("error").GetString()=="generation_mismatch", "stale writes rejected");
        tick(3,1,0);
        Check(Request("{\"op\":\"get_state\"}").GetProperty("loadGeneration").GetInt32()==1, "generation survives ready callback");
        var revoked=Request("{\"op\":\"set_feature\",\"feature\":\"hp_guard\",\"enabled\":false,\"generation\":1}").GetProperty("id").GetInt32();
        tick(0,1,1); tick(1,16,16);
        Check(Request($"{{\"op\":\"get_command_result\",\"id\":{revoked}}}").GetProperty("state").GetString()=="rejected", "capability removal rejects an already queued command");
        tick(0,1,15);
        tick(2,2,-1); tick(4,2,0); tick(1,16,16);
        var failedLoad=Request("{\"op\":\"get_state\"}");
        Check(failedLoad.GetProperty("phase").GetInt32()==1 && failedLoad.GetProperty("lastLoadOutcome").GetInt32()==2 &&
            failedLoad.GetProperty("loadSlot").GetInt32()==-1 && failedLoad.GetProperty("lastCompletedGeneration").GetInt32()==1,
            "failed load can return to ready while retaining failed outcome and previous completed generation");
        Check(SlowPartialRequestIsRejectedAsync().GetAwaiter().GetResult(), "partial request cannot extend the whole-line timeout");
        Check(Request("{\"op\":\"ping\"}").GetProperty("ok").GetBoolean(), "pipe recovers after an incomplete request timeout");
        foreach (var malformed in new[] {"{}", "{\"op\":\"ping\",\"op\":\"ping\"}", "{\"op\":\"set_feature\",\"enabled\":f}", "{\"op\":\"get_command_result\",\"id\":2147483648}", "{\"op\":\"ping\"} junk", "{\"op\":\"ping\"}\0junk"})
            Check(!Request(malformed).GetProperty("ok").GetBoolean(), "malformed protocol rejected");
        tick(1,16,16);
        string Action(int action,int arg=0,int arg2=0,int gen=2) => $"{{\"op\":\"action\",\"action\":{action},\"argument\":{arg},\"argument2\":{arg2},\"generation\":{gen}}}";
        Check(Request(Action(2,5005)).GetProperty("error").GetString()=="script_hook_unavailable", "old script rejects semantic commands");
        tick(0,1,31); tick(1,16,16);
        Check(Request(Action(3)).GetProperty("error").GetString()=="battle_controls_unavailable", "no battle cannot end a turn");
        tick(20,123,1); control(5,1,0);
        Check(control(4,123,0)==1 && control(4,124,0)==0, "selector identity guards control availability");
        var skill=Request(Action(4,3,1)).GetProperty("id").GetInt32();
        tick(1,16,16);
        Check(control(0,0,0)==4 && control(1,0,0)==3 && control(2,0,0)==1, "pump leaves semantic command for script dispatcher");
        Check(Task.Run(()=>control(3,1,0)).GetAwaiter().GetResult()==0 && control(0,0,0)==4, "background thread cannot consume action");
        Check(Request(Action(2,5005)).GetProperty("error").GetString()=="command_pending", "one pending action prevents overlapping submission");
        control(3,0,0);
        Check(Request($"{{\"op\":\"get_command_result\",\"id\":{skill}}}").GetProperty("state").GetString()=="rejected", "unusable skill can be rejected by script");
        var load=Request(Action(2,5005)).GetProperty("id").GetInt32(); control(3,1,0); tick(2,1,5005);
        Check(Request($"{{\"op\":\"get_command_result\",\"id\":{load}}}").GetProperty("state").GetString()=="applied", "load acknowledgement survives generation change");
        tick(3,1,0); tick(1,16,16);
        Check(Request(Action(2,5005)).GetProperty("error").GetString()=="generation_mismatch", "semantic stale generation rejected");
        Check(!Request(Action(4,7,0,3)).GetProperty("ok").GetBoolean() && !Request(Action(2,9999,0,3)).GetProperty("ok").GetBoolean(), "semantic argument bounds enforced natively");
        var expiry=Request(Action(2,5005,0,3)).GetProperty("id").GetInt32(); Thread.Sleep(3010); tick(1,16,16);
        Check(Request($"{{\"op\":\"get_command_result\",\"id\":{expiry}}}").GetProperty("state").GetString()=="rejected", "unconsumed action expires without later execution");
        var cards=Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(dll,"Cards"));
        string CardsRequest(bool enable,int gen=3) => JsonSerializer.Serialize(new { op="set_feature",feature="no_duplicate_cards",enabled=enable,generation=gen });
        Check(Request(CardsRequest(true)).GetProperty("error").GetString()=="script_hook_unavailable","old script rejects card feature");
        tick(0,1,63); tick(1,16,16);
        Request(CardsRequest(true));
        cards(1,0,0); Check(cards(0,0,0)==0,"queued feature does not change a batch"); cards(2,0,0);
        tick(1,16,16); cards(1,0,0);
        Check(cards(0,0,0)==1 && cards(3,0,0)==0,"heap index zero is a valid borrowed batch");
        Check(Task.Run(()=>cards(2,0,0)).GetAwaiter().GetResult()==0 && cards(0,0,0)==1,"background thread cannot clear borrowed batch");
        Request(CardsRequest(false)); tick(1,16,16);
        Check(cards(0,0,0)==1,"toggle changes apply only to next batch");
        cards(2,0,0); cards(1,123,0); Check(cards(0,0,0)==0,"next batch observes disabled state");
        Request(CardsRequest(true)); tick(1,16,16); cards(1,123,0); cards(4,0,0); cards(5,0,0);
        var cardState=Request("{\"op\":\"get_state\"}");
        Check(cardState.GetProperty("noDuplicateCards").GetBoolean() && cardState.GetProperty("filteredCards").GetInt32()==1 && cardState.GetProperty("fallbackCards").GetInt32()==1,"card telemetry is separate from HP");
        tick(2,1,5005); Check(cards(0,0,0)==0 && cards(3,0,0)==-1,"load invalidates borrowed array handle");
        tick(3,1,0); tick(1,16,16);
        var feature=Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(dll,"Feature"));
        string SetFeature(string name,bool enabled,int gen=4) => JsonSerializer.Serialize(new {op="set_feature",feature=name,enabled,generation=gen});
        Check(Request(SetFeature("food_redraw",true)).GetProperty("error").GetString()=="script_hook_unavailable","old script rejects new branch switches");
        tick(0,1,255); tick(1,16,16);
        Request(SetFeature("food_redraw",true)); Check(feature(6,0,0)==0,"queued redraw does not alter script branch");
        tick(1,16,16); Check(feature(6,0,0)==1 && feature(7,0,0)==0,"redraw enabled independently");
        Check(Task.Run(()=>feature(6,0,0)).GetAwaiter().GetResult()==0,"background thread cannot query script feature");
        Request(SetFeature("auto_redistribute",true)); tick(1,16,16);
        Request(SetFeature("food_redraw",false)); tick(1,16,16);
        Check(feature(6,0,0)==0 && feature(7,0,0)==1,"disabling redraw preserves redistribution");
        tick(2,1,5005); Check(feature(7,0,0)==0,"loading suspends branch switches");
        Check(Request(SetFeature("auto_redistribute",false)).GetProperty("error").GetString()=="generation_mismatch","old generation cannot change branch switches");
        tick(3,1,0); tick(0,1,63); Check(feature(7,0,0)==0,"removed capability cannot retain active script effect");
        Console.WriteLine($"{checks-failed} passed, {failed} failed native helper checks; simulation=true; this is not a game compatibility test.");
        return failed==0 ? 0 : 1;
    }
    private static void Check(bool condition,string message)
    { checks++; if (!condition) failed++; Console.WriteLine((condition ? "PASS " : "FAIL ")+message); }
    private static async Task<bool> SlowPartialRequestIsRejectedAsync()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var pipe=new NamedPipeClientStream(".","R10Bridge."+Environment.ProcessId,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        try
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"op\":\"ping\"}"),deadline.Token);
            await Task.Delay(700,deadline.Token);
            await pipe.WriteAsync(new byte[]{(byte)' '},deadline.Token);
            await Task.Delay(700,deadline.Token);
            await pipe.WriteAsync(new byte[]{(byte)'\n'},deadline.Token);
            using var reader=new StreamReader(pipe,Encoding.UTF8,leaveOpen:true);
            var response=await reader.ReadLineAsync(deadline.Token);
            if(response is null) return true;
            using var document=JsonDocument.Parse(response);
            return !document.RootElement.GetProperty("ok").GetBoolean();
        }
        catch(IOException) { return true; }
    }
    private static JsonElement Request(string line) => RequestAsync(line).GetAwaiter().GetResult();
    private static async Task<JsonElement> RequestAsync(string line)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe=new NamedPipeClientStream(".","R10Bridge."+Environment.ProcessId,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        var bytes=Encoding.UTF8.GetBytes(line+"\n");
        await pipe.WriteAsync(bytes,deadline.Token); await pipe.FlushAsync(deadline.Token);
        using var reader=new StreamReader(pipe,Encoding.UTF8,leaveOpen:true);
        string response=await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("No response");
        using var document=JsonDocument.Parse(response); return document.RootElement.Clone();
    }
}
