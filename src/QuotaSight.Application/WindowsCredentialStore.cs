using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace QuotaSight.Application;

public enum WindowsCredentialError { None = 0, NotFound = 1168, Other = 1 }
public sealed record WindowsCredentialReadResult(byte[]? Blob, WindowsCredentialError Error);
public sealed record WindowsCredentialEnumerationResult(IReadOnlyList<string>? Targets, WindowsCredentialError Error);
public interface IWindowsCredentialApi
{
    int Write(string target, ReadOnlySpan<byte> blob, uint type, uint persist);
    WindowsCredentialReadResult Read(string target);
    WindowsCredentialEnumerationResult Enumerate(string prefix);
    int Delete(string target);
}

[SupportedOSPlatform("windows")]
internal static class WindowsCredentialMutexPolicy
{
    internal const MutexRights RequiredRights = MutexRights.FullControl;

    internal static MutexSecurity CreateSecurityDescriptor(string sidValue)
    {
        var sid = new SecurityIdentifier(sidValue);
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(sid, RequiredRights, AccessControlType.Allow));
        return security;
    }
}

public sealed class WindowsCredentialStore(IWindowsCredentialApi api) : ICredentialStore
{
    private enum BlobClassification { Manifest, ValidLegacy, Corrupt }
    private const int GenericType = 1;
    private const uint PersistLocalMachine = 2;
    private const int SafeBlobBytes = 2400;
    private const int MaxPayloadBytes = 8 * 1024 * 1024;
    private const int MaxChunks = 4096;
    private const string ManifestMagic = "QuotaSight.Generation.v1\n";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding LegacyEncoding = new UnicodeEncoding(false, false, true);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccountGates = new(StringComparer.OrdinalIgnoreCase);

    public static WindowsCredentialStore Create() => new(new WindowsCredentialApi());
    public CredentialStoreAvailability Availability { get; private set; } = CredentialStoreAvailability.SecureStore;

