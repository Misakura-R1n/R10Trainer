using System.Buffers.Binary;
using System.Collections.ObjectModel;
using R10.Core.Memory;

namespace R10.Core.Data;

public sealed record SceneObservation(long Generation, bool GameplayReady, bool BattleReady,
    string? Error = null, bool DeathRateWritable = false);

public sealed class DataLock
{
    internal DataLock(FeatureId id, long generation, uint owner, int[] values, Func<bool>? contextValid = null)
    { Id = id; Generation = generation; Owner = owner; Values = Array.AsReadOnly((int[])values.Clone()); ContextValid=contextValid; }
    internal Func<bool>? ContextValid { get; }
    public FeatureId Id { get; }
    public long Generation { get; }
    public uint Owner { get; }
    public IReadOnlyList<int> Values { get; }
}

public sealed class DataWriteException : IOException
{
    internal DataWriteException(string reason, IReadOnlyList<uint> conflicts, Exception? inner = null)
        : base(reason + (conflicts.Count == 0 ? "；未修改或已恢复原值。" :
            "；未能确认恢复：" + string.Join("、", conflicts.Select(x => $"0x{x:X8}"))), inner)
    { UnrestoredAddresses = conflicts; }
    public IReadOnlyList<uint> UnrestoredAddresses { get; }
    public bool RollbackComplete => UnrestoredAddresses.Count == 0;
}

/// <summary>
/// Fixed Chinese BugFix data layout. The session verifies the executable/AIN fingerprints.
/// This engine never opens a process, modifies a game file, or starts its own worker.
/// </summary>
public sealed class R10DataEngine
{
    private const int RootOffset = 0x3FCBB0, TurnTable = 0x3F8, ResourcesTable = 0x400,
        PartyTable = 0x410, EnemyTable = 0x48C, CampaignTable = 0x500, DifficultyOffset = 0x560;
    private const int MaximumHp = 100000000;
    private static readonly int[] ArmyOffsets = [0x64, 0x68, 0x6C, 0x70];
    private readonly IProcessMemory _memory;
    private readonly int _processId;
    private PointerGraph? _graph;
    private SceneOwners? _owners;
    private long _generation;
    private SceneObservation _scene = new(0, false, false);
    private readonly Dictionary<FeatureId, string> _readErrors = [];

    public R10DataEngine(IProcessMemory memory)
    { _memory = memory ?? throw new ArgumentNullException(nameof(memory)); _processId = memory.ProcessId; }

    public SceneObservation Current => _scene;
    public IReadOnlyDictionary<FeatureId, string> ReadErrors => new ReadOnlyDictionary<FeatureId, string>(_readErrors);

    public SceneObservation Refresh()
    {
        try
        {
            if (!_memory.IsAlive || _memory.ProcessId != _processId) throw new IOException("原游戏进程已退出。");
            var graph = ReadGraph();
            if (_graph != graph) { _generation++; _graph = graph; _owners = null; }
            uint turn = ResolveType(graph, TurnTable), party = ResolveType(graph, PartyTable),
                maximum = ResolveType(graph, PartyTable, 0), campaign = ResolveType(graph, CampaignTable);
            var owners = new SceneOwners(turn, party, maximum, campaign);
            if (_owners is not null && _owners != owners) _generation++;
            _owners = owners;
            int turnValue = ReadInt(turn, 8), phase = ReadInt(turn, 12), currentHp = ReadInt(party, 16), maximumHp = ReadInt(maximum);
            bool gameplay = InRange(turnValue, 0, 999) && InRange(phase, 1, 999) &&
                            InRange(maximumHp, 1, MaximumHp) && InRange(currentHp, 1, maximumHp);
            bool battle = false;
            if (gameplay)
            {
                // Battle objects may legitimately be absent while walking the campaign map.
                try
                {
                    uint round = ResolveType(graph, 0x490, 13), enemy = ResolveType(graph, EnemyTable);
                    int roundValue = ReadInt(round), enemyHp = ReadInt(enemy), enemyMax = ReadInt(enemy, 0x28);
                    battle = InRange(roundValue, 1, 1000) && InRange(enemyMax, 1, MaximumHp) && InRange(enemyHp, 0, enemyMax);
                }
                catch (IOException) { battle = false; }
            }
            if (ReadGraph() != graph || ResolveType(graph, TurnTable) != turn ||
                ResolveType(graph, PartyTable) != party || ResolveType(graph, PartyTable, 0) != maximum ||
                ResolveType(graph, CampaignTable) != campaign)
                throw new IOException("游戏对象正在切换，等待重新读取。");
            _scene = new(_generation, gameplay, battle, null, gameplay && phase != 16);
        }
        catch (Exception ex) when (IsMemoryFailure(ex))
        { Invalidate(); _scene = new(_generation, false, false, ex.Message); }
        return _scene;
    }

