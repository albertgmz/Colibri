using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Colibri.Platform.DBus;

/// <summary>
/// One shared connection to the user's D-Bus session bus (Linux), opened on first use. D-Bus is how
/// Linux desktop programs talk to each other: the notification server, the file manager and the tray
/// host all listen on this bus.
/// </summary>
internal sealed class SessionBus : IDisposable
{
    public const string BusService = "org.freedesktop.DBus";
    public const string BusPath = "/org/freedesktop/DBus";

    private readonly ILogger<SessionBus> _logger;
    private readonly Lazy<Task<DBusConnection?>> _connection;

    public SessionBus(ILogger<SessionBus> logger)
    {
        _logger = logger;
        _connection = new Lazy<Task<DBusConnection?>>(ConnectAsync);
    }

    /// <summary>The connection, or null when there is no session bus (for example over SSH or in a container).</summary>
    public Task<DBusConnection?> GetConnectionAsync() => _connection.Value;

    /// <summary>Whether some program currently owns the bus name <paramref name="name"/>.</summary>
    public static Task<bool> NameHasOwnerAsync(DBusConnection connection, string name)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusService, path: BusPath, @interface: BusService, member: "NameHasOwner", signature: "s");
        writer.WriteString(name);
        return connection.CallMethodAsync(writer.CreateMessage(), (message, _) => message.GetBodyReader().ReadBool(), null);
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
        {
            _connection.Value.Result?.Dispose();
        }
    }

    private async Task<DBusConnection?> ConnectAsync()
    {
        // DBUS_SESSION_BUS_ADDRESS, set by the desktop session.
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            _logger.LogInformation("No D-Bus session bus; desktop integration is disabled");
            return null;
        }

        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not connect to the D-Bus session bus; desktop integration is disabled");
            connection.Dispose();
            return null;
        }
    }
}
