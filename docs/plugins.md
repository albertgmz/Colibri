# Writing a link resolver

A link resolver turns a link the user gives Colibri (typed into Add URL, or captured from the browser)
into the actual files to download. The built-in `DirectLinkResolver` downloads the URL as it is. A
site-specific resolver can do more: turn a file-sharing page into its direct link, expand a folder link
into several files, add the headers a site needs.

This guide covers the contract, a complete example, and how to register a resolver.

## Status: no plugin loading yet

v1 has no dynamic loading. A resolver is registered in the app's dependency injection setup and compiled
into Colibri (see [Registering a resolver today](#registering-a-resolver-today)).

The plan is a `plugins/` folder next to the executable whose assemblies are loaded at startup. That is
why Native AOT and trimming are not turned on for any build: both remove or precompile code ahead of
time and cannot load arbitrary assemblies at runtime.

The contracts are already in the shape a plugin needs. They live in `Colibri.Core`, which has no
dependency on the app, Avalonia, aria2 or any OS code; its only package dependency is
`Microsoft.Extensions.Logging.Abstractions`. A resolver written against `Colibri.Core` today will keep
working when the loader arrives.

## The contract

`src/Colibri.Core/Abstractions/ILinkResolver.cs`:

```csharp
public interface ILinkResolver
{
    /// <summary>Stable resolver id, used in logs.</summary>
    string Id { get; }

    /// <summary>Resolvers with a higher priority are asked first.</summary>
    int Priority { get; }

    /// <summary>
    /// Returns the downloads for <paramref name="url"/>, or null when this resolver does not handle it.
    /// </summary>
    Task<IReadOnlyList<DownloadRequest>?> ResolveAsync(Uri url, LinkContext context, CancellationToken ct);
}
```

### How resolvers are asked

`LinkResolverPipeline` (`src/Colibri.Core/Services/LinkResolverPipeline.cs`) asks every registered
resolver in order of `Priority`, highest first. Resolvers with the same priority keep their registration
order. `DirectLinkResolver` has `int.MinValue`, so it always runs last and accepts every link. Any
priority above that runs before it. There is no other fixed scale yet: use a plain value such as `100`
and leave gaps.

What your answer means:

| You return | Meaning |
|---|---|
| `null` | Not my link. The next resolver is asked. |
| An empty list | My link, but there is nothing to download (file deleted, empty folder). The chain **stops** here and the user sees "nothing to download". |
| A list of requests | My link; these are the files. The chain stops. One request is the usual case; several become several downloads. |
| An exception | Logged as a warning, and the next resolver is asked, as if you had returned `null`. In v1 that is the direct-link fallback, which downloads the original URL as it is (for a share page, the HTML page). |

If the caller's token is cancelled, an `OperationCanceledException` is passed on, not swallowed.

Decide quickly. Every link goes through your resolver until you say no, so the "not mine" check should be
a cheap test on the URL (host, path) with no I/O.

### What you receive

`url` is an absolute `http`, `https` or `ftp` URL that has already passed `UrlPolicy`.

`LinkContext` (`src/Colibri.Core/Models/LinkContext.cs`) is what is known about the link besides its URL.
For a browser capture most fields are filled; for a URL typed into Add URL it is usually empty.

| Field | Type | Meaning |
|---|---|---|
| `Referrer` | `string?` | The page the link came from. |
| `Cookies` | `string?` | Raw `Cookie` header value for the URL. |
| `Headers` | `IReadOnlyDictionary<string, string>` | Extra request headers (case-insensitive names). |
| `UserAgent` | `string?` | The browser's user agent. |
| `FileName` | `string?` | File name proposed by the browser; not sanitized. |
| `Size` | `long?` | Expected size in bytes, if known. |
| `MimeType` | `string?` | MIME type, if known. |

### What you return

`DownloadRequest` (`src/Colibri.Core/Models/DownloadRequest.cs`) is an immutable record; build it with an
object initializer.

| Field | Type | Meaning |
|---|---|---|
| `Uri` | `Uri` (required) | What to download. Must be an **absolute** `http`, `https` or `ftp` URI; anything else is dropped. |
| `SuggestedFileName` | `string?` | File name proposed by the source. Need not be safe; Colibri sanitizes it. |
| `Headers` | `IReadOnlyDictionary<string, string>` | Extra request headers. Default: empty. Headers that must not reach the engine (such as `Range`, `Host`, `Accept-Encoding`, `Proxy-*`) are dropped; see below. |
| `Referrer` | `string?` | Sent as `Referer`. |
| `UserAgent` | `string?` | Sent as `User-Agent`. |
| `Cookies` | `string?` | Raw `Cookie` header value. Stored with the download in plain text (decision 7). |
| `Size` | `long?` | Expected size in bytes, shown until the engine knows better. |
| `MimeType` | `string?` | MIME type, if known. |

Nothing from the context is copied for you. If the direct link still needs the browser's cookies or user
agent, copy them into the request yourself. Only send cookies to the host they belong to: if the direct
link is on another host (a CDN), leave `Cookies` empty.

### What Colibri does with your answer

You do not have to be defensive about these, but you should know they happen:

- Each request's URL is checked again with `UrlPolicy` (scheme, length, no spaces or control
  characters). A request that fails is dropped and logged; the others are kept. The same happens to a
  `null` entry in the list and to a request with a relative `Uri`.
- Header names and values, `Referrer`, `UserAgent` and `Cookies` are checked with the `HttpHeaders`
  validators. An invalid header is dropped; an invalid `Referrer`, `UserAgent` or `Cookies` value
  becomes `null`. This blocks header injection (CR/LF in a value).
- Headers the engine must set itself or that belong to the browser's own connection are dropped
  (`HttpHeaders.IsForwardable`): `Range`, `Host`, `Content-Length`, `Accept-Encoding`, `Connection`,
  `Keep-Alive`, `Transfer-Encoding`, `TE`, `Upgrade`, every `Proxy-*` header, and `Cookie`, `Referer` and
  `User-Agent` (use the `Cookies`, `Referrer` and `UserAgent` fields for those). This applies to every
  resolver, including `DirectLinkResolver` passing on the browser's headers.
- The file name is passed through `FileNameSanitizer` (reserved names, invalid characters, length) and
  made unique in the target folder. The category, and so the default folder, follows from the file
  extension.
- The first engine whose `CanHandle` accepts the request downloads it (see [Engines](#engines)).

How your `SuggestedFileName` relates to the Add URL window: the window shows a file name (from the
last part of the URL) and the matching category folder as a preview, but it passes them on only if the
user changed them. So when your resolver returns exactly one request, your `SuggestedFileName` (and the
category folder that follows from it) is used unless the user edited the name, or the folder, in the
window, or the browser supplied a file name with a captured download; those win. When you return
several requests, each keeps its `SuggestedFileName`, and only a folder the user chose applies to all of
them. Decision 52 has the details.

### Threading and cancellation

- One instance of your resolver is created and shared (a singleton). `ResolveAsync` can run on any
  thread pool thread, and two calls can overlap (two Add URL windows). Keep no per-call state in fields;
  `HttpClient` is safe to share.
- Pass `ct` to every async call you make. Do not rely on it as a timeout, though: in v1 the Add URL
  window passes `CancellationToken.None`, and the user waits until you answer. Set a timeout on your
  `HttpClient` (or use your own `CancellationTokenSource` with `CancelAfter`).
- Use `await` all the way down. Never block with `.Result` or `.Wait()`.

## Example: a file-sharing page

A hypothetical site serves share pages at `https://example-host.com/file/<id>`. The page is HTML; the
real file is behind a JSON API, `GET https://example-host.com/api/files/<id>`, which answers
`{"downloadUrl": "...", "name": "...", "size": 123}`.

The resolver goes in its own class library that references only `Colibri.Core`:

```xml
<!-- src/Colibri.Resolvers.ExampleHost/Colibri.Resolvers.ExampleHost.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Colibri.Core\Colibri.Core.csproj" />
  </ItemGroup>
</Project>
```

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Colibri.Core.Abstractions;
using Colibri.Core.Models;

namespace ExampleHost;

/// <summary>
/// Turns share pages such as https://example-host.com/file/abc123 into the direct download link,
/// using the site's (hypothetical) JSON API.
/// </summary>
public sealed partial class ExampleHostResolver : ILinkResolver
{
    private readonly HttpClient _http;

    public ExampleHostResolver(HttpClient http)
    {
        _http = http;
    }

    public string Id => "example-host";

    // Anything above int.MinValue runs before the direct-link fallback.
    public int Priority => 100;

    public async Task<IReadOnlyList<DownloadRequest>?> ResolveAsync(Uri url, LinkContext context, CancellationToken ct)
    {
        // Not ours: answer null quickly, without any I/O, so the next resolver is asked.
        if (!string.Equals(url.Host, "example-host.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = SharePage().Match(url.AbsolutePath);
        if (!match.Success)
        {
            return null;
        }

        var api = new Uri($"https://example-host.com/api/files/{match.Groups["id"].Value}");
        using var response = await _http.GetAsync(api, ct);

        // Ours, but the file is gone: an empty list stops the chain and the user sees
        // "nothing to download" instead of Colibri saving the HTML share page.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        // Any other failure throws; the pipeline logs it and falls back to the next resolver.
        response.EnsureSuccessStatusCode();
        var file = await response.Content.ReadFromJsonAsync<FileAnswer>(ct)
            ?? throw new InvalidDataException("example-host.com returned an empty answer.");

        if (!Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out var direct))
        {
            throw new InvalidDataException("example-host.com returned an invalid download URL.");
        }

        return
        [
            new DownloadRequest
            {
                Uri = direct,
                SuggestedFileName = file.Name,
                Size = file.Size,
                Referrer = url.AbsoluteUri,
                UserAgent = context.UserAgent,

                // The direct link is on another host (a CDN), so the share page's cookies are not sent.
            },
        ];
    }

    [GeneratedRegex("^/file/(?<id>[A-Za-z0-9]+)/?$")]
    private static partial Regex SharePage();

    // {"downloadUrl": "...", "name": "...", "size": 123}
    private sealed record FileAnswer(string DownloadUrl, string? Name, long? Size);
}
```

Notes for readers coming from other languages:

- `partial` on the class and the `[GeneratedRegex]` method let the compiler generate the regular
  expression code at build time.
- `ReadFromJsonAsync` uses web defaults: JSON property names are matched case-insensitively, so
  `downloadUrl` fills `DownloadUrl`.
- `return [];` and `return [ ... ];` are collection expressions; they create the list.
- Unknown hosts and paths return `null`, the deleted file returns `[]`, and other errors throw so that
  the fallback still gets a chance. If downloading the share page itself is never useful for your site,
  return `[]` on errors too.

To test it, give the `HttpClient` a fake `HttpMessageHandler` that returns canned responses, then check
the answer for your URL, another host's URL, a 404 and a server error (the pipeline's own tests are in
`tests/Colibri.Core.Tests/Services/LinkResolverPipelineTests.cs`). Running it through a `LinkResolverPipeline`
together with `DirectLinkResolver` also shows the fallback behaviour.

## Registering a resolver today

Resolvers are registered in `AddColibriApp` in `src/Colibri.App/AppServices.cs`, next to the built-in
one. `LinkResolverPipeline` receives every registered `ILinkResolver`, so adding one is one line:

```csharp
services.AddSingleton<ILinkResolver, DirectLinkResolver>();
services.AddSingleton<ILinkResolver>(_ => new ExampleHostResolver(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
services.AddSingleton<LinkResolverPipeline>();
```

Then add the project to `Colibri.slnx` and reference it from `src/Colibri.App/Colibri.App.csproj`:

```xml
<ProjectReference Include="..\Colibri.Resolvers.ExampleHost\Colibri.Resolvers.ExampleHost.csproj" />
```

A resolver whose constructor takes only registered services can use the short form,
`services.AddSingleton<ILinkResolver, MyResolver>();`. `ILogger<T>` is available that way.
`HttpClient` is not registered in the container, which is why the example creates its own.

## Registering a resolver later (planned, not implemented)

> **Future.** Nothing below exists yet; it describes the intended design so that resolvers written now
> fit it. Details may change.

- Colibri looks in a `plugins/` folder next to the executable, one subfolder per plugin, and loads each
  plugin's assemblies into its own `AssemblyLoadContext`. `Colibri.Core` (and the
  `Microsoft.Extensions.*` abstractions) are shared with the app, so `ILinkResolver` in the plugin is the
  same type as in Colibri.
- Every public, non-abstract class implementing `ILinkResolver` is registered as a singleton, built by
  the container, so its constructor may ask for the services Colibri offers (at least `ILogger<T>`).
- A plugin ships its own dependencies in its folder, but not `Colibri.Core`. Reference Core so that it is
  not copied:

  ```xml
  <ProjectReference Include="..\Colibri.Core\Colibri.Core.csproj" Private="false" ExcludeAssets="runtime" />
  ```

A sketch of the loader, for orientation only:

```csharp
// Future: not in the code base.
foreach (var dll in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "plugins"), "*.dll", SearchOption.AllDirectories))
{
    var assembly = new PluginLoadContext(dll).LoadFromAssemblyPath(dll);
    foreach (var type in assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILinkResolver).IsAssignableFrom(t)))
    {
        services.AddSingleton(typeof(ILinkResolver), type);
    }
}
```

## Engines

`IDownloadEngine` (`src/Colibri.Core/Engine/IDownloadEngine.cs`) is the second extension point: the
thing that actually transfers bytes. v1 has one, `Aria2Engine`. The next one expected is yt-dlp, for
video sites where a resolver alone is not enough.

The parts that matter for a new engine:

- `Id` is stored with every download (`DownloadItem.EngineId`), so it must never change.
- `CanHandle(DownloadRequest)` says whether the engine can download a request. The download manager uses
  the **first registered** engine that says yes. aria2 is registered first and accepts every `http`,
  `https` and `ftp` request, so in v1 a second engine would never be picked. Adding one will need an
  ordering rule or a way for a resolver to name the engine; that is part of the yt-dlp work.
- Handles: `CreateHandle()` returns a new handle string (for aria2 a GID). The manager stores it with the
  download **before** calling `AddAsync` with it, so a download whose add timed out can still be found
  later (decision 31). `AddAsync` can also be given an old handle to re-add a download the engine forgot
  and continue the partial file (decision 22). Every later call (`PauseAsync`, `ResumeAsync`,
  `RemoveAsync`, `GetStatusAsync`) takes that handle.
- `GetAllAsync` is polled every second (every 5 seconds while the window is hidden) and its snapshots
  update the downloads; `DownloadEvent` and `StateChanged` report lifecycle changes.
- Refused operations throw `EngineOperationException`; lost connections and timeouts surface as
  `IOException` and `TimeoutException`.

Engines are registered in `AppServices.cs` as `IDownloadEngine` singletons, like resolvers.

## Compatibility

- Reference `Colibri.Core` only. Never reference `Colibri.App`, `Colibri.Platform`,
  `Colibri.Engine.Aria2` or Avalonia from a resolver: those are not contracts and will change without
  notice.
- Target `net10.0` (not a Windows-specific framework), so one build works on every OS.
- The contracts (`ILinkResolver`, `LinkContext`, `DownloadRequest`, `IDownloadEngine` and the types they
  use) are kept stable. New optional properties may be added to `LinkContext` and `DownloadRequest`;
  existing code keeps compiling and working. Removing or changing a member is a breaking change and will
  be announced as such.
- Keep `Id` stable across versions of your resolver; it appears in logs.
