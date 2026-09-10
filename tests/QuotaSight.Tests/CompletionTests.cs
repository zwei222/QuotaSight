using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using QuotaSight.Application;
using QuotaSight.Core;
using QuotaSight.Infrastructure;
using QuotaSight.UI;

namespace QuotaSight.Tests;

public sealed class CompletionTests
{
    private sealed class BoundedSingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (callbacks) callbacks.Enqueue((callback, state));
        }

        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (callbacks)
                {
                    if (callbacks.Count == 0) return;
                    work = callbacks.Dequeue();
                }

                work.Callback(work.State);
            }
        }
    }

    private sealed class FakeSecretRunner(int exitCode) : ISecretToolProcessRunner
    {
        public ValueTask<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? stdin, CancellationToken cancellationToken)
            => ValueTask.FromResult(new SecretToolResult(exitCode, string.Empty, "locked"));
    }

    [Fact]
    public async Task Linux_secret_store_exit_one_falls_back_to_session_value()
    {
        var primary = new LinuxSecretToolCredentialStore(new FakeSecretRunner(1));
        var fallback = new InMemoryCredentialStore();
        var store = new FallbackCredentialStore(primary, fallback);
        await store.SetAsync("key", "value", default);
        Assert.Equal("value", await store.GetAsync("key", default));
        Assert.Equal(CredentialStoreAvailability.Locked, store.Availability);
    }
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_mutex_policy_uses_explicit_non_inherited_sid_only_full_control_acl()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string sidValue = "S-1-5-21-111111111-222222222-333333333-1001";
        var security = WindowsCredentialMutexPolicy.CreateSecurityDescriptor(sidValue);
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));

        Assert.True(security.AreAccessRulesProtected);
        Assert.Single(rules);
        var rule = Assert.IsType<System.Security.AccessControl.MutexAccessRule>(rules[0]);
        Assert.Equal(sidValue, rule.IdentityReference.Value);
        Assert.Equal(System.Security.AccessControl.MutexRights.FullControl, rule.MutexRights);
        Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, rule.AccessControlType);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_mutex_acl_create_opens_existing_mutex_with_sid_only_acl()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var name = $"Global\\QuotaSight-acl-create-{Guid.NewGuid():N}";
        var security = WindowsCredentialMutexPolicy.CreateSecurityDescriptor(sid);

        using var first = MutexAcl.Create(false, name, out var firstCreated, security);
        using var second = MutexAcl.Create(false, name, out var secondCreated, security);

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.True(first.WaitOne(TimeSpan.FromSeconds(2)));
        first.ReleaseMutex();
        Assert.True(second.WaitOne(TimeSpan.FromSeconds(2)));
        second.ReleaseMutex();
    }

    [Fact]
    public async Task Windows_credentials_write_uses_target_and_utf16_blob()
    {
        var api = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(api);
        await store.SetAsync("alice", "secret", default);
        Assert.Equal("QuotaSight:alice", api.Target);
        Assert.Equal("secret", api.WrittenSecret);
        Assert.Equal(1u, api.Type); Assert.Equal(2u, api.Persist);
    }

    [Fact]
    public async Task Windows_credentials_classifies_corrupt_manifest_magic_as_unavailable()
    {
        var api = new FakeWindowsCredentialApi();
        api.StoredTargets["QuotaSight:corrupt-magic"] = Encoding.UTF8.GetBytes("QuotaSight.Generation.v1\n{}");
        api.StoredTargets["QuotaSight:corrupt-magic"][0] ^= 0x01;

        var store = new WindowsCredentialStore(api);
        Assert.Null(await store.GetAsync("corrupt-magic", default));
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
    }

    [Fact]
    public async Task Windows_credentials_reads_strict_valid_legacy_utf16le_blob()
    {
        var api = new FakeWindowsCredentialApi();
        api.StoredTargets["QuotaSight:legacy"] = Encoding.Unicode.GetBytes("legacy-value\0");

        Assert.Equal("legacy-value", await new WindowsCredentialStore(api).GetAsync("legacy", default));
    }

    [Fact]
    public async Task Windows_credentials_rejects_legacy_without_terminal_nul()
    {
        var api = new FakeWindowsCredentialApi();
        api.StoredTargets["QuotaSight:legacy-no-nul"] = Encoding.Unicode.GetBytes("legacy-value");

        var store = new WindowsCredentialStore(api);
        Assert.Null(await store.GetAsync("legacy-no-nul", default));
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
    }

    [Fact]
    public async Task Windows_credentials_rejects_truncated_magic_odd_length_and_invalid_surrogate()
    {
        var cases = new (string Account, byte[] Blob)[]
        {
            ("truncated-magic", Encoding.UTF8.GetBytes("QuotaSight.Generation.v1")),
            ("odd-length", [0, 0, 0]),
            ("invalid-surrogate", [0x00, 0xD8, 0x00, 0x00])
        };

        foreach (var item in cases)
        {
            var api = new FakeWindowsCredentialApi();
            api.StoredTargets[$"QuotaSight:{item.Account}"] = item.Blob;
            var store = new WindowsCredentialStore(api);
            Assert.Null(await store.GetAsync(item.Account, default));
            Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
        }
    }

    [Fact]
    public async Task Windows_credentials_recovers_orphans_from_failed_cleanup_on_next_remove()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        var store = new WindowsCredentialStore(api);
        await store.SetAsync("orphan-recovery", new string('o', 5000), default);
        api.FailDeleteTarget = api.StoredTargets.Keys.First(target => target.StartsWith("QuotaSight:chunk:", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync("orphan-recovery", "small", default).AsTask());
        api.FailDeleteTarget = null;

        await new WindowsCredentialStore(api).RemoveAsync("orphan-recovery", default);
        Assert.Empty(api.StoredTargets);
    }

    [Fact]
    public async Task Windows_credentials_enumeration_failure_fails_set_and_remove_safely()
    {
        var api = new FakeWindowsCredentialApi { EnumerationError = WindowsCredentialError.Other };
        var store = new WindowsCredentialStore(api);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync("enumeration-failure", "value", default).AsTask());
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync("enumeration-failure", default).AsTask());
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
        Assert.Empty(api.StoredTargets);
    }

    [Fact]
    public async Task Windows_enumeration_failure_reports_session_fallback_instead_of_secure_store()
    {
        var primary = new WindowsCredentialStore(new FakeWindowsCredentialApi { EnumerationError = WindowsCredentialError.Other });
        var fallback = new InMemoryCredentialStore();
        var store = new FallbackCredentialStore(primary, fallback);

        await store.SetAsync("session-fallback", "synthetic-secret", default);

        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
        Assert.Equal("synthetic-secret", await store.GetAsync("session-fallback", default));
    }

    [Fact]
    public async Task Windows_invalid_manifest_reports_session_fallback_instead_of_secure_store()
    {
        var api = new FakeWindowsCredentialApi();
        api.StoredTargets["QuotaSight:invalid-manifest"] = Encoding.UTF8.GetBytes("QuotaSight.Generation.v1\n{}");
        var primary = new WindowsCredentialStore(api);
        var fallback = new InMemoryCredentialStore();
        var store = new FallbackCredentialStore(primary, fallback);

        await store.SetAsync("invalid-manifest", "synthetic-secret", default);

        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
        Assert.Equal("synthetic-secret", await store.GetAsync("invalid-manifest", default));
    }

    [Fact]
    public async Task Windows_credentials_native_integration_roundtrips_on_windows_without_existing_targets()
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = "integration-" + Guid.NewGuid().ToString("N");
        var secret = string.Concat(Enumerable.Repeat("synthetic-sentinel-", 700));
        var store = WindowsCredentialStore.Create();
        try
        {
            await store.SetAsync(account, secret, default);
            Assert.Equal(secret, await WindowsCredentialStore.Create().GetAsync(account, default));
            await store.SetAsync(account, "small-synthetic-sentinel", default);
            Assert.Equal("small-synthetic-sentinel", await WindowsCredentialStore.Create().GetAsync(account, default));
        }
        finally
        {
            await WindowsCredentialStore.Create().RemoveAsync(account, default);
        }
        Assert.Null(await WindowsCredentialStore.Create().GetAsync(account, default));
    }

    [Fact]
    public async Task Windows_credentials_large_secret_roundtrips_after_new_store_instance()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        var secret = string.Concat(Enumerable.Repeat("😀漢字-token-", 700));
        await new WindowsCredentialStore(api).SetAsync("large", secret, default);

        Assert.True(api.StoredTargets.Count > 1);
        Assert.Equal(secret, await new WindowsCredentialStore(api).GetAsync("large", default));
    }

    [Fact]
    public async Task Windows_credentials_small_replacement_removes_previous_generation_chunks()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        var store = new WindowsCredentialStore(api);
        await store.SetAsync("replace-small", new string('o', 5000), default);
        await store.SetAsync("replace-small", "new-small", default);

        Assert.Equal("new-small", await new WindowsCredentialStore(api).GetAsync("replace-small", default));
        Assert.DoesNotContain(api.StoredTargets.Keys, target => target.StartsWith("QuotaSight:chunk:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windows_credentials_target_identity_is_case_insensitive_for_set_get_and_remove()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        await new WindowsCredentialStore(api).SetAsync("alice", new string('x', 5000), default);

        Assert.Equal(new string('x', 5000), await new WindowsCredentialStore(api).GetAsync("ALICE", default));
        await new WindowsCredentialStore(api).RemoveAsync("AlIcE", default);

        Assert.Empty(api.StoredTargets);
    }

    [Fact]
    public async Task Windows_credentials_cleanup_cancellation_does_not_remove_published_generation()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        var store = new WindowsCredentialStore(api);
        await store.SetAsync("replace-large", new string('o', 5000), default);
        using var cancellation = new CancellationTokenSource();
        api.CancelAfterRootWrite = cancellation;

        await store.SetAsync("replace-large", new string('n', 5000), cancellation.Token);

        Assert.Equal(new string('n', 5000), await new WindowsCredentialStore(api).GetAsync("replace-large", default));
    }

    [Fact]
    public async Task Windows_credentials_get_zeroes_every_managed_blob_returned_by_api()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        await new WindowsCredentialStore(api).SetAsync("zero", new string('z', 5000), default);
        api.ReturnedBlobs.Clear();

        Assert.Equal(new string('z', 5000), await new WindowsCredentialStore(api).GetAsync("zero", default));
        Assert.NotEmpty(api.ReturnedBlobs);
        Assert.All(api.ReturnedBlobs, blob => Assert.All(blob, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task Windows_credentials_shared_api_allows_concurrent_reads_from_multiple_stores()
    {
        var api = new FakeWindowsCredentialApi { DelayReads = true };
        await new WindowsCredentialStore(api).SetAsync("shared", "value", default);

        var results = await Task.WhenAll(
            new WindowsCredentialStore(api).GetAsync("shared", default).AsTask(),
            new WindowsCredentialStore(api).GetAsync("shared", default).AsTask());

        Assert.All(results, result => Assert.Equal("value", result));
        Assert.False(api.ConcurrentReadDetected);
    }

    [Fact]
    public async Task Windows_credentials_success_restores_secure_store_availability_after_transient_failure()
    {
        var api = new FakeWindowsCredentialApi { ReadError = WindowsCredentialError.Other };
        var store = new WindowsCredentialStore(api);
        Assert.Null(await store.GetAsync("recover", default));
        api.ReadError = WindowsCredentialError.None;
        await store.SetAsync("recover", "recovered", default);

        Assert.Equal(CredentialStoreAvailability.SecureStore, store.Availability);
        Assert.Equal("recovered", await store.GetAsync("recover", default));
    }

    [Theory]
    [InlineData(1198)]
    [InlineData(1200)]
    public async Task Windows_credentials_keeps_small_utf16_secret_legacy_and_chunks_at_boundary(int characterCount)
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        var secret = new string('a', characterCount);
        await new WindowsCredentialStore(api).SetAsync("boundary", secret, default);
        Assert.Equal(secret.Length > 1198, api.StoredTargets.Keys.Any(k => k.StartsWith("QuotaSight:chunk:", StringComparison.Ordinal)));
        Assert.Equal(secret, await new WindowsCredentialStore(api).GetAsync("boundary", default));
    }

    [Fact]
    public async Task Windows_credentials_tampered_or_missing_chunk_fails_closed_without_legacy_fallback()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        await new WindowsCredentialStore(api).SetAsync("tamper", new string('x', 5000), default);
        var chunk = api.StoredTargets.Keys.First(k => k.StartsWith("QuotaSight:chunk:", StringComparison.Ordinal));
        api.StoredTargets[chunk][0] ^= 0x01;
        var store = new WindowsCredentialStore(api);
        Assert.Null(await store.GetAsync("tamper", default));
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
        api.StoredTargets.Remove(chunk);
        Assert.Null(await store.GetAsync("tamper", default));
    }

    [Fact]
    public async Task Windows_credentials_root_publish_failure_preserves_old_value_and_cleans_new_chunks()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        var store = new WindowsCredentialStore(api);
        await store.SetAsync("replace", "old", default);
        api.FailWriteTarget = "QuotaSight:replace";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync("replace", new string('n', 5000), default).AsTask());
        api.FailWriteTarget = null;
        Assert.Equal("old", await new WindowsCredentialStore(api).GetAsync("replace", default));
        Assert.Single(api.StoredTargets);
    }

    [Fact]
    public async Task Windows_credentials_verification_failure_preserves_old_value_and_cleans_new_chunks()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        await new WindowsCredentialStore(api).SetAsync("verify-rollback", "old", default);
        api.FailVerificationReads = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsCredentialStore(api).SetAsync("verify-rollback", new string('n', 5000), default).AsTask());

        api.FailVerificationReads = false;
        Assert.Equal("old", await new WindowsCredentialStore(api).GetAsync("verify-rollback", default));
        Assert.Single(api.StoredTargets);
    }

    [Fact]
    public async Task Windows_credentials_cancellation_before_verification_preserves_old_value_and_cleans_new_chunks()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        await new WindowsCredentialStore(api).SetAsync("cancel-rollback", "old", default);
        using var cancellation = new CancellationTokenSource();
        api.CancelAfterChunkWrite = cancellation;

        await Assert.ThrowsAsync<OperationCanceledException>(() => new WindowsCredentialStore(api).SetAsync("cancel-rollback", new string('n', 5000), cancellation.Token).AsTask());

        Assert.Equal("old", await new WindowsCredentialStore(api).GetAsync("cancel-rollback", default));
        Assert.Single(api.StoredTargets);
    }

    [Fact]
    public async Task Windows_credentials_remove_deletes_root_and_every_chunk()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        await new WindowsCredentialStore(api).SetAsync("logout", new string('z', 5000), default);
        await new WindowsCredentialStore(api).RemoveAsync("logout", default);
        Assert.Empty(api.StoredTargets);
    }

    [Fact]
    public async Task Windows_credentials_concurrent_operations_are_serialized()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        var store = new WindowsCredentialStore(api);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.SetAsync("same", new string((char)('a' + i), 5000), default).AsTask()));
        Assert.NotNull(await store.GetAsync("same", default));
        Assert.Equal(1, api.StoredTargets.Keys.Count(k => k == "QuotaSight:same"));
    }

    [Fact]
    public async Task Windows_credentials_static_gate_blocks_second_task_while_first_sync_core_is_blocked()
    {
        var api = new FakeWindowsCredentialApi();
        await new WindowsCredentialStore(api).SetAsync("barrier", "value", default);
        api.BlockReads = true;
        api.ReadEntered.Reset();
        api.ReleaseReads.Reset();
        api.ReadEntries = 0;

        var first = Task.Run(() => new WindowsCredentialStore(api).GetAsync("barrier", default).AsTask());
        Assert.True(api.ReadEntered.Wait(TimeSpan.FromSeconds(2)));
        var second = Task.Run(() => new WindowsCredentialStore(api).GetAsync("barrier", default).AsTask());
        await Task.Delay(50);
        Assert.Equal(1, api.ReadEntries);
        api.ReleaseReads.Set();

        Assert.Equal("value", await first);
        Assert.Equal("value", await second);
        Assert.False(api.ConcurrentReadDetected);
    }

    [Fact]
    public async Task Windows_credentials_releases_named_mutex_for_another_task_thread()
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = "mutex-thread-" + Guid.NewGuid().ToString("N");
        var api = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(api);
        await store.SetAsync(account, "value", default);
        api.BlockReads = true;
        api.BlockReadTarget = "QuotaSight:" + account;
        api.ReadEntered.Reset();
        api.ReleaseReads.Reset();

        var operation = Task.Run(() => store.GetAsync(account, default).AsTask());
        try
        {
            Assert.True(api.ReadEntered.Wait(TimeSpan.FromSeconds(2)));
            var root = "QuotaSight:" + account.ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            using var mutex = MutexAcl.OpenExisting($"Global\\QuotaSight-{sid}-{hash}", WindowsCredentialMutexPolicy.RequiredRights);
            var security = mutex.GetAccessControl();
            var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.Single(rules.Cast<System.Security.AccessControl.MutexAccessRule>());
            Assert.Equal(sid, rule.IdentityReference.Value);
            Assert.Equal(System.Security.AccessControl.MutexRights.FullControl, rule.MutexRights);
            Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, rule.AccessControlType);
            api.ReleaseReads.Set();
            Assert.Equal("value", await operation);
            Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(2)));
            mutex.ReleaseMutex();
        }
        finally
        {
            api.ReleaseReads.Set();
            await operation;
            await store.RemoveAsync(account, default);
        }
    }

    [Fact]
    public async Task Windows_credentials_read_success_frees_native_pointer_and_zeroes_managed_blob()
    {
        var api = new FakeWindowsCredentialApi { ReadValue = "secret" };
        var store = new WindowsCredentialStore(api);
        Assert.Equal("secret", await store.GetAsync("alice", default));
        Assert.NotEmpty(api.ReturnedBlobs);
        Assert.All(api.ReturnedBlobs, blob => Assert.All(blob, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task Windows_credentials_not_found_is_null()
    {
        var api = new FakeWindowsCredentialApi { ReadError = WindowsCredentialError.NotFound };
        Assert.Null(await new WindowsCredentialStore(api).GetAsync("alice", default));
    }

    [Fact]
    public async Task Windows_credentials_error_is_unavailable_without_secret_logging()
    {
        var api = new FakeWindowsCredentialApi { ReadError = WindowsCredentialError.Other };
        var store = new WindowsCredentialStore(api);
        Assert.Null(await store.GetAsync("alice", default));
        Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
        Assert.DoesNotContain("secret", api.Log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Windows_credentials_delete_uses_target()
    {
        var api = new FakeWindowsCredentialApi();
        await new WindowsCredentialStore(api).RemoveAsync("alice", default);
        Assert.Equal("QuotaSight:alice", api.Target);
        Assert.True(api.DeleteCalled);
    }

    [Fact]
    public async Task Windows_credentials_delete_not_found_is_success_but_other_error_is_failure()
    {
        var notFound = new FakeWindowsCredentialApi { DeleteResult = (int)WindowsCredentialError.NotFound };
        await new WindowsCredentialStore(notFound).RemoveAsync("alice", default);

        var other = new FakeWindowsCredentialApi { DeleteResult = 5 };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsCredentialStore(other).RemoveAsync("alice", default).AsTask());
        Assert.Equal("Windows credential removal failed.", error.Message);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Windows_credentials_remove_attempts_root_after_chunk_failure_and_returns_first_failure()
    {
        var api = new FakeWindowsCredentialApi { MaxBlobBytes = 2560 };
        await new WindowsCredentialStore(api).SetAsync("remove-best-effort", new string('z', 5000), default);
        var chunk = api.StoredTargets.Keys.First(k => k.StartsWith("QuotaSight:chunk:", StringComparison.OrdinalIgnoreCase));
        api.FailDeleteTargets.Add(chunk);
        api.FailDeleteTargets.Add("QuotaSight:remove-best-effort");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsCredentialStore(api).RemoveAsync("REMOVE-BEST-EFFORT", default).AsTask());

        Assert.Equal("Windows credential removal failed.", error.Message);
        Assert.Contains(api.DeleteAttempts, target => string.Equals(target, "QuotaSight:remove-best-effort", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Linux_secret_store_delete_requires_zero_exit_and_hides_failures()
    {
        await new LinuxSecretToolCredentialStore(new FakeSecretRunner(0)).RemoveAsync("key", default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new LinuxSecretToolCredentialStore(new FakeSecretRunner(1)).RemoveAsync("key", default).AsTask());
        Assert.Equal("Linux credential removal failed.", error.Message);
        Assert.DoesNotContain("locked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fallback_get_prefers_session_value_and_primary_success_removes_stale_value()
    {
        var primary = new InMemoryCredentialStore();
        var fallback = new InMemoryCredentialStore();
        await primary.SetAsync("alice", "old", default);
        await fallback.SetAsync("alice", "new", default);
        var store = new FallbackCredentialStore(primary, fallback);

        Assert.Equal("new", await store.GetAsync("alice", default));
        await store.SetAsync("alice", "fresh", default);
        Assert.Equal("fresh", await primary.GetAsync("alice", default));
        Assert.Null(await fallback.GetAsync("alice", default));
    }

    [Fact]
    public async Task Fallback_remove_propagates_primary_failure_after_trying_fallback()
    {
        var primary = new FailingCredentialStore();
        var fallback = new RecordingRemoveStore();
        var store = new FallbackCredentialStore(primary, fallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync("alice", default).AsTask());
        Assert.True(fallback.RemoveCalled);
    }

    [Fact]
    public async Task Fallback_credential_store_keeps_secret_in_session_when_primary_write_fails()
    {
        var primary = new FailingCredentialStore(); var fallback = new InMemoryCredentialStore(); var store = new FallbackCredentialStore(primary, fallback);
        await store.SetAsync("alice", "secret", default); Assert.Equal("secret", await store.GetAsync("alice", default)); Assert.Equal(CredentialStoreAvailability.Unavailable, store.Availability);
    }

    [Fact]
    public async Task Fallback_set_propagates_cancellation_without_writing_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var primary = new CancellationThrowingStore();
        var fallback = new InMemoryCredentialStore();
        var store = new FallbackCredentialStore(primary, fallback);

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SetAsync("alice", "secret", cancellation.Token).AsTask());
        Assert.Null(await fallback.GetAsync("alice", default));
    }

    [Fact]
    public void Ui_composition_root_selects_linux_secret_service_store_on_linux()
    {
        if (!OperatingSystem.IsLinux()) return;

        var facade = Assert.IsType<UiProviderFacade>(CompositionRoot.CreateProviderFacade(clientId: "test"));
        var field = typeof(UiProviderFacade).GetField("credentialStore", BindingFlags.Instance | BindingFlags.NonPublic);
        var store = field?.GetValue(facade);

        Assert.IsType<FallbackCredentialStore>(store);
    }

    [Fact]
    public async Task Gh_probe_distinguishes_available_authenticated_from_quota()
    {
        var result = await CreateExitProbe(0).ProbeAsync(default);
        Assert.Equal(GhProbeStatus.AvailableAuthenticated, result.Status);
        Assert.False(result.QuotaAvailable);
    }

    [Fact]
    public async Task Gh_probe_distinguishes_unauthorized()
    {
        var result = await CreateExitProbe(1).ProbeAsync(default);
        Assert.Equal(GhProbeStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task Gh_probe_distinguishes_missing_and_timeout()
    {
        Assert.Equal(GhProbeStatus.Missing, (await new GhCliProbe("/missing/gh").ProbeAsync(default)).Status);
        Assert.Equal(GhProbeStatus.Timeout, (await CreateTimeoutProbe().ProbeAsync(default)).Status);
    }

    [Fact]
    public async Task Provider_facade_exposes_gh_probe_status_without_output()
    {
        var result = await new UiProviderFacade(ghProbe: CreateExitProbe(0)).ProbeGitHubCliAsync(default);
        Assert.Contains("authenticated", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stdout", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_store_save_async_roundtrips()
    {
        var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await store.SaveAsync(new AppSettingsDto(Theme: "Dark", Language: "Japanese", RefreshMinutes: 5, OverallThreshold: 70, NotificationsEnabled: false, GithubOAuthClientId: "id"));
        var loaded = await store.LoadAsync();
        Assert.Equal("Dark", loaded.Theme); Assert.Equal("id", loaded.GithubOAuthClientId);
    }

    [Fact]
    public void Settings_store_async_save_completes_when_blocked_on_a_single_thread_context()
    {
        var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var previous = SynchronizationContext.Current;
        var context = new BoundedSingleThreadSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var save = store.SaveAsync(new AppSettingsDto(Theme: "context-theme")).AsTask();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!save.IsCompleted && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Assert.True(save.IsCompleted, "SaveAsync captured the single-thread context and deadlocked.");
            Assert.Equal("context-theme", store.Load().Theme);
        }
        finally
        {
            context.Drain();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task Settings_store_concurrent_saves_from_multiple_instances_leave_valid_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var stores = Enumerable.Range(0, 8).Select(_ => new AppSettingsStore(root)).ToArray();
        var settings = Enumerable.Range(0, 64)
            .Select(i => new AppSettingsDto(Theme: $"theme-{i}", Language: $"language-{i}", GithubOAuthClientId: $"client-{i}"))
            .ToArray();

        var errors = await Record.ExceptionAsync(async () =>
            await Task.WhenAll(settings.Select((value, i) => stores[i % stores.Length].SaveAsync(value).AsTask())));

        Assert.Null(errors);
        var loaded = await stores[0].LoadAsync();
        Assert.Contains(loaded, settings);
    }

    [Fact]
    public async Task Settings_store_save_and_save_async_are_mutually_exclusive_across_instances()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var asyncStore = new AppSettingsStore(root);
        var syncStore = new AppSettingsStore(root);
        var asyncSettings = new AppSettingsDto(Theme: "async-theme", Language: "async-language", GithubOAuthClientId: "async-client");
        var syncSettings = new AppSettingsDto(Theme: "sync-theme", Language: "sync-language", GithubOAuthClientId: "sync-client");

        var errors = await Record.ExceptionAsync(async () =>
        {
            var asyncSave = asyncStore.SaveAsync(asyncSettings).AsTask();
            var syncSave = Task.Run(() => syncStore.Save(syncSettings));
            await Task.WhenAll(asyncSave, syncSave);
        });

        Assert.Null(errors);
        var loaded = await asyncStore.LoadAsync();
        Assert.Contains(loaded, new[] { asyncSettings, syncSettings });
    }

    [Fact]
    public async Task Settings_store_corrupt_file_is_backed_up()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new AppSettingsStore(root); await File.WriteAllTextAsync(store.Path, "not-json");
        _ = await store.LoadAsync(); Assert.True(File.Exists(store.Path + ".bak"));
    }

    [Fact]
    public async Task Settings_changes_are_enabled_and_saved_by_view_model()
    {
        var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: store);
        vm.SetSettings(new AppSettingsDto(Theme: "Light", Language: "English", RefreshMinutes: 10, OverallThreshold: 80, NotificationsEnabled: true, GithubOAuthClientId: "changed"));
        Assert.Equal("changed", (await store.LoadAsync()).GithubOAuthClientId);
    }

    [Fact]
    public async Task Settings_save_recreates_github_factory_with_current_client_id()
    {
        var factory = new RecordingGitHubFactory(); var vm = new MainViewModel(new EmptyDashboardSource(), settingsStore: new AppSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), githubFactory: factory);
        vm.SetSettings(new AppSettingsDto(GithubOAuthClientId: "current")); await Task.Delay(20); Assert.Equal("current", factory.LastClientId);
    }

    [Fact]
    public async Task Refresh_prunes_history_at_thirty_days()
    {
        var history = new RecordingHistory(); var app = new QuotaApplication([], history, new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero)));
        await app.RefreshAsync(default); Assert.Equal(new DateOnly(2026, 8, 6), history.PrunedBefore);
    }

    [Fact]
    public async Task Startup_prunes_history()
    {
        var history = new RecordingHistory(); var vm = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, timeProvider: new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero))); await vm.InitializeAsync(); Assert.NotNull(history.PrunedBefore);
    }

    [Fact]
    public async Task Startup_keeps_valid_history_available_when_another_day_is_corrupt()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        try
        {
            using var history = new JsonlQuotaHistory(root, new FixedTimeProvider(now));
            var snapshot = new QuotaSnapshot(ProviderKind.ChatGpt, "acct", "Messages", new(QuotaWindowKind.Weekly, now.AddDays(-1), now.AddDays(1)), 40, 100, null, "%", now, now, QuotaSource.Manual, QuotaConfidence.Manual, null);
            await history.AppendAsync([snapshot], default);
            await File.WriteAllTextAsync(Path.Combine(root, "2026-09-04.jsonl"), "not-json\n");

            await Assert.ThrowsAsync<InvalidDataException>(async () => await history.ReadAsync(new DateOnly(2026, 9, 4), default));

            var viewModel = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, timeProvider: new FixedTimeProvider(now));
            await viewModel.InitializeAsync();

            Assert.Contains(viewModel.History.Entries, entry => entry.Account == "acct");
            Assert.Contains(viewModel.Cards, card => card.Account == "acct");
            Assert.Equal("History data is damaged; showing available entries.", viewModel.History.LoadError);

            await viewModel.History.DeleteAllAsync();
            Assert.True(Directory.Exists(root));
            Assert.Empty(Directory.EnumerateFiles(root, "*.jsonl"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Manual_add_reloads_jsonl_into_dashboard_and_history()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); using var history = new JsonlQuotaHistory(root, new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero))); var vm = new MainViewModel(new EmptyDashboardSource(), new ManualQuotaService(history), history, new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero)));
        var snapshot = new QuotaSnapshot(ProviderKind.ChatGpt, "acct", "Messages", new(QuotaWindowKind.Weekly, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), 40, 100, null, "%", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, QuotaSource.Manual, QuotaConfidence.Manual, null);
        await vm.ApplyManualSnapshotAsync(snapshot); await Task.Delay(30); var reloaded = new MainViewModel(new EmptyDashboardSource(), quotaHistory: history, timeProvider: new FixedTimeProvider(new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero))); await reloaded.InitializeAsync(); Assert.Contains(reloaded.Cards, c => c.Account == "acct"); Assert.Contains(reloaded.History.Entries, e => e.Account == "acct");
    }

    private static GhCliProbe CreateExitProbe(int exitCode) => OperatingSystem.IsWindows()
        ? new GhCliProbe("cmd.exe", ["/c", $"exit {exitCode}"])
        : new GhCliProbe("/bin/sh", ["-c", $"exit {exitCode}"]);

    private static GhCliProbe CreateTimeoutProbe() => OperatingSystem.IsWindows()
        ? new GhCliProbe("cmd.exe", ["/c", "ping -n 3 127.0.0.1 > nul"], TimeSpan.FromMilliseconds(10))
        : new GhCliProbe("/bin/sh", ["-c", "sleep 2"], TimeSpan.FromMilliseconds(10));

    private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi
    {
        private readonly object sync = new();
        private readonly Dictionary<string, byte[]> blobs = new(StringComparer.OrdinalIgnoreCase);
        private int activeReads;
        public string? Target;
        public string? WrittenSecret;
        public string Log = "";
        public string? ReadValue;
        public WindowsCredentialError ReadError;
        public bool DeleteCalled;
        public uint Type;
        public uint Persist;
        public int DeleteResult;
        public int MaxBlobBytes = int.MaxValue;
        public string? FailWriteTarget { get; set; }
        public string? FailReadTarget { get; set; }
        public string? FailDeleteTarget { get; set; }
        public HashSet<string> FailDeleteTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DeleteAttempts { get; } = [];
        public bool DelayReads;
        public bool ConcurrentReadDetected;
        public CancellationTokenSource? CancelAfterRootWrite;
        public CancellationTokenSource? CancelAfterChunkWrite;
        public bool FailVerificationReads;
        public bool BlockReads;
        public string BlockReadTarget = "QuotaSight:barrier";
        public int ReadEntries;
        public ManualResetEventSlim ReadEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseReads { get; } = new(false);
        public WindowsCredentialError EnumerationError;
        public List<byte[]> ReturnedBlobs { get; } = [];
        public Dictionary<string, byte[]> StoredTargets => blobs;
        public int Write(string target, ReadOnlySpan<byte> blob, uint type, uint persist)
        {
            Target = target; Type = type; Persist = persist;
            if (string.Equals(target, FailWriteTarget, StringComparison.OrdinalIgnoreCase) || blob.Length > MaxBlobBytes) return 87;
            lock (sync) blobs[target] = blob.ToArray();
            WrittenSecret = blob.Length % 2 == 0 ? MemoryMarshal.Cast<byte, char>(blob).ToString().TrimEnd('\0') : null;
            if (target == "QuotaSight:replace-large") CancelAfterRootWrite?.Cancel();
            if (target.StartsWith("QuotaSight:chunk:", StringComparison.Ordinal)) CancelAfterChunkWrite?.Cancel();
            return 0;
        }
        public WindowsCredentialReadResult Read(string target)
        {
            Target = target;
            if (string.Equals(target, FailReadTarget, StringComparison.OrdinalIgnoreCase)) return new(null, WindowsCredentialError.Other);
            if (FailVerificationReads && target.StartsWith("QuotaSight:chunk:", StringComparison.OrdinalIgnoreCase)) return new(null, WindowsCredentialError.Other);
            if (ReadError != 0) return new(null, ReadError);
            if (target.StartsWith("QuotaSight:", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref ReadEntries);
                if (BlockReads && string.Equals(target, BlockReadTarget, StringComparison.OrdinalIgnoreCase))
                {
                    ReadEntered.Set();
                    ReleaseReads.Wait();
                }
            }
            if (DelayReads && Interlocked.Increment(ref activeReads) > 1) ConcurrentReadDetected = true;
            try
            {
                if (DelayReads) Thread.Sleep(2);
                lock (sync)
                {
                    if (blobs.TryGetValue(target, out var blob)) { var copy = blob.ToArray(); ReturnedBlobs.Add(copy); return new(copy, WindowsCredentialError.None); }
                }
            }
            finally { if (DelayReads) Interlocked.Decrement(ref activeReads); }
            if (ReadValue is null) return new(null, WindowsCredentialError.NotFound);
            var valueCopy = Encoding.Unicode.GetBytes(ReadValue + "\0");
            ReturnedBlobs.Add(valueCopy);
            return new(valueCopy, WindowsCredentialError.None);
        }
        public WindowsCredentialEnumerationResult Enumerate(string prefix)
        {
            if (EnumerationError != WindowsCredentialError.None) return new(null, EnumerationError);
            lock (sync)
            {
                return new(blobs.Keys.Where(target => target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray(), WindowsCredentialError.None);
            }
        }
        public int Delete(string target) { Target = target; DeleteCalled = true; DeleteAttempts.Add(target); if (string.Equals(target, FailDeleteTarget, StringComparison.OrdinalIgnoreCase) || FailDeleteTargets.Contains(target)) return 5; lock (sync) { if (!blobs.Remove(target)) return DeleteResult == 0 ? (int)WindowsCredentialError.NotFound : DeleteResult; } return 0; }
    }
    private sealed class FailingCredentialStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.Unavailable;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
    private sealed class CancellationThrowingStore : ICredentialStore
    {
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SecureStore;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken);
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class RecordingRemoveStore : ICredentialStore
    {
        public bool RemoveCalled;
        public CredentialStoreAvailability Availability => CredentialStoreAvailability.SessionOnly;
        public ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string account, CancellationToken cancellationToken) { RemoveCalled = true; return ValueTask.CompletedTask; }
    }
    private sealed class RecordingHistory : IQuotaHistory
    {
        public DateOnly? PrunedBefore;
        public ValueTask AppendAsync(IReadOnlyList<QuotaSnapshot> s, CancellationToken c) => ValueTask.CompletedTask; public ValueTask<IReadOnlyList<QuotaSnapshot>> ReadAsync(DateOnly d, CancellationToken c) => ValueTask.FromResult<IReadOnlyList<QuotaSnapshot>>([]); public ValueTask PruneAsync(DateOnly b, CancellationToken c) { PrunedBefore = b; return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<QuotaHistoryEntry>> ReadEventsAsync(DateOnly d, CancellationToken c) => ValueTask.FromResult<IReadOnlyList<QuotaHistoryEntry>>([]); public ValueTask DeleteEventAsync(Guid id, CancellationToken c) => ValueTask.CompletedTask; public ValueTask DeleteAsync(DateOnly? d, CancellationToken c) => ValueTask.CompletedTask; public ValueTask ExportJsonAsync(Stream o, CancellationToken c) => ValueTask.CompletedTask; public ValueTask ExportCsvAsync(Stream o, CancellationToken c) => ValueTask.CompletedTask;
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class RecordingGitHubFactory : IGitHubClientFactory { public string? LastClientId; public GitHubDeviceFlowClient Create(string clientId) { LastClientId = clientId; return null!; } }
}
