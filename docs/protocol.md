# Browser protocol v2

The extension talks only to `com.colibri.host` through native messaging.
There is no TCP listener or WebSocket. Browser frames have a four-byte little-endian
length followed by UTF-8 JSON. The native host forwards validated messages through
the owner-only local pipe. Messages are limited to 1 MiB; credentials are never logged.

`hello` carries `protocolVersion: 2`, `extensionVersion`, `capabilities` and a unique
`requestId`. The response echoes `requestId`, reports `protocolVersion`, `appVersion`
and supported capabilities. Incompatible versions fail without capturing. Legacy
ping/config remain readable, but v2 confirmation is required before canceling a download.
Hello does not launch Colibri. When the paired app is closed, the host returns
`ok: false, error: "app-not-running"` together with its protocol version and
capabilities. Only an explicitly compatible closed-app handshake permits an
intentional `add`, `bulk-add` or `open` to launch Colibri. Settings discovery stays
passive. The five-second hello deadline covers pipe discovery, not app startup;
intentional offers retain the twenty-second request deadline.
The checked schema is `protocol.schema.json`; the extension checks its committed copy
against a sibling checkout locally. The extension's CI checks out `albertgmz/Colibri`
`main` and always runs its drift test against these files.
Unknown optional fields are ignored for additive updates.

Requests and responses use the same `requestId` for correlation on a persistent port.
`add` supplies URL, optional final URL, filename, referrer, cookies, user agent, size,
MIME type, real forwardable headers, redirects, content disposition and response status.
Only GET requests can be replayed. Captured headers belong to the final request, so
Colibri downloads `finalUrl` when available. It never replays final-hop credentials
at the original redirect host. Protocol validation strips hop-by-hop and Range headers.

An offer returns `ok: true, pending: true, accepted: false, state: "pending"` and a
`captureId`. This means the dialog is visible, not that the browser should cancel.
The extension polls `capture-status` with that ID. Only `state: "accepted"` with
`accepted: true` after successful user confirmation permits browser cancellation.
`browser` and `rejected` resume the browser download. `capture-cancel` invalidates a
pending offer before browser fallback; accepted offers cannot be retroactively canceled.
The extension also honors an accepted `capture-cancel` response when acceptance
wins a race with its last status request. If the native port is lost before an
authoritative ownership response arrives, fallback can still race an app-side
acceptance: native messaging cannot provide atomic ownership across that failure.
Pending offers expire after five minutes and close their dialogs. Expired IDs may
be evicted from the bounded store; an unknown ID is a failure and keeps the browser file.

`capture-cancel` uses a twenty-second deadline so Add/Resume cleanup can finish.
Only its explicit correlated `ok: true, state: "pending"` response establishes a
cleanup hold: Colibri cannot confirm that its transfer stopped, so the extension
keeps the browser entry paused and records an attention message. This response does
not permit browser cancellation. The user must inspect/stop the Colibri transfer
before manually resuming the browser file. A contradictory `accepted: true` flag
or mismatched `captureId` is unusable, not a reason to hold the browser. Missing,
malformed, unknown, timed-out or disconnected cancellation replies retain browser
fallback; they cannot establish app ownership. The unavoidable lost-reply race
described above still applies.

The app answers cancellation promptly with its current authoritative state. It
does not wait for engine cleanup before replying: `pending` prevents the native
host's ordinary response deadline from being mistaken for permission to resume
the browser. Status becomes rejected/browser only after cleanup is confirmed.

`bulk-add` contains 1–500 add-context objects in `links`. It opens a checkbox list
with a text filter, select-visible and clear-visible controls before any transfer.
`open` shows Colibri. `config` returns authoritative `captureExtensions`, `minSizeKiB`,
`enabled`, `excludedSites`, `capturePrivate`, `bypassModifier`, `theme` and `accent`.
`settings-update` contains a `patch` of enabled/site/private/modifier preferences;
the app persists them and returns authoritative rules. The extension refreshes the
cache over its native port; app settings remain the owner. Modifier values are
`none`, `alt`, `shift` or `ctrl`. Excluded sites are host names.

Cookies and authorization travel only to the native host. Recent-capture summaries
must exclude credential values and URL query strings. An interrupted port, invalid
reply or timeout keeps the browser download. POST, unsupported protocols and
disallowed private-window captures remain with the browser.
# Background palettes

Config-bearing replies may include `palette`: `warm`, `graphite`, `ocean`, or `forest`. This additive protocol v2 field is owned by application Settings. Older applications omit it; extensions default missing or unknown values to `warm`. The existing `theme` and `accent` remain independent. Popup and onboarding surfaces apply the palette when their existing configuration refresh runs; no additional polling or appearance selector is introduced in the extension.


### Capture policy capability

`capture-policy-v1` adds optional `capturePolicy` action maps (`categories` with stable catalog IDs, `extensions` without dots) and `exclusionRules` to config replies. Actions are `capture`, `ask`, and `browser` (Keep in browser). Catalog and browser rules are app-owned in `docs/capture-policy.json` and `docs/capture-rules.ts`; `node scripts/generate-capture-policy.mjs --check` checks generated app and sibling artifacts for drift.

Old settings migrate additively: legacy allowlisted extensions Ask, known extensions excluded by that allowlist Keep, unknown types Ask through Other. The minimum remains unchanged; unknown sizes do not prove a download is below it. The browser-suggested filename takes precedence over URL and MIME. Only genuine browser downloads enter automatic evaluation; observed HTTP responses never independently create offers. The existing required GET observation prevents replay of POST or unassociated requests.

Precedence: GET/private restrictions and any exclusion, enabled setting, minimum known size, extension action, category action. Supported exclusions: `host:example.com` exact host, `domain:example.com` host plus subdomains, `path:example.com/files` exact host plus path segment subtree, and `type:pdf` all hosts. All matching exclusions Keep; queries, wildcards, credentials, and control characters are rejected. Original/final/redirect/referrer hosts are checked. Manual context-menu and bulk offers cannot override these safety restrictions.

`automatic-capture-v1` permits optional add `captureAction: capture` only after hello negotiation. `ask` opens confirmation; absent retains manual confirmation behavior. The app reevaluates authoritative preferences and private/exclusion safety. Automatic acquisition uses the same confirmation command and capture session rollback path. Pending replies always leave the browser paused; cancellation requires explicit `accepted: true` with authoritative accepted state after desktop acquisition. Duplicate confirmation, failed acquisition, restart, and lost replies retain the existing cleanup/attention limits; this optional capability does not promise exactly-once delivery across process loss.

`privateWindow` is an optional bounded boolean on add and each bulk link. Settings remembers only the last bounded decision reason in memory; `decisionReason` replies contain a code and never a URL, headers or credentials. Older apps ignore optional policy fields and receive a compatible allowlist/minimum representation; automatic capture downgrades to Ask when negotiation is absent.