    public IReadOnlyDictionary<FeatureId, decimal?> ReadValues()
    {
        _readErrors.Clear();
        var values = FeatureCatalog.All.Where(f => f.Kind != FeatureKind.Toggle).ToDictionary(f => f.Id, _ => (decimal?)null);
        if (!_scene.GameplayReady || _graph is null) return new ReadOnlyDictionary<FeatureId, decimal?>(values);
        foreach (var id in values.Keys.ToArray())
        {
            try
            {
                var graph = RequireGraph();
                long generation = _generation;
                decimal? value = id switch
                {
                    FeatureId.Difficulty => CheckedValue(id, ReadInt(Add(graph.Offsets, DifficultyOffset))),
                    FeatureId.Turn => ReadFieldValue(id, graph, TurnTable, 8),
                    FeatureId.DeathRate => ReadFieldValue(id, graph, CampaignTable, 0x94, 10m),
                    FeatureId.Food => ReadFieldValue(id, graph, ResourcesTable, 0),
                    FeatureId.Medal => ReadFieldValue(id, graph, ResourcesTable, 4),
                    FeatureId.Friendship => ReadFieldValue(id, graph, ResourcesTable, 8),
                    FeatureId.Unit => ReadFieldValue(id, graph, ResourcesTable, 0x20),
                    FeatureId.Army => UnifiedArmyValue(ReadArmies()),
                    // There is no honest single observed value for a batch of created characters.
                    FeatureId.Star => null,
                    _ => null
                };
                if (_generation != generation || ReadGraph() != graph) throw new IOException("读取时游戏对象已改变。");
                values[id] = value;
            }
            catch (Exception ex) when (IsMemoryFailure(ex)) { _readErrors[id] = ex.Message; }
        }
        return new ReadOnlyDictionary<FeatureId, decimal?>(values);
    }

    public IReadOnlyList<int> ReadArmies()
    {
        var graph = RequireGraph();
        uint owner = ResolveType(graph, CampaignTable);
        int[] values = ArmyOffsets.Select(offset => ReadInt(owner, offset)).ToArray();
        foreach (int value in values) CheckRange(value, 0, 99999999, "四国兵力");
        if (ReadGraph() != graph || ResolveType(graph, CampaignTable) != owner) throw new IOException("读取兵力时对象已改变。");
        return Array.AsReadOnly(values);
    }

    public void WriteValue(FeatureId id, decimal value)
    {
        if (!FeatureCatalog.ValidValue(id, value)) throw new ArgumentOutOfRangeException(nameof(value), "数值超出允许范围。");
        var graph = RequireGameplay();
        int integer = checked((int)value);
        switch (id)
        {
            case FeatureId.Difficulty:
                WriteSingle(Add(graph.Offsets, DifficultyOffset), integer, Guard(graph, () => true)); break;
            case FeatureId.Turn:
                WriteFields(graph, TurnTable, [8], [integer]); break;
            case FeatureId.DeathRate:
                WriteDeathRate(graph, checked((int)(value * 10))); break;
            case FeatureId.Army:
                WriteFields(graph, CampaignTable, ArmyOffsets, Enumerable.Repeat(integer, 4).ToArray()); break;
            case FeatureId.Food: case FeatureId.Medal: case FeatureId.Friendship:
                int offset = id == FeatureId.Food ? 0 : id == FeatureId.Medal ? 4 : 8;
                uint resourceOwner = ValidateFields(graph, ResourcesTable, [offset], 0, 3);
                WriteFields(graph, ResourcesTable, [offset], [integer], resourceOwner); break;
            case FeatureId.Unit:
                WriteFields(graph, ResourcesTable, [0x20], [integer]); break;
            case FeatureId.Star:
                WriteStars(graph, integer); break;
            default: throw new ArgumentException("该功能不接受数值。", nameof(id));
        }
    }

