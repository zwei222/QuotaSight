using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace QuotaSight.UI;

/// <summary>
/// The compact window the tray's primary click opens: a quick look at the observed
/// usage the usage API reported, with today's and the seven-day totals per selected
/// provider. No ring, no gauge and no threshold — an observation surface only.
/// </summary>
public partial class CompactQuotaWindow : Window
{
    public MainViewModel ViewModel { get; }
    public Action? OpenFullRequested { get; set; }
    public Func<CancellationToken, Task>? RefreshRequested { get; set; }
    public Func<bool>? IsExiting { get; set; }
    private bool terminalClose;

    public CompactQuotaWindow() : this(new MainViewModel()) { }

    public CompactQuotaWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, args) => { if (terminalClose || IsExiting?.Invoke() == true) return; args.Cancel = true; Hide(); };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    public void ShowOrActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    private async void RefreshClick(object? sender, RoutedEventArgs e)
    {
        if (IsExiting?.Invoke() == true) return;
        try { await (RefreshRequested?.Invoke(CancellationToken.None) ?? ViewModel.RefreshTokScaleAsync(force: true)); }
        catch { }
    }
    private void OpenFullClick(object? sender, RoutedEventArgs e) { if (IsExiting?.Invoke() == true) return; OpenFullRequested?.Invoke(); }
    public void Release() { terminalClose = true; Close(); }
}
