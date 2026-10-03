using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Colibri.Core.Platform;

namespace Colibri.Platform.Security;

/// <summary>AES-GCM whose key lives in the user's non-synchronizing macOS Keychain.</summary>
public sealed class MacCredentialProtector : ICredentialProtector
{
    private readonly OsKeyCredentialProtector _protector = new(new MacCredentialKeyStore());
    public byte[] Protect(byte[] plaintext, Guid downloadId) => _protector.Protect(plaintext, downloadId);
    public byte[] Unprotect(byte[] ciphertext, Guid downloadId) => _protector.Unprotect(ciphertext, downloadId);
}

internal sealed class MacCredentialKeyStore : ICredentialKeyStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int NotFound = -25300;
    private const int Duplicate = -25299;
    private static readonly Lazy<IntPtr> SecurityHandle = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<IntPtr> CoreHandle = new(() => NativeLibrary.Load(Core));

    public byte[] GetKey(bool createIfMissing)
    {
        if (!OperatingSystem.IsMacOS()) throw new CredentialProtectionException();
        var query = CreateQuery();
        try
        {
            var existing = Read(query);
            if (existing is not null) return existing;
            if (!createIfMissing) throw new CredentialProtectionException();
            var key = RandomNumberGenerator.GetBytes(32);
            var data = CFDataCreate(IntPtr.Zero, key, key.Length);
            if (data == IntPtr.Zero)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CredentialProtectionException();
            }
            try
            {
                CFDictionaryRemoveValue(query, Sec("kSecReturnData"));
                CFDictionaryRemoveValue(query, Sec("kSecMatchLimit"));
                CFDictionarySetValue(query, Sec("kSecValueData"), data);
                CFDictionarySetValue(query, Sec("kSecAttrAccessible"), Sec("kSecAttrAccessibleWhenUnlockedThisDeviceOnly"));
                var status = SecItemAdd(query, IntPtr.Zero);
                if (status == 0) return key.ToArray();
                if (status != Duplicate) throw new CredentialProtectionException();
                // Another process won creation. Never overwrite its key.
                CFDictionaryRemoveValue(query, Sec("kSecValueData"));
                CFDictionaryRemoveValue(query, Sec("kSecAttrAccessible"));
                AddReadOptions(query);
                return Read(query) ?? throw new CredentialProtectionException();
            }
            finally
            {
                CFRelease(data);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally { CFRelease(query); }
    }

    private static byte[]? Read(IntPtr query)
    {
        var status = SecItemCopyMatching(query, out var result);
        try
        {
            if (status == NotFound) return null;
            if (status != 0 || result == IntPtr.Zero || CFDataGetLength(result) != 32) throw new CredentialProtectionException();
            var key = new byte[32];
            Marshal.Copy(CFDataGetBytePtr(result), key, 0, key.Length);
            return key;
        }
        finally { if (result != IntPtr.Zero) CFRelease(result); }
    }

    private static IntPtr CreateQuery()
    {
        var query = CFDictionaryCreateMutable(IntPtr.Zero, 0,
            NativeLibrary.GetExport(CoreHandle.Value, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(CoreHandle.Value, "kCFTypeDictionaryValueCallBacks"));
        if (query == IntPtr.Zero) throw new CredentialProtectionException();
        try
        {
            CFDictionarySetValue(query, Sec("kSecClass"), Sec("kSecClassGenericPassword"));
            SetString(query, "kSecAttrService", "Colibri.RequestCredentials.v1");
            SetString(query, "kSecAttrAccount", "encryption-key");
            CFDictionarySetValue(query, Sec("kSecAttrSynchronizable"), Cf("kCFBooleanFalse"));
            CFDictionarySetValue(query, Sec("kSecUseAuthenticationUI"), Sec("kSecUseAuthenticationUIFail"));
            AddReadOptions(query);
            return query;
        }
        catch { CFRelease(query); throw; }
    }

    private static void AddReadOptions(IntPtr query)
    {
        CFDictionarySetValue(query, Sec("kSecReturnData"), Cf("kCFBooleanTrue"));
        CFDictionarySetValue(query, Sec("kSecMatchLimit"), Sec("kSecMatchLimitOne"));
    }

    private static void SetString(IntPtr query, string key, string value)
    {
        var text = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100); // UTF-8
        if (text == IntPtr.Zero) throw new CredentialProtectionException();
        try { CFDictionarySetValue(query, Sec(key), text); }
        finally { CFRelease(text); }
    }

    private static IntPtr Sec(string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle.Value, symbol));
    private static IntPtr Cf(string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreHandle.Value, symbol));

    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Core)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(Core)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(Core)] private static extern void CFDictionaryRemoveValue(IntPtr dictionary, IntPtr key);
    [DllImport(Core)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(Core)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(Core)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(Core)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(Core)] private static extern void CFRelease(IntPtr value);
}
