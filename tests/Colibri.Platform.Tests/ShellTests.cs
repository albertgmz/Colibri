using Colibri.Platform.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.Platform.Tests;

[Collection("CurrentDirectory")] // see PlatformTests
public sealed class ShellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "colibri-shell-tests", Guid.NewGuid().ToString("N"));

    public ShellTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Existing_files_and_folders_are_handed_to_the_os()
    {
        var file = Path.Combine(_root, "a.zip");
        File.WriteAllText(file, string.Empty);
        var shell = new RecordingShell();

        await shell.OpenFileAsync(file);
        await shell.OpenFolderAsync(_root);
        await shell.RevealInFolderAsync(file);

        Assert.Equal([$"open {file}", $"open {_root}", $"select {file}"], shell.Calls);
    }

    [Fact]
    public async Task Revealing_a_deleted_file_opens_its_folder()
    {
        var shell = new RecordingShell();

        await shell.RevealInFolderAsync(Path.Combine(_root, "gone.zip"));

        Assert.Equal([$"open {_root}"], shell.Calls);
    }

    [Fact]
    public async Task Missing_files_and_folders_do_nothing_and_do_not_throw()
    {
        var missingFolder = Path.Combine(_root, "missing");
        var shell = new RecordingShell();

        await shell.OpenFileAsync(Path.Combine(_root, "gone.zip"));
        await shell.OpenFolderAsync(missingFolder);
        await shell.RevealInFolderAsync(Path.Combine(missingFolder, "gone.zip"));
        await shell.OpenFileAsync(string.Empty);
        await shell.RevealInFolderAsync(" ");
        await shell.OpenFileAsync("bad\0name.zip");

        Assert.Empty(shell.Calls);
    }

    [Fact]
    public async Task Os_failures_are_logged_not_thrown()
    {
        var file = Path.Combine(_root, "a.zip");
        File.WriteAllText(file, string.Empty);
        var shell = new RecordingShell { Fail = true };

        await shell.OpenFileAsync(file);
        await shell.RevealInFolderAsync(file);

        Assert.Equal(2, shell.Calls.Count);
    }

    [Fact]
    public async Task Relative_paths_are_made_absolute()
    {
        var previous = Environment.CurrentDirectory;
        File.WriteAllText(Path.Combine(_root, "-rf"), string.Empty);
        Environment.CurrentDirectory = _root;
        try
        {
            var shell = new RecordingShell();

            await shell.OpenFileAsync("-rf");

            // Compare against the current directory as the OS reports it: on macOS the temp folder
            // /var/folders/... is a symlink and comes back as /private/var/folders/...
            Assert.Equal([$"open {Path.Combine(Environment.CurrentDirectory, "-rf")}"], shell.Calls);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Theory]
    [InlineData("/home/ana/Downloads/a.zip", "file:///home/ana/Downloads/a.zip")]
    [InlineData("/home/ana/My Files/c#d%e?.txt", "file:///home/ana/My%20Files/c%23d%25e%3F.txt")]
    [InlineData("/home/ana/Descargas/añо.zip", "file:///home/ana/Descargas/a%C3%B1%D0%BE.zip")]
    public void File_uris_percent_encode_each_segment(string path, string expected)
    {
        Assert.Equal(expected, LinuxShellService.FileUri(path));
    }

    private sealed class RecordingShell() : ShellServiceBase(NullLogger.Instance)
    {
        public List<string> Calls { get; } = [];

        public bool Fail { get; init; }

        protected override Task OpenAsync(string path) => Record($"open {path}");

        protected override Task SelectAsync(string path) => Record($"select {path}");

        private Task Record(string call)
        {
            Calls.Add(call);
            return Fail ? Task.FromException(new InvalidOperationException("no file manager")) : Task.CompletedTask;
        }
    }
}
