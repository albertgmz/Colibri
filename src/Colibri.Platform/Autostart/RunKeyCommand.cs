namespace Colibri.Platform.Autostart;

/// <summary>
/// The command line stored in the Windows Run key. Pure string handling, so it is testable on every OS.
/// </summary>
internal static class RunKeyCommand
{
    /// <summary>The exe path is quoted: it usually contains spaces ("C:\Program Files\...").</summary>
    public static string Build(string executablePath) =>
        $"\"{executablePath}\" {AutostartTarget.MinimizedArgument}";

    /// <summary>The program part of a command line: the quoted part, or everything up to the first space.</summary>
    public static string ExecutableOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote < 0 ? command[1..] : command[1..closingQuote];
        }

        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }
}