    public void MaintainToggle(FeatureId id)
    {
        var graph = RequireGameplay();
        switch (id)
        {
            case FeatureId.Resources:
                uint resourceOwner = ValidateFields(graph, ResourcesTable, [0, 4, 8], 0, 3);
                WriteFields(graph, ResourcesTable, [0, 4, 8], [3, 3, 3], resourceOwner); break;
            case FeatureId.War:
                uint campaignOwner = ValidateFields(graph, CampaignTable, [0xBC, 0xC0, 0xC4, 0xC8], 0, 3);
                WriteFields(graph, CampaignTable, [0xBC, 0xC0, 0xC4, 0xC8], [3, 3, 3, 3], campaignOwner); break;
            case FeatureId.Treasure:
                uint treasure = ResolveEncoded(graph, 0x479);
                CheckRange(ReadInt(treasure, 0x34), 0, 1000, "宝箱发现加成");
                // The known token aliases PartyStatus/CommonStatus; verify both routes.
                if (ResolveType(graph, PartyTable) != treasure) throw new IOException("宝箱数据所属对象不一致。");
                WriteSingle(Add(treasure, 0x34), 100, Guard(graph,
                    () => ResolveEncoded(graph, 0x479) == treasure && ResolveType(graph, PartyTable) == treasure)); break;
            case FeatureId.Hp:
                RequireBattle();
                uint party = ResolveType(graph, PartyTable), maximumAddress = ResolveType(graph, PartyTable, 0);
                int current = ReadInt(party, 0x10), maximum = ReadInt(maximumAddress);
                CheckRange(maximum, 1, MaximumHp, "我方 HP 上限"); CheckRange(current, 1, maximum, "我方 HP");
                WriteSingle(Add(party, 0x10), maximum, Guard(graph,
                    () => ResolveType(graph, PartyTable) == party && ResolveType(graph, PartyTable, 0) == maximumAddress && ReadInt(maximumAddress) == maximum)); break;
            case FeatureId.EnemyKill: case FeatureId.EnemyNear:
                RequireBattle();
                uint enemy = ResolveType(graph, EnemyTable);
                int enemyCurrent = ReadInt(enemy), enemyMax = ReadInt(enemy, 0x28);
                CheckRange(enemyMax, 1, MaximumHp, "敌人 HP 上限"); CheckRange(enemyCurrent, 0, enemyMax, "敌人 HP");
                int target = id == FeatureId.EnemyKill ? 0 : Math.Min(enemyCurrent, 1);
                WriteSingle(enemy, target, Guard(graph,
                    () => ResolveType(graph, EnemyTable) == enemy && ReadInt(enemy, 0x28) == enemyMax)); break;
            default: throw new ArgumentException("该功能不是数据开关。", nameof(id));
        }
    }

    public DataLock CaptureLock(FeatureId id, Func<bool>? contextValid = null)
    {
        var graph = RequireGameplay();
        (int table, int[] offsets) = LockLayout(id);
        if (id == FeatureId.DeathRate) RequireDeathRatePhase(graph);
        uint owner = ResolveType(graph, table);
        int[] values = offsets.Select(offset => ReadInt(owner, offset)).ToArray();
        foreach (int value in values)
            CheckRange(value, 0, id == FeatureId.Army ? 99999999 : id == FeatureId.DeathRate ? 1000 : 999, "锁定值");
        var guard = Guard(graph, () => ResolveType(graph, table) == owner);
        if (!guard() || contextValid?.Invoke()==false) throw new IOException("捕获锁定值时游戏对象或读档代次已改变。");
        return new DataLock(id, _generation, owner, values, contextValid);
    }

