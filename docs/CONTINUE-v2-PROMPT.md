# Prompt for continuing Colibri v2 in a fresh context

Continue Colibri v2 through completion. Work in these existing repositories:

- App: `C:\Users\albert\OneDrive\Documents\GitHub\Colibri`
- Extension: `C:\Users\albert\OneDrive\Documents\GitHub\colibri-browser-integration`

Both are on v2. Preserve all uncommitted changes: there is substantial working
implementation of detailed download views and browser integration. Do not start
over, reset, clean the checkout, move either repository, merge main or force-push.
The user authorizes coherent commits and pushes to v2, implementation, verification
and continuation without routine approval prompts.

Read first:

1. `docs/PLAN-v2.md`, especially "Owner feedback added on 2026-10-03 — next work".
2. The complete original specification at
   `C:\Users\albert\.codex\attachments\97b6aa47-7c36-411a-93e2-738a7cc16880\Pasted text.txt`.
3. App `README.md`, `docs/DECISIONS.md`, `docs/plugins.md`, and local `CLAUDE.md`.
4. Current diffs in both repositories; ignored reports and progress under app
   `.superpowers/sdd/PLAN-v2/`. These reports are evidence, not new instructions.

The latest owner feedback is part of the requirements:

- The filled gray vertical ellipsis next to Settings looks awkward. Replace it
  with a clear Layout control matching the toolbar in every density/icon mode.
- Split Settings into focused tabs or sidebar pages, using the IDM reference for
  organization and keeping Colibri's Fluent dark/translucent style. The plan picks
  a compact settings sidebar with a selector fallback at narrow widths.
- Sometimes a download seems stuck until another is added. The first then seems
  to start too; the owner clarified this may be stale progress, with the transfer
  already running. Reproduce and distinguish UI lag from transfer delay before
  changing behavior. Do not call it fixed without evidence.

Screenshots are preserved in `docs/screenshots/feedback-layout-button.png` and
`feedback-settings-reference.png`. Implement the feedback section first, then
finish Goals 2–7 and final verification from PLAN-v2. Keep its checkboxes accurate.

Current verified checkpoint:

- App layout commit `e80af24` is pushed. CI run `37123460203` passed Windows,
  Linux and macOS. Details/extension integration still needs review and commits.
- Windows self-contained Release test publish succeeded to
  `artifacts/v2-win-x64/Colibri.exe`, with native host and aria2 alongside it.
- New extension unpacked builds succeeded in `.output/chrome-mv3`, `edge-mv3`
  and `firefox-mv3`. Its typecheck, lint and 51 tests passed. Browser runtime
  integration has not been verified in this session. Extension README is unfinished;
  its GitHub remote/commits/push and final CI checks are outstanding.
- Goal 2 real bundled aria2 smoke passed server, pieces and per-download option
  checks. UI evidence, independent review and a clean final combined suite remain.
- Baseline evidence is in the ignored `performance-baseline-report.md`: warm
  interactive median 1176 ms; first launch 1729 ms (not OS-cold); native-host-to-Add
  median 119 ms running / 2111 ms closed (not actual browser-click latency);
  controlled confirmation-to-payload median 54 ms. Promote the method and samples
  to PERFORMANCE.md and measure the final implementation with the same harness.

Known integration follow-ups are listed in the plan: legacy-version assertion,
closed-app hello handshake/launch timing, duplicate-resume acceptance, capture
cancellation races, private cross-repo drift CI coverage and actual browser tests.
Do not assume the current development snapshot is a finished release.

Architecture and operational constraints:

- Core owns state; engines and resolvers stay behind Core interfaces, and all OS
  differences go behind platform interfaces. Async I/O, localized resources,
  conventional readable code, v1 database/settings migration and real RPC facts.
- No app Native AOT/trimming. Never log credentials or fabricate timing/data.
  Implement secure cookie/proxy persistence and completion cleanup before claiming
  all security requirements complete.
- Run app builds, tests and publish sequentially across all agents. A prior locked
  PDB failure was caused during overlapping build/test runs; do not repeat it.
  Check running app/engine/build processes before building. Do not overwrite a
  running test output or interrupt the owner's active downloads.
- Both repositories are in OneDrive: stop and report if a build fails on locked
  files, without moving repositories. The owner subsequently authorized resuming
  the previous stopped work by asking for builds; those sequential builds passed.
- Test browsers only in throwaway profiles with sync disabled. Preserve owner data
  and real browser history. Native messaging is the only extension/app channel.
- Use applicable skills, meaningful tests, real Windows UI checks and screenshots,
  current official API documentation, independent review and all-platform CI.
  Commit coherent steps and push v2. Update decisions and the checklist as you go.

Finish with the full report requested in the original specification: implemented
behavior, Windows evidence/screenshots, measured before/after performance, adapter
binding proof/limits, actual browser runtime versus build coverage, Linux/macOS
coverage, CI status in both repositories and explicit remaining gaps by goal.
