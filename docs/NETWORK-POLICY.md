# Network policy boundary

`DownloadNetworkPolicy` is shared by settings, persisted download requests and engines.
Null requests inherit the default; an explicit empty policy chooses the system route.
Each added aria2 GID retains the effective policy used for that request. Changing an
individual policy pauses the transfer, waits until it is no longer active and updates
options; it does not resume the transfer. Proxy credentials belong in the protected
credential payload, never plain settings, request logs or command arguments.

Stored network policies use a versioned purpose envelope and a network-specific binding
derived from the download ID, preventing same-row header ciphertext substitution.
An exact nonsecret marker represents an explicit empty/direct policy without requiring
an unlocked OS vault. Legacy NULL policies also become explicit direct policies; they
never inherit newly configured credentials. Credential cleanup preserves the endpoint
and required adapter while stripping proxy identity and password. Reading or clearing
nonempty encrypted policies requires the vault; unavailable or corrupt data fails closed.
An existing unreadable settings file blocks startup rather than falling back to a route.

## Supported routing

aria2 supports an HTTP proxy endpoint for HTTP, HTTPS (CONNECT) and FTP transfers.
The engine explicitly supplies every protocol proxy option, clears `no-proxy`, and
removes inherited proxy environment variables. An explicit proxy has no configured
direct fallback. This is routing, not an anonymity or DNS-isolation guarantee: resolving
the proxy host still uses the system resolver. The system route has no proxy endpoint.

HTTPS proxy transport and SOCKS5 are represented and validated by the shared contract,
but rejected by this engine before adding work. The official aria2 proxy format is
`[http://][USER:PASSWORD@]HOST[:PORT]`, including its `https-proxy` setting; that setting
means an HTTP proxy serving an HTTPS destination.
[aria2 manual](https://aria2.github.io/manual/en/html/aria2c.html#cmdoption-all-proxy).

Native engine logs are disabled and redirected output is drained without logging.
RPC failures and download error status show safe operation text/numeric error codes,
because native text can echo secrets. Session saving remains disabled. Automatically
following downloaded torrents and Metalinks is disabled so fetched metadata cannot
create ungoverned transfers.

## Strict adapter limitation

The platform inventories adapters, connection state and both IP address families.
It reports `SupportsStrictEnforcement = false` on Windows, Linux and macOS. Choosing
any required interface blocks aria2 launch, add and resume. Applying an unsupported
default policy first stops the owned engine, then rejects the setting. There is no
source-address binding presented as a kill switch.

No OS firewall isolation, DNS confinement or subprocess socket isolation is installed.
Consequently strict policies remain rejected for connected and disconnected adapters,
IPv4/IPv6 address changes, DNS, proxy traffic, torrent traffic and future subprocesses.
Future engines, resolvers and probes must refuse these policies before network I/O
until they have an enforceable isolation boundary. The current direct resolver does
no network I/O. New torrent/media implementations must not silently bypass this guard.

Windows provides process/interface filtering through WFP, but this implementation does
not install filters or modify the user's firewall.
[Microsoft WFP overview](https://learn.microsoft.com/en-us/windows/win32/fwp/windows-filtering-platform-start-page).
The aria2 `interface` option describes socket binding rather than this complete isolation
contract. [aria2 interface option](https://aria2.github.io/manual/en/html/aria2c.html#cmdoption-interface).

Tests verify refusal before RPC, supported proxy serialization, credential-safe diagnostics
and explicit direct overrides. They do not prove adapter routing, packet-level leak
prevention, disconnected-adapter zero bytes or cross-platform runtime behavior.
