using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using QuotaSight.Application;
using QuotaSight.Infrastructure;

namespace QuotaSight.UI;

public interface ITrayController
{
    void Show();
    void Refresh();
    void Exit();
}

public enum TrayCommand { Show, Refresh, Exit }

public sealed class TrayController : ITrayController
{
    private readonly Action showFull;
    private readonly Action showCompact;
    private readonly Action refresh;
    private readonly Action exit;
    private readonly Func<bool>? canPrimary;
    private bool refreshShowsFull;
    public bool IsNativeBackendAvailable { get; }
    public IReadOnlyList<TrayCommand> MenuCommands { get; } = [TrayCommand.Show, TrayCommand.Refresh, TrayCommand.Exit];
    public TrayController(Func<MainWindow?> window, Action exit, bool nativeBackendAvailable = false)
        : this(() => window()?.ShowFullWindow(), () => window()?.ShowFullWindow(), () => window()?.ShowFullWindow(), exit, nativeBackendAvailable) { refreshShowsFull = true; }
    public TrayController(Action showFull, Action showCompact, Action refresh, Action exit, bool nativeBackendAvailable = false, Func<bool>? canPrimary = null)
    {
        this.showFull = showFull; this.showCompact = showCompact; this.refresh = refresh; this.exit = exit; this.canPrimary = canPrimary; IsNativeBackendAvailable = nativeBackendAvailable;
    }
    public Action? RefreshAction { get; set; }
    public void PrimaryClick() { if (canPrimary?.Invoke() != false) showCompact(); }
    public void Show() => showFull();
    public void Refresh() { (RefreshAction ?? refresh)(); if (refreshShowsFull && RefreshAction is not null) showFull(); }
    public void Exit() => exit();
}

public sealed class TrayDelegateCommand(Action action) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

