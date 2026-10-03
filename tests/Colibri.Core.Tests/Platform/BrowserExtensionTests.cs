using System.Security.Cryptography;
using System.Text.Json;
using Colibri.Core.Platform;

namespace Colibri.Core.Tests.Platform;

public class BrowserExtensionTests
{
    [Fact]
    public void Id_matches_the_key_in_the_extension_manifest()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "extension", "manifest.json")));
        var publicKey = Convert.FromBase64String(manifest.RootElement.GetProperty("key").GetString()!);

        // Chrome's rule: SHA-256 of the DER public key, first 16 bytes as hex, digits 0-f written as a-p.
        var hex = Convert.ToHexStringLower(SHA256.HashData(publicKey), 0, 16);
        var id = new string(hex.Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());

        Assert.Equal(BrowserExtension.Id, id);
        Assert.Equal(BrowserExtension.AllowedOrigin, $"chrome-extension://{id}/");
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Colibri.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("The repository root (Colibri.slnx) was not found.");
    }
}
