using System.Diagnostics;
using System.IO;
using R10.Bridge.Install;
using R10.Bridge.Runtime;
using R10.Core;

namespace R10.App.ViewModels;

public sealed class GameSetupViewModel : ObservableObject
{
    private readonly UnifiedTrainerRuntime? _runtime;
    private readonly BridgeInstaller? _installer;
    private readonly AppSettings _settings;
    private readonly bool _demo;
    private readonly Func<string?> _chooseGame;
    private BridgeInstallStatus? _installation;
    private string? _directory;
    private string _message, _error = "";
    private bool _busy, _stopping, _expanded, _started, _lastRunning;
    private int? _processId;
    private Task _pending = Task.CompletedTask;
    private Process? _launchedProcess;

    public GameSetupViewModel(UnifiedTrainerRuntime? runtime, BridgeInstaller? installer,
        AppSettings settings, bool demo, Func<string?> chooseGame)
    {
        _runtime = runtime; _installer = installer; _settings = settings; _demo = demo; _chooseGame = chooseGame;
        _directory = demo ? null : runtime?.GameDirectory ?? settings.GameDirectory;
        _expanded = !demo && _directory is null;
        _message = demo ? "演示数据 · 不会连接或启动真实游戏。" : "选择 Rance10.exe，安装游戏组件后即可启动游戏。";
        SelectCommand = new AsyncCommand(() => RunAsync(SelectAsync), () => CanSelect);
        InstallCommand = new AsyncCommand(() => RunAsync(() => ChangeInstallationAsync(false)), () => CanInstall);
        RestoreCommand = new AsyncCommand(() => RunAsync(() => ChangeInstallationAsync(true)), () => CanRestore);
        LaunchCommand = new AsyncCommand(() => RunAsync(LaunchAsync), () => CanLaunch);
        RefreshCommand = new AsyncCommand(() => RunAsync(InspectAsync), () => CanRefresh);
    }

