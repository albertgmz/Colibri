using System.Globalization;
using Colibri.App.Formatting;
using Colibri.App.Resources;
using Colibri.Core.Models;
using Colibri.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Colibri.App.ViewModels;

/// <summary>
/// The Add URL window. The file name follows the URL while the user types, until they edit the name
/// themselves; the folder follows the file name's category until they choose a folder. Can be created
/// with a <see cref="LinkContext"/> from a browser capture (name, size, referrer, cookies, headers).
/// </summary>
public partial class AddUrlViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly LinkContext _context;
    private readonly ILogger _logger;

    // Above zero while this class itself sets FileName or SaveFolder (the calls nest), so those changes
    // do not count as user edits.
    private int _settingAutomatically;
    private bool _fileNameEdited;
    private bool _folderEdited;
    private DownloadItem? _duplicate;
    private string? _duplicateApprovedUrl;

    [ObservableProperty]
    private bool _isDuplicatePrompt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeDuplicateCommand))]
    private bool _canResumeDuplicate;

    public event EventHandler? RenameRequested;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _saveFolder = string.Empty;

    [ObservableProperty]
    private string _sizeText;

    [ObservableProperty]
    private string? _errorText;

    public AddUrlViewModel(DownloadManager manager, LinkContext context, string? url, ILogger logger)
    {
        _manager = manager;
        _context = context;
        _logger = logger;
        _sizeText = DisplayFormat.Size(context.Size);

        if (!string.IsNullOrWhiteSpace(context.FileName))
        {
            // A name chosen by the browser counts as chosen: typing in the URL box must not replace it.
            SetAutomatically(() => FileName = FileNameSanitizer.Sanitize(context.FileName));
            _fileNameEdited = true;
        }

        Url = url ?? string.Empty;
        UpdateFolder();
    }

    /// <summary>Raised when the window should close (after a successful add, or on Cancel).</summary>
    public event EventHandler? CloseRequested;

    partial void OnUrlChanged(string value)
    {
        IsDuplicatePrompt = false;
        _duplicateApprovedUrl = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            ErrorText = null;
            return;
        }

        if (!UrlPolicy.TryValidateWithReason(value, out var uri, out var reason))
        {
            ErrorText = Describe(reason);
            return;
        }

        ErrorText = null;
        if (!_fileNameEdited)
        {
            SetAutomatically(() => FileName = FileNameSanitizer.Sanitize(NameFromUrl(uri)));
        }
    }

    partial void OnFileNameChanged(string value)
    {
        if (_settingAutomatically == 0)
        {
            // Clearing the box hands the name back to the URL.
            _fileNameEdited = !string.IsNullOrWhiteSpace(value);
        }

        UpdateFolder();
    }

    partial void OnSaveFolderChanged(string value)
    {
        if (_settingAutomatically == 0)
        {
            _folderEdited = !string.IsNullOrWhiteSpace(value);
        }
    }

    /// <summary>
    /// A folder picked with Browse. It counts as chosen even when it is the folder already shown, which
    /// raises no change.
    /// </summary>
    public void ChooseFolder(string path)
    {
        SaveFolder = path;
        _folderEdited = !string.IsNullOrWhiteSpace(path);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (!UrlPolicy.TryValidateWithReason(Url, out _, out var reason))
        {
            ErrorText = Describe(reason);
            return;
        }

        if (!string.IsNullOrWhiteSpace(SaveFolder) && !Path.IsPathFullyQualified(SaveFolder))
        {
            ErrorText = Strings.AddUrlFolderNotFullPath;
            return;
        }

        try
        {
            if (_duplicateApprovedUrl != Url.Trim())
            {
                _duplicate = (await _manager.GetItemsAsync(CancellationToken.None))
                    .OrderByDescending(i => i.AddedAt).FirstOrDefault(i => i.Url == Url.Trim());
                if (_duplicate is not null)
                {
                    CanResumeDuplicate = _duplicate.State is DownloadState.Paused or DownloadState.Failed;
                    IsDuplicatePrompt = true;
                    return;
                }
            }
            // The name and folder shown are only a preview of what the URL suggests; unless the user (or the
            // browser, for the name) chose them, the link resolvers decide (DECISIONS 52).
            var added = await _manager.AddAsync(
                Url.Trim(), _context, _fileNameEdited ? FileName : null, _folderEdited ? SaveFolder : null, CancellationToken.None);
            if (added.Count == 0)
            {
                ErrorText = Strings.AddUrlNothingToDownload;
                return;
            }

            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adding a download failed");
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.AddUrlFailedFormat, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanResumeDuplicate))]
    private async Task ResumeDuplicateAsync()
    {
        if (_duplicate is null) return;
        try
        {
            await _manager.ResumeAsync([_duplicate.Id], CancellationToken.None);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resuming a duplicate download failed");
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.AddUrlFailedFormat, ex.Message);
        }
    }

    [RelayCommand]
    private async Task RedownloadDuplicateAsync()
    {
        _duplicateApprovedUrl = Url.Trim();
        IsDuplicatePrompt = false;
        await DownloadAsync();
    }

    [RelayCommand]
    private void RenameDuplicate()
    {
        _duplicateApprovedUrl = Url.Trim();
        IsDuplicatePrompt = false;
        RenameRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateFolder()
    {
        if (!_folderEdited)
        {
            SetAutomatically(() => SaveFolder = _manager.GetCategoryFolder(CategoryMapper.FromFileName(FileName)));
        }
    }

    private void SetAutomatically(Action set)
    {
        _settingAutomatically++;
        try
        {
            set();
        }
        finally
        {
            _settingAutomatically--;
        }
    }

    /// <summary>The last path segment, URL-decoded ("" when the path ends with a slash).</summary>
    private static string NameFromUrl(Uri uri)
    {
        var last = uri.Segments.Length > 0 ? uri.Segments[^1] : string.Empty;
        return last.EndsWith('/') ? string.Empty : Uri.UnescapeDataString(last);
    }

    private static string Describe(UrlValidationError reason) => reason switch
    {
        UrlValidationError.Empty => Strings.UrlErrorEmpty,
        UrlValidationError.TooLong => Strings.UrlErrorTooLong,
        UrlValidationError.InvalidCharacters => Strings.UrlErrorInvalidCharacters,
        UrlValidationError.NotAbsolute => Strings.UrlErrorNotAbsolute,
        UrlValidationError.UnsupportedScheme => Strings.UrlErrorUnsupportedScheme,
        _ => Strings.UrlErrorNoHost,
    };
}