    public bool MaintainLock(DataLock dataLock)
    {
        ArgumentNullException.ThrowIfNull(dataLock);
        if (dataLock.ContextValid?.Invoke()==false) return false;
        var scene = Refresh();
        if (scene.Error is not null) throw new IOException(scene.Error);
        if (dataLock.Generation != _generation) return false;
        if (!scene.GameplayReady) throw new InvalidOperationException("游戏场景尚未就绪，锁定暂时暂停。");
        var graph = RequireGraph();
        (int table, int[] offsets) = LockLayout(dataLock.Id);
        if (ReadGraph() != graph || ResolveType(graph, table) != dataLock.Owner) return false;
        if (dataLock.Id == FeatureId.DeathRate) WriteDeathRate(graph, dataLock.Values[0], dataLock.Owner, dataLock.ContextValid);
        else WriteFields(graph, table, offsets, dataLock.Values.ToArray(), dataLock.Owner, dataLock.ContextValid);
        return true;
    }

    private void WriteDeathRate(PointerGraph graph, int tenths, uint? expectedOwner = null, Func<bool>? contextValid = null)
    {
        RequireDeathRatePhase(graph);
        // Cumulative R/I/D/L only; stage cache S and historical/script flags are intentionally untouched.
        WriteFields(graph, CampaignTable, [0x94, 0x90, 0x88, 0x84],
            [tenths, tenths / 10, tenths * 300, 300000 - tenths * 300], expectedOwner, contextValid);
    }

    private void RequireDeathRatePhase(PointerGraph graph)
    {
        uint turn = ResolveType(graph, TurnTable);
        if (ReadInt(turn, 12) == 16) throw new InvalidOperationException("当前阶段暂停死亡率修改，锁定目标仍保留。");
    }

    private void WriteStars(PointerGraph graph, int level)
    {
        int nextExperience = level switch { 10 => 259, 40 => 4525, 80 or 200 => 99999, _ => throw new ArgumentOutOfRangeException(nameof(level)) };
        uint container = ResolveData(graph, 0x404, 0);
        uint array = ReadPointer(Add(container, 0x10));
        int byteLength = ReadInt(container, 0x14);
        if (byteLength < 0 || byteLength % 4 != 0 || byteLength / 4 > 1080) throw new IOException("角色数组长度无效。");
        var records = new HashSet<uint>();
        var writes = new List<WriteIntent>();
        for (int i = 0; i < byteLength / 4; i++)
        {
            int index = i;
            uint record = ResolveGenerated(graph, array, checked(index * 4));
            if (!records.Add(record)) throw new IOException("角色数组含重复对象，已取消批量修改。");
            var guard = Guard(graph, () => ResolveData(graph, 0x404, 0) == container &&
                ReadPointer(Add(container, 0x10)) == array && ReadInt(container, 0x14) == byteLength &&
                ResolveGenerated(graph, array, checked(index * 4)) == record);
            writes.Add(new(Add(record, 4), Ints(level, 0, nextExperience), guard));
        }
        WriteTransaction(writes);
    }

    private void WriteFields(PointerGraph graph, int table, int[] offsets, int[] values, uint? expectedOwner = null, Func<bool>? contextValid = null)
    {
        if (offsets.Length != values.Length) throw new ArgumentException("写入字段数不匹配。");
        uint owner = ResolveType(graph, table);
        if (expectedOwner.HasValue && owner != expectedOwner.Value) throw new IOException("锁定对象已替换，未写入旧值。");
        var guard = Guard(graph, () => ResolveType(graph, table) == owner && contextValid?.Invoke()!=false);
        WriteTransaction(offsets.Select((offset, i) => new WriteIntent(Add(owner, offset), Ints(values[i]), guard)).ToArray());
    }

    private void WriteSingle(uint address, int value, Func<bool> guard) => WriteTransaction([new(address, Ints(value), guard)]);

