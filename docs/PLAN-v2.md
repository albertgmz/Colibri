# Colibri v2 implementation checklist

The v2 branch starts at v1 commit `42d8967`. Work proceeds in priority order;
unfinished items stay unchecked. Do not merge main or force-push.

An earlier execution stopped on 2026-10-03 under the owner's locked-file rule:
`dotnet build src/Colibri.App/Colibri.App.csproj --no-restore --verbosity quiet`
failed CS2012 writing `obj/Debug/net10.0-windows10.0.19041.0/Colibri.pdb`,
reported locked by `.NET Host` PID 38392. A concurrent agent test run was active;
the cause is not established as OneDrive. No retry or repository move was attempted.
Layout commit `e80af24` is pushed and CI passed on all three operating systems.
Goal 2 implementation and Goal 3 integration are currently uncommitted; do not
discard them. Agent reports and baseline measurements are in the ignored
`.superpowers/sdd/PLAN-v2/` workspace. The owner subsequently requested new test
builds, authorizing resumption. Sequential Windows self-contained Release publish
and Chrome/Edge/Firefox unpacked builds succeeded. Extension typecheck, lint and
51 tests passed. Test artifacts are `artifacts/v2-win-x64/Colibri.exe` and sibling
extension `.output/{chrome,edge,firefox}-mv3/`. These are development snapshots,
not completion of the remaining goals. Keep all future app build/test/publish
commands sequential; never rebuild an output containing a running app.

## Owner feedback added on 2026-10-03 — next work

The owner is handing this work to a fresh-context agent. Complete these corrections
before continuing the remaining numbered goals. The implementation is still
uncommitted in both repositories; review and preserve it rather than starting over.

### A. Replace the awkward toolbar Layout button

Feedback: `docs/screenshots/feedback-layout-button.png` shows a small filled gray
vertical ellipsis beside Settings. It looks disconnected from the other commands
and does not explain its purpose.

Files: `src/Colibri.App/Views/MainWindow.axaml`, `MainWindow.Layout.cs`,
`src/Colibri.App/Resources/Icons.axaml`, `Strings.resx`, and
`tests/Colibri.App.Tests/WindowLayoutTests.cs`.

- [x] Replace the text ellipsis with a suitable existing or original Fluent-style
  layout icon; use the same button size, background, padding and alignment as the
  neighboring commands. In labels mode show the localized label "Layout".
- [x] Retain a tooltip and accessible name in every mode. Icons-only and small-icons
  modes follow the other toolbar buttons. Keep density, sidebar, toolbar and reset
  choices available in its menu.
- [x] Verify at 960 x 600 and 640 x 400 in all three toolbar modes, including keyboard
  access, and save updated screenshots. Search and commands must not overlap.

### B. Split Settings into focused pages

Feedback: `docs/screenshots/feedback-settings-reference.png` is an IDM example of
grouped settings navigation. Use it for organization, while preserving Colibri's
dark/translucent Fluent styling; do not copy its old Windows appearance.

Choose a compact left sidebar of settings pages, falling back to a compact page
selector when necessary at minimum width. This implements the owner's request for
tabs or a sidebar without another long scrolling page.

Files: `src/Colibri.App/Views/SettingsView.axaml` and its code-behind,
`ViewModels/SettingsViewModel.cs`, `Resources/Strings.resx`, and the existing app
settings tests. Page state belongs in the view/view model; persisted preferences
retain their existing keys and migration behavior.

- [x] Add pages General (startup/tray), Appearance (theme/accent/layout), Downloads
  (folders/concurrency/connections/speed), Browser Integration (registration and
  capture), and Advanced (engine path/logs). Put upcoming proxy/adapter controls on
  Network and scheduled queue controls on Queues when those goals are implemented.
- [x] Show only the selected page, with its own scrolling region and descriptive
  heading. Keep changes saved as they are made. Do not reset fields when switching
  pages or recreate the entire settings view on every switch.
- [x] Keep Back/close navigation, accessible page names and keyboard selection.
  Validate every current setting remains reachable and v1 preferences survive.
- [x] Test page switching and persistence; run the real Windows app at default and
  minimum sizes and save screenshots before committing.

### C. Investigate apparent download/progress stall

Owner report: sometimes a download appears stuck; adding another makes the first
start too. The owner then clarified it may already have been downloading and only
the displayed progress was stale. This is a hypothesis, not a confirmed root cause.

Files to trace: `Core/Services/DownloadManager.cs` (poll loop, semaphore and events),
`Engine.Aria2/Aria2RpcClient.cs` (TellAll/RPC notifications),
`App/ViewModels/MainWindowViewModel.cs` (Dispatcher posts and AddOrUpdate),
`DownloadItemViewModel.cs` (property notifications), and table bindings in
`Views/MainWindow.axaml`. Tests: `tests/Colibri.App.Tests/MainWindowTests.cs`,
`DetailsPaneTests.cs`, Core manager tests and engine RPC tests.

