using Colibri.Core.Services;
using Colibri.Core.Settings;
namespace Colibri.Core.Tests.Services;
public class BrowserCaptureRulesTests
{
    [Theory]
    [InlineData(".exe", "programs", "")]
    [InlineData("C:\\temp\\report.pdf.exe", "programs", "exe")]
    [InlineData("/temp.tar.apk/data.tar.unknown", "compressed", "unknown")]
    [InlineData("/temp.exe/no-extension", "other", "")]
    [InlineData("report.pdf.exe", "programs", "exe")]
    [InlineData(".hidden.apk", "programs", "apk")]
    public void ClassificationMatchesCategoryMapperOnBasename(string name, string category, string extension)
    {
        Assert.Equal(CategoryMapper.FromFileName(BrowserCaptureRules.BaseName(name)), CaptureCatalog.Categories.First(c => c.Id == BrowserCaptureRules.Category(name)).Category);
        Assert.Equal(category, BrowserCaptureRules.Category(name));
        Assert.Equal(extension, BrowserCaptureRules.Extension(name));
    }
    [Fact] public void LegacyMigrationPreservesAllowlistAndOffersUnlisted() {
        var s = new AppSettings { BrowserCaptureExtensions = ["zip"] };
        Assert.Equal("ask", BrowserCaptureRules.Decide(s, "https://example.com/a", "a.zip", null).Action);
        Assert.Equal("browser", BrowserCaptureRules.Decide(s, "https://example.com/a", "a.apk", null).Action);
        Assert.Equal("ask", BrowserCaptureRules.Decide(s, "https://example.com/a", "a.neverlisted", null).Action);
        Assert.Equal("programs", BrowserCaptureRules.Category("a.apk"));
    }
    [Fact] public void ExclusionsAndSafetyAlwaysOverrideCapture() {
        var s = new AppSettings { BrowserCapturePolicy = new(new() { ["other"] = "capture" }, new()), BrowserExclusionRules = ["domain:example.com", "path:other.test/files"] };
        Assert.Equal("excluded", BrowserCaptureRules.Decide(s, "https://sub.example.com/a?secret=yes", "a.new", null).Reason);
        Assert.Equal("excluded", BrowserCaptureRules.Decide(s, "https://other.test/files/a", "a.new", null).Reason);
        Assert.Equal("capture", BrowserCaptureRules.Decide(s, "https://other.test/files-extra/a", "a.new", null).Action);
        Assert.Equal("private", BrowserCaptureRules.Decide(s, "https://other.test/a", "a.new", null, true).Reason);
        s.BrowserCaptureEnabled = false;
        Assert.Equal("excluded", BrowserCaptureRules.Decide(s, "https://sub.example.com/a", "a.new", null).Reason);
        Assert.Equal("method", BrowserCaptureRules.Decide(s, "https://other.test/a", "a.new", null, false, "POST").Reason);
    }
    [Theory] [InlineData("path:example.com/a?token=x")] [InlineData("host:https://example.com")] [InlineData("domain:example.com/anything")]
    [InlineData("host:bücher.de")] [InlineData("path:bücher.de/files")]
    public void InvalidRulesRejected(string rule) => Assert.False(BrowserCaptureRules.TryNormalizeRule(rule, out _));
}
