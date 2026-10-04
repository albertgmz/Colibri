namespace Colibri.Engine.Aria2;

/// <summary>
/// A connected, message-based channel to aria2's RPC server. One instance is one connection;
/// disposing it closes the connection.
/// </summary>
internal interface IAria2Transport : IDisposable
{
    /// <summary>Sends one text message.</summary>
    Task SendAsync(string message, CancellationToken ct);

    /// <summary>
    /// Yields each complete text message as it arrives. Ends when the other side closes the
    /// connection, and throws when the connection fails.
    /// </summary>
    IAsyncEnumerable<string> ReceiveAllAsync(CancellationToken ct);
}
