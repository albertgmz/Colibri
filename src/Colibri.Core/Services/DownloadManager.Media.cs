using Colibri.Core.Media;
using Colibri.Core.Models;
using Colibri.Core.Network;
namespace Colibri.Core.Services;
public sealed partial class DownloadManager
{
    public Task<DownloadItem> AddMediaAsync(string source, LinkContext context, MediaSelection selection,
        string? fileName, string? folder, Guid queueId, DownloadNetworkPolicy? network, CancellationToken ct) =>
        RunLockedAsync(async changes => {
            if (!UrlPolicy.TryValidate(source, out var uri, out string? _) || uri!.Scheme is not ("http" or "https")) throw new MediaHelperException("Enter an HTTP or HTTPS media URL.");
            selection.Validate();
            var policy = network ?? _settings.DefaultNetworkPolicy;
            MediaPolicy.Validate(policy, context);
            if (!_settings.MediaEnabled) throw new MediaHelperException("Optional media tools are disabled.");
            if (!_queues.ContainsKey(queueId)) throw new MediaHelperException("The selected queue is unavailable.");
            return await AddOneAsync(new DownloadRequest {
                Uri = uri, SuggestedFileName = FileNameSanitizer.Sanitize(fileName ?? "media") + "." + selection.OutputExtension,
                Headers = context.Headers, Cookies = context.Cookies, Referrer = context.Referrer, UserAgent = context.UserAgent,
                MediaSelection = selection, NetworkPolicy = policy
            }, null, folder, changes, ct, queueId);
        }, ct);
}
