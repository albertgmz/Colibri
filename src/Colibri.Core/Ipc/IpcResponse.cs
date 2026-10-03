namespace Colibri.Core.Ipc;

/// <summary>The answer to an <see cref="IpcRequest"/>.</summary>
/// <param name="Ok">Whether the request was accepted.</param>
/// <param name="Error">Why it was not, in English (it is shown in logs, not to the user).</param>
public sealed record IpcResponse(bool Ok, string? Error = null)
{
    public static IpcResponse Success { get; } = new(true);

    public static IpcResponse Failure(string error) => new(false, error);
}
