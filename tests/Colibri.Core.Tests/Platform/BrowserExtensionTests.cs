using System.Security.Cryptography;
using Colibri.Core.Platform;

namespace Colibri.Core.Tests.Platform;

public class BrowserExtensionTests
{
    // Must match "key" in manifest.base.json of the albertgmz/colibri-browser-integration repository.
    private const string PublishedKey = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAuywiCytcWjzgmmxtn25LYgdty1uuY6EbUgfIkIVA7ijsyJtRIDrjTY58W8RUjRCNTohI/5N1oBVo7s+PnYv7n8BRLRPzbvieKFYn2/FieAk2Ac3odOYFsaRrzMsbV8X/+UFlIBwQnwWw7dBmy9+iMbtH7ni72oKJJRiTiDRaTfgCbAc/GPMFjgnZM4CGgvcORbXlihFBv3+qIBKONRJKxEyo7c7JA2yMMp8VKfLApzLYZGppIK36euwFJHCgz1+xbAxWBr6ZDGYvkhpckJTPzbwiOBh/F8tNVAnbR7VRgXGI6BfKRS2O+R/Ek83HVxB5DzRJE2GDwxGtuyLA23QNawIDAQAB";

    [Fact]
    public void Id_matches_the_published_extension_key()
    {
        var publicKey = Convert.FromBase64String(PublishedKey);

        // Chrome's rule: SHA-256 of the DER public key, first 16 bytes as hex, digits 0-f written as a-p.
        var hex = Convert.ToHexStringLower(SHA256.HashData(publicKey), 0, 16);
        var id = new string(hex.Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());

        Assert.Equal(BrowserExtension.Id, id);
        Assert.Equal(BrowserExtension.AllowedOrigin, $"chrome-extension://{id}/");
    }
}
