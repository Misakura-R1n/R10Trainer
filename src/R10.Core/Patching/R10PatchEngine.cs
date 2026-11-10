using System.Collections.ObjectModel;
using System.Diagnostics;
using R10.Core.Memory;

namespace R10.Core.Patching;

public sealed record PatchObservation(bool Requested, bool Active, bool Supported, string? Error = null);

/// <summary>
/// Serial, external-memory AIN operand editor. An attempted write creates a restoration lease before
/// entering Win32; neither a thrown write nor a failed read-back can erase that responsibility.
/// No method suspends the game, so guarded reads do not imply an atomic transaction with game execution.
/// </summary>
public sealed class R10PatchEngine
{
    private sealed class Lease(PatchDefinition definition)
    {
        internal PatchDefinition Definition { get; } = definition;
        internal bool Owned;
        internal bool Pending;
        internal byte[]? Observed;
        internal bool Active;
        internal void Clear() { Owned = Pending = Active = false; Observed = null; }
    }
    private static readonly FeatureId[] FeatureIds = [FeatureId.Redraw, FeatureId.Redistribute];
    private readonly IProcessMemory _memory;
    private readonly int _pid;
    private readonly Func<string> _fingerprint;
    private uint _codeLength = BugFixProfile.CodeLength;
    private readonly Lease[] _leases = BugFixProfile.Definitions.Select(d => new Lease(d)).ToArray();
    private readonly HashSet<FeatureId> _requested = [];
    private readonly Dictionary<FeatureId, string?> _errors = [];
    private uint? _codeBase;
    private bool _verified;
    private ScanCursor? _scan;
    private readonly int _scanByteBudget;
    private readonly long _scanTimeBudget;
    private readonly Func<long> _scanClock;
    internal const int DefaultScanByteBudget = 1024 * 1024;
    internal static readonly TimeSpan DefaultScanTimeBudget = TimeSpan.FromMilliseconds(8);

