# Colibri v2 implementation checklist

The v2 branch starts at v1 commit `42d8967`. Work proceeds in priority order;
unfinished items stay unchecked. Do not merge main or force-push.

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
- [ ] Build/test, run real Windows app, save default/minimum screenshots and commit/push.

## 2. Detailed download view

- [ ] Core detail snapshots and bounded speed/event histories; engine RPC tests.
- [ ] Overview with available facts; record unavailable fields explicitly.
- [ ] Pieces from bitfield, counts, piece size and legend; real RPC server rows.
- [ ] Speed graph, event log, per-download limits/connections, tabs and pop-out window.
- [ ] Opt-in automatic details window; build/test/run/screenshots/commit/push.

## 3. Extension migration

- [ ] WXT strict TypeScript and lightweight DOM UI; port all 29 behavioural tests before removal.
- [ ] Preserve Chromium manifest key; fixed Firefox ID; Firefox registration on all OSes.
- [ ] Versioned hello/capabilities protocol, docs/schema and checked-copy drift test.
- [ ] Pause/offer/confirm/cancel; resume on rejection, failure or browser fallback.
- [ ] Capture real headers/cookies/response metadata/redirects; POST bypass.
- [ ] Link/media menus and checked/filterable bulk import in app.
- [ ] App-owned capture settings/cache, site switch, modifier bypass and optional private capture.
- [ ] Status/recent-captures/open-app popup; onboarding and shared generated design tokens.
- [ ] Minimum permission justification, no telemetry or credential logging.
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
- [ ] Secure cookie persistence/migration and completion cleanup.
- [ ] Default eight connections and rejection backoff.
- [ ] Browser acknowledgment after confirmation and explicit browser fallback.
- [ ] Resolver deadline and deterministic engine choice.
- [ ] Remove release debug symbols; build/test/commit/push.

## Verification and handoff

- [ ] Record Windows evidence, screenshots and performance numbers.
- [ ] Record adapter proof limits and actual browser runtime/build coverage.
- [ ] List Linux/macOS implementations not run and both CI results.
- [ ] List remaining requirements and known issues by goal; keep this checklist current.
