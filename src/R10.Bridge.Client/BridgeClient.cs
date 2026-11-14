using System.Text;
using System.Text.Json;

namespace R10.Bridge.Client;

public sealed class BridgeClient : IBridgeClient
{
    private static readonly Encoding CardEncoding = CodePagesEncodingProvider.Instance.GetEncoding(936,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;
    private static readonly Encoding JapaneseCardEncoding = CodePagesEncodingProvider.Instance.GetEncoding(932,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;
    private readonly IBridgeTransport _transport;
    public int ProcessId { get; }
    public bool IsSimulation => _transport.IsSimulation;
    public string? Session => _transport.Session;

    public BridgeClient(int processId, TimeSpan? timeout = null)
        : this(processId, new NamedPipeBridgeTransport(processId, timeout)) { }

    public BridgeClient(int processId, IBridgeTransport transport)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        ProcessId = processId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<BridgeState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(new { op = "get_state" }, false, cancellationToken).ConfigureAwait(false);
        var pid = Integer(response, "pid");
        if (pid != ProcessId) throw new BridgeException("response_pid_mismatch", "The bridge state belongs to a different process.");
        var phase = Integer(response, "phase");
        if (phase is < 0 or > 2) throw Invalid("phase");
        long? capabilities = NullableInteger(response, "capabilities");
        // The DLL's state field distinguishes support from an AIN bit forwarded by an older DLL.
        bool? treasureFind = response.TryGetProperty("treasureFind", out _) ? Boolean(response, "treasureFind") : null;
        return new BridgeState(ProcessId, Integer(response, "gameThreadId"), Integer(response, "heartbeat"), Integer(response, "frame"),
            Integer(response, "loadGeneration"), (int)phase, Boolean(response, "hpGuard"), Integer(response, "lastCommandId"),
            Text(response, "lastCommandState"), NullableInteger(response, "playerHp"), NullableInteger(response, "playerHpMax"), NullableInteger(response, "enemyHp"),
            NullableInteger(response, "heartbeatAgeMs"), NullableInteger(response, "blockedHits"), NullableInteger(response, "lastRequestedHp"),
            NullableInteger(response, "scriptVersion"), capabilities,
            NullableNonnegativeInteger(response, "loadKind"), NullableInteger(response, "loadSlot"),
            NullableNonnegativeInteger(response, "lastCompletedGeneration"), NullableNonnegativeInteger(response, "lastLoadOutcome"))
        { ResourcesFull = response.TryGetProperty("resourcesFull", out var full) && full.ValueKind == JsonValueKind.True, DataReady = response.TryGetProperty("dataReady", out var data) && data.ValueKind == JsonValueKind.True, Food=NullableInteger(response,"food"), Medal=NullableInteger(response,"medal"), Friendship=NullableInteger(response,"friendship"), Unit=NullableInteger(response,"unit"), FoodRedraw = response.TryGetProperty("foodRedraw", out var redraw) && redraw.ValueKind == JsonValueKind.True, AutoRedistribute = response.TryGetProperty("autoRedistribute", out var redistribute) && redistribute.ValueKind == JsonValueKind.True, NoDuplicateCards = response.TryGetProperty("noDuplicateCards", out var cards) && cards.ValueKind == JsonValueKind.True, FilteredCards = NullableInteger(response,"filteredCards"), FallbackCards = NullableInteger(response,"fallbackCards"), Session = Session, BattleReady = response.TryGetProperty("battleReady", out var ready) && ready.ValueKind == JsonValueKind.True,
            CardContext = response.TryGetProperty("cardContext", out _) ? BoundedInteger(response, "cardContext", int.MaxValue) : 0,
            CardBatchReadAvailable = response.TryGetProperty("cardBatchReadAvailable", out _) && Boolean(response, "cardBatchReadAvailable"),
            ItemStarAvailable = response.TryGetProperty("itemStarAvailable", out _) && Boolean(response, "itemStarAvailable"),
            ExtraTreasurePick = response.TryGetProperty("extraTreasurePick", out _) && Boolean(response, "extraTreasurePick"),
            TreasureFind = treasureFind == true,
            TreasureFindAvailable = treasureFind.HasValue && (capabilities.GetValueOrDefault() & BridgeState.TreasureFindCapability) != 0,
            OrdinaryBattle = response.TryGetProperty("ordinaryBattle", out _) && Boolean(response, "ordinaryBattle"),
            BattleContext = response.TryGetProperty("battleContext", out _) ? BoundedInteger(response, "battleContext", int.MaxValue) : 0,
            BattleRules = response.TryGetProperty("battleRules", out _) ? (BattleRules)BoundedInteger(response, "battleRules", int.MaxValue) : BattleRules.None,
            Ap = NullableNonnegativeInteger(response, "ap"), ApMax = NullableNonnegativeInteger(response, "apMax"),
            EnemyHpMax = NullableNonnegativeInteger(response, "enemyHpMax") };
    }

    public static bool ValidDataValue(string feature, int value) => feature switch
    {
        "food" or "medal" or "friendship" => value is >=0 and <=3,
        "unit" => value is >=0 and <=999,
        "star" => value is 10 or 40 or 80 or 200,
        _ => false
    };
    public async Task<BridgeCommand> SetValueForSessionAsync(string session, string feature, int value, long generation, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        if (!ValidDataValue(feature,value) || generation<0) throw new BridgeException("invalid_value","数值或功能不受支持。");
        var response=await ExchangeAsync(new { op="set_value", feature, value, generation },true,cancellationToken).ConfigureAwait(false);
        try { return ParseCommand(response); }
        catch (BridgeException error) { throw new BridgeException(error.Code,error.Message,true,error); }
    }

    public async Task<BridgeCommand> SetBattleHpForSessionAsync(string session, long generation, int context, int side, int value, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        if (generation < 0 || context <= 0 || side is < 0 or > 1 || value < 1)
            throw new BridgeException("invalid_battle_hp", "Battle HP requires a current battle, a valid side and a positive value.");
        var response = await ExchangeAsync(new { op = "set_battle_hp", generation, context, argument = side, value },
            true, cancellationToken).ConfigureAwait(false);
        try { return ParseCommand(response); }
        catch (BridgeException error) { throw new BridgeException(error.Code, error.Message, true, error); }
    }

    public async Task<BridgeCommand> ActionForSessionAsync(string session, int action, int argument, int argument2, long generation, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        if (action is < 2 or > 4 || generation < 0 ||
            (action == 2 && (argument is < 0 or > 9998 || argument2 != 0)) ||
            (action == 3 && (argument != 0 || argument2 != 0)) ||
            (action == 4 && (argument is < 0 or > 6 || argument2 is < 0 or > 1)))
            throw new BridgeException("invalid_action", "Invalid semantic action arguments.");
        var response = await ExchangeAsync(new { op = "action", action, argument, argument2, generation }, true, cancellationToken).ConfigureAwait(false);
        try { return ParseCommand(response); }
        catch (BridgeException error) { throw new BridgeException(error.Code, error.Message, true, error); }
    }

    public async Task<BridgeCommand> SubmitCardForSessionAsync(string session, long generation, int context, CardRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        bool validTarget = request.Operation switch
        {
            CardOperation.SetStar => request.Star >= 0,
            CardOperation.SetItemStar => request.Star is >= 0 and <= CardRequest.MaximumItemStar,
            _ => request.Star == 0
        };
        if (generation < 0 || context <= 0 || request.Index is < 0 or >= CardRequest.RowLimit ||
            (!request.IsPage && !request.IsItemStar && request.Operation is < CardOperation.Inspect or > CardOperation.SetStar) ||
            !validTarget || (request.IsItemStar && request.Index != 0) ||
            (request.IsPage || request.IsItemStar ? request.Key.Length != 0 : request.Key.Length is < 3 or > 257))
            throw new BridgeException("invalid_card_request", "卡片、读档状态或目标★无效。");
        var response = await ExchangeAsync(new { op = "card", action = (int)request.Operation, argument = request.Index,
            argument2 = request.Star, generation, context, key = request.Key }, request.Writes, cancellationToken).ConfigureAwait(false);
        try { return ParseCommand(response); }
        catch (BridgeException error) when (request.Writes) { throw new BridgeException(error.Code, error.Message, true, error); }
    }

    public async Task<BridgeCommand> SetFeatureAsync(string feature, bool enabled, long generation, CancellationToken cancellationToken = default)
    {
        if (feature is not ("hp_guard" or "no_duplicate_cards" or "food_redraw" or "auto_redistribute" or "resources_full" or "extra_treasure_pick" or "treasure_find"
            or "ap_full" or "unlimited_skills" or "status_immunity" or "repeat_actions" or "unlimited_actions"))
            throw new BridgeException("unsupported_feature", "Unsupported bridge feature.");
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        var response = await ExchangeAsync(new { op = "set_feature", feature, enabled, generation }, true, cancellationToken).ConfigureAwait(false);
        try { return ParseCommand(response); }
        catch (BridgeException error) { throw new BridgeException(error.Code, error.Message, true, error); }
    }

    public async Task<BridgeCommand> GetCommandResultAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        var response = await ExchangeAsync(new { op = "get_command_result", id }, false, cancellationToken).ConfigureAwait(false);
        var command = ParseCommand(response);
        if (command.Id != id) throw new BridgeException("command_id_mismatch", "The bridge returned a different command result.");
        return command;
    }