    private sealed class RegionScan(MemoryRegion region)
    {
        internal MemoryRegion Region { get; } = region;
        internal ulong Offset;
        internal ulong? Match;
    }
    private sealed class BoundaryScan(ulong boundary, ulong start, ulong end, MemoryRegion[] regions)
    {
        internal ulong Boundary { get; } = boundary;
        internal ulong Start { get; } = start;
        internal ulong End { get; } = end;
        internal MemoryRegion[] Regions { get; } = regions;
        internal bool Complete;
        internal ulong[] Matches = [];
        internal bool SameContext(BoundaryScan other) => Boundary == other.Boundary && Start == other.Start && End == other.End && Regions.SequenceEqual(other.Regions);
    }
    private sealed class ScanCursor
    {
        internal RegionScan[] Regions { get; private set; } = [];
        internal Queue<RegionScan> Pending { get; private set; } = new();
        internal BoundaryScan[] Boundaries { get; private set; } = [];
        internal Queue<BoundaryScan> PendingBoundaries { get; private set; } = new();
        internal ulong Total => Regions.Aggregate(0ul, (sum, r) => sum + r.Region.Size) + Boundaries.Aggregate(0ul, (sum, b) => sum + b.End - b.Start);
        internal ulong Completed => Regions.Aggregate(0ul, (sum, r) => sum + r.Offset) + Boundaries.Where(b => b.Complete).Aggregate(0ul, (sum, b) => sum + b.End - b.Start);
        internal bool Reconcile(MemoryRegion[] map)
        {
            var readable = map.Where(r => r.IsReadable).ToArray();
            if (Regions.Select(r => r.Region).SequenceEqual(readable)) return false;
            var previous = Regions.ToDictionary(r => r.Region);
            var next = readable.Select(r => previous.GetValueOrDefault(r) ?? new RegionScan(r)).ToArray();
            var retained = next.ToHashSet();
            var pending = Pending.Where(retained.Contains).ToList();
            // New/changed mappings join behind existing work, rather than repeatedly restarting
            // all unchanged heaps and CODE ranges when the game allocates unrelated objects.
            pending.AddRange(next.Where(r => !previous.ContainsKey(r.Region)));
            Regions = next; Pending = new(pending);
            var oldBoundaries = Boundaries.ToDictionary(b => b.Boundary);
            var nextBoundaries = new List<BoundaryScan>();
            // Every contiguous readable boundary gets its own small, separately-budgeted window.
            // A window may span more than two tiny regions; native reads never cross a region.
            ulong overlap = (uint)BugFixProfile.Anchors[0].Bytes.Length - 1;
            for (int first = 0; first < readable.Length;)
            {
                int last = first;
                while (last + 1 < readable.Length && (ulong)readable[last].BaseAddress + readable[last].Size == readable[last + 1].BaseAddress) last++;
                ulong runStart = readable[first].BaseAddress, runEnd = (ulong)readable[last].BaseAddress + readable[last].Size;
                for (int index = first + 1; index <= last; index++)
                {
                    ulong boundary = readable[index].BaseAddress, start = Math.Max(runStart, boundary - Math.Min(boundary, overlap)), end = Math.Min(runEnd, boundary + overlap);
                    var parts = readable.Skip(first).Take(last - first + 1).Where(r => (ulong)r.BaseAddress < end && (ulong)r.BaseAddress + r.Size > start).ToArray();
                    var fresh = new BoundaryScan(boundary, start, end, parts);
                    nextBoundaries.Add(oldBoundaries.TryGetValue(boundary, out var old) && old.SameContext(fresh) ? old : fresh);
                }
                first = last + 1;
            }
            var retainedBoundaries = nextBoundaries.ToHashSet();
            var boundaryQueue = PendingBoundaries.Where(retainedBoundaries.Contains).ToList();
            boundaryQueue.AddRange(nextBoundaries.Where(b => !oldBoundaries.TryGetValue(b.Boundary, out var old) || !ReferenceEquals(b, old)));
            Boundaries = nextBoundaries.ToArray(); PendingBoundaries = new(boundaryQueue);
            return true;
        }
        internal ulong? UniqueMatch()
        {
            var matches = Regions.Where(r => r.Match.HasValue).Select(r => r.Match!.Value).Concat(Boundaries.SelectMany(b => b.Matches)).Distinct().Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("AIN CODE 锚点存在多个候选；为避免写错位置，未启用脚本功能。");
            return matches.Length == 1 ? matches[0] : null;
        }
    }

    public R10PatchEngine(IProcessMemory memory, string ainPath)
        : this(memory, () => ReadFingerprint(ainPath)) { }

    internal R10PatchEngine(IProcessMemory memory, Func<string> fingerprint,
        int scanByteBudget = DefaultScanByteBudget, TimeSpan? scanTimeBudget = null, Func<long>? scanClock = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _pid = memory.ProcessId;
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        if (scanByteBudget < 4096 || scanByteBudget > DefaultScanByteBudget) throw new ArgumentOutOfRangeException(nameof(scanByteBudget));
        var time = scanTimeBudget ?? DefaultScanTimeBudget;
        if (time <= TimeSpan.Zero || time > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(scanTimeBudget));
        _scanByteBudget = scanByteBudget;
        _scanTimeBudget = Math.Max(1, (long)(time.TotalSeconds * Stopwatch.Frequency));
        _scanClock = scanClock ?? Stopwatch.GetTimestamp;
    }

    public bool HasRecoveryDebt => _leases.Any(l => l.Owned || l.Pending);
    public bool IsLocating => _scan != null;
    public double? ScanProgress => _scan is { } scan ? scan.Total == 0 ? 0 : Math.Clamp((double)scan.Completed / scan.Total, 0, 1) : null;
    public int ScanLayoutChanges { get; private set; }
    internal int LastScanReadBytes { get; private set; }
    public IReadOnlyDictionary<FeatureId, PatchObservation> Observations =>
        new ReadOnlyDictionary<FeatureId, PatchObservation>(FeatureIds.ToDictionary(id => id,
            id => new PatchObservation(_requested.Contains(id),
                _requested.Contains(id) && _leases.Where(l => l.Definition.Feature == id).All(l => l.Active),
                _verified, _errors.GetValueOrDefault(id))));

