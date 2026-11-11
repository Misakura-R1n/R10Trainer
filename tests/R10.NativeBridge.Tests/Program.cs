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
        var expiry=Request(Action(2,5005,0,3)).GetProperty("id").GetInt32(); Thread.Sleep(3050); tick(1,16,16);
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
        var data=Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(dll,"Data"));
        string SetValue(string name,int value,int generation=5) => JsonSerializer.Serialize(new {op="set_value",feature=name,value,generation});
        Check(Request(SetValue("food",3)).GetProperty("error").GetString()=="script_hook_unavailable","old script rejects typed values");
        tick(0,1,511); tick(1,16,16);
        Check(Request(SetValue("food",3)).GetProperty("error").GetString()=="data_not_ready","title data is unavailable");
        data(6,1,0);
        foreach(var invalid in new[] {SetValue("food",4),SetValue("unit",1000),SetValue("star",99),SetValue("unknown",1)})
            Check(!Request(invalid).GetProperty("ok").GetBoolean(),"typed bounds enforced in native parser");
        int typed=Request(SetValue("food",3)).GetProperty("id").GetInt32(); tick(1,16,16);
        Check(data(0,0,0)==1 && data(1,0,0)==3,"typed value waits for AIN game-thread setter");
        Task.Run(()=>data(2,1,0)).GetAwaiter().GetResult();
        Check(data(0,0,0)==1,"background thread cannot acknowledge value");
        data(2,1,0);
        Check(Request(JsonSerializer.Serialize(new {op="get_command_result",id=typed})).GetProperty("state").GetString()=="applied","AIN readback acknowledges typed value");
        Request(SetFeature("resources_full",true,5)); tick(1,16,16);
        Check(data(0,0,0)==6 && data(7,0,0)==0,"full enable waits for valid game context");
        data(2,1,0); Check(data(7,0,0)==1,"full enable applied on game thread");
        Check(Request(SetValue("food",0)).GetProperty("error").GetString()=="resources_full_enabled","manual resources blocked while full enabled");
        Request(SetFeature("resources_full",false,5)); tick(1,16,16); Check(data(7,0,0)==0,"full disable independent of object readiness");
        foreach (var (name,value,kind) in new[] { ("food",3,1), ("unit",80,4), ("star",200,5) })
        {
            tick(1,16,16);
            int claimed=Request(SetValue(name,value)).GetProperty("id").GetInt32();
            Check(Task.Run(()=>data(0,0,0)).GetAwaiter().GetResult()==0,"background thread cannot claim data command");
            Check(data(0,0,0)==kind,"game thread claims typed command");
            Thread.Sleep(3050); // Suspend execution across the original queue deadline.
            Check(data(1,0,0)==value,"claimed arguments survive deadline before setter");
            tick(1,16,16);
            Check(data(0,0,0)==kind && data(1,0,0)==value,"pump cannot expire executing command or change readback argument");
            Check(Request(SetValue("food",1)).GetProperty("error").GetString()=="command_pending","executing slot cannot be replaced");
            data(2,1,0);
            Check(Request(JsonSerializer.Serialize(new {op="get_command_result",id=claimed})).GetProperty("state").GetString()=="applied","executing command acknowledges original ID after deadline");
        }
        tick(1,16,16);
        int expiredData=Request(SetValue("star",40)).GetProperty("id").GetInt32();
        Thread.Sleep(3050);
        Check(data(0,0,0)==0 && Request(JsonSerializer.Serialize(new {op="get_command_result",id=expiredData})).GetProperty("state").GetString()=="rejected","unclaimed data command still expires before any setter");
        tick(1,16,16);
        int stale=Request(SetValue("star",200)).GetProperty("id").GetInt32(); tick(2,1,5005); tick(1,16,16);
        Check(Request(JsonSerializer.Serialize(new {op="get_command_result",id=stale})).GetProperty("state").GetString()=="rejected" && data(0,0,0)==0,"load invalidates queued data command");
        Check(!Request("{\"op\":\"get_state\"}").GetProperty("dataReady").GetBoolean(),"load clears data snapshot readiness");
        TestCardManagement(Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(dll,"CardEdit")));
        TestBattleFeatures(Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(dll,"Battle")));
        Console.WriteLine($"{checks-failed} passed, {failed} failed native helper checks; simulation=true; this is not a game compatibility test.");
        return failed==0 ? 0 : 1;
    }

    private static void TestCardManagement(Call edit)
    {
        const int generation = 6;
        string Submit(int action, int context, string key = "", int star = 0, int loadGeneration = generation) =>
            JsonSerializer.Serialize(new { op = "card", action, argument = 0, argument2 = star, generation = loadGeneration, context, key });
        JsonElement Result(int id) => Request(JsonSerializer.Serialize(new { op = "get_command_result", id }));
        int Context() => Request("{\"op\":\"get_state\"}").GetProperty("cardContext").GetInt32();
        tick(3,1,0); tick(1,16,16);
        Check(Request(Submit(10,1)).GetProperty("error").GetString()=="script_hook_unavailable", "old script rejects card management");
        tick(0,1,1007); tick(1,16,16); edit(0,1,0);
        int context = Context();
        int page = Request(Submit(10,context)).GetProperty("id").GetInt32();
        Check(Task.Run(() => edit(1,0,0)).GetAwaiter().GetResult()==0 && edit(1,0,0)==10 && edit(1,0,0)==0,
            "only the game thread can claim a card request and cannot reenter it");
        edit(4,0,6); edit(5,'L',0); edit(5,0xC0BC,0); edit(6,0xC0BC,0); edit(11,1,1);
        var result = Result(page);
        var row = result.GetProperty("cardRows")[0];
        Check(result.GetProperty("state").GetString()=="applied" && result.GetProperty("cardTotal").GetInt32()==1 &&
            row.GetProperty("idHex").GetString()=="4CC0BC" && row.GetProperty("characterHex").GetString()=="C0BC",
            "card page carries copied CP936 bytes and the exact row");

        int editId = Request(Submit(13,context,"4CC0BC.C0BC",80)).GetProperty("id").GetInt32();
        Check(edit(1,0,0)==13 && edit(3,0,0)==80, "single-card star edit pins the requested target");
        edit(4,0,7); edit(5,'L',0); edit(5,0xC0BC,0); edit(6,0xC0BC,0);
        Check(edit(7,0,0)==1, "matching card and character IDs authorize a selected-row operation");
        edit(9,80,0); edit(10,99999,999); edit(11,1,1);
        Check(Result(editId).GetProperty("star").GetInt32()==80, "terminal reply carries the observed star");

        int mismatch = Request(Submit(12,context,"4CC0BC.C0BD")).GetProperty("id").GetInt32();
        edit(1,0,0); edit(4,0,6); edit(5,'L',0); edit(5,0xC0BC,0); edit(6,0xC0BC,0);
        Check(edit(7,0,0)==0, "same row and card ID with a changed character cannot authorize the operation");
        edit(11,0,0);
        Check(Result(mismatch).GetProperty("state").GetString()=="rejected", "mismatched identity leaves no applied result");

        int stale = Request(Submit(12,context,"4CC0BC.C0BC")).GetProperty("id").GetInt32();
        edit(0,0,0); edit(0,1,0);
        Check(Context()!=context && edit(1,0,0)==0 && Result(stale).GetProperty("state").GetString()=="rejected",
            "leaving and reentering an eligible scene invalidates the old request");
        context=Context();
        int oldSave = Request(Submit(12,context,"4CC0BC.C0BC")).GetProperty("id").GetInt32();
        tick(2,1,0); tick(1,16,16);
        Check(Context()==0 && edit(1,0,0)==0 && Result(oldSave).GetProperty("state").GetString()=="rejected",
            "loading cancels card commands and clears card readiness");

        tick(3,1,0); tick(0,1,1007|2048); tick(1,16,16); edit(0,1,0);
        int japanese = Request(Submit(10,Context(),loadGeneration:generation+1)).GetProperty("id").GetInt32();
        Check(edit(1,0,0)==10, "Japanese card request claimed");
        edit(4,0,6);
        foreach (int character in new[] { 0x8389, 0x8393, 0x8358 }) { edit(5,character,0); edit(6,character,0); }
        edit(11,1,1);
        var japaneseResult = Result(japanese);
        Check(japaneseResult.GetProperty("cardCodePage").GetInt32()==932 &&
            japaneseResult.GetProperty("cardRows")[0].GetProperty("idHex").GetString()=="838983938358",
            "Japanese component reports CP932 and preserves card bytes");
    }

    private static void TestBattleFeatures(Call battle)
    {
        const int generation = 7;
        const int vmContext = 123;
        JsonElement State() => Request("{\"op\":\"get_state\"}");
        int Context() => State().GetProperty("battleContext").GetInt32();
        JsonElement Toggle(string feature, bool enabled) =>
            Request(JsonSerializer.Serialize(new { op = "set_feature", feature, generation, enabled }));
        JsonElement Hp(int context, int argument, int value) =>
            Request(JsonSerializer.Serialize(new { op = "set_battle_hp", generation, context, argument, value }));
        string? Result(int id) => Request(JsonSerializer.Serialize(new { op = "get_command_result", id }))
            .GetProperty("state").GetString();

        tick(3,1,0); tick(1,16,16);
        Check(Toggle("ap_full", true).GetProperty("error").GetString() == "script_hook_unavailable",
            "older player component rejects new battle features");
        tick(0,1,2031); tick(1,16,16);
        foreach (var (name, bit) in new[] { ("ap_full", 1), ("unlimited_skills", 2), ("status_immunity", 4),
                     ("repeat_actions", 8), ("unlimited_actions", 16) })
        {
            int id = Toggle(name, true).GetProperty("id").GetInt32();
            tick(1,16,16);
            Check(Result(id) == "applied" && State().GetProperty("battleRules").GetInt32() == bit &&
                battle(0,bit,vmContext) == 0, "battle rule is independent and waits for the ordinary battle");
            battle(1,vmContext,0);
            Check(battle(0,bit,vmContext) == 1 && battle(0,bit,vmContext+1) == 0,
                "enabled battle rule binds the live ordinary battle");
            int disabled = Toggle(name, false).GetProperty("id").GetInt32();
            tick(1,16,16);
            Check(Result(disabled) == "applied" && battle(0,bit,vmContext) == 0, "battle rule closes independently");
            battle(2,0,0);
        }

        battle(1,vmContext,0); tick(20,10,1); control(5,1,20);
        battle(3,6,6); tick(10,75,100); tick(11,75,500);
        var state = State();
        Check(state.GetProperty("ordinaryBattle").GetBoolean() && state.GetProperty("ap").GetInt32() == 6 &&
            state.GetProperty("enemyHpMax").GetInt32() == 500, "battle snapshot reports AP and both HP limits");
        int context = Context();
        Check(Hp(context, 2, 25).GetProperty("error").GetString() == "invalid_battle_hp" &&
            Hp(context, 1, 0).GetProperty("error").GetString() == "invalid_battle_hp", "battle HP rejects invalid sides and zero");
        int id1 = Hp(context, 1, 25).GetProperty("id").GetInt32();
        Check(Task.Run(() => battle(4,0,0)).GetAwaiter().GetResult() == 0 && battle(4,0,0) == 2 &&
            battle(4,0,0) == 0 && battle(5,0,0) == 25, "battle HP pins a single game-thread claim and its exact argument");
        battle(6,25,0);
        Check(Result(id1) == "applied", "battle HP requires exact game readback");
        int id2 = Hp(context, 0, 25).GetProperty("id").GetInt32();
        battle(4,0,0); battle(6,50,0);
        Check(Result(id2) == "rejected", "battle HP mismatched readback is rejected");
        int stale = Hp(context, 1, 30).GetProperty("id").GetInt32();
        battle(7,vmContext,0); control(5,1,20);
        tick(1,16,16);
        Check(Context() != context && Result(stale) == "rejected" && battle(4,0,0) == 0 &&
            Hp(context,1,30).GetProperty("error").GetString() == "battle_context_changed",
            "an in-place enemy change invalidates old HP requests");
        int leaving = Hp(Context(),0,30).GetProperty("id").GetInt32();
        int claimed = battle(4,0,0);
        battle(2,0,0);
        Check(claimed == 1 && battle(5,0,0) == 0, "battle exit invalidates the executing HP target");
        battle(6,30,0);
        int next = Toggle("ap_full", false).GetProperty("id").GetInt32();
        tick(1,16,16);
        Check(Result(leaving) == "rejected" && Result(next) == "applied",
            "finishing an HP edit after battle exit releases the command slot");
        battle(1,vmContext,0); tick(20,10,1); control(5,1,20);
        int pending = Hp(Context(),0,30).GetProperty("id").GetInt32();
        tick(2,1,0); tick(1,16,16);
        Check(Context() == 0 && Result(pending) == "rejected", "readiness and pending HP are cleared on load");
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