    public string DirectoryLabel => _directory ?? (_demo ? "演示模式" : "尚未选择游戏");
    public string Summary => _busy ? "正在处理…" : _demo ? "演示模式" : _directory is null ? "选择游戏以开始" :
        _installation?.State switch
        {
            BridgeInstallState.Installed => "游戏组件已安装",
            BridgeInstallState.NotInstalled => "游戏组件未安装",
            BridgeInstallState.RecoveryRequired => "游戏文件需要恢复",
            BridgeInstallState.Unsupported => "当前版本不受支持",
            BridgeInstallState.Error => "暂时无法检查游戏",
            _ => HasError ? "暂时无法检查游戏" : "正在检查游戏"
        };
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Error { get => _error; private set { if (Set(ref _error, value)) Changed(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool IsBusy => _busy;
    public bool CanUseFeatures => !_busy && !_stopping;
    public bool CanSelect => !_demo && !_busy && !_stopping;
    public bool CanRefresh => CanSelect && _directory is not null;
    public bool CanInstall => CanRefresh && _installation?.CanInstall == true;
    public bool CanRestore => CanRefresh && _installation?.CanRestore == true;
    public bool ShowRestore => _installation is { IsInstalled: true } or { CanRestore: true } or { State: BridgeInstallState.RecoveryRequired };
    public bool CanLaunch => CanRefresh && !GameRunning && _installation?.State is BridgeInstallState.Installed or BridgeInstallState.NotInstalled;
    public bool GameRunning
    {
        get
        {
            if (_processId.HasValue) return true;
            try { return _launchedProcess is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
    }
    public AsyncCommand SelectCommand { get; }
    public AsyncCommand InstallCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public AsyncCommand LaunchCommand { get; }
    public AsyncCommand RefreshCommand { get; }

    public async Task InitializeAsync()
    {
        if (_started) return;
        _started = true;
        if (!_demo && _directory is not null) await RunAsync(InspectAsync);
    }

    public void Observe(TrainerSnapshot snapshot)
    {
        var changed = _processId != snapshot.ProcessId;
        _processId = snapshot.ProcessId;
        var running = GameRunning;
        if (!changed && running == _lastRunning) return;
        _lastRunning = running;
        RefreshProperties();
        if (_started && CanRefresh) _ = RunAsync(InspectAsync);
    }

    public async Task PrepareToCloseAsync()
    {
        _stopping = true; RefreshProperties();
        await _pending;
        _launchedProcess?.Dispose(); _launchedProcess = null;
    }

    public void ResumeAfterFailedClose() { _stopping = false; RefreshProperties(); }

    private Task RunAsync(Func<Task> action)
    {
        if (_busy || _stopping || _demo) return Task.CompletedTask;
        _busy = true; Error = ""; RefreshProperties();
        _pending = RunCoreAsync(action);
        return _pending;
    }

    private async Task RunCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { Error = error.Message; IsExpanded = true; }
        finally { _busy = false; RefreshProperties(); }
    }

    private async Task SelectAsync()
    {
        var executable = _chooseGame();
        if (executable is null) return;
        if (!string.Equals(Path.GetFileName(executable), "Rance10.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择游戏目录中的 Rance10.exe。");
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var result = await _runtime!.SelectGameAsync(directory);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "切换游戏未完成，请重试。");
        _directory = _runtime.GameDirectory;
        _processId = _runtime.Current.ProcessId;
        _launchedProcess?.Dispose(); _launchedProcess = null;
        _settings.GameDirectory = _directory;
        _installation = null; IsExpanded = true;
        await InspectAsync();
        try { _settings.Save(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Error = "游戏已选择，但设置暂未保存：" + error.Message; }
    }

    private async Task InspectAsync()
    {
        if (_directory is null) return;
        _installation = null;
        _installation = await _installer!.InspectAsync(_directory);
        Message = _installation.Message;
    }

    private async Task ChangeInstallationAsync(bool restore)
    {
        if (_directory is null) return;
        var selected = _directory;
        Message = restore ? "正在停止修改并恢复游戏文件…" : "正在停止修改，准备备份并安装游戏组件…";
        var disconnected = await _runtime!.SelectGameAsync(null);
        if (!disconnected.Success)
            throw new InvalidOperationException(disconnected.Error ?? "无法停止当前修改，请先重试关闭功能。");
        Exception? failure = null;
        try
        {
            _installation = restore ? await _installer!.RestoreAsync(selected) : await _installer!.InstallAsync(selected);
            Message = _installation.Message;
        }
        catch (Exception error) { failure = error; }
        finally
        {
            var reconnected = await _runtime.SelectGameAsync(selected);
            _directory = _runtime.GameDirectory ?? selected;
            _processId = _runtime.Current.ProcessId;
            if (!reconnected.Success)
                failure = new InvalidOperationException((failure is null ? "" : failure.Message + "\n") +
                    "游戏文件操作已结束，但重新连接未完成：" + reconnected.Error);
            try { await InspectAsync(); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) throw failure;
    }

    private async Task LaunchAsync()
    {
        if (_directory is null) return;
        await InspectAsync();
        if (_installation?.State is not (BridgeInstallState.Installed or BridgeInstallState.NotInstalled))
            throw new InvalidOperationException("请先完成游戏文件检查或恢复，再启动游戏。");
        var executable = Path.Combine(_directory, "Rance10.exe");
        foreach (var process in Process.GetProcessesByName("Rance10"))
        {
            using (process)
            {
                if (process.HasExited) continue;
                string? runningPath;
                try { runningPath = process.MainModule?.FileName; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                { throw new InvalidOperationException("无法确认现有游戏进程，请先退出游戏后再启动。", error); }
                if (string.Equals(runningPath, executable, StringComparison.OrdinalIgnoreCase))
                { Message = "游戏已经运行，可直接使用下方功能。"; return; }
            }
        }
        _launchedProcess?.Dispose();
        _launchedProcess = Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = _directory, UseShellExecute = true })
            ?? throw new InvalidOperationException("未能启动游戏，请检查游戏目录与访问权限。");
        Message = "游戏已启动，正在等待游戏数据就绪。";
    }

    private void RefreshProperties()
    {
        foreach (var name in new[] { nameof(DirectoryLabel), nameof(Summary), nameof(IsBusy), nameof(CanUseFeatures), nameof(CanSelect),
                     nameof(CanRefresh), nameof(CanInstall), nameof(CanRestore), nameof(ShowRestore), nameof(CanLaunch), nameof(GameRunning) }) Changed(name);
        SelectCommand?.Refresh(); InstallCommand?.Refresh(); RestoreCommand?.Refresh(); LaunchCommand?.Refresh(); RefreshCommand?.Refresh();
    }
}