    /// <summary>Updates intent only. Call Maintain on the serial worker to apply or restore.</summary>
    public void SetRequested(FeatureId feature, bool enabled)
    {
        if (!FeatureIds.Contains(feature)) throw new ArgumentOutOfRangeException(nameof(feature));
        if (enabled) _requested.Add(feature); else _requested.Remove(feature);
        if (!enabled) foreach (var lease in ForFeature(feature)) lease.Active = false;
        if (_requested.Count == 0 && !HasRecoveryDebt) _scan = null;
        _errors.Remove(feature);
    }

    public void Maintain() => Maintain(null);
    public void Maintain(Func<bool>? shouldYield)
    {
        LastScanReadBytes = 0;
        if (_requested.Count == 0 && !HasRecoveryDebt) return;
        if (shouldYield?.Invoke() == true) return;
        try
        {
            RequireSession();
            VerifySource();
            if (shouldYield?.Invoke() == true || !EnsureBinding(shouldYield)) return;
            if (shouldYield?.Invoke() == true) return;
            foreach (var lease in _leases.Where(l => l.Pending)) RecoverOriginal(lease, false);
            foreach (var id in FeatureIds.Where(id => !_requested.Contains(id))) RestoreFeature(id);
            // Preflight every requested operand before starting any new feature's writes.
            foreach (var lease in _leases.Where(l => _requested.Contains(l.Definition.Feature))) Classify(lease);
            foreach (var id in FeatureIds.Where(_requested.Contains))
                if (!EnableFeature(id, shouldYield)) return;
            foreach (var id in FeatureIds) _errors.Remove(id);
        }
        catch (Exception error) when (IsOperational(error))
        {
            _scan = null; // Incomplete observations never survive a read or identity failure.
            foreach (var lease in _leases) lease.Active = false;
            foreach (var id in FeatureIds.Where(id => _requested.Contains(id) || ForFeature(id).Any(l => l.Owned || l.Pending)))
                _errors[id] = error.Message;
            throw;
        }
    }

    /// <summary>Restores only this instance's writes. Failed leases remain owned and retryable.</summary>
    public void RestoreAll()
    {
        CancelRequests();
        if (!HasRecoveryDebt) return;
        try
        {
            RequireSession();
            if (!EnsureBinding()) return;
            List<Exception> failures = [];
            // Closing converges towards original bytes; a successful restore is never re-patched
            // merely because a different operand failed to restore.
            foreach (var lease in _leases.Reverse().Where(l => l.Owned || l.Pending))
            {
                try { Restore(lease); _errors.Remove(lease.Definition.Feature); }
                catch (Exception ex) when (IsOperational(ex))
                { _errors[lease.Definition.Feature] = ex.Message; failures.Add(ex); }
            }
            if (failures.Count != 0) throw new AggregateException("AIN 补丁尚未完全恢复，请保留修改器并重试。", failures);
        }
        catch (Exception error) when (IsOperational(error))
        {
            foreach (var id in FeatureIds.Where(id => ForFeature(id).Any(l => l.Owned || l.Pending)))
                _errors[id] = error.Message;
            throw;
        }
    }

    /// <summary>Cancels intent and read-only locating without querying the process or dropping leases.</summary>
    public void CancelRequests()
    {
        _requested.Clear(); _scan = null;
        foreach (var lease in _leases) lease.Active = false;
    }

