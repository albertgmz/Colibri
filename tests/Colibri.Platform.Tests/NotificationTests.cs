using Colibri.Core.Platform;
using Colibri.Platform.Notifications;

namespace Colibri.Platform.Tests;

public sealed class NotificationTests
{
    // ---- Windows toast arguments ----

    [Theory]
    [InlineData(NotificationAction.Open)]
    [InlineData(NotificationAction.ShowInFolder)]
    [InlineData(NotificationAction.Retry)]
    public void Toast_arguments_round_trip_the_action_and_download_id(NotificationAction action)
    {
        var id = Guid.NewGuid();

        var decoded = ToastArguments.Decode(ToastArguments.Encode(action, id));

        Assert.Equal(new NotificationActionInvoked(id, action), decoded);
    }

    [Fact]
    public void Toast_arguments_are_short_and_contain_no_spaces()
    {
        var encoded = ToastArguments.Encode(NotificationAction.ShowInFolder, Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));

        Assert.Equal("folder;0f8fad5bd9cb469fa16570867728950e", encoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("open")]
    [InlineData("open;not-a-guid")]
    [InlineData("launch;0f8fad5bd9cb469fa16570867728950e")]
    [InlineData("OPEN;0f8fad5bd9cb469fa16570867728950e")]
    [InlineData("open;0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("open;0f8fad5bd9cb469fa16570867728950e;extra")]
    [InlineData(";0f8fad5bd9cb469fa16570867728950e")]
    public void Toast_arguments_that_colibri_did_not_write_are_rejected(string? arguments)
    {
        Assert.Null(ToastArguments.Decode(arguments));
    }

    // ---- Linux (D-Bus) ----

    [Theory]
    [InlineData("default", NotificationAction.Open)]
    [InlineData("open", NotificationAction.Open)]
    [InlineData("folder", NotificationAction.ShowInFolder)]
    [InlineData("retry", NotificationAction.Retry)]
    public void Dbus_action_keys_map_to_notification_actions(string key, NotificationAction expected)
    {
        Assert.Equal(expected, LinuxNotificationService.ActionFromKey(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Open")]
    [InlineData("dismiss")]
    public void Unknown_dbus_action_keys_are_ignored(string key)
    {
        Assert.Null(LinuxNotificationService.ActionFromKey(key));
    }

    [Fact]
    public void Notification_body_is_escaped_only_for_servers_with_markup()
    {
        const string body = "Tom & Jerry <1>.mp4";

        Assert.Equal("Tom &amp; Jerry &lt;1&gt;.mp4", LinuxNotificationService.FormatBody(body, supportsMarkup: true));
        Assert.Equal(body, LinuxNotificationService.FormatBody(body, supportsMarkup: false));
    }

    // ---- macOS (osascript) ----

    [Theory]
    [InlineData("plain.zip", "\"plain.zip\"")]
    [InlineData("say \"hi\".txt", "\"say \\\"hi\\\".txt\"")]
    [InlineData(@"back\slash", "\"back\\\\slash\"")]
    [InlineData("line1\nline2", "\"line1\\nline2\"")]
    [InlineData("a\tb\r", "\"a\\tb\\r\"")]
    [InlineData("", "\"\"")]
    public void AppleScript_strings_escape_quotes_backslashes_and_line_breaks(string value, string expected)
    {
        Assert.Equal(expected, MacNotificationService.AppleScriptString(value));
    }

    [Fact]
    public void AppleScript_notification_script_cannot_be_broken_out_of()
    {
        // A file name that tries to end the string and run another command stays inside the literal.
        var script = MacNotificationService.BuildScript("Download complete", "x\" & do shell script \"rm -rf ~\" & \"");

        Assert.Equal(
            "display notification \"x\\\" & do shell script \\\"rm -rf ~\\\" & \\\"\" with title \"Download complete\"",
            script);
    }

    [Fact]
    public void Default_notification_texts_are_english()
    {
        var texts = new NotificationTexts();

        Assert.Equal("Download complete", texts.DownloadCompleteTitle);
        Assert.Equal("Download failed", texts.DownloadFailedTitle);
        Assert.Equal("Show in folder", texts.ShowInFolder);
    }
}