    private void WriteTransaction(IReadOnlyList<WriteIntent> writes)
    {
        var before = new byte[writes.Count][];
        var completed = new bool[writes.Count];
        var attempted = new List<int>();
        try
        {
            // Prepare the whole batch before its first write.
            for (int i = 0; i < writes.Count; i++)
            {
                var write = writes[i];
                if (!write.Guard()) throw new IOException("预检时游戏对象已改变。");
                before[i] = _memory.Read(write.Address, write.Bytes.Length);
            }
            for (int i = 0; i < writes.Count; i++)
            {
                var write = writes[i];
                if (before[i].AsSpan().SequenceEqual(write.Bytes)) continue;
                if (!write.Guard() || !_memory.Read(write.Address, write.Bytes.Length).AsSpan().SequenceEqual(before[i]))
                    throw new IOException("写入前对象或数值已改变。");
                attempted.Add(i); // Includes partial writes and failures from the native adapter.
                Exception? writeError = null;
                try { _memory.Write(write.Address, write.Bytes); completed[i] = true; }
                catch (Exception ex) when (IsMemoryFailure(ex)) { writeError = ex; }
                byte[]? observed = null;
                try { observed = _memory.Read(write.Address, write.Bytes.Length); }
                catch (Exception ex) when (IsMemoryFailure(ex)) { writeError ??= ex; }
                if (writeError is not null) throw new IOException("写入或回读失败。", writeError);
                if (observed is null || !observed.AsSpan().SequenceEqual(write.Bytes) || !write.Guard())
                    throw new IOException("写入结果或对象身份校验失败。");
            }
            foreach (var write in writes)
                if (!write.Guard() || !_memory.Read(write.Address, write.Bytes.Length).AsSpan().SequenceEqual(write.Bytes))
                    throw new IOException("批量写入最终校验失败。");
        }
        catch (Exception ex) when (IsMemoryFailure(ex))
        {
            var conflicts = new List<uint>();
            for (int n = attempted.Count - 1; n >= 0; n--)
            {
                int i = attempted[n]; var write = writes[i];
                try
                {
                    if (!write.Guard()) throw new IOException("原对象身份不明。");
                    byte[] current = _memory.Read(write.Address, write.Bytes.Length);
                    if (current.AsSpan().SequenceEqual(before[i])) continue;
                    // A failed/partial native write cannot establish ownership of its readback.
                    // Only a completed write still containing our exact target is eligible.
                    if (!completed[i] || !current.AsSpan().SequenceEqual(write.Bytes))
                        throw new IOException("存在第三方修改或未知写入结果。");
                    _memory.Write(write.Address, before[i]);
                    if (!_memory.Read(write.Address, write.Bytes.Length).AsSpan().SequenceEqual(before[i]) || !write.Guard())
                        throw new IOException("恢复后校验失败。");
                }
                catch (Exception restoreError) when (IsMemoryFailure(restoreError)) { conflicts.Add(write.Address); }
            }
            throw new DataWriteException(ex.Message, conflicts.AsReadOnly(), ex);
        }
    }

    private Func<bool> Guard(PointerGraph graph, Func<bool> ownerCheck)
    {
        long generation = _generation;
        bool lost = false;
        return () =>
        {
            if (lost) return false;
            try
            {
                if (!_memory.IsAlive || _memory.ProcessId != _processId || _generation != generation || ReadGraph() != graph || !ownerCheck())
                    lost = true;
            }
            catch (Exception ex) when (IsMemoryFailure(ex)) { lost = true; }
            return !lost; // A witnessed ownership gap cannot heal during rollback.
        };
    }

    private PointerGraph ReadGraph()
    {
        uint module = _memory.ModuleBase;
        if (module == 0) throw new IOException("游戏模块基址无效。");
        uint root = ReadPointer(Add(module, RootOffset)), pointerBase = ReadPointer(Add(root, 8));
        uint first = ReadPointer(pointerBase), second = ReadPointer(first), offsets = ReadPointer(Add(second, 0x10));
        return new(module, root, pointerBase, offsets);
    }