- [x] Reproduce with one controlled local download. Compare actual destination
  bytes and aria2 completedLength with the displayed bytes/speed once per second,
  before adding any second item. Then add a second item and compare again.
- [x] Repeat visible, hidden in tray and restored. Current intended poll intervals
  are one second visible and five seconds hidden. Distinguish rounding small
  percentages, DNS/connection delay, actual queue waiting and stale UI updates.
- [x] Check that a poll failure cannot silently leave progress frozen, UI work does
  not block the engine receiver, and a new item is not required to refresh existing
  rows. Use timestamps/IDs and byte counts for diagnostics, never credential values.
- [x] Add a regression test for the reproduced cause and fix that cause. If not
  reproduced, record evidence and the remaining uncertainty; do not claim a fix or
  add speculative restart/re-download behavior.

### D. Finish the in-progress integration safely

- [x] Review Goal 2 and Goal 3 working-tree changes and the ignored agent reports.
  Goal 2's real aria2 smoke passed, but its real Windows tab/popout evidence and
  independent review are still outstanding.
- [x] Fix the known NativeHost test assertion mismatch: unsupported version reports
  "Unsupported protocol version." rather than containing "protocol v2". Verify the
  semantic rejection, not a stale message string.
- [x] Review native hello launch behavior: extension startup sends hello, while the
  current ColibriClient may launch the app for hello; its handshake timeout is five
  seconds. Define and test a compatible handshake while the app is closed, followed
  by intentional launch for add/open, without timeout-triggered duplicate offers.
- [x] Verify duplicate-resume confirmation: `AddUrlViewModel.ResumeDuplicateAsync`
  currently closes after resume without marking the CaptureSession accepted.
  Cover successful resume, rejection, fallback and cancellation races.
- [ ] Complete browser runtime checks using throwaway profiles with sync disabled.
  Existing Chrome/Edge/Firefox builds and 51 extension tests passed, but no runtime
  browser check was completed here. The extension README is still only a heading;
  finish permissions, loading and debugging instructions before publishing code.
- [x] Both repositories are private. Cross-repo canonical drift CI needs a read-only
  app-repo secret; local sibling-copy drift verification passes. Document CI coverage
  accurately without creating tokens or pretending the conditional check ran.
- [ ] Keep the old app extension until the new migration and tests are verified.
  Finish coherent commits/pushes on v2; the new extension repo has no remote yet.
  Do not merge main, force-push, discard pending changes or touch real browser history.

## Constraints

Core owns settings and transfer state. View models consume Core interfaces; OS
services belong in Platform. Preserve v1 settings and SQLite data. All I/O is
asynchronous; text belongs in resources. Keep the dark translucent Fluent style
and warm red accent. No app AOT/trimming, runtime plugin loading, stream capture,
Safari, installers, publishing to stores, or Spanish localization.
References are read-only. Stop on OneDrive locks. Browser runs use temporary
profiles with sync disabled. Credentials must never appear in logs.

## Baseline

- [x] Read README, all 52 decisions, plugin guide and v1 plan; inspect both repositories.
- [x] Inspect references: IDM images present; annotated screenshot and extension notes absent.
- [x] Build v1: zero warnings/errors. Test: 757 pass, one Unix-only skip; extension: 29 pass.
- [x] Run Windows v1 and save `docs/screenshots/v1-default.png`.
- [ ] Measure process start to interactive window, native handoff and first written byte;
  document method, sample counts and measurements in PERFORMANCE.md.

## 1. Layout and customization

Implementation: Core `Settings/WindowLayout.cs` stores finite bounded dimensions,
splitters, density, toolbar mode, collapsed sidebar, stable column keys and sort
descriptors. App `JsonSettingsStore` normalizes old/malformed layout values.
`MainWindow` maps visual geometry to this model through MainWindowViewModel,
which owns asynchronous settings persistence. No database change is needed.

- [x] Test v1 settings migration and layout roundtrip, invalid dimensions and null collections.
- [x] Smaller 960 x 600 default, 640 x 400 minimum; 160px draggable sidebar.
- [x] Compact 28px rows, tighter navigation/toolbar, comfortable density and three toolbar modes.
- [x] Collapse empty details to a thin bar; retain selected details splitter height.
- [x] Persist window geometry, both splitters, columns/order/visibility/sort and pane state.
- [x] Header column chooser; responsive visibility must not overwrite user preferences.
- [x] Icon sidebar with counts, Reset layout, additional real-data columns.
- [x] Accent selection; existing system/light/dark theme remains available.
- [x] Shortcuts, URL paste/drop and duplicate resume/re-download/rename choice.
- [x] Destination free space, speed limit and network mode in status bar.
- [x] Initial layout build/test, Windows default/minimum screenshots and commit/push
  (`e80af24`); the additional owner-feedback changes above remain pending.

