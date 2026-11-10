using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using R10.App.ViewModels;
using R10.Bridge.Install;
using R10.Bridge.Runtime;
using R10.Core;

namespace R10.App;

public partial class MainWindow : Window
{
    private readonly ITrainerRuntime _runtime;
    private readonly AppSettings _settings;
    private readonly bool _demo;
    private readonly MainViewModel _viewModel;
    private readonly GameSetupViewModel _gameSetup;
    private readonly Dictionary<string, ContentControl> _groups = new();
    private TaskCompletionSource<bool>? _confirmation;
    private IInputElement? _focusBeforeConfirmation;
    private bool _closing, _closed, _layoutWide;
    private string _layoutPage = "";
    public MainWindow(ITrainerRuntime runtime, AppSettings settings, bool demo)
    {
        _runtime = runtime; _settings = settings; _demo = demo;
        InitializeComponent();
        _gameSetup = new GameSetupViewModel(runtime as UnifiedTrainerRuntime,
            demo ? null : new BridgeInstaller(AppContext.BaseDirectory), settings, demo, ChooseGame);
        _viewModel = new MainViewModel(runtime, ConfirmStarAsync, _gameSetup, text => ConfirmAsync("修改角色★等级", text));
        DataContext = _viewModel;
        Title = demo ? "R10 修改器 · 演示" : "R10 修改器";
        foreach (var (key, group) in new[] { ("battle", _viewModel.Battle), ("strategy", _viewModel.Strategy), ("resources", _viewModel.Resources), ("growth", _viewModel.Growth) })
            _groups.Add(key, new ContentControl { Content = group, ContentTemplate = (DataTemplate)Resources["GroupTemplate"] });
        _viewModel.NavigationChanged += () => { ArrangeGroups(); ContentScroll.ScrollToTop(); };
        _runtime.SnapshotChanged += RuntimeSnapshotChanged;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Width = Math.Min(settings.Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(settings.Height, SystemParameters.WorkArea.Height);
        if (settings.Left is { } left && settings.Top is { } top)
        {
            // Recover a window saved on a display that is no longer connected.
            var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (visible.IntersectsWith(new Rect(left, top, Width, Height))) { WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top; }
        }
        if (settings.Maximized) WindowState = WindowState.Maximized;
        ArrangeGroups();
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.StartAsync();
            _viewModel.Update(_runtime.Current);
            await _gameSetup.InitializeAsync();
            _viewModel.SetFooter(_demo ? "未连接真实游戏" : "仅适配中文 BugFix 版");
        }
        catch (Exception ex) { _viewModel.SetError(ex.Message); }
        ArrangeGroups();
    }
    private void RuntimeSnapshotChanged(TrainerSnapshot snapshot)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess()) _viewModel.Update(snapshot);
        else _ = Dispatcher.BeginInvoke(() => { if (!_closed) _viewModel.Update(snapshot); });
    }
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewModel is null) return;
        var compact = ActualWidth < 760;
        _viewModel.CompactNavigation = compact;
        SidebarColumn.Width = new GridLength(compact ? 62 : 170);
        BrandLabel.Visibility = WorkspaceLabel.Visibility = ThemeLabel.Visibility = GameCaption.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        KeyboardHint.Visibility = ActualWidth < 900 ? Visibility.Collapsed : Visibility.Visible;
        ArrangeGroups();
    }
    private void ArrangeGroups()
    {
        if (_groups.Count == 0) return;
        var wide = ActualWidth - SidebarColumn.Width.Value - 75 >= 700 && _viewModel.Page == "all";
        if (_layoutPage == _viewModel.Page && _layoutWide == wide) return;
        _layoutPage = _viewModel.Page; _layoutWide = wide;
        LeftGroups.Children.Clear(); RightGroups.Children.Clear();
        GroupGap.Width = new GridLength(wide ? 30 : 0); RightColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        foreach (var (key, control) in _groups)
        {
            if (_viewModel.Page != "all" && _viewModel.Page != key) continue;
            if (wide && key is "resources" or "growth") RightGroups.Children.Add(control); else LeftGroups.Children.Add(control);
        }
    }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeManager.Current == "dark" ? "light" : "dark";
        ThemeManager.Apply(_settings.Theme);
    }
    private string? ChooseGame()
    {
        var picker = new OpenFileDialog
        {
            Title = "选择兰斯 10 的 Rance10.exe",
            Filter = "兰斯 10 (Rance10.exe)|Rance10.exe",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "Rance10.exe"
        };
        if (_settings.GameDirectory is { } directory && Directory.Exists(directory)) picker.InitialDirectory = directory;
        return picker.ShowDialog(this) == true ? picker.FileName : null;
    }
    private Task<bool> ConfirmStarAsync(decimal value) => ConfirmAsync("批量修改角色★等级",
        $"将已创建角色的★等级设为 {value}。这会清空经验，可能降低等级，并影响继承。");

    private Task<bool> ConfirmAsync(string title, string text)
    {
        if (_confirmation is not null) return Task.FromResult(false);
        _confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _focusBeforeConfirmation = Keyboard.FocusedElement;
        ConfirmationTitle.Text = title;
        ConfirmationText.Text = text;
        ConfirmationMode.Text = _demo ? "当前为演示模式，不会写入游戏。" : "请确认目标等级后应用。关闭功能不会撤销这次数值修改。";
        ApplicationSurface.IsEnabled = false;
        ConfirmationOverlay.Visibility = Visibility.Visible;
        CancelConfirmation.Focus();
        return _confirmation.Task;
    }
    private void EndConfirmation(bool confirmed)
    {
        var completion = _confirmation; _confirmation = null;
        ConfirmationOverlay.Visibility = Visibility.Collapsed; ApplicationSurface.IsEnabled = true;
        if (_focusBeforeConfirmation is not null) Keyboard.Focus(_focusBeforeConfirmation);
        completion?.TrySetResult(confirmed);
    }
    private void ConfirmStar(object sender, RoutedEventArgs e) => EndConfirmation(true);
    private void CancelStar(object sender, RoutedEventArgs e) => EndConfirmation(false);
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_confirmation is not null && e.Key == Key.Escape) { EndConfirmation(false); e.Handled = true; }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; EndConfirmation(false); ApplicationSurface.IsEnabled = false;
        _viewModel.SetFooter(_gameSetup.IsBusy ? "等待当前游戏文件操作完成后关闭…" : "正在停止功能并恢复本实例的脚本补丁…");
        try
        {
            await _gameSetup.PrepareToCloseAsync();
            var result = await _runtime.TryCloseAsync();
            if (!result.Success)
            {
                _viewModel.SetError("关闭未完成：" + (result.Error ?? "补丁恢复尚未完成。") + "\n窗口已保留，可再次关闭以重试。");
                _viewModel.SetFooter("恢复未完成，保留连接以便重试"); return;
            }
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _settings.Left = bounds.Left; _settings.Top = bounds.Top; _settings.Width = bounds.Width; _settings.Height = bounds.Height; _settings.Maximized = WindowState == WindowState.Maximized;
            try { _settings.Save(_demo); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Presentation settings must not prevent an already-restored session from closing. */ }
            _runtime.SnapshotChanged -= RuntimeSnapshotChanged;
            // A completed Task can resume inside the original Closing event. Defer the
            // final Close until WPF has left that event to avoid a reentrant-close error.
            _closed = true; _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex) { _viewModel.SetError("关闭未完成：" + ex.Message + "\n窗口已保留，可再次关闭以重试。"); }
        finally { _closing = false; ApplicationSurface.IsEnabled = true; if (!_closed) _gameSetup.ResumeAfterFailedClose(); }
    }
}
