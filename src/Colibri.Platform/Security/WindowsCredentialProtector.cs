using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Colibri.Core.Platform;

namespace Colibri.Platform.Security;

/// <summary>Current-user DPAPI. The download ID is additional entropy, preventing row substitution.</summary>
public sealed class WindowsCredentialProtector : ICredentialProtector
{
    private const uint UiForbidden = 1;

    public byte[] Protect(byte[] plaintext, Guid downloadId) => Transform(plaintext, downloadId, protect: true);
    public byte[] Unprotect(byte[] ciphertext, Guid downloadId) => Transform(ciphertext, downloadId, protect: false);

    private static byte[] Transform(byte[] input, Guid downloadId, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new CredentialProtectionException();
        var entropy = Encoding.UTF8.GetBytes($"Colibri/request-headers/v1/{downloadId:D}");
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var source = new DataBlob { Length = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var binding = new DataBlob { Length = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var success = protect
                ? CryptProtectData(ref source, null, ref binding, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref binding, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!success) throw new CredentialProtectionException();
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // Unprotect's unmanaged result contains plaintext too.
                if (!protect && output.Length > 0) Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
            inputHandle.Free();
            entropyHandle.Free();
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
