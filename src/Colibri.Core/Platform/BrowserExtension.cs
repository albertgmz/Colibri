namespace Colibri.Core.Platform;

/// <summary>
/// The Colibri browser extension (the <c>albertgmz/colibri-browser-integration</c> repository) as the native-messaging host knows it.
/// </summary>
/// <remarks>
/// The ID follows from the public key in that repository's <c>manifest.base.json</c> ("key"): the first 32 hex digits
/// of the SHA-256 of the key, written with the letters a-p instead of 0-f. The key fixes the ID of the
/// unpacked extension wherever its folder is; a store build gets its own ID. A unit test recomputes the ID
/// from a pinned copy of that key; update the copy whenever the extension's key changes.
/// </remarks>
public static class BrowserExtension
{
    public const string Id = "lelenggmjjaffoebgecdpemmjakhofni";

    /// <summary>The origin the browser passes to the host, and the only entry of the manifest's allowed_origins.</summary>
    public const string AllowedOrigin = "chrome-extension://" + Id + "/";
}
