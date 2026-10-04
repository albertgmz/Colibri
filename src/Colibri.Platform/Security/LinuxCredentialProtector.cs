using System.Security.Cryptography;
using Colibri.Core.Platform;
using Tmds.DBus.Protocol;

namespace Colibri.Platform.Security;

/// <summary>AES-GCM whose key lives in the user's freedesktop Secret Service.</summary>
public sealed class LinuxCredentialProtector : ICredentialProtector
{
    private readonly OsKeyCredentialProtector _protector = new(new LinuxCredentialKeyStore());
    public byte[] Protect(byte[] plaintext, Guid downloadId) => _protector.Protect(plaintext, downloadId);
    public byte[] Unprotect(byte[] ciphertext, Guid downloadId) => _protector.Unprotect(ciphertext, downloadId);
}

internal sealed class LinuxCredentialKeyStore : ICredentialKeyStore
{
    private const string Destination = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The repository calls this only inside Task.Run. All D-Bus work is awaited without a UI context.
    public byte[] GetKey(bool createIfMissing)
    {
        if (!OperatingSystem.IsLinux()) throw new CredentialProtectionException();
        return GetKeyAsync(createIfMissing).GetAwaiter().GetResult();
    }

    private static async Task<byte[]> GetKeyAsync(bool createIfMissing)
    {
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address)) throw new CredentialProtectionException();
        using var connection = new DBusConnection(address);
        await connection.ConnectAsync().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
        var matches = await SearchAsync(connection).ConfigureAwait(false);
        if (matches.Locked.Length != 0 || matches.Unlocked.Length > 1) throw new CredentialProtectionException();
        if (matches.Unlocked.Length == 0 && !createIfMissing) throw new CredentialProtectionException();

        // 'plain' is the standardized local transport over the private current-user D-Bus connection.
        // The key is persisted exclusively by Secret Service, never in an app file or command argument.
        var session = await connection.CallMethodAsync(OpenSessionMessage(connection), (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.ReadVariantValue();
            return reader.ReadObjectPathAsString();
        }, null).WaitAsync(Timeout).ConfigureAwait(false);
        if (session == "/") throw new CredentialProtectionException();

        if (matches.Unlocked.Length == 0)
        {
            var collection = await connection.CallMethodAsync(ReadAliasMessage(connection),
                (message, _) => message.GetBodyReader().ReadObjectPathAsString(), null).WaitAsync(Timeout).ConfigureAwait(false);
            if (collection == "/") throw new CredentialProtectionException();
            var locked = await connection.CallMethodAsync(LockedMessage(connection, collection),
                (message, _) => message.GetBodyReader().ReadVariantValue().GetBool(), null).WaitAsync(Timeout).ConfigureAwait(false);
            if (locked) throw new CredentialProtectionException();
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var created = await connection.CallMethodAsync(CreateItemMessage(connection, collection, session, key),
                    (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return (Item: reader.ReadObjectPathAsString(), Prompt: reader.ReadObjectPathAsString());
                    }, null).WaitAsync(Timeout).ConfigureAwait(false);
                if (created.Item == "/" || created.Prompt != "/") throw new CredentialProtectionException();
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            // Refuse an ambiguous race rather than overwrite a key which already protects downloads.
            matches = await SearchAsync(connection).ConfigureAwait(false);
            if (matches.Locked.Length != 0 || matches.Unlocked.Length != 1) throw new CredentialProtectionException();
        }
        return await connection.CallMethodAsync(GetSecretMessage(connection, matches.Unlocked[0].ToString(), session),
            (message, _) =>
            {
                var reader = message.GetBodyReader();
                reader.AlignStruct();
                var returnedSession = reader.ReadObjectPathAsString();
                var parameters = reader.ReadArrayOfByte();
                var key = reader.ReadArrayOfByte();
                var contentType = reader.ReadString();
                if (returnedSession != session || parameters.Length != 0 || key.Length != 32 || contentType != "application/octet-stream")
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw new CredentialProtectionException();
                }
                return key;
            }, null).WaitAsync(Timeout).ConfigureAwait(false);
    }

    private static Task<(ObjectPath[] Unlocked, ObjectPath[] Locked)> SearchAsync(DBusConnection connection) =>
        connection.CallMethodAsync(SearchMessage(connection), (message, _) =>
        {
            var reader = message.GetBodyReader();
            return (reader.ReadArrayOfObjectPath(), reader.ReadArrayOfObjectPath());
        }, null).WaitAsync(Timeout);

    private static MessageBuffer SearchMessage(DBusConnection connection)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Destination, path: ServicePath, @interface: ServiceInterface, member: "SearchItems", signature: "a{ss}");
            WriteAttributes(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private static void WriteAttributes(ref MessageWriter writer)
    {
        var attributes = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("application");
        writer.WriteString("Colibri");
        writer.WriteDictionaryEntryStart();
        writer.WriteString("purpose");
        writer.WriteString("request-credentials-v1");
        writer.WriteDictionaryEnd(attributes);
    }

    private static MessageBuffer OpenSessionMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Destination, path: ServicePath, @interface: ServiceInterface, member: "OpenSession", signature: "sv");
        writer.WriteString("plain");
        writer.WriteVariantString("");
        return writer.CreateMessage();
    }

    private static MessageBuffer ReadAliasMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Destination, path: ServicePath, @interface: ServiceInterface, member: "ReadAlias", signature: "s");
        writer.WriteString("default");
        return writer.CreateMessage();
    }

    private static MessageBuffer LockedMessage(DBusConnection connection, string collection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Destination, path: collection, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString(CollectionInterface);
        writer.WriteString("Locked");
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateItemMessage(DBusConnection connection, string collection, string session, byte[] key)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Destination, path: collection, @interface: CollectionInterface, member: "CreateItem", signature: "a{sv}(oayays)b");
            var properties = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString(ItemInterface + ".Label");
            writer.WriteVariantString("Colibri request credential encryption key");
            writer.WriteDictionaryEntryStart();
            writer.WriteString(ItemInterface + ".Attributes");
            writer.WriteSignature("a{ss}");
            WriteAttributes(ref writer);
            writer.WriteDictionaryEnd(properties);
            writer.WriteStructureStart();
            writer.WriteObjectPath(session);
            writer.WriteArray(System.Array.Empty<byte>());
            writer.WriteArray(key);
            writer.WriteString("application/octet-stream");
            writer.WriteBool(false); // Never replace an existing encryption key.
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private static MessageBuffer GetSecretMessage(DBusConnection connection, string item, string session)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Destination, path: item, @interface: ItemInterface, member: "GetSecret", signature: "o");
        writer.WriteObjectPath(session);
        return writer.CreateMessage();
    }
}
