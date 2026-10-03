using Colibri.Core.Models;
using Colibri.Core.Services;

namespace Colibri.Core.Tests.Services;

public class CategoryMapperTests
{
    [Theory]
    [InlineData("archive.zip", DownloadCategory.Compressed)]
    [InlineData("archive.rar", DownloadCategory.Compressed)]
    [InlineData("archive.7z", DownloadCategory.Compressed)]
    [InlineData("disk.iso", DownloadCategory.Compressed)]
    [InlineData("data.zst", DownloadCategory.Compressed)]
    [InlineData("report.pdf", DownloadCategory.Documents)]
    [InlineData("letter.docx", DownloadCategory.Documents)]
    [InlineData("sheet.xlsx", DownloadCategory.Documents)]
    [InlineData("book.epub", DownloadCategory.Documents)]
    [InlineData("notes.txt", DownloadCategory.Documents)]
    [InlineData("song.mp3", DownloadCategory.Music)]
    [InlineData("song.flac", DownloadCategory.Music)]
    [InlineData("voice.opus", DownloadCategory.Music)]
    [InlineData("setup.exe", DownloadCategory.Programs)]
    [InlineData("installer.msi", DownloadCategory.Programs)]
    [InlineData("App.dmg", DownloadCategory.Programs)]
    [InlineData("tool.AppImage", DownloadCategory.Programs)]
    [InlineData("package.deb", DownloadCategory.Programs)]
    [InlineData("movie.mp4", DownloadCategory.Video)]
    [InlineData("movie.mkv", DownloadCategory.Video)]
    [InlineData("clip.webm", DownloadCategory.Video)]
    public void Maps_known_extensions(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, CategoryMapper.FromFileName(fileName));
    }

    [Theory]
    [InlineData("ARCHIVE.ZIP", DownloadCategory.Compressed)]
    [InlineData("Movie.Mp4", DownloadCategory.Video)]
    [InlineData("SETUP.EXE", DownloadCategory.Programs)]
    public void Is_case_insensitive(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, CategoryMapper.FromFileName(fileName));
    }

    [Theory]
    [InlineData("linux-6.0.tar.gz")]
    [InlineData("backup.TAR.XZ")]
    [InlineData("source.tar.bz2")]
    [InlineData("source.tar.lz4")]
    public void Tar_double_extensions_are_compressed(string fileName)
    {
        Assert.Equal(DownloadCategory.Compressed, CategoryMapper.FromFileName(fileName));
    }

    [Theory]
    [InlineData("setup.tar.exe", DownloadCategory.Programs)]
    [InlineData("backup.tar.mp4", DownloadCategory.Video)]
    [InlineData("notes.tar.pdf", DownloadCategory.Documents)]
    public void Known_last_extension_wins_over_the_tar_rule(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, CategoryMapper.FromFileName(fileName));
    }

    [Theory]
    [InlineData("README")]
    [InlineData("file.unknownext")]
    [InlineData("trailing.")]
    [InlineData("download.mp4.part")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Unknown_or_missing_extension_is_other(string? fileName)
    {
        Assert.Equal(DownloadCategory.Other, CategoryMapper.FromFileName(fileName));
    }
}
