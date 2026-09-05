using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using QuotaSight.Application;

namespace QuotaSight.Application;

public enum WindowsCredentialError { None = 0, NotFound = 1168, Other = 1 }
public sealed record WindowsCredentialReadResult(byte[]? Blob, WindowsCredentialError Error);
public interface IWindowsCredentialApi
{
    int Write(string target, ReadOnlySpan<byte> blob, uint type, uint persist);
    WindowsCredentialReadResult Read(string target);
    void Free();
    int Delete(string target);
    void ObserveZeroing();
    void ZeroNativeBlob();
}

public sealed class WindowsCredentialStore(IWindowsCredentialApi api) : ICredentialStore
{
    public static WindowsCredentialStore Create() => new(new WindowsCredentialApi());

    public CredentialStoreAvailability Availability { get; private set; } = CredentialStoreAvailability.SecureStore;
    private static string Target(string account) => $"QuotaSight:{account}";
    public async ValueTask<string?> GetAsync(string account, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var result = api.Read(Target(account));
        if (result.Error == WindowsCredentialError.NotFound) return null;
        if (result.Error != WindowsCredentialError.None || result.Blob is null) { Availability = CredentialStoreAvailability.Unavailable; return null; }
        var managed = result.Blob;
        try { return Encoding.Unicode.GetString(managed).TrimEnd('\0'); }
        finally { CryptographicOperations.ZeroMemory(managed); api.ZeroNativeBlob(); api.ObserveZeroing(); api.Free(); }
    }
    public async ValueTask SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var blob = Encoding.Unicode.GetBytes(secret + "\0");
        try { if (api.Write(Target(account), blob, 1, 2) != 0) { Availability = CredentialStoreAvailability.Unavailable; throw new InvalidOperationException("Windows credential write failed."); } }
        finally { CryptographicOperations.ZeroMemory(blob); api.ObserveZeroing(); }
    }
    public async ValueTask RemoveAsync(string account, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (api.Delete(Target(account)) != 0) Availability = CredentialStoreAvailability.Unavailable;
    }
}

internal sealed partial class WindowsCredentialApi : IWindowsCredentialApi
{
    private IntPtr native;
    public int Write(string target, ReadOnlySpan<byte> blob, uint type, uint persist) { var ptr = Marshal.AllocHGlobal(blob.Length); var name = Marshal.StringToHGlobalUni(target); var credential = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>()); try { Marshal.Copy(blob.ToArray(), 0, ptr, blob.Length); Marshal.StructureToPtr(new NativeCredential { Type = type, Persist = persist, TargetName = name, CredentialBlob = ptr, CredentialBlobSize = (uint)blob.Length }, credential, false); return CredWriteW(credential, 0) ? 0 : Marshal.GetLastWin32Error(); } finally { Marshal.FreeHGlobal(credential); Marshal.FreeHGlobal(ptr); Marshal.FreeHGlobal(name); } }
    public WindowsCredentialReadResult Read(string target)
    {
        if (!CredReadW(target, 1, 0, out native)) return new(null, (WindowsCredentialError)Marshal.GetLastWin32Error());
        var credential = Marshal.PtrToStructure<NativeCredential>(native);
        return new(credential.CredentialBlobSize == 0 ? [] : credential.CredentialBlob.CopyToManaged((int)credential.CredentialBlobSize), WindowsCredentialError.None);
    }
    public void Free() { if (native != IntPtr.Zero) { CredFree(native); native = IntPtr.Zero; } }
    public int Delete(string target) => CredDeleteW(target, 1, 0) ? 0 : Marshal.GetLastWin32Error();
    public void ObserveZeroing() { }
    public void ZeroNativeBlob() { if (native == IntPtr.Zero) return; var credential = Marshal.PtrToStructure<NativeCredential>(native); if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0) { var zeros = new byte[credential.CredentialBlobSize]; Marshal.Copy(zeros, 0, credential.CredentialBlob, zeros.Length); } }
    [StructLayout(LayoutKind.Sequential)] private struct NativeCredential { public uint Flags; public uint Type; public IntPtr TargetName; public IntPtr Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public IntPtr Attributes; public IntPtr TargetAlias; public IntPtr UserName; }
    [LibraryImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredWriteW(IntPtr credential, uint flags);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredDeleteW(string target, uint type, uint flags);
    [LibraryImport("advapi32.dll")] private static partial void CredFree(IntPtr credential);
}

internal static class MarshalExtensions { public static byte[] CopyToManaged(this IntPtr ptr, int length) { var bytes = new byte[length]; Marshal.Copy(ptr, bytes, 0, length); return bytes; } }
