# Torrent metadata and content boundary

Local v1 metainfo is parsed before any engine call. Parsing is limited to 2 MiB,
10,000 files, bounded nesting/node counts and canonical bencoding. V2 and hybrid
metadata are rejected. Traversal, absolute paths, device names, symlinks, ambiguous
UTF-8 alternatives and duplicate/colliding paths are rejected across supported OSes.
Tracker/web-seed URIs are bounded and validated; credentials in URI authority and
local file schemes are rejected. The raw metainfo may contain private tracker query
tokens and therefore belongs in the protected torrent payload, not a log.

Magnet preview is a separate temporary aria2 GID with `bt-metadata-only=true`,
`bt-save-metadata=true` and `pause-metadata=true`. It cannot promote content. Only
known bounded parameters and v1 SHA-1/base32 identities are accepted. The returned
metainfo must hash to the requested identity. Preview removes its engine job and owned
temporary metadata files before returning, including cancellation and the two-minute
timeout. Temporary metadata is briefly plaintext inside the per-user data directory;
it is never saved as the persisted download record or a session file. Cleanup failure
stops the owned engine before attempting directory cleanup. Unexpected subdirectories
are not recursively deleted.

Content starts only after the user accepts a nonempty one-based file selection. Every
file is mapped with aria2's `index-out` option under a reserved content directory:
`saveFolder/item.FileName/relativePath`. Multi-file torrent root names are stripped from
the relative path; a single-file torrent uses its validated metadata filename. Existing
symbolic links or junctions in output ancestors are rejected. Filesystem races against
another local process are not eliminated by these preflight checks.

HTTP proxy settings do not cover torrent tracker, DHT or peer traffic. Any effective
proxy or required-interface policy rejects torrent operations before RPC. An explicit
direct policy permits direct torrent networking; there is no anonymity promise.

DHT is globally disabled for both IP families. Its native background lifetime can outlive
one torrent and must not leak discovery into later HTTP/proxy work. In aria2, disabling
DHT also disables UDP trackers. Magnet preview consequently requires an HTTP or HTTPS
tracker; trackerless and UDP-only magnets are rejected before RPC. Local metainfo can
use HTTP/HTTPS trackers and web seeds. DHT, UDP tracker discovery and v2-only torrents
remain unsupported capabilities, not verified successes. Private torrents additionally
disable peer exchange; local peer discovery is disabled for every torrent.

Seeding after completion defaults off (`seed-time=0`). Opt-in settings contain bounded
ratio, time and upload limits. Uploading pieces while downloading is normal torrent
behavior even when post-completion seeding is disabled. Status reports selected-file
bytes, upload speed, uploaded bytes and a distinct seeding flag while engine state stays
active. BitTorrent content completion is distinct from final completion. Stop Seeding
removes the engine job while preserving content. Core persists cumulative upload/time
budgets and enforces them across reconstructed GIDs; native session counters alone do
not preserve those budgets.

The bounded parser follows the [v1 BitTorrent specification](https://www.bittorrent.org/beps/bep_0003.html).
RPC/base64, metadata-only acquisition, output mapping and seeding options follow the
[official aria2 manual](https://aria2.github.io/manual/en/html/aria2c.html).
Tests cover parsing safety, selection, proxy refusal, metadata cleanup on success/cancel,
paused content options, default/opt-in seeding and selected/upload telemetry. They do not
prove public-swarm availability, packet-level isolation or cross-platform runtime behavior.