    private static string Target(string account) => $"QuotaSight:{account}";
    private static string CanonicalRoot(string root)
        => root.StartsWith("QuotaSight:", StringComparison.OrdinalIgnoreCase)
            ? "QuotaSight:" + root["QuotaSight:".Length..].ToUpperInvariant()
            : root.ToUpperInvariant();
    private static string RootHash(string root) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(CanonicalRoot(root)))).ToLowerInvariant();
    private static string ChunkPrefix(string root) => $"QuotaSight:chunk:{RootHash(root)}:";
    private static string ChunkTarget(string root, string generation, int index) => $"{ChunkPrefix(root)}{generation}:{index}";

    public async ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken)
    {
        var gate = await AcquireGateAsync(account, cancellationToken).ConfigureAwait(false);
        try { return ExecuteWithMutex(account, cancellationToken, () => GetCore(account, cancellationToken)); }
        finally { gate.Release(); }
    }

    private string? GetCore(string account, CancellationToken cancellationToken)
    {
        var root = Target(account);
        var read = api.Read(root);
        if (read.Error == WindowsCredentialError.NotFound) { Availability = CredentialStoreAvailability.SecureStore; return null; }
        if (read.Error != WindowsCredentialError.None || read.Blob is null) return FailRead();
        try
        {
            var classification = Classify(read.Blob);
            if (classification == BlobClassification.ValidLegacy)
            {
                Availability = CredentialStoreAvailability.SecureStore;
                return DecodeLegacy(read.Blob);
            }
            if (classification == BlobClassification.Corrupt) return FailRead();
            var manifest = ParseManifest(read.Blob, root);
            var payload = new byte[manifest.TotalBytes];
            try
            {
                var offset = 0;
                for (var i = 0; i < manifest.ChunkCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = api.Read(ChunkTarget(root, manifest.Generation, i));
                    try
                    {
                        if (chunk.Error != WindowsCredentialError.None || chunk.Blob is null || chunk.Blob.Length > payload.Length - offset) return FailRead();
                        chunk.Blob.CopyTo(payload, offset);
                        offset += chunk.Blob.Length;
                    }
                    finally { if (chunk.Blob is not null) CryptographicOperations.ZeroMemory(chunk.Blob); }
                }
                if (offset != payload.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), Convert.FromHexString(manifest.Sha256))) return FailRead();
                var value = Utf8.GetString(payload);
                Availability = CredentialStoreAvailability.SecureStore;
                return value;
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        catch (InvalidDataException) { return FailRead(); }
        catch (DecoderFallbackException) { return FailRead(); }
        catch (FormatException) { return FailRead(); }
        finally { CryptographicOperations.ZeroMemory(read.Blob); }
    }

    public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        var gate = await AcquireGateAsync(account, cancellationToken).ConfigureAwait(false);
        try { ExecuteWithMutex(account, cancellationToken, () => SetCore(account, secret, cancellationToken)); }
        finally { gate.Release(); }
    }

    private void SetCore(string account, string secret, CancellationToken cancellationToken)
    {
        var root = Target(account);
        var payload = Utf8.GetBytes(secret);
        try
        {
            _ = ReadExisting(root);
            var prefix = ChunkPrefix(root);
            var existing = EnumerateOrThrow(prefix);
            if (Encoding.Unicode.GetByteCount(secret) + 2 <= SafeBlobBytes)
            {
                var legacy = Encoding.Unicode.GetBytes(secret + "\0");
                try { WriteOrThrow(root, legacy, cancellationToken); }
                finally { CryptographicOperations.ZeroMemory(legacy); }
                CleanupChunks(existing, prefix, null);
                Availability = CredentialStoreAvailability.SecureStore;
                return;
            }
            if (payload.Length > MaxPayloadBytes) throw new InvalidOperationException("Windows credential secret is too large.");
            var chunkCount = (payload.Length + SafeBlobBytes - 1) / SafeBlobBytes;
            if (chunkCount > MaxChunks) throw new InvalidOperationException("Windows credential secret is too large.");
            var generation = Guid.NewGuid().ToString("N");
            var written = new List<string>(chunkCount);
            var published = false;
            try
            {
                for (var i = 0; i < chunkCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = Math.Min(SafeBlobBytes, payload.Length - i * SafeBlobBytes);
                    var chunk = payload.AsSpan(i * SafeBlobBytes, length).ToArray();
                    var target = ChunkTarget(root, generation, i);
                    try { WriteOrThrow(target, chunk, cancellationToken); written.Add(target); VerifyChunk(target, chunk, cancellationToken); }
                    finally { CryptographicOperations.ZeroMemory(chunk); }
                }
                var manifest = new WindowsCredentialManifest(1, generation, RootHash(root), payload.Length, chunkCount, Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
                var manifestBytes = Utf8.GetBytes(ManifestMagic + JsonSerializer.Serialize(manifest, WindowsCredentialJsonContext.Default.WindowsCredentialManifest));
                try { WriteOrThrow(root, manifestBytes, cancellationToken); published = true; }
                finally { CryptographicOperations.ZeroMemory(manifestBytes); }
                CleanupChunks(EnumerateOrThrow(prefix), prefix, generation);
                Availability = CredentialStoreAvailability.SecureStore;
            }
            catch
            {
                if (!published) foreach (var target in written) { try { DeleteOrThrow(target); } catch { } }
                throw;
            }
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public async ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
    {
        var gate = await AcquireGateAsync(account, cancellationToken).ConfigureAwait(false);
        try { ExecuteWithMutex(account, cancellationToken, () => RemoveCore(account, cancellationToken)); }
        finally { gate.Release(); }
    }

    private void RemoveCore(string account, CancellationToken cancellationToken)
    {
        var root = Target(account);
        var rootRead = api.Read(root);
        try { if (rootRead.Blob is not null) _ = Classify(rootRead.Blob); }
        finally { if (rootRead.Blob is not null) CryptographicOperations.ZeroMemory(rootRead.Blob); }
        var prefix = ChunkPrefix(root);
        Exception? failure = null;
        IReadOnlyList<string> targets = [];
        try { targets = EnumerateOrThrow(prefix); }
        catch (Exception ex) { failure = ex; }
        try { CleanupChunks(targets, prefix, null); }
        catch (Exception ex) { failure ??= ex; }
        try { DeleteOrThrow(root); }
        catch (Exception ex) { failure ??= ex; }
        if (failure is not null) throw failure;
        Availability = CredentialStoreAvailability.SecureStore;
    }

    private static async ValueTask<SemaphoreSlim> AcquireGateAsync(string account, CancellationToken cancellationToken)
    {
        var key = Target(account);
        var gate = AccountGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return gate;
    }

    [SupportedOSPlatform("windows")]
    private static string CurrentUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Windows credential synchronization identity is unavailable.", ex);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string MutexName(string account)
        => $"Global\\QuotaSight-{CurrentUserSid()}-{RootHash(Target(account))}";

    [SupportedOSPlatform("windows")]
    private static Mutex CreateMutex(string account)
    {
        var sid = CurrentUserSid();
        var security = WindowsCredentialMutexPolicy.CreateSecurityDescriptor(sid);
        return MutexAcl.Create(false, MutexName(account), out _, security);
    }

    private static T ExecuteWithMutex<T>(string account, CancellationToken cancellationToken, Func<T> action)
    {
        if (!OperatingSystem.IsWindows()) return action();

        using var mutex = CreateMutex(account);
        var acquired = false;
        try
        {
            try
            {
                var waitResult = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]);
                if (waitResult == 1) throw new OperationCanceledException(cancellationToken);
                acquired = true;
            }
            catch (AbandonedMutexException) { acquired = true; }
            return action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void ExecuteWithMutex(string account, CancellationToken cancellationToken, Action action)
        => ExecuteWithMutex(account, cancellationToken, () => { action(); return true; });

    private WindowsCredentialReadResult ReadBlob(string target) => api.Read(target);
    private WindowsCredentialManifest? ReadExisting(string root)
    {
        var old = ReadBlob(root);
        if (old.Error == WindowsCredentialError.NotFound) return null;
        if (old.Error != WindowsCredentialError.None || old.Blob is null) throw CredentialFailure("Windows credential read failed.");
        try
        {
            var classification = Classify(old.Blob);
            if (classification == BlobClassification.Corrupt) throw CredentialFailure("Windows credential root is invalid.");
            if (classification != BlobClassification.Manifest) return null;
            try { return ParseManifest(old.Blob, root); }
            catch (InvalidDataException) { throw CredentialFailure("Windows credential root is invalid."); }
        }
        finally { CryptographicOperations.ZeroMemory(old.Blob); }
    }

    private IReadOnlyList<string> EnumerateOrThrow(string prefix)
    {
        var result = api.Enumerate(prefix);
        if (result.Error != WindowsCredentialError.None || result.Targets is null) throw CredentialFailure("Windows credential enumeration failed.");
        return result.Targets.Where(target => target.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    private void CleanupChunks(IReadOnlyList<string> targets, string prefix, string? keepGeneration)
    {
        Exception? failure = null;
        foreach (var target in targets)
        {
            if (!target.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (keepGeneration is not null && target.StartsWith($"{prefix}{keepGeneration}:", StringComparison.Ordinal)) continue;
            try { DeleteOrThrow(target); } catch (Exception ex) { failure ??= ex; }
        }
        if (failure is not null) throw failure;
    }

    private void WriteOrThrow(string target, byte[] blob, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (api.Write(target, blob, GenericType, PersistLocalMachine) != 0) { Availability = CredentialStoreAvailability.Unavailable; throw new InvalidOperationException("Windows credential write failed."); }
    }
    private void VerifyChunk(string target, byte[] expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = api.Read(target);
        try { if (read.Error != WindowsCredentialError.None || read.Blob is null || !CryptographicOperations.FixedTimeEquals(expected, read.Blob)) throw CredentialFailure("Windows credential verification failed."); }
        finally { if (read.Blob is not null) CryptographicOperations.ZeroMemory(read.Blob); }
    }
    private void DeleteOrThrow(string target)
    {
        var error = api.Delete(target);
        if (error == (int)WindowsCredentialError.NotFound) return;
        if (error != 0) { Availability = CredentialStoreAvailability.Unavailable; throw new InvalidOperationException("Windows credential removal failed."); }
    }
    private InvalidOperationException CredentialFailure(string message)
    {
        Availability = CredentialStoreAvailability.Unavailable;
        return new InvalidOperationException(message);
    }
    private string? FailRead() { Availability = CredentialStoreAvailability.Unavailable; return null; }

    private static BlobClassification Classify(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Utf8.GetBytes(ManifestMagic))) return BlobClassification.Manifest;
        return IsValidLegacy(bytes) ? BlobClassification.ValidLegacy : BlobClassification.Corrupt;
    }
    private static bool IsValidLegacy(byte[] bytes)
    {
        if (bytes.Length < 2 || (bytes.Length & 1) != 0 || bytes[^1] != 0 || bytes[^2] != 0) return false;
        try { _ = LegacyEncoding.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }
    private static string DecodeLegacy(byte[] blob) => LegacyEncoding.GetString(blob)[..^1];
    private static WindowsCredentialManifest ParseManifest(byte[] bytes, string root)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize(bytes.AsSpan(Utf8.GetByteCount(ManifestMagic)), WindowsCredentialJsonContext.Default.WindowsCredentialManifest) ?? throw new InvalidDataException();
            if (manifest.Version != 1 || manifest.RootHash != RootHash(root) || string.IsNullOrEmpty(manifest.Generation) || manifest.Generation.Length != 32 || manifest.ChunkCount < 1 || manifest.ChunkCount > MaxChunks || manifest.TotalBytes < 1 || manifest.TotalBytes > MaxPayloadBytes || manifest.Sha256.Length != 64 || !manifest.Generation.All(Uri.IsHexDigit) || !manifest.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException();
            return manifest;
        }
        catch { throw new InvalidDataException("Windows credential manifest is invalid."); }
    }
}

internal sealed record WindowsCredentialManifest(int Version, string Generation, string RootHash, int TotalBytes, int ChunkCount, string Sha256);

[JsonSerializable(typeof(WindowsCredentialManifest))]
internal partial class WindowsCredentialJsonContext : JsonSerializerContext;

internal sealed partial class WindowsCredentialApi : IWindowsCredentialApi
{
    public int Write(string target, ReadOnlySpan<byte> blob, uint type, uint persist)
    {
        var ptr = Marshal.AllocHGlobal(blob.Length); var name = Marshal.StringToHGlobalUni(target); var credential = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>()); var copy = blob.ToArray();
        try { Marshal.Copy(copy, 0, ptr, blob.Length); Marshal.StructureToPtr(new NativeCredential { Type = type, Persist = persist, TargetName = name, CredentialBlob = ptr, CredentialBlobSize = (uint)blob.Length }, credential, false); return CredWriteW(credential, 0) ? 0 : Marshal.GetLastWin32Error(); }
        finally { CryptographicOperations.ZeroMemory(copy); if (blob.Length > 0) Marshal.Copy(new byte[blob.Length], 0, ptr, blob.Length); Marshal.FreeHGlobal(credential); Marshal.FreeHGlobal(ptr); Marshal.FreeHGlobal(name); }
    }
    public WindowsCredentialReadResult Read(string target)
    {
        IntPtr native = IntPtr.Zero;
        try { if (!CredReadW(target, 1, 0, out native)) return new(null, (WindowsCredentialError)Marshal.GetLastWin32Error()); var credential = Marshal.PtrToStructure<NativeCredential>(native); return new(credential.CredentialBlobSize == 0 ? [] : credential.CredentialBlob.CopyToManaged((int)credential.CredentialBlobSize), WindowsCredentialError.None); }
        finally { FreeCredential(native); }
    }
    public WindowsCredentialEnumerationResult Enumerate(string prefix)
    {
        IntPtr native = IntPtr.Zero;
        uint count = 0;
        try
        {
            if (!CredEnumerateW(prefix + "*", 0, out count, out native))
            {
                var error = (WindowsCredentialError)Marshal.GetLastWin32Error();
                return error == WindowsCredentialError.NotFound ? new([], WindowsCredentialError.None) : new(null, error);
            }
            if (count > (uint)(int.MaxValue / IntPtr.Size))
            {
                count = 0;
                throw new InvalidDataException("Windows credential enumeration is invalid.");
            }
            var targets = new List<string>(checked((int)count));
            for (var i = 0u; i < count; i++)
            {
                var credential = ReadEnumeratedCredential(native, i);
                if (credential.TargetName != IntPtr.Zero)
                {
                    var target = Marshal.PtrToStringUni(credential.TargetName);
                    if (target is not null && target.StartsWith(prefix, StringComparison.Ordinal)) targets.Add(target);
                }
            }
            return new(targets, WindowsCredentialError.None);
        }
        finally
        {
            if (native != IntPtr.Zero)
            {
                for (var i = 0u; i < count; i++)
                {
                    try { ZeroCredentialBlob(ReadEnumeratedCredential(native, i)); }
                    catch (Exception) { }
                }
                CredFree(native);
            }
        }
    }
    public int Delete(string target) => CredDeleteW(target, 1, 0) ? 0 : Marshal.GetLastWin32Error();
    private static void FreeCredential(IntPtr native)
    {
        if (native == IntPtr.Zero) return;
        var credential = Marshal.PtrToStructure<NativeCredential>(native);
        if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0) Marshal.Copy(new byte[credential.CredentialBlobSize], 0, credential.CredentialBlob, (int)credential.CredentialBlobSize);
        CredFree(native);
    }
    private static NativeCredential ReadEnumeratedCredential(IntPtr outer, uint index)
    {
        var offset = checked((int)(index * (uint)IntPtr.Size));
        var credential = Marshal.ReadIntPtr(outer, offset);
        if (credential == IntPtr.Zero) throw new InvalidDataException("Windows credential enumeration is invalid.");
        return Marshal.PtrToStructure<NativeCredential>(credential);
    }
    private static void ZeroCredentialBlob(NativeCredential credential)
    {
        if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return;
        if (credential.CredentialBlobSize > int.MaxValue) throw new InvalidDataException("Windows credential blob is invalid.");
        Marshal.Copy(new byte[checked((int)credential.CredentialBlobSize)], 0, credential.CredentialBlob, checked((int)credential.CredentialBlobSize));
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeCredential { public uint Flags; public uint Type; public IntPtr TargetName; public IntPtr Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public IntPtr Attributes; public IntPtr TargetAlias; public IntPtr UserName; }
    [LibraryImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredWriteW(IntPtr credential, uint flags);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredEnumerateW(string? filter, uint flags, out uint count, out IntPtr credentials);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredDeleteW(string target, uint type, uint flags);
    [LibraryImport("advapi32.dll")] private static partial void CredFree(IntPtr credential);
}

internal static class MarshalExtensions { public static byte[] CopyToManaged(this IntPtr ptr, int length) { var bytes = new byte[length]; Marshal.Copy(ptr, bytes, 0, length); return bytes; } }