    private IEnumerable<Lease> ForFeature(FeatureId id) => _leases.Where(l => l.Definition.Feature == id);
    private static bool IsOperational(Exception ex) => ex is IOException or InvalidOperationException or
        UnauthorizedAccessException or ArgumentException or AggregateException or System.ComponentModel.Win32Exception;
    private static string ReadFingerprint(string path)
    {
        return GameConnection.ReadAinFingerprint(path);
    }
    private void VerifySource()
    {
        if (_verified) return;
        string fingerprint = _fingerprint();
        if (GameConnection.IsBridgeAinFingerprint(fingerprint))
            throw new InvalidOperationException("已安装组件的食券重抽和自动战斗重配必须通过组件开关控制，不修改运行中的脚本。");
        if (!GameConnection.IsSupportedAinFingerprint(fingerprint))
            throw new InvalidOperationException("此 Rance10.ain 不属于首版支持的中文 BugFix 版本，脚本功能未启用。");
        _codeLength = BugFixProfile.CodeLength;
        _verified = true;
    }
    private void RequireSession()
    {
        if (_memory.ProcessId != _pid || !_memory.IsAlive)
            throw new InvalidOperationException("AIN 补丁所属进程已变化；不允许将旧恢复责任转移到新进程。");
    }
    private uint At(uint pc)
    {
        if (_codeBase is not { } start) throw new InvalidOperationException("尚未定位 AIN CODE。");
        return checked(start + pc);
    }
    private void VerifyAnchors(uint start)
    {
        RequireSession();
        foreach (var anchor in BugFixProfile.Anchors)
            if (!_memory.Read(checked(start + anchor.Pc), anchor.Bytes.Length).AsSpan().SequenceEqual(anchor.Bytes))
                throw new InvalidOperationException("AIN CODE 身份已变化，已暂停脚本写入。");
    }
    private bool EnsureBinding(Func<bool>? shouldYield = null)
    {
        if (_codeBase is { } old)
        {
            try { VerifyAnchors(old); VerifyAnchors(old); return true; }
            catch (Exception ex) when (IsOperational(ex))
            {
                // Invalid anchors alone do not prove that our old patch bytes disappeared.
                if (HasRecoveryDebt && !EntireRangeFree(old))
                    throw new InvalidOperationException("旧 AIN CODE 的恢复责任仍在；无法确认整段地址已释放，已停止脚本写入。", ex);
                foreach (var lease in _leases) lease.Clear();
                _codeBase = null;
                _scan = null;
            }
        }
        if (_requested.Count == 0 && !HasRecoveryDebt) return false;
        LocateSlice(shouldYield);
        // Binding is read-only. Applying is deliberately a later worker turn, so a close/cancel
        // arriving during the last slice can still win before the first operand write.
        return false;
    }
    private bool EntireRangeFree(uint start)
    {
        RequireSession();
        var cursor = (ulong)start;
        var limit = cursor + _codeLength;
        if (limit > 0x1_0000_0000ul) return false;
        foreach (var region in _memory.QueryRegions().OrderBy(r => r.BaseAddress))
        {
            var end = (ulong)region.BaseAddress + region.Size;
            if (end <= cursor) continue;
            if (region.BaseAddress > cursor || end > 0x1_0000_0000ul || region.Size == 0 || region.State != 0x10000)
                return false;
            cursor = Math.Min(end, limit);
            if (cursor == limit) { RequireSession(); return true; }
        }
        return false;
    }
    private MemoryRegion[] ReadScanMap()
    {
        RequireSession();
        var regions = _memory.QueryRegions().OrderBy(r => r.BaseAddress).ToArray();
        ulong previousEnd = 0;
        foreach (var region in regions)
        {
            var end = (ulong)region.BaseAddress + region.Size;
            if (region.Size == 0 || end > 0x1_0000_0000ul || region.BaseAddress < previousEnd)
                throw new IOException("AIN 内存区域范围无效或重叠。");
            previousEnd = end;
        }
        RequireSession();
        return regions;
    }
    private byte[] ReadForScan(uint address, int length)
    {
        if (length > _scanByteBudget - LastScanReadBytes) throw new InvalidOperationException("AIN 扫描超出单片读取预算。");
        LastScanReadBytes += length;
        return _memory.Read(address, length);
    }
    private void VerifyScanCandidate(ulong match)
    {
        var first = BugFixProfile.Anchors[0];
        if (match <= first.Pc || match - first.Pc + _codeLength > 0x1_0000_0000ul)
            throw new InvalidOperationException("AIN CODE 候选超出 32 位地址空间。");
        RequireSession();
        uint start = (uint)(match - first.Pc);
        foreach (var anchor in BugFixProfile.Anchors)
            if (!ReadForScan(checked(start + anchor.Pc), anchor.Bytes.Length).AsSpan().SequenceEqual(anchor.Bytes))
                throw new InvalidOperationException("AIN CODE 在分段定位期间发生变化，已取消本次定位。");
        RequireSession();
    }
    private void LocateSlice(Func<bool>? shouldYield)
    {
        if (shouldYield?.Invoke() == true) return;
        long started = _scanClock();
        // QueryRegions and the first file hash are synchronous OS operations. The elapsed budget
        // is cooperative between reads, not a hard timeout or a promise that Win32 cannot block.
        var map = ReadScanMap();
        if (_scan == null)
        {
            _scan = new(); _scan.Reconcile(map); ScanLayoutChanges = 0;
        }
        else if (_scan.Reconcile(map)) ScanLayoutChanges++;
        var scan = _scan;
        var anchor = BugFixProfile.Anchors[0];
        if (shouldYield?.Invoke() == true) return;
        var unique = scan.UniqueMatch();
        if (unique is { } previousMatch) VerifyScanCandidate(previousMatch);
        int chunks = 0;
        // Reserve enough bytes for final anchor verification. Every ReadForScan call counts,
        // including overlaps and validation reads; a single slice never exceeds its byte budget.
        const int verificationReserve = 1024;
        while (scan.Pending.TryPeek(out var work))
        {
            if (shouldYield?.Invoke() == true || (chunks != 0 && _scanClock() - started >= _scanTimeBudget)) return;
            int available = _scanByteBudget - LastScanReadBytes - verificationReserve;
            if (available < anchor.Bytes.Length) return;
            var region = work.Region;
            if (region.Size - work.Offset < (uint)anchor.Bytes.Length)
            {
                work.Offset = region.Size; scan.Pending.Dequeue(); continue;
            }
            int count = (int)Math.Min((ulong)Math.Min(65536, available), region.Size - work.Offset);
            ulong cursor = region.BaseAddress + work.Offset;
            var buffer = ReadForScan((uint)cursor, count);
            chunks++;
            int search = 0;
            while (search <= buffer.Length - anchor.Bytes.Length)
            {
                int match = buffer.AsSpan(search).IndexOf(anchor.Bytes);
                if (match < 0) break;
                ulong address = cursor + (uint)(search + match);
                if (unique.HasValue && unique.Value != address)
                    throw new InvalidOperationException("AIN CODE 锚点存在多个候选；为避免写错位置，未启用脚本功能。");
                unique = work.Match = address;
                search += match + 1;
            }
            if (work.Offset + (uint)count == region.Size)
            { work.Offset = region.Size; scan.Pending.Dequeue(); }
            else work.Offset += (uint)(count - anchor.Bytes.Length + 1);
        }
        while (scan.PendingBoundaries.TryPeek(out var boundary))
        {
            if (shouldYield?.Invoke() == true || (chunks != 0 && _scanClock() - started >= _scanTimeBudget)) return;
            int count = checked((int)(boundary.End - boundary.Start));
            if (_scanByteBudget - LastScanReadBytes < count + verificationReserve) return;
            var buffer = new byte[count];
            foreach (var region in boundary.Regions)
            {
                ulong begin = Math.Max(boundary.Start, region.BaseAddress), end = Math.Min(boundary.End, (ulong)region.BaseAddress + region.Size);
                ReadForScan((uint)begin, checked((int)(end - begin))).CopyTo(buffer, checked((int)(begin - boundary.Start)));
            }
            chunks++;
            var matches = new List<ulong>(); int search = 0;
            while (search <= buffer.Length - anchor.Bytes.Length)
            {
                int hit = buffer.AsSpan(search).IndexOf(anchor.Bytes);
                if (hit < 0) break;
                ulong address = boundary.Start + (uint)(search + hit);
                if (address < boundary.Boundary && address + (uint)anchor.Bytes.Length > boundary.Boundary)
                {
                    if (unique.HasValue && unique.Value != address)
                        throw new InvalidOperationException("AIN CODE 锚点在相邻可读区域边界存在额外候选，未启用脚本功能。");
                    unique = address; matches.Add(address);
                }
                search += hit + 1;
            }
            boundary.Matches = matches.ToArray(); boundary.Complete = true; scan.PendingBoundaries.Dequeue();
        }
        if (shouldYield?.Invoke() == true) return;
        // Finish only against the same complete map, same process, and all current CODE anchors.
        var finalMap = ReadScanMap();
        if (scan.Reconcile(finalMap)) { ScanLayoutChanges++; return; }
        if (scan.UniqueMatch() is not { } finalMatch) throw new InvalidOperationException("尚未找到受支持的 AIN CODE，等待游戏载入。");
        VerifyScanCandidate(finalMatch);
        if (shouldYield?.Invoke() == true) return;
        _codeBase = (uint)(finalMatch - anchor.Pc);
        _scan = null;
    }

