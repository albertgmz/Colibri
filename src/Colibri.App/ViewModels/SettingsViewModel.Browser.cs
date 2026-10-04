using System.Collections.ObjectModel;
using Colibri.Core.Services;
using Colibri.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Colibri.App.ViewModels;

public partial class SettingsViewModel
{
    public string LastCaptureDecision => _settings.LastBrowserCaptureReason switch {
        CaptureCatalog.Reasons.Safety => Strings.CaptureReasonSafety, CaptureCatalog.Reasons.Minimum => Strings.CaptureReasonMinimum,
        CaptureCatalog.Reasons.Disabled => Strings.CaptureReasonDisabled, CaptureCatalog.Reasons.Preference => Strings.CaptureReasonPreference,
        _ => Strings.CaptureReasonNone
    };
    public ObservableCollection<CaptureCategoryPreference> CaptureCategories { get; } = [];
    public string[] CaptureActionLabels { get; } = [Strings.CaptureActionCapture, Strings.CaptureActionAsk, Strings.CaptureActionBrowser];
    [ObservableProperty] private string _captureExtensionOverrides = "";
    [ObservableProperty] private string _captureExclusionRules = "";
    [ObservableProperty] private string? _capturePolicyError;
    [ObservableProperty] private bool _browserCaptureEnabled;
    [ObservableProperty] private bool _browserCapturePrivate;

    private void LoadCapturePolicy()
    {
        var policy = _settings.BrowserCapturePolicy ?? BrowserCaptureRules.Migrate(_settings.BrowserCaptureExtensions);
        _settings.BrowserCapturePolicy = policy;
        OnPropertyChanged(nameof(LastCaptureDecision));
        CaptureCategories.Clear();
        foreach (var category in CaptureCatalog.Categories)
            CaptureCategories.Add(new(category.Id, CategoryFolders.First(c => c.Category == category.Category).Label, CaptureActionLabels,
                ActionIndex(policy.Categories.GetValueOrDefault(category.Id) ?? "ask"), (id, index) => {
                    var previous = _settings.BrowserCapturePolicy!;
                    var current = new BrowserCapturePolicy(new(previous.Categories), new(previous.Extensions));
                    current.Categories[id] = ActionName(index);
                    foreach (var extension in CaptureCatalog.Categories.First(c => c.Id == id).Extensions) current.Extensions.Remove(extension);
                    Change(() => _settings.BrowserCapturePolicy = current);
                    SetQuietly(() => CaptureExtensionOverrides = FormatOverrides(current));
                }));
        CaptureExtensionOverrides = FormatOverrides(policy);
        CaptureExclusionRules = string.Join(Environment.NewLine, _settings.BrowserExclusionRules);
        BrowserCaptureEnabled = _settings.BrowserCaptureEnabled;
        BrowserCapturePrivate = _settings.BrowserCapturePrivate;
    }
    private static string FormatOverrides(BrowserCapturePolicy p) => string.Join(", ", p.Extensions.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
    private static int ActionIndex(string action) => action == "capture" ? 0 : action == "ask" ? 1 : 2;
    private static string ActionName(int index) => index == 0 ? "capture" : index == 1 ? "ask" : "browser";
    partial void OnBrowserCaptureEnabledChanged(bool value) => Change(() => _settings.BrowserCaptureEnabled = value);
    partial void OnBrowserCapturePrivateChanged(bool value) => Change(() => _settings.BrowserCapturePrivate = value);
    partial void OnCaptureExtensionOverridesChanged(string value)
    {
        if (_loading) return;
        var map = new Dictionary<string, string>();
        foreach (var entry in value.Split([',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            var pair = entry.Split('=', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || pair[0].Length is < 1 or > 16 || !pair[0].All(char.IsAsciiLetterOrDigit) || !BrowserCaptureRules.IsAction(pair[1]) || map.Count >= 256) {
                CapturePolicyError = Strings.CapturePolicyInvalid; return;
            }
            map[pair[0].ToLowerInvariant()] = pair[1];
        }
        CapturePolicyError = null;
        Change(() => _settings.BrowserCapturePolicy = new(new(_settings.BrowserCapturePolicy!.Categories), map));
    }
    partial void OnCaptureExclusionRulesChanged(string value)
    {
        if (_loading) return;
        var rules = new List<string>();
        foreach (var entry in value.Split(['\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            if (rules.Count >= 256 || !BrowserCaptureRules.TryNormalizeRule(entry, out var rule)) { CapturePolicyError = Strings.CaptureExclusionInvalid; return; }
            rules.Add(rule);
        }
        CapturePolicyError = null;
        Change(() => _settings.BrowserExclusionRules = rules.Distinct().ToList());
    }
}
public sealed partial class CaptureCategoryPreference : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string[] Actions { get; }
    private readonly Action<string, int> _changed;
    [ObservableProperty] private int _actionIndex;
    public CaptureCategoryPreference(string id, string name, string[] actions, int index, Action<string, int> changed) {
        Id = id; Name = name; Actions = actions; _actionIndex = index; _changed = changed;
    }
    partial void OnActionIndexChanged(int value) => _changed(Id, value);
}
