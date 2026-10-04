using Colibri.Core.Models;
using Colibri.Core.Services;
using Colibri.Core.Settings;

namespace Colibri.Core.Tests.Services;

public class CaptureCatalogTests
{
    [Theory]
    [InlineData("app.apk", DownloadCategory.Programs)]
    [InlineData("app.APK", DownloadCategory.Programs)]
    [InlineData("report.pdf.exe", DownloadCategory.Programs)]
    [InlineData("data.tar.unlisted", DownloadCategory.Compressed)]
    [InlineData("data.tar.exe", DownloadCategory.Programs)]
    [InlineData("file.completelyunlisted", DownloadCategory.Other)]
    public void Canonical_classification_keeps_last_extension_precedence(string name, DownloadCategory category)
    {
        Assert.Equal(category, CategoryMapper.FromFileName(name));
    }

    [Fact]
    public void Catalog_covers_stable_categories_and_keeps_legacy_default_capture_order()
    {
        Assert.Equal(Enum.GetValues<DownloadCategory>(), CaptureCatalog.Categories.Select(c => c.Category));
        Assert.Equal(new[] { "compressed", "documents", "music", "programs", "video", "other" },
            CaptureCatalog.Categories.Select(c => c.Id));
        Assert.Equal(new[] { "zip", "rar", "7z", "gz", "tar", "exe", "msi", "dmg", "pkg", "deb", "rpm", "appimage", "iso",
            "pdf", "epub", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "mp3", "flac", "wav", "mp4", "mkv", "avi", "mov", "webm" },
            new AppSettings().BrowserCaptureExtensions);
    }

    [Fact]
    public void Default_settings_do_not_share_mutable_extension_lists()
    {
        var settings = new AppSettings();
        settings.BrowserCaptureExtensions.Clear();
        Assert.NotEmpty(new AppSettings().BrowserCaptureExtensions);
    }
}
