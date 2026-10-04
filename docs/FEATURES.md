# Capture, queues, torrents and optional media

This document describes the supported feature boundaries of the current implementation.

## Browser capture and bulk import

Settings → Browser integration provides category and extension Capture / Ask / Keep
in browser choices, minimum size, private-window policy, and validated exclusions.
Legacy preferences migrate additively. App-owned catalog and decision reasons generate
the browser representation; drift checks guard sibling copies. A genuine browser file
download with an unknown extension can Ask through Other. Arbitrary HTTP responses do
not create capture offers. Exclusions and private/method restrictions take precedence.

Bulk import keeps each request's credentials and redirect context separate. Identical
complete requests are deduplicated; different signed links or contexts remain distinct.
Filter/select controls apply to visible rows, and the destination and queue are shown.
Refresh selected browser links relinquishes the pending offer and opens up to ten selected public
source pages. Select fresh requests with the browser picker. Expired links are not
replayed and accepted downloads are not silently associated with new credentials.

See [capture policy](CAPTURE-POLICY.md) and [protocol](protocol.md) for negotiation,
authoritative acceptance, and residual duplicate/lost-response limits.

## Network and queue controls

Default and per-download HTTP proxy settings use protected credential storage. An
individual policy change pauses the transfer and stays paused. Existing downloads retain
their policy snapshots. HTTPS destinations can use an HTTP CONNECT proxy; HTTPS proxy
transport and SOCKS are rejected by this aria2 boundary. Proxy routing does not establish
an anonymity or DNS-isolation guarantee.

Adapter choices inventory IPv4/IPv6 and connection state, but strict enforcement is
unsupported on Windows, Linux and macOS. Required-interface operations reject before
network work. No firewall kill switch is installed. Tests and a zero-request localhost
fixture do not prove packet-level adapter-loss or DNS leak prevention. See
[network policy](NETWORK-POLICY.md) for the exact boundary.

Settings → Queues creates persistent named queues with independent concurrency,
start/stop, one-off and recurring schedules, timezone and weekday selection. Queues
appear in the sidebar, Add/bulk/torrent/media destination choices, and Move to queue.
Stopped queues remain held through polling, retry and restart. Manual pauses remain
manual. Schedule recovery evaluates the latest event after a restart or clock change;
fall-back times fire once at the first occurrence, and nonexistent spring-forward
times move to the next valid minute. Global speed budgets
are conservatively divided across registered engines; unused shares/remainders are not
borrowed, so one engine can transfer below the configured overall ceiling.

## Torrent import and seeding

Import torrent opens a bounded BitTorrent v1 file-tree preview. Select individual files,
destination and queue before confirming. Metainfo, selected file indices, seeding options
and tracker information are protected at rest. Uploaded bytes and the seeding start time
persist so restarting does not grant a fresh ratio/time budget.

Settings → General can enable magnet recognition on explicit Paste/Add actions. It is
off by default; the clipboard is never monitored. A magnet first opens a preview.
Fetching metadata requires a separate confirmation and uses an owned temporary job;
content only begins after metadata preview and file selection. Cancellation removes the
metadata job before deleting its owned files. Content and unknown user files are retained.

Magnet metadata discovery currently requires an HTTP/HTTPS tracker. DHT, IPv6 DHT,
local peer discovery and UDP-only discovery are disabled rather than retaining an
uncontrolled background discovery lifetime. Trackerless/UDP-only magnets and v2 metainfo
are rejected with a limitation. Imported v1 metainfo can use supported peers/webseeds.
Configured torrent proxies and required adapters reject before metadata or content work,
because aria2's HTTP proxy cannot cover every tracker, peer and upload connection.

Post-completion seeding is disabled by default. Uploads while receiving torrent content
can still occur. Explicit seeding provides ratio, time and upload limits, actual upload
rate/bytes, and Stop seeding. Active seeding is displayed as seeding, rather than implying
that downloaded content means upload traffic has stopped. Selected file paths reject
traversal, symlinks, alternate path aliases, reserved device names and case collisions.

## Optional media tools

Direct HTTP media downloads continue through the ordinary download engine without
optional tools. Settings → Advanced enables explicitly configured native yt-dlp/FFmpeg
paths. No tools are installed automatically. Version/capability and bounded license checks
show the installed tool information; those checks do not authenticate a binary's origin.

The Media window inspects formats and offers audio/video/quality selection, output
preview, destination and queue. Extraction is desktop-owned. Browser discovery remains
a separate capability. Only supported unprotected finite transfers are accepted.
Credentials that cannot safely be restricted to their originating host are rejected by
the helper; authenticated direct-file downloads retain the existing path. Required
adapter and proxy policies are rejected before helper or managed network activity.

Transfer uses checked direct or finite segment snapshots. HLS encryption/key directives,
live or unsupported playlist features, DASH content protection, unsupported fragment
plans and DRM are rejected. FFmpeg receives owned local files with a local-file protocol
whitelist; it does not fetch remote manifests or receive decryption keys. Symlink or
junction paths are rejected for torrent/media workspaces. Deleting media with Delete
files removes only generated partial files and preserves unknown workspace files. Cancellation
stops owned subprocesses and network operations before reporting paused. Retry rebuilds
the checked plan under the saved selection. This does not promise support for every site,
format, live stream or protected source.
