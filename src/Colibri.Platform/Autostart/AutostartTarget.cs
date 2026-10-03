namespace Colibri.Platform.Autostart;

/// <summary>What the autostart entries launch.</summary>
internal static class AutostartTarget
{
    /// <summary>Tells Colibri to start hidden in the tray.</summary>
    public const string MinimizedArgument = "--minimized";

    /// <summary>
    /// The running executable. For a published app this is Colibri itself (inside a macOS .app bundle it is
    /// <c>Colibri.app/Contents/MacOS/&lt;name&gt;</c>, which launchd can start directly). When started as
    /// <c>dotnet Colibri.App.dll</c> it is the dotnet host, which cannot start Colibri on its own; that only
    /// happens during development.
    /// </summary>
    public static string ExecutablePath() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running executable is unknown.");
}
