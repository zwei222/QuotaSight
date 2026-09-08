using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
    private readonly Func<MainWindow?> window;
    private readonly Action exit;
    public Action? RefreshAction { get; set; }
    public bool IsNativeBackendAvailable => nativeBackendAvailable;
    private readonly bool nativeBackendAvailable;
    public IReadOnlyList<TrayCommand> MenuCommands { get; } = [TrayCommand.Show, TrayCommand.Refresh, TrayCommand.Exit];
    public TrayController(Func<MainWindow?> window, Action exit, bool nativeBackendAvailable = false)
    {
        this.window = window; this.exit = exit; this.nativeBackendAvailable = nativeBackendAvailable;
    }
    public void Show() => window()?.Show();
    public void Refresh() { if (RefreshAction is not null) RefreshAction(); window()?.Show(); }
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
    public TrayController? Tray { get; private set; }
    public Func<bool>? TrayFactory { get; set; } = () => true;
    private TrayIcon? nativeTray;
    private RefreshScheduler? refreshScheduler;
    private CancellationTokenSource? refreshLifetime;
    private Task? refreshTask;
    private MainViewModel? trayViewModel;
    private NativeMenuItem? trayShowItem;
    private NativeMenuItem? trayRefreshItem;
    private NativeMenuItem? trayExitItem;
    public TrayIcon? NativeTray => nativeTray;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            trayViewModel = window.ViewModel;
            trayViewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.Language)) UpdateTrayCopy(); };
            window.Opened += async (_, _) =>
            {
                await window.InitializeAsync();
                StartRefreshScheduling(window);
            };
            Tray = new TrayController(() => desktop.MainWindow as MainWindow, () =>
            {
                ExitRequested = true;
                desktop.Shutdown();
            });
            try
            {
                var menu = new NativeMenu();
                trayShowItem = new NativeMenuItem("Show") { Command = new TrayDelegateCommand(() => Tray.Show()) };
                trayRefreshItem = new NativeMenuItem("Refresh") { Command = new TrayDelegateCommand(() => Tray.Refresh()) };
                trayExitItem = new NativeMenuItem("Exit") { Command = new TrayDelegateCommand(() => Tray.Exit()) };
                menu.Items.Add(trayShowItem); menu.Items.Add(trayRefreshItem); menu.Items.Add(trayExitItem);
                UpdateTrayCopy();
                nativeTray = new TrayIcon { ToolTipText = "QuotaSight", Menu = menu };
                TrayIcon.SetIcons(this, new TrayIcons { nativeTray });
                window.TrayAvailable = Tray.IsNativeBackendAvailable;
            }
            catch { window.TrayAvailable = false; window.ViewModel.Notify(window.ViewModel.CopyText.TrayUnavailableTitle, window.ViewModel.CopyText.TrayUnavailable); }
            desktop.Exit += (_, _) => { refreshLifetime?.Cancel(); refreshScheduler?.Dispose(); refreshLifetime?.Dispose(); window.ViewModel.Dispose(); nativeTray?.Dispose(); nativeTray = null; ExitRequested = true; };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void UpdateTrayCopy()
    {
        if (trayViewModel is null) return;
        var copy = trayViewModel.CopyText;
        if (trayShowItem is not null) trayShowItem.Header = copy.Dashboard;
        if (trayRefreshItem is not null) trayRefreshItem.Header = copy.Refresh;
        if (trayExitItem is not null) trayExitItem.Header = copy.IsJapanese ? "終了" : "Exit";
    }

    private void StartRefreshScheduling(MainWindow window)
    {
        if (refreshScheduler is not null) return;
        refreshScheduler = new RefreshScheduler(() => TimeSpan.FromMinutes(window.ViewModel.Settings.RefreshMinutes));
        refreshLifetime = new CancellationTokenSource();
        refreshTask = refreshScheduler.RunAsync(token => new ValueTask(window.ViewModel.RefreshAsync(token)), refreshLifetime.Token).AsTask();
    }
}
