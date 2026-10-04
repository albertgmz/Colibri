using Colibri.Core.Models;

namespace Colibri.Core.Ipc;

/// <summary>A request sent to the running Colibri over the local pipe.</summary>
public abstract record IpcRequest;

/// <summary>
/// Sent by a second Colibri process: bring the running one to the front.
/// </summary>
/// <param name="Args">The second process's command-line arguments (for example <c>--minimized</c>).</param>
public sealed record ActivateRequest(IReadOnlyList<string> Args) : IpcRequest;

/// <summary>
/// A download captured in the browser. The payload is untrusted; <see cref="IpcProtocol"/> has already
/// validated it when this object exists.
/// </summary>
/// <param name="Url">The download URL, accepted by <c>UrlPolicy</c>.</param>
/// <param name="FinalUrl">The URL after redirects, when the browser knows it (also accepted by <c>UrlPolicy</c>).</param>
/// <param name="Context">Referrer, cookies, forwardable headers, user agent, file name, size and MIME type.</param>
public sealed record AddRequest(string Url, string? FinalUrl, LinkContext Context, string? CaptureAction = null, bool PrivateWindow = false) : IpcRequest;

/// <summary>Sent by the native-messaging host to check that Colibri is running; always answered "ok".</summary>
public sealed record PingRequest : IpcRequest;

/// <summary>
/// Sent by the native-messaging host for the browser extension: which downloads to capture. Answered with
/// <see cref="IpcResponse.Config"/>.
/// </summary>
public sealed record ConfigRequest : IpcRequest;
