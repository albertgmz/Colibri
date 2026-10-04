using Colibri.Core.Models;
namespace Colibri.Core.Engine;
/// <summary>Deletes only engine-owned temporary files after transfer removal is confirmed.</summary>
public interface IEngineFileCleanup
{
    Task DeleteOwnedPartialFilesAsync(DownloadItem item, CancellationToken ct);
}
