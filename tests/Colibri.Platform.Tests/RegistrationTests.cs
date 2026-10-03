using Colibri.Core.Platform;
using Colibri.Platform.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Colibri.Platform.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public async Task Every_platform_service_resolves_on_this_os()
    {
        using var provider = BuildProvider(null);

        // IAppPaths is left out: it creates the real data folder.
        Assert.NotNull(provider.GetRequiredService<INotificationService>());
        Assert.NotNull(provider.GetRequiredService<IShellService>());
        Assert.NotNull(provider.GetRequiredService<ITaskbarProgress>());
        Assert.NotNull(provider.GetRequiredService<IAria2Locator>());
        Assert.True(provider.GetRequiredService<IAutostartService>().IsSupported);

        // Never throws, whatever the desktop (CI runners have no tray host and often no session bus).
        _ = await provider.GetRequiredService<ITrayAvailability>().IsAvailableAsync();
    }

    [Fact]
    public void App_supplied_notification_texts_are_used()
    {
        var texts = new NotificationTexts { DownloadCompleteTitle = "Descarga completada" };

        using var provider = BuildProvider(texts);

        Assert.Same(texts, provider.GetRequiredService<NotificationTexts>());
    }

    [Fact]
    public void English_texts_are_used_when_the_app_supplies_none()
    {
        using var provider = BuildProvider(null);

        Assert.Equal(new NotificationTexts(), provider.GetRequiredService<NotificationTexts>());
    }

    private static ServiceProvider BuildProvider(NotificationTexts? texts)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddColibriPlatform(texts);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
