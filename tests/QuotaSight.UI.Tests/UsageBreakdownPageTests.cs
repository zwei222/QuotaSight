using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using QuotaSight.UI;
using Xunit;

namespace QuotaSight.UI.Tests;

/// <summary>
/// Headless behavior of the usage page's breakdown card: rows, the dimension switcher,
/// the expanded components, the loading suppression, and the failure banner.
/// </summary>
public sealed class UsageBreakdownPageTests
{
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly To = new(2026, 11, 7);

    [AvaloniaFact]
    public async Task TheCardRendersRowsTheSwitcherAndTheFixedNote()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        // Two reads are consumed by the dashboard's startup refresh (today, week); the
        // third is the usage page's own breakdown read.
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown(
            [Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2), Part("codex", "openai", "m2", 1, 0, 0, 0, 0)],
            [Cov(41, 41)],
            [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await window.InitializeAsync();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            var rows = window.FindControl<ItemsControl>("BreakdownRows")!;
            Assert.Equal(2, viewModel.BreakdownRows.Count);
            Assert.True(window.FindControl<TextBlock>("UsageBreakdownNote")!.IsVisible);
            Assert.Contains("TokScale", window.FindControl<TextBlock>("UsageBreakdownNote")!.Text, StringComparison.Ordinal);
            var switcher = window.FindControl<ComboBox>("BreakdownDimensionBox")!;
            Assert.True(switcher.IsEnabled);
            Assert.False(window.FindControl<ProgressBar>("UsageBreakdownLoadingBar")!.IsVisible);

            // Switching through the real control regroups the rows without a read.
            switcher.SelectedItem = ((IReadOnlyList<LocalizedChoice<UsageBreakdownDimension>>)switcher.ItemsSource!).Single(choice => choice.Value == UsageBreakdownDimension.Provider);
            window.UpdateLayout();
            Assert.Equal(2, viewModel.BreakdownRows.Count);
            Assert.Equal("anthropic", viewModel.BreakdownRows[0].DisplayName);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ExpandingARowRevealsTheFiveComponents()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        port.Breakdown.Enqueue(() => Breakdown(
            [Part("claude", "anthropic", "claude-sonnet-5-5[1m]", 10, 20, 5, 3, 2)],
            [Cov(40, 40)],
            [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            var rows = window.FindControl<ItemsControl>("BreakdownRows")!;
            var expander = FindExpander(rows);
            Assert.NotNull(expander);
            expander!.IsExpanded = true;
            window.UpdateLayout();

            var partRows = FindDescendantItemsControl(expander, "PartRows") ?? FindItemsControlInContent(expander);
            Assert.NotNull(partRows);
            var copy = new UiCopy(UiLanguage.English);
            var model = Assert.Single(viewModel.BreakdownRows[0].PartRows);
            Assert.Equal($"{copy.UsageBreakdownComponentInput} 10", model.InputText);
            Assert.Equal($"{copy.UsageBreakdownComponentOutput} 20", model.OutputText);
            Assert.Equal($"{copy.UsageBreakdownComponentCacheRead} 5", model.CacheReadText);
            Assert.Equal($"{copy.UsageBreakdownComponentCacheWrite} 3", model.CacheWriteText);
            Assert.Equal($"{copy.UsageBreakdownComponentReasoning} 2", model.ReasoningText);
            Assert.Equal($"{copy.UsageBreakdownTotal} 40", model.TotalText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheLoadingStateShowsProgressAndDisablesTheSwitcher()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        port.Daily.Enqueue(() => new DailyUsage(From, To, [], 0));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            var loading = port.GateBreakdown();
            viewModel.NavigateToUsage();
            var pending = viewModel.BreakdownLoadTask;
            await viewModel.UsageLoadTask;
            window.UpdateLayout();

            Assert.True(window.FindControl<ProgressBar>("UsageBreakdownLoadingBar")!.IsVisible);
            Assert.False(window.FindControl<ComboBox>("BreakdownDimensionBox")!.IsEnabled);
            Assert.Equal("Loading", viewModel.BreakdownState);

            port.ReleaseBreakdown(Breakdown([], [], [Col("col-a", 1, null)]));
            await loading;
            await pending;
            window.UpdateLayout();
            Assert.False(window.FindControl<ProgressBar>("UsageBreakdownLoadingBar")!.IsVisible);
            Assert.True(window.FindControl<ComboBox>("BreakdownDimensionBox")!.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AnUnknownCollectorShowsAFailureBannerWithTheSettingsDetour()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        port.Daily.Enqueue(() => new DailyUsage(From, To, [], 0));
        port.Breakdown.Enqueue(() => throw new UsageReadException(UsageReadFailure.CollectorNotFound, "classified"));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            var panel = window.FindControl<Border>("UsageBreakdownFailurePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Contains("error", panel.Classes, StringComparer.Ordinal);
            Assert.True(window.FindControl<Button>("UsageBreakdownOpenSettingsButton")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ServerWithoutBreakdownSupportGetsNoSettingsDetour()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        port.Daily.Enqueue(() => new DailyUsage(From, To, [], 0));
        port.Breakdown.Enqueue(() => throw new UsageReadException(UsageReadFailure.NotFound, "classified"));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            Assert.Equal("Unsupported", viewModel.BreakdownState);
            var panel = window.FindControl<Border>("UsageBreakdownFailurePanel")!;
            Assert.True(panel.IsVisible);
            Assert.Contains("error", panel.Classes, StringComparer.Ordinal);
            Assert.False(window.FindControl<Button>("UsageBreakdownOpenSettingsButton")!.IsVisible);
            Assert.Equal(new UiCopy(UiLanguage.English).UsageBreakdownState("Unsupported"), window.FindControl<TextBlock>("UsageBreakdownStatus")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheObservationNoteWrapsInsideANarrowWindow()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "", "", 1, 0, 0, 0, 0)], [Cov(1, 1)], [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            window.Width = 420;
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            var note = window.FindControl<TextBlock>("UsageBreakdownNote")!;
            Assert.True(note.IsVisible);
            Assert.True(note.TextLayout!.TextLines.Count > 1, "The note must wrap instead of clipping at the card edge.");
            var widest = note.TextLayout.TextLines.Max(line => line.Width);
            Assert.True(widest <= note.Bounds.Width + 1, $"The note measures {widest:0.#}px but its element is {note.Bounds.Width:0.#}px wide.");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheDimensionSwitcherShowsTheSelectedDimensionOffersThreeChoicesAndIsNamed()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        // Two reads feed the dashboard startup (today and week); the third is the usage page.
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2)], [Cov(41, 41)], [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await window.InitializeAsync();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            var switcher = window.FindControl<ComboBox>("BreakdownDimensionBox")!;
            var choices = (IReadOnlyList<LocalizedChoice<UsageBreakdownDimension>>)switcher.ItemsSource!;
            Assert.Equal(3, choices.Count);
            // The selected item must be visible in the closed box, never blank.
            var selected = Assert.IsType<LocalizedChoice<UsageBreakdownDimension>>(switcher.SelectedItem);
            Assert.Equal(UsageBreakdownDimension.Client, selected.Value);
            Assert.Equal("Client", selected.DisplayName);
            Assert.Equal("Breakdown grouping", Avalonia.Automation.AutomationProperties.GetName(switcher));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheDimensionSwitcherFollowsTheLanguageOnTheWindow()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        // Two reads feed the dashboard startup (today and week); the third is the usage page.
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2)], [Cov(41, 41)], [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await window.InitializeAsync();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            await viewModel.SetLanguageAsync(UiLanguage.Japanese);
            window.UpdateLayout();

            var switcher = window.FindControl<ComboBox>("BreakdownDimensionBox")!;
            var selected = Assert.IsType<LocalizedChoice<UsageBreakdownDimension>>(switcher.SelectedItem);
            Assert.Equal(UsageBreakdownDimension.Client, selected.Value);
            Assert.Equal("クライアント", selected.DisplayName);
            Assert.Equal("内訳の集計単位", Avalonia.Automation.AutomationProperties.GetName(switcher));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheDimensionSwitcherSupportsKeyboardSelection()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        // Two reads feed the dashboard startup (today and week); the third is the usage page.
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown(
            [Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2), Part("codex", "openai", "m2", 1, 0, 0, 0, 0)],
            [Cov(41, 41)],
            [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await window.InitializeAsync();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            var switcher = window.FindControl<ComboBox>("BreakdownDimensionBox")!;
            switcher.Focus();
            switcher.IsDropDownOpen = true;
            window.UpdateLayout();
            for (var i = 0; i < 2; i++)
            {
                switcher.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Down });
            }

            switcher.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            window.UpdateLayout();

            Assert.False(switcher.IsDropDownOpen);
            var selected = Assert.IsType<LocalizedChoice<UsageBreakdownDimension>>(switcher.SelectedItem);
            Assert.Equal(UsageBreakdownDimension.Model, selected.Value);
            // Regrouping through the keyboard costs no read: the loaded parts regroup locally.
            Assert.Equal(2, viewModel.BreakdownRows.Count);
            Assert.Equal("m1", viewModel.BreakdownRows[0].DisplayName);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheRowHeaderSeparatesTheShareFromTheTotal()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        // Two reads feed the dashboard startup (today and week); the third is the usage page.
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "m1", 68, 51, 20, 4, 3)], [Cov(150, 150)], [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await window.InitializeAsync();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            var expander = FindExpander(window.FindControl<ItemsControl>("BreakdownRows")!)!;
            var headerTexts = expander.GetVisualDescendants().OfType<TextBlock>().ToList();
            var share = headerTexts.Single(text => text.Text!.Contains('%'));
            var total = headerTexts.Single(text => text.Text == viewModel.BreakdownRows[0].Total.ToString("N0", CultureInfo.InvariantCulture));
            Assert.True(
                total.Bounds.X >= share.Bounds.X + share.Bounds.Width + 8,
                $"The share '{share.Text}' ends at {share.Bounds.X + share.Bounds.Width:0.#} but the total '{total.Text}' starts at {total.Bounds.X:0.#}; a visible gap is required.");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheBreakdownRowsInheritTheCardSurfacesNotTheFluentChrome()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        // Two reads feed the dashboard startup (today and week); the third is the usage page.
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2)], [Cov(41, 41)], [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await window.InitializeAsync();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            window.UpdateLayout();

            var expander = FindExpander(window.FindControl<ItemsControl>("BreakdownRows")!);
            // A row must be on screen at all: without it the walk below proves nothing.
            Assert.NotNull(expander);
            // The Fluent Expander paints its own chrome (#CC-ish translucent header, opaque
            // content panel); the breakdown rows must read as rows of the usage card instead.
            var headerSurface = FindNamedBorder(expander, "ToggleButtonBackground");
            var contentSurface = FindNamedBorder(expander, "ExpanderContent");
            Assert.True(
                IsTransparent(headerSurface?.Background),
                $"The expander header paints its own background ({Describe(headerSurface?.Background)}) instead of the card's surface.");
            Assert.True(
                IsTransparent(contentSurface?.Background),
                $"The expander content paints its own background ({Describe(contentSurface?.Background)}) instead of the card's surface.");
        }
        finally { window.Close(); }
    }

    /// <summary>Null-safe template walk: the headless visual tree can contain null slots.</summary>
    private static Border? FindNamedBorder(Avalonia.Visual? root, string name)
    {
        if (root is not Border { } walked) return null;
        if (walked.Name == name) return walked;
        foreach (var child in walked.GetVisualChildren())
        {
            if (FindNamedBorder(child, name) is { } found) return found;
        }

        return null;
    }

    private static bool IsTransparent(Avalonia.Media.IBrush? brush) =>
        brush is null || (brush is Avalonia.Media.ISolidColorBrush color && color.Color.A == 0);

    private static string Describe(Avalonia.Media.IBrush? brush) =>
        brush is Avalonia.Media.ISolidColorBrush color ? $"#{color.Color.ToString().ToUpperInvariant()}" : brush?.ToString() ?? "null";

    [AvaloniaFact]
    public async Task TheBreakdownNoteAndStatusFollowTheLanguageOnTheWindow()
    {
        var port = new ScriptedDualReadPort();
        using var viewModel = new MainViewModel(new EmptyDashboardSource(), uiDispatcher: new ImmediateUiDispatcher(), usageReadPort: port);
        // Two reads feed the dashboard startup (today and week); the third is the usage page.
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([], [], [Col("col-a", 1, null)]));
        port.Breakdown.Enqueue(() => Breakdown([Part("claude", "anthropic", "m1", 10, 20, 5, 3, 2)], [Cov(41, 41)], [Col("col-a", 1, null)]));
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            await window.InitializeAsync();
            viewModel.NavigateToUsage();
            await viewModel.UsageLoadTask;
            await viewModel.BreakdownLoadTask;
            var note = window.FindControl<TextBlock>("UsageBreakdownNote")!;
            var status = window.FindControl<TextBlock>("UsageBreakdownStatus")!;
            Assert.Contains("30 days by default", note.Text, StringComparison.Ordinal);

            await viewModel.SetLanguageAsync(UiLanguage.Japanese);
            window.UpdateLayout();

            // The fixed note and the state sentence must follow the switch on screen,
            // not stay at the language the window was first bound in.
            Assert.Contains("既定30日", note.Text, StringComparison.Ordinal);
            Assert.Contains("表示しています", status.Text, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    }

    private static Avalonia.Controls.Expander? FindExpander(ItemsControl rows)
    {
        foreach (var container in rows.GetRealizedContainers())
        {
            if (container is Avalonia.Controls.Expander expander) return expander;
            if (container is Avalonia.Visual visual
                && visual.GetVisualDescendants().OfType<Avalonia.Controls.Expander>().FirstOrDefault() is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static ItemsControl? FindItemsControlInContent(Avalonia.Controls.Expander expander)
    {
        ItemsControl? found = null;
        void Walk(Avalonia.Visual node)
        {
            if (found is not null) return;
            if (node is ItemsControl control && node != expander) { found = control; return; }
            foreach (var child in node.GetVisualChildren()) Walk(child);
        }

        Walk(expander);
        return found;
    }

    private static ItemsControl? FindDescendantItemsControl(Avalonia.Visual root, string name)
    {
        if (root is ItemsControl control && (control.Name == name || string.IsNullOrEmpty(control.Name))) return null;
        foreach (var child in root.GetVisualChildren())
        {
            if (child is ItemsControl found && found.Name == name) return found;
            var deeper = FindDescendantItemsControl(child, name);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    private static UsageBreakdown Breakdown(UsagePart[] parts, UsageCoverage[] coverage, UsageCollectorReceipt[] collectors) =>
        new(From, To, parts, coverage, collectors);

    private static UsagePart Part(string client, string provider, string model, params long[] components)
    {
        long total = 0;
        foreach (var component in components) total += component;
        return new UsagePart(client, provider, model, components[0], components[1], components[2], components[3], components[4], total);
    }

    private static UsageCoverage Cov(long totalTokens, long attributedTokens, string date = "2026-11-01", string collectorId = "col-a") =>
        new(DateOnly.Parse(date, CultureInfo.InvariantCulture), collectorId, totalTokens, attributedTokens);

    private static UsageCollectorReceipt Col(string collectorId, long generation, string? lastReceivedAt) =>
        new(collectorId, generation, lastReceivedAt is null ? null : DateTimeOffset.Parse(lastReceivedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    private sealed class ScriptedDualReadPort : IUsageReadPort
    {
        public Queue<Func<DailyUsage>> Daily { get; } = new();
        public Queue<Func<UsageBreakdown>> Breakdown { get; } = new();
        private TaskCompletionSource<UsageBreakdown>? gate;

        public Task<DailyUsage> ReadDailyAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            // An unqueued daily read is an empty answer; the tests script what they assert on.
            if (!Daily.TryDequeue(out var next))
            {
                return Task.FromResult(new DailyUsage(from, to, [], 0));
            }

            try
            {
                return Task.FromResult(next());
            }
            catch (Exception failure)
            {
                return Task.FromException<DailyUsage>(failure);
            }
        }

        public Task<UsageBreakdown> ReadBreakdownAsync(DateOnly from, DateOnly to, string? collectorId, CancellationToken cancellationToken)
        {
            if (gate is not null) return gate.Task;
            if (!Breakdown.TryDequeue(out var next))
            {
                return Task.FromResult(new UsageBreakdown(from, to, [], [], []));
            }

            try
            {
                return Task.FromResult(next());
            }
            catch (Exception failure)
            {
                return Task.FromException<UsageBreakdown>(failure);
            }
        }

        public Task<UsageBreakdown> GateBreakdown()
        {
            gate = new TaskCompletionSource<UsageBreakdown>(TaskCreationOptions.RunContinuationsAsynchronously);
            return gate.Task;
        }

        public void ReleaseBreakdown(UsageBreakdown next) => gate!.TrySetResult(next);
    }
}