    private PointerGraph RequireGraph() => _graph ?? throw new IOException("游戏对象尚未就绪。");
    private PointerGraph RequireGameplay()
    {
        // Re-read readiness at every command boundary; cached UI readiness is not write authority.
        var previous = _scene;
        var scene = Refresh();
        if (!scene.GameplayReady) throw new InvalidOperationException(scene.Error ?? "请先进入有效游戏场景。");
        if (!previous.GameplayReady || previous.Generation != scene.Generation)
            throw new InvalidOperationException("游戏对象刚刚切换，请等待稳定后重试。");
        return RequireGraph();
    }
    private void RequireBattle() { if (!_scene.BattleReady) throw new InvalidOperationException("当前不是可修改的战斗场景。"); }
    private void Invalidate()
    { if (_graph is not null) _generation++; _graph = null; _owners = null; }

    private uint ResolveType(PointerGraph graph, int table, params int[] indexes)
    {
        uint owner = ResolveEncoded(graph, ReadInt(Add(graph.Offsets, table)));
        foreach (int index in indexes) owner = ResolveGenerated(graph, owner, checked(index * 4));
        return owner;
    }
    private uint ResolveGenerated(PointerGraph graph, uint owner, int offset) => ResolveEncoded(graph, ReadInt(owner, offset));
    private uint ResolveEncoded(PointerGraph graph, int token) => ReadPointer(Add(ResolvePointerData(graph, token), 0x10));
    private uint ResolvePointerData(PointerGraph graph, int token)
    {
        int remainder = token & 3;
        uint block = ReadPointer(Add(graph.PointerBase, unchecked(token - remainder)));
        return ReadPointer(Add(block, remainder * 4));
    }
    private uint ResolveData(PointerGraph graph, int table, int index)
        => ResolvePointerData(graph, ReadInt(ResolveType(graph, table), checked(index * 4)));
    private uint ReadPointer(uint address)
    {
        uint pointer = _memory.ReadUInt32(address);
        if (pointer == 0) throw new IOException($"空游戏指针：0x{address:X8}。");
        return pointer;
    }
    private int ReadInt(uint address, int offset = 0) => _memory.ReadInt32(Add(address, offset));
    private static uint Add(uint address, int offset) => R10.Core.Memory.MemoryExtensions.Add(address, offset);
    private decimal ReadFieldValue(FeatureId id, PointerGraph graph, int table, int offset, decimal divisor = 1)
    {
        uint owner = ResolveType(graph, table);
        decimal value = CheckedValue(id, ReadInt(owner, offset) / divisor);
        if (ResolveType(graph, table) != owner) throw new IOException("读取时对象已替换。");
        return value;
    }
    private uint ValidateFields(PointerGraph graph, int table, int[] offsets, int minimum, int maximum)
    {
        uint owner = ResolveType(graph, table);
        foreach (int offset in offsets) CheckRange(ReadInt(owner, offset), minimum, maximum, "资源字段");
        return owner;
    }
    private static decimal CheckedValue(FeatureId id, decimal value)
    {
        if (!FeatureCatalog.ValidValue(id, value)) throw new IOException("游戏数值超出预期范围。");
        return value;
    }
    private static decimal? UnifiedArmyValue(IReadOnlyList<int> values) => values.All(x => x == values[0]) ? values[0] : null;
    private static bool InRange(int value, int minimum, int maximum) => value >= minimum && value <= maximum;
    private static void CheckRange(int value, int minimum, int maximum, string field)
    { if (!InRange(value, minimum, maximum)) throw new IOException($"{field}超出预期范围。"); }
    private static (int, int[]) LockLayout(FeatureId id) => id switch
    {
        FeatureId.Turn => (TurnTable, [8]), FeatureId.DeathRate => (CampaignTable, [0x94]),
        FeatureId.Army => (CampaignTable, ArmyOffsets), _ => throw new ArgumentException("该功能不支持锁定。", nameof(id))
    };
    private static byte[] Ints(params int[] values)
    {
        var bytes = new byte[checked(values.Length * 4)];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        return bytes;
    }
    private static bool IsMemoryFailure(Exception ex) => ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException;
    private sealed record PointerGraph(uint Module, uint Root, uint PointerBase, uint Offsets);
    private sealed record SceneOwners(uint Turn, uint Party, uint Maximum, uint Campaign);
    private sealed record WriteIntent(uint Address, byte[] Bytes, Func<bool> Guard);
}