## 2. Detailed download view

- [x] Core detail snapshots and bounded speed/event histories; engine RPC tests.
- [x] Overview with available facts; record unavailable fields explicitly.
- [x] Pieces from bitfield, counts, piece size and legend; real RPC server rows.
- [x] Speed graph, event log, per-download limits/connections, tabs and pop-out window.
- [ ] Opt-in automatic details window; build/test/run/screenshots/commit/push.

## 3. Extension migration

- [x] WXT strict TypeScript and lightweight DOM UI; port all 29 behavioural tests before removal.
- [x] Preserve Chromium manifest key; fixed Firefox ID; Firefox registration on all OSes.
- [x] Versioned hello/capabilities protocol, docs/schema and checked-copy drift test.
- [x] Pause/offer/confirm/cancel; resume on rejection, failure or browser fallback.
- [x] Capture real headers/cookies/response metadata/redirects; POST bypass.
- [x] Link/media menus and checked/filterable bulk import in app.
- [x] App-owned capture settings/cache, site switch, modifier bypass and optional private capture.
- [x] Status/recent-captures/open-app popup; onboarding and shared generated design tokens.
- [x] Minimum permission justification, no telemetry or credential logging.
- [ ] CI typecheck/lint/tests and Chrome/Edge/Firefox zip builds; temporary-profile runtime checks.
- [ ] Replace app extension folder with README pointer only after migration passes.
- [ ] Build/test both repos, commit/push both v2 branches and check CI.

## 4. Performance

- [ ] Record before/after cold/warm startup, running/closed handoff and first-byte times.
- [ ] Evaluate background engine/reconciliation, lazy settings/details and ReadyToRun.
- [ ] Evaluate native-host AOT, persistent port, early transfer/cancel and immediate-start option.
- [ ] Report measured floor when 1s cold/300ms running handoff targets cannot be reached.

## 5. Network control

- [ ] None/system/manual proxies and secure OS credential stores; verify bundled aria2 types.
- [ ] Fail-closed adapter policy shared by engine, probes and resolvers; address-change handling.
- [ ] Prove zero bytes with adapter down and expected source address when connected;
  document DNS and other limits, without firewall changes or administrator rights.
- [ ] Global speed presets in status/tray; per-download limit.
- [ ] Core policy tests, Windows runtime checks, build/test/commit/push.

## 6. Queues

- [ ] Migrate persistent named queues, concurrency and one-off/recurring time/day schedules.
- [ ] Start/stop commands, sidebar queues, Add window destination and tray scheduling.
- [ ] Catch up on launch within schedule window; test DST, missed windows and concurrency.
- [ ] Platform power actions with cancellable 60s countdown; build/test/run/commit/push.

## 7. Carried-over fixes

- [ ] Internet provenance (Windows zone/macOS quarantine).
- [x] Secure cookie persistence/migration and completion cleanup.
- [ ] Default eight connections and rejection backoff.
- [x] Browser acknowledgment after confirmation and explicit browser fallback.
- [ ] Resolver deadline and deterministic engine choice.
- [ ] Remove release debug symbols; build/test/commit/push.

## Verification and handoff

- [ ] Record Windows evidence, screenshots and performance numbers.
- [ ] Record adapter proof limits and actual browser runtime/build coverage.
- [ ] List Linux/macOS implementations not run and both CI results.
- [ ] List remaining requirements and known issues by goal; keep this checklist current.

## Verified continuation checkpoint — 2026-10-03

Owner feedback A/B is implemented with real Windows screenshots in all toolbar
modes and both sizes; automated layout checks use 640 x 400 client bounds. The
native minimum screenshot includes the Windows frame (approximately 640 x 431).
Settings page switching, keyboard navigation and folder persistence were checked.
Feedback C has two controlled observations, including active tray restoration;
see PROGRESS-v2.md. The intermittent owner report was not reproduced and no fix
is claimed. Goal 2 details/pop-out UI and real aria2 pieces, servers and options
were checked; opt-in automatic opening still needs an explicit Windows check.

Release build: zero warnings/errors. Full suite: 884 passed, one Unix-only test
skipped on Windows (885 total). Scoped details/capture and independent credential
security reviews approved. Real aria2 crash/restart smoke reconstructed a stable
paused download from Core state and completed after resume without a session
file. It does not establish an exact count of reused completed pieces.

The new private extension remote exists. Commit 0e240ce passed CI; follow-up
346040c has 84 passing tests, typecheck/lint and all three zip builds verified
locally. Actual browser runtime remains outstanding, so retain the old extension.
Canonical cross-repo checks are conditional and skipped without COLIBRI_READ_TOKEN;
no token was created. Linux Secret Service and macOS Keychain runtime remain
unverified. Goals 4–6 and the unchecked carried-over fixes remain work in progress.