    public async Task PingAsync(CancellationToken cancellationToken = default) =>
        _ = await ExchangeAsync(new { op = "ping" }, false, cancellationToken).ConfigureAwait(false);

    public Task<BridgeCommand> SetFeatureForSessionAsync(string session, string feature, bool enabled, long generation, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        return SetFeatureAsync(feature, enabled, generation, cancellationToken);
    }

    public Task<BridgeCommand> GetCommandResultForSessionAsync(string session, long id, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        return GetCommandResultAsync(id, cancellationToken);
    }

    private void ValidateSession(string session)
    {
        if (string.IsNullOrEmpty(session) || Session is null || Session != session)
            throw new BridgeException("session_changed", "The command session does not match this process. Read get_state again.");
    }

    private async Task<JsonElement> ExchangeAsync(object request, bool mutation, CancellationToken cancellationToken)
    {
        var response = await _transport.ExchangeAsync(JsonSerializer.SerializeToElement(request), mutation, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.ValueKind != JsonValueKind.Object) throw Invalid("response");
            var ok = Boolean(response, "ok");
            if (!ok) throw new BridgeException("bridge_rejected", Text(response, "error"));
            if (Integer(response, "protocol") != BridgeProtocol.Version || Text(response, "build") != BridgeProtocol.Build)
                throw new BridgeException("unsupported_bridge", "The bridge protocol or build is not supported.");
            return response;
        }
        catch (BridgeException error) when (mutation && error.Code != "bridge_rejected")
        {
            throw new BridgeException(error.Code, error.Message, true, error);
        }
    }

    private static BridgeCommand ParseCommand(JsonElement response)
    {
        var id = Integer(response, "id");
        var state = Text(response, "state");
        if (id <= 0 || state is not ("queued" or "applied" or "rejected")) throw Invalid("command");
        var error = response.TryGetProperty("error", out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        return new BridgeCommand(id, state, error)
        {
            Cards = state == "applied" ? ParseCardResult(response) : null
        };
    }

    private static CardReply? ParseCardResult(JsonElement response)
    {
        if (response.TryGetProperty("itemStar", out _))
        {
            int level = BoundedInteger(response, "itemStar", int.MaxValue);
            int effective = BoundedInteger(response, "effectiveItemStar", int.MaxValue);
            if (effective != level && effective != 2L * level) throw Invalid("effectiveItemStar");
            return new(0, []) { ItemStar = new(level, effective) };
        }
        if (response.TryGetProperty("cardRows", out var rows)) return ParseCards(response, rows);
        if (!response.TryGetProperty("cardOwned", out _)) return null;
        int total = BoundedInteger(response, "cardTotal", CardRequest.RowLimit);
        int start = BoundedInteger(response, "cardStart", total - 1);
        string flags = Text(response, "cardOwned");
        if (flags.Length != Math.Min(CardRequest.OwnershipPageSize, total - start) || flags.Any(flag => flag is not ('0' or '1')))
            throw Invalid("cardOwned");
        return new(total, []) { Ownership = new(start, flags) };
    }

    private static CardReply ParseCards(JsonElement response, JsonElement rows)
    {
        var encoding = response.TryGetProperty("cardCodePage", out _) ? Integer(response, "cardCodePage") switch
        {
            936 => CardEncoding,
            932 => JapaneseCardEncoding,
            _ => throw Invalid("cardCodePage")
        } : CardEncoding;
        int total = BoundedInteger(response, "cardTotal", CardRequest.RowLimit);
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > CardRequest.CatalogPageSize || total == 0)
            throw Invalid("cardRows");
        var entries = new List<CardEntry>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw Invalid("cardRows");
            int index = BoundedInteger(row, "index", total - 1);
            if (entries.Count != 0 && index != entries[^1].Index + 1) throw Invalid("index");
            string idHex = Text(row, "idHex"), characterHex = Text(row, "characterHex");
            string id = DecodeCardText(idHex, encoding), character = DecodeCardText(characterHex, encoding);
            if (id.Length == 0) throw Invalid("idHex");
            entries.Add(new(index, id, character, BoundedInteger(row, "flags", 7), idHex + "." + characterHex));
        }
        if (!response.TryGetProperty("star", out _)) return new(total, entries);
        long star = NullableInteger(response, "star") ?? throw Invalid("star");
        int maxStar = BoundedInteger(response, "maxStar", int.MaxValue);
        if (entries.Count != 1 || star < -1 || star > int.MaxValue) throw Invalid("star");
        return new(total, entries, star < 0 ? null : (int)star, BoundedInteger(response, "exp", int.MaxValue),
            BoundedInteger(response, "nextExp", int.MaxValue), maxStar);
    }

    private static string DecodeCardText(string hex, Encoding encoding)
    {
        if (hex.Length > 128) throw Invalid("card text");
        try { return encoding.GetString(Convert.FromHexString(hex)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw Invalid("card text"); }
    }

    private static int BoundedInteger(JsonElement root, string name, int max)
    {
        long number = Integer(root, name);
        return number <= max ? (int)number : throw Invalid(name);
    }

    private static long Integer(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0
        ? number : throw Invalid(name);
    private static long? NullableInteger(JsonElement root, string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
        ? null : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw Invalid(name);
    private static long? NullableNonnegativeInteger(JsonElement root, string name)
    {
        var number = NullableInteger(root, name);
        return number is <0 ? throw Invalid(name) : number;
    }
    private static bool Boolean(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw Invalid(name);
    private static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw Invalid(name);
    private static BridgeException Invalid(string name) => new("invalid_response", $"The bridge response has an invalid or missing {name} field.");
}
