using Colibri.Core.Engine;

namespace Colibri.Engine.Aria2;

/// <summary>
/// What <see cref="Aria2Engine"/> reads each time it starts.
/// </summary>
/// <param name="Aria2Path">Configured path to aria2c; null or empty means the platform default.</param>
/// <param name="Options">Engine-wide download settings.</param>
public sealed record Aria2Settings(string? Aria2Path, EngineOptions Options);
