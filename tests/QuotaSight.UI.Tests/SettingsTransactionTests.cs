using QuotaSight.Core;
using QuotaSight.Infrastructure;
using Xunit;

namespace QuotaSight.UI.Tests;

public sealed class SettingsTransactionTests
{
    [Fact]
    public async Task Failed_language_save_does_not_publish_candidate_settings()
    {
        using var vm = new MainViewModel(new DemoDashboardSource(), settingsStore: new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var cardsBefore = vm.Cards.ToArray();
        var historyBefore = vm.History.Entries.ToArray();
        var saveCalls = 0;
        vm.SettingsSaveAsyncForTests = (_, _) =>
        {
            saveCalls++;
            return ValueTask.FromException(new IOException("save failed"));
        };

        await Assert.ThrowsAsync<IOException>(() => vm.SetLanguageAsync(UiLanguage.Japanese));

        Assert.Equal(UiLanguage.English, vm.Language);
        Assert.Equal(cardsBefore, vm.Cards);
        Assert.Equal(historyBefore, vm.History.Entries);
        Assert.Equal(UiLanguage.English, vm.History.Language);
        Assert.Equal(1, saveCalls);
    }

    [Fact]
    public async Task Pre_cancelled_settings_update_does_not_publish_or_save()
    {
        using var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var saveCalls = 0;
        vm.SettingsSaveAsyncForTests = (_, _) =>
        {
            saveCalls++;
            return ValueTask.CompletedTask;
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.SetLanguageAsync(UiLanguage.Japanese, cancellation.Token));

        Assert.Equal(UiLanguage.English, vm.Language);
        Assert.Equal(0, saveCalls);
    }

    [Fact]
    public async Task Queued_update_after_disposal_does_not_save_or_publish()
    {
        using var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var firstSaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCalls = 0;
        vm.SettingsSaveAsyncForTests = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref saveCalls) == 1)
            {
                firstSaveEntered.TrySetResult();
                await releaseFirstSave.Task.WaitAsync(cancellationToken);
            }
        };

        var first = vm.SetThemeAsync(ThemeMode.Dark);
        await firstSaveEntered.Task;
        var queued = vm.SetLanguageAsync(UiLanguage.Japanese);
        vm.Dispose();
        releaseFirstSave.TrySetResult();

        await first;
        await queued;

        Assert.Equal(UiLanguage.English, vm.Language);
        Assert.Equal(1, saveCalls);
    }

    [Fact]
    public async Task In_flight_update_after_disposal_does_not_publish_candidate()
    {
        using var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SettingsSaveAsyncForTests = async (_, cancellationToken) =>
        {
            saveEntered.TrySetResult();
            await releaseSave.Task.WaitAsync(cancellationToken);
        };

        var update = vm.SetLanguageAsync(UiLanguage.Japanese);
        await saveEntered.Task;
        vm.Dispose();
        releaseSave.TrySetResult();
        await update;

        Assert.Equal(UiLanguage.English, vm.Language);
    }

    [Fact]
    public async Task Reference_and_language_writes_preserve_both_changes_on_disk_and_live_state()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(root);
            using var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: store);
            var firstSaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = 0;
            vm.SettingsSaveAsyncForTests = async (settings, cancellationToken) =>
            {
                if (Interlocked.Increment(ref first) == 1)
                {
                    firstSaveEntered.TrySetResult();
                    await releaseFirstSave.Task.WaitAsync(cancellationToken);
                }
                await store.SaveAsync(settings, cancellationToken);
            };

            var referenceSave = vm.SetCopilotReferenceAsync("2000");
            await firstSaveEntered.Task;
            var languageSave = vm.SetLanguageAsync(UiLanguage.Japanese);
            releaseFirstSave.TrySetResult();

            Assert.True(await referenceSave);
            await languageSave;

            var persisted = await store.LoadAsync();
            Assert.Equal(2000m, persisted.CopilotReferenceCredits);
            Assert.Equal("Japanese", persisted.Language);
            Assert.Equal(2000m, vm.Settings.CopilotReferenceCredits);
            Assert.Equal(UiLanguage.Japanese, vm.Language);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
