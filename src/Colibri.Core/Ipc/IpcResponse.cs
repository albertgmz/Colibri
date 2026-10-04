namespace Colibri.Core.Ipc;

/// <summary>The answer to an <see cref="IpcRequest"/>.</summary>
/// <param name="Ok">Whether the request was accepted.</param>
/// <param name="Error">Why it was not, in English (it is shown in logs, not to the user).</param>
/// <param name="Config">The capture rules, in the answer to a <see cref="ConfigRequest"/>.</param>
public sealed record IpcResponse(bool Ok, string? Error = null, CaptureConfig? Config = null,
    string? State = null, string? CaptureId = null, int? ProtocolVersion = null,
    string? AppVersion = null, IReadOnlyList<string>? Capabilities = null, string? DecisionReason = null)
{
    public static IpcResponse Success { get; } = new(true);

    public static IpcResponse Failure(string error) => new(false, error);
}

/// <summary>Which browser downloads the extension hands over to Colibri.</summary>
/// <param name="Extensions">File extensions, lower case, without the dot.</param>
/// <param name="MinSizeKiB">Downloads known to be smaller than this stay in the browser; 0 = any size.</param>
public sealed record CaptureConfig(IReadOnlyList<string> Extensions, int MinSizeKiB,
    bool Enabled = true, IReadOnlyList<string>? ExcludedSites = null,
    bool CapturePrivate = false, string BypassModifier = "none", string Theme = "system", string Accent = "#C42B1C", string Palette = "warm", Colibri.Core.Services.BrowserCapturePolicy? CapturePolicy = null, IReadOnlyList<string>? ExclusionRules = null);
