namespace Colibri.Core.Ipc;

/// <summary>Versioned browser capabilities; all traffic uses native messaging.</summary>
public static class BrowserProtocol
{
    public const int Version = 2;
    public const string FirefoxId = "colibri-browser-integration@colibri.download";
    public static IReadOnlyList<string> Capabilities { get; } =
        ["capture-confirmation", "request-context", "bulk-add", "settings-push", "capture-policy-v1", "automatic-capture-v1"];
}

public sealed record HelloRequest(int ProtocolVersion, string ExtensionVersion, IReadOnlyList<string> Capabilities) : IpcRequest;
public sealed record OpenRequest : IpcRequest;
public sealed record CaptureStatusRequest(string CaptureId) : IpcRequest;
public sealed record CaptureCancelRequest(string CaptureId) : IpcRequest;
public sealed record BulkAddRequest(IReadOnlyList<AddRequest> Links) : IpcRequest;
public sealed record CaptureSettingsPatch(bool? Enabled = null, IReadOnlyList<string>? ExcludedSites = null,
    bool? CapturePrivate = null, string? BypassModifier = null);
public sealed record SettingsUpdateRequest(CaptureSettingsPatch Patch) : IpcRequest;