    private bool Classify(Lease lease)
    {
        var d = lease.Definition;
        var instruction = _memory.Read(At(d.InstructionPc), d.Original.Length);
        if (instruction.AsSpan().SequenceEqual(d.Original))
        {
            VerifyContext(d, instruction); lease.Clear(); return false;
        }
        if (instruction.AsSpan().SequenceEqual(d.Patched))
        {
            VerifyContext(d, instruction); lease.Active = true; return true;
        }
        lease.Active = false;
        throw new InvalidOperationException($"脚本指令与支持版本不符：{d.Name}。未覆盖未知字节。");
    }
    private void VerifyContext(PatchDefinition definition, ReadOnlySpan<byte> instruction)
    {
        var expected = definition.Window(instruction);
        if (!_memory.Read(At(definition.WindowPc), expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"脚本上下文已变化：{definition.Name}。");
        if (definition.ReturnPc != 0 && !_memory.Read(At(definition.ReturnPc), 2).AsSpan().SequenceEqual(new byte[] { 0x2f, 0 }))
            throw new InvalidOperationException($"脚本 RETURN 校验失败：{definition.Name}。");
    }
    private bool EnableFeature(FeatureId feature, Func<bool>? shouldYield)
    {
        if (shouldYield?.Invoke() == true) return false;
        var targets = ForFeature(feature).ToArray();
        foreach (var lease in targets) Classify(lease);
        List<Lease> written = [];
        try
        {
            foreach (var lease in targets)
            {
                if (Classify(lease)) continue; // An existing external patch never becomes our lease.
                // Yield before the first native write, including after any preflight reads. Once a
                // feature transaction has begun, finish/rollback it instead of creating a half feature.
                if (!Change(lease, lease.Definition.Original, lease.Definition.Patched, written.Count == 0 ? shouldYield : null)) return false;
                written.Add(lease);
            }
            foreach (var lease in targets)
                if (!Classify(lease))
                    throw new InvalidOperationException($"脚本功能未保持完整目标状态：{lease.Definition.Name}。");
            return true;
        }
        catch (Exception original) when (IsOperational(original))
        {
            List<Exception> errors = [original];
            foreach (var lease in written.AsEnumerable().Reverse())
                try { Restore(lease); }
                catch (Exception rollback) when (IsOperational(rollback)) { errors.Add(rollback); }
            foreach (var lease in targets) lease.Active = false;
            throw new AggregateException($"{FeatureCatalog.Get(feature).Label}未完整应用，已尝试回滚。", errors);
        }
    }
    private void RestoreFeature(FeatureId feature)
    {
        List<Exception> errors = [];
        foreach (var lease in ForFeature(feature).Reverse().Where(l => l.Owned || l.Pending))
            try { Restore(lease); }
            catch (Exception ex) when (IsOperational(ex)) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException($"{FeatureCatalog.Get(feature).Label}的恢复尚未完成。", errors);
    }
    private void Restore(Lease lease)
    {
        if (lease.Pending) { RecoverOriginal(lease, false); return; }
        if (!lease.Owned) return;
        if (!Classify(lease)) return;
        Change(lease, lease.Definition.Patched, lease.Definition.Original);
    }
    private bool Change(Lease lease, byte[] expected, byte[] replacement, Func<bool>? beforeFirstWrite = null)
    {
        var d = lease.Definition;
        VerifyAnchors(_codeBase!.Value);
        VerifyContext(d, expected);
        if (!_memory.Read(At(d.InstructionPc), expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"写入前脚本字节发生变化：{d.Name}。");
        if (beforeFirstWrite?.Invoke() == true) return false;
        lease.Owned = lease.Pending = true; lease.Observed = null; lease.Active = false;
        try
        {
            _memory.Write(At(d.InstructionPc + (uint)d.OperandOffset), replacement.AsSpan(d.OperandOffset, 4));
            if (!_memory.Read(At(d.InstructionPc), replacement.Length).AsSpan().SequenceEqual(replacement))
                throw new IOException($"脚本写后回读不一致：{d.Name}。");
            VerifyContext(d, replacement);
            VerifyAnchors(_codeBase.Value);
            lease.Pending = false; lease.Observed = null;
            lease.Owned = lease.Active = replacement.AsSpan().SequenceEqual(d.Patched);
            return true;
        }
        catch (Exception original) when (IsOperational(original))
        {
            try { RecoverOriginal(lease, true); }
            catch (Exception recovery) when (IsOperational(recovery))
            { throw new AggregateException($"{d.Name}写入失败，恢复责任已保留。", original, recovery); }
            throw new IOException($"{d.Name}写入失败，已恢复原始指令。", original);
        }
    }

    // Pending recovery accepts only an exact trusted observation, or a fully known original/patched
    // instruction. It never overwrites an arbitrary value merely because the opcode still matches.
    private void RecoverOriginal(Lease lease, bool captureAfterAttempt)
    {
        var d = lease.Definition;
        VerifyAnchors(_codeBase!.Value);
        var current = _memory.Read(At(d.InstructionPc), d.Original.Length);
        VerifyContext(d, current);
        if (current.AsSpan().SequenceEqual(d.Original)) { lease.Clear(); return; }
        if (!PlausiblePartial(d, current) ||
            (!captureAfterAttempt && !(lease.Observed is { } observed ? current.AsSpan().SequenceEqual(observed) : current.AsSpan().SequenceEqual(d.Patched))))
            throw new InvalidOperationException($"{d.Name}恢复时发现第三方或未知字节；保留责任，不覆盖冲突。");
        lease.Observed = current;
        lease.Owned = lease.Pending = true;
        try
        {
            // Recheck after context/identity reads, directly before the write.
            if (!_memory.Read(At(d.InstructionPc), current.Length).AsSpan().SequenceEqual(current))
                throw new InvalidOperationException($"{d.Name}恢复前字节再次变化。");
            _memory.Write(At(d.InstructionPc + (uint)d.OperandOffset), d.Original.AsSpan(d.OperandOffset, 4));
            if (!_memory.Read(At(d.InstructionPc), d.Original.Length).AsSpan().SequenceEqual(d.Original))
                throw new IOException($"{d.Name}恢复回读失败。");
            VerifyContext(d, d.Original);
            VerifyAnchors(_codeBase.Value);
            lease.Clear();
        }
        catch
        {
            // Keep the prior trusted observation if a new read is unavailable. A different arbitrary
            // value is never adopted for retry. A partial original write can be captured immediately.
            try
            {
                var after = _memory.Read(At(d.InstructionPc), d.Original.Length);
                if (PlausiblePartial(d, after)) { VerifyContext(d, after); lease.Observed = after; }
            }
            catch (Exception ex) when (IsOperational(ex)) { }
            throw;
        }
    }
    private static bool PlausiblePartial(PatchDefinition definition, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != definition.Original.Length) return false;
        for (var i = 0; i < bytes.Length; ++i)
            if (bytes[i] != definition.Original[i] && bytes[i] != definition.Patched[i]) return false;
        return true;
    }
}
