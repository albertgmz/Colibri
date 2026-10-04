namespace Colibri.Engine.Aria2;

/// <summary>
/// An error answer from aria2's JSON-RPC server.
/// </summary>
public sealed class Aria2RpcException : Exception
{
    public Aria2RpcException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// The JSON-RPC error code. aria2 uses 1 for almost every failure, so the message is what tells
    /// the cases apart.
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// Whether aria2 does not know the GID. aria2 reports this only through its message
    /// ("GID 2089b05ecca3d829 is not found").
    /// </summary>
    public bool IsGidNotFound => Message.Contains("is not found", StringComparison.Ordinal);
}
