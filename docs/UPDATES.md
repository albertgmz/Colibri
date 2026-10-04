# Application updates

Settings → General → Updates displays the installed version, check result and available version. Automatic checks are enabled by default: one background metadata check after startup and another every hour, including while the app is in the tray. Turn the option off to use Check now only. Startup does not wait for GitHub and does not show a modal prompt.

The feed is the anonymous stable release endpoint for `albertgmz/Colibri`. Until a release is published, a private/no-release response is shown as unavailable rather than “up to date”. Linux/macOS can check release metadata, but the current release pipeline provides Windows packages only.

Download update is an explicit action. It downloads the versioned installer for a recognized per-user setup installation, or the versioned ZIP for a portable copy. Release tags, repository URLs, filenames, uploaded state, bounded sizes and required GitHub SHA256 asset digests are validated. Metadata is bounded to 1 MiB with a 15-second deadline; downloads have a 512 MiB limit and 20-minute deadline. Redirects allow only HTTPS GitHub release delivery hosts. No browser cookies, repository tokens or aria2 credentials are sent. A completed file must match its declared size and SHA256 before it becomes available.

Show package reveals the downloaded file. For a portable copy, exit Colibri, extract the ZIP into a new folder and run the new executable; the existing per-user settings and history remain in their normal location. The updater does not extract over running binaries.

For a recognized Windows installation, Exit and install verifies the local package again, prepares a hidden helper, waits for readiness and authorizes the handoff before requesting the normal app exit. The helper checks the initiating process identity, waits up to two minutes for that exact process to exit, then rechecks the installer size/hash while holding a handle that denies writes/deletion. Setup independently refuses running installed Colibri/native-host/aria2 processes. A surviving engine or locked file stops setup; no process is killed by the updater and shutdown completion is not treated as proof of engine exit. A helper failure records a generic `handoff-error.txt` beside the package; reopen Colibri and download again to retry.

Hourly/manual checks share the operation, repeated feed checks are briefly throttled, and unavailable/rate-limited feeds back off. Cancellation removes only the current partial download. A newer discovered version invalidates the displayed older cached package. Closing the app cancels and awaits update operations. Background checks never silently install or restart the app.

SHA256 and HTTPS verify bytes relative to the trusted repository/GitHub feed; packages currently have no independent publisher signature. [Release publishing](RELEASES.md) describes the no-overwrite workflow and optional GitHub release immutability.

Verification includes fake HTTP feeds, size/hash/redirect rejection, changed-cache detection, private-feed handling, ETag caching, controlled-clock startup/hourly checks, saved preferences and handoff failure without exit. A real public update and installed upgrade still require a published newer release and isolated installed runtime verification; local package creation alone does not establish those results.