public partial class App : Avalonia.Application
{
    public static bool ExitRequested { get; private set; }
    public static void RequestExit() => ExitRequested = true;
    public TrayController? Tray { get; private set; }
    private Func<bool>? trayFactory = () => OperatingSystem.IsWindows();
    private bool trayFactoryOverrideSet;
    public Func<bool>? TrayFactory { get => trayFactory; set { trayFactory = value; trayFactoryOverrideSet = true; } }
    private bool? TrayFactoryOverride => trayFactoryOverrideSet ? trayFactory?.Invoke() : null;
    private TrayIcon? nativeTray;
    private RefreshScheduler? refreshScheduler;
    private CancellationTokenSource? refreshLifetime;
    private Task? refreshTask;
    private MainViewModel? trayViewModel;
    private NativeMenuItem? trayShowItem;
    private NativeMenuItem? trayRefreshItem;
    private NativeMenuItem? trayExitItem;
    private AppLifecycleCoordinator? lifecycle;
    private readonly AppStartupGuard startupGuard = new();
    private PropertyChangedEventHandler? trayLanguageChanged;
    private EventHandler? trayClicked;
    public TrayIcon? NativeTray => nativeTray;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();
            desktop.MainWindow = window;
            var compact = new CompactQuotaWindow(window.ViewModel);
            lifecycle = new AppLifecycleCoordinator(_ => CleanupAsync(window, compact).AsTask());
            window.OperationRunner = lifecycle.Run;
            compact.OpenFullRequested = window.ShowFullWindow;
            compact.IsExiting = () => lifecycle.IsExiting;
            compact.RefreshRequested = token => lifecycle.Run(token => window.ViewModel.RefreshAsync(token));
            trayViewModel = window.ViewModel;
            trayLanguageChanged = (_, args) => { if (args.PropertyName == nameof(MainViewModel.Language)) UpdateTrayCopy(); };
            trayViewModel.PropertyChanged += trayLanguageChanged;
            Tray = new TrayController(window.ShowFullWindow, compact.ShowOrActivate,
                () => lifecycle.Run(() => window.ViewModel.RefreshAsync()),
                () => _ = RequestExitAsync(desktop),
                nativeBackendAvailable: false,
                canPrimary: () => window.EffectiveResidentMode && window.TrayAvailable && !(lifecycle?.IsExiting ?? true));
            try
            {
                var menu = new NativeMenu();
                trayShowItem = new NativeMenuItem("Show") { Command = new TrayDelegateCommand(() => Tray.Show()) };
                trayRefreshItem = new NativeMenuItem("Refresh") { Command = new TrayDelegateCommand(() => Tray.Refresh()) };
                trayExitItem = new NativeMenuItem("Exit") { Command = new TrayDelegateCommand(() => Tray.Exit()) };
                menu.Items.Add(trayShowItem); menu.Items.Add(trayRefreshItem); menu.Items.Add(trayExitItem);
                UpdateTrayCopy();
                using var iconStream = AssetLoader.Open(new Uri("avares://QuotaSight.UI/Assets/quotasight-tray.png"));
                var icon = new WindowIcon(iconStream);
                nativeTray = new TrayIcon { ToolTipText = "QuotaSight", Menu = menu, Icon = icon, IsVisible = false };
                trayClicked = (_, _) => Tray.PrimaryClick();
                nativeTray.Clicked += trayClicked;
                TrayIcon.SetIcons(this, new TrayIcons { nativeTray });
            }
            catch { window.SetTrayCapability(false); }
            window.ResidentModeChanged = enabled => { if (nativeTray is not null) nativeTray.IsVisible = enabled && window.TrayAvailable; };
            window.ExitRequestedAsync = () => RequestExitAsync(desktop);
            desktop.ShutdownRequested += (_, args) =>
            {
                if (!ShutdownPolicy.ShouldCancelExternalRequest(lifecycle.IsExiting, lifecycle.IsCleanupCompleted)) return;
                args.Cancel = true;
                _ = RequestExitAsync(desktop);
            };
            desktop.Exit += (_, _) =>
            {
                ExitRequested = true;
                lifecycle.MarkExitWithoutCleanup();
                if (nativeTray is not null) nativeTray.IsVisible = false;
            };
            window.Opened += async (_, _) =>
            {
                try
                {
                    var startup = startupGuard.StartOnceAsync(async token =>
                    {
                        var initialization = window.InitializeAsync(token);
                        lifecycle.Track(initialization);
                        await initialization;
                        var probe = nativeTray is null ? Task.FromResult(false) : LinuxStatusNotifierAvailability.DetectAsync(nativeTray, token);
                        lifecycle.Track(probe);
                        var detected = await probe;
                        window.SetTrayCapability(LinuxStatusNotifierAvailability.ResolveOverride(TrayFactoryOverride, detected));
                        if (Tray is not null) Tray = new TrayController(window.ShowFullWindow, compact.ShowOrActivate,
                            () => lifecycle.Run(token => window.ViewModel.RefreshAsync(token)), () => _ = RequestExitAsync(desktop),
                            nativeBackendAvailable: window.TrayAvailable,
                            canPrimary: () => window.EffectiveResidentMode && window.TrayAvailable && !lifecycle.IsExiting);
                        if (nativeTray is not null) nativeTray.IsVisible = window.ViewModel.Settings.ResidentMode && window.TrayAvailable;
                        if (!lifecycle.IsExiting) StartRefreshScheduling(window);
                        if (OperatingSystem.IsLinux() && TrayFactoryOverride is null && nativeTray is not null)
                        {
                            var monitor = new LinuxStatusNotifierMonitor(
                                () => LinuxStatusNotifierAvailability.IsAvailableAsync(nativeTray, token),
                                available => Dispatcher.UIThread.Post(() => ApplyTrayAvailability(window, compact, available)));
                            lifecycle.Track(monitor.RunAsync(lifecycle.CancellationToken));
                        }
                        if (window.ViewModel.Settings.ResidentMode && !window.TrayAvailable)
                            window.ViewModel.NotifyLocalized(copy => (copy.TrayUnavailableTitle, copy.TrayUnavailable));
                    }, lifecycle.CancellationToken);
                    lifecycle.Track(startup);
                    await startup;
                }
                catch { if (!lifecycle.IsExiting && window.ViewModel.Settings.ResidentMode) window.ViewModel.NotifyLocalized(copy => (copy.TrayUnavailableTitle, copy.TrayUnavailable)); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void UpdateTrayCopy()
    {
        if (trayViewModel is null) return;
        var copy = trayViewModel.CopyText;
        if (trayShowItem is not null) trayShowItem.Header = copy.OpenFullWindow;
        if (trayRefreshItem is not null) trayRefreshItem.Header = copy.Refresh;
        if (trayExitItem is not null) trayExitItem.Header = copy.IsJapanese ? "終了" : "Exit";
    }

    private void StartRefreshScheduling(MainWindow window)
    {
        if (refreshScheduler is not null || lifecycle?.IsExiting == true) return;
        var currentLifecycle = lifecycle ?? throw new InvalidOperationException("Lifecycle is not initialized.");
        refreshScheduler = new RefreshScheduler(() => TimeSpan.FromMinutes(window.ViewModel.Settings.RefreshMinutes));
        refreshLifetime = AppLifecycleCoordinator.CreateLinkedLifetime(currentLifecycle.CancellationToken);
        refreshTask = refreshScheduler.RunAsync(token => new ValueTask(window.ViewModel.RefreshAsync(RefreshOrigin.Scheduled, token)), refreshLifetime.Token).AsTask();
        currentLifecycle.Track(refreshTask);
    }

    private async Task RequestExitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (lifecycle is null) { desktop.Shutdown(); return; }
        ExitRequested = true;
        await lifecycle.BeginExitAsync();
        desktop.Shutdown();
    }

    private async ValueTask CleanupAsync(MainWindow window, CompactQuotaWindow compact)
    {
        var lifetime = AppLifecycleCoordinator.DetachLifetime(ref refreshLifetime);
        lifetime?.Cancel();
        refreshScheduler?.Dispose();
        lifetime?.Dispose();
        if (trayViewModel is not null && trayLanguageChanged is not null) trayViewModel.PropertyChanged -= trayLanguageChanged;
        if (nativeTray is not null)
        {
            nativeTray.IsVisible = false;
            if (trayClicked is not null) nativeTray.Clicked -= trayClicked;
        }
        TrayIcon.SetIcons(this, null);
        window.SetExiting();
        compact.Release();
        window.ShutdownCleanup();
        window.ViewModel.Dispose();
        nativeTray = null;
        trayViewModel = null;
        trayLanguageChanged = null;
        trayClicked = null;
        trayShowItem = null;
        trayRefreshItem = null;
        trayExitItem = null;
        await ValueTask.CompletedTask;
    }

    private void ApplyTrayAvailability(MainWindow window, CompactQuotaWindow compact, bool available)
    {
        if (lifecycle?.IsExiting == true) return;
        window.SetTrayCapability(available);
        if (nativeTray is not null) nativeTray.IsVisible = window.ViewModel.Settings.ResidentMode && available;
        window.ApplyTrayAvailability(available, compact);
    }
}
