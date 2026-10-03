namespace Colibri.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Loads the settings, returning defaults when none are stored yet.</summary>
    Task<AppSettings> LoadAsync(CancellationToken ct);

    /// <summary>Saves the settings.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken ct);
}
