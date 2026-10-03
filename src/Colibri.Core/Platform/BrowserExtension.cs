namespace Colibri.Core.Platform;

/// <summary>
/// The Colibri browser extension (in <c>extension/</c>) as the native-messaging host knows it.
/// </summary>
/// <remarks>
/// The ID follows from the public key in <c>extension/manifest.json</c> ("key"): the first 32 hex digits
/// of the SHA-256 of the key, written with the letters a-p instead of 0-f. The key fixes the ID of the
/// unpacked extension wherever its folder is; a store build gets its own ID. A unit test recomputes the ID
/// from the manifest so the two cannot drift apart.
/// </remarks>
public static class BrowserExtension
{
    public const string Id = "lelenggmjjaffoebgecdpemmjakhofni";

    /// <summary>The origin the browser passes to the host, and the only entry of the manifest's allowed_origins.</summary>
    public const string AllowedOrigin = "chrome-extension://" + Id + "/";
}
