# Windows progress investigation

Measured on 2026-10-03 using the real Avalonia Windows backend and bundled aria2
1.37.0 in an isolated copy of the v2 working tree. The owner's running instance,
database, settings, downloads and IPC endpoint were untouched. Notifications were
disabled in the test copy. These are controlled reproduction attempts, not proof
that the owner's intermittent report cannot happen.

The fixture serves 32 MiB of `0x5A` over loopback, supports HTTP ranges, and sends
16 KiB every 125 ms. Only one connection per server is allowed. The probe adds the
first transfer, adds a second after ten samples, and observes every second for 65
samples. Each sample reads actual aria2 RPC progress, Core's snapshot, the bound
UI row on the dispatcher, window visibility, and actual readable payload bytes.
File length is insufficient: aria2 preallocates and buffers disk writes.

| Sample | aria2 bytes | Core/UI bytes | Readable payload bytes | Window |
|---:|---:|---:|---:|---|
| 1 | 147456 | 131072 | 0 | Visible |
| 7 | 950272 | 933888 | 0 | Visible |
| 8 | 1081344 | 1064960 | 1048576 | Visible |
| 9 | 1212416 | 1196032 | 1048576 | Visible; second not added |
| 10 | 1343488 | 1327104 | 1048576 | Visible; second added |
| 19 | 2539520 | 2523136 | 2097152 | Visible |
| 60 | 7962624 | 7618560 | 7782400 | Hidden in tray |
| 63 | 8355840 | 8273920 | 7782400 | Hidden in tray |
| 64 | 8486912 | 8273920 | 8388608 | Hidden in tray |

The first transfer advanced before the second was added. Visible Core and UI
values matched within one poll of aria2. Hidden rows continued changing at the
configured five-second polling interval. No transfer stall or indefinitely stale
UI was reproduced; no speculative restart or download behavior was added.

Restoring the isolated app after the probe showed the persisted progress and
paused rows. A later manual resume advanced the first transfer from 25.7% through
completion while the second remained paused; live connections showed the fixture
host and speed, and the pieces tab showed the actual 32-piece bitfield. The probe
had already ended before restoration, so this does **not** establish measured
active-transfer latency across tray restoration.

A second run extended the same probe to 95 samples and restored the window while
both downloads were still active. The first transfer again advanced before the
second was added. Tray minimization occurred at sample 27; restoration occurred
at sample 81. Core and the bound UI row matched throughout the repeat.

| Sample | aria2 bytes | Core/UI bytes | Readable payload bytes | Window |
|---:|---:|---:|---:|---|
| 9 | 1212416 | 1163264 | 1048576 | Visible; second not added |
| 26 | 3457024 | 3407872 | 3145728 | Visible |
| 27 | 3604480 | 3506176 | 3145728 | Hidden in tray |
| 31 | 4128768 | 3506176 | 3145728 | Hidden in tray |
| 35 | 4653056 | 4161536 | 4194304 | Hidden in tray |
| 80 | 10616832 | 10059776 | 10485760 | Hidden in tray |
| 81 | 10747904 | 10715136 | 10485760 | Restored; transfer active |
| 85 | 11272192 | 11239424 | 10485760 | Visible; transfer active |
| 94 | 12484608 | 12435456 | 11534336 | Visible; transfer active |

Hidden progress updated at the intended five-second interval. On restoration,
visible progress caught up within one one-second poll. The controlled fixture
reproduced neither an engine stall nor a permanently stale row. This evidence
does not identify the cause of the owner's intermittent report; no transfer
restart fix is claimed. The probe deliberately paused both downloads after its
last sample.

Raw samples: [progress-visible-hidden.json](benchmarks/progress-visible-hidden.json).
Repeat samples: [progress-tray-restored.json](benchmarks/progress-tray-restored.json).
Test-only source: [ProgressProbe.cs](benchmarks/ProgressProbe.cs) and
[progress-fixture.py](benchmarks/progress-fixture.py). Apply the profile, IPC and
notification isolation described in [PERFORMANCE.md](PERFORMANCE.md) to a copied
source tree before running the probe; never inject it into an owner's instance.

UI evidence is in `screenshots/v2-progress-visible.png`,
`screenshots/v2-details-connections.png`, and `screenshots/v2-details-pieces.png`.
The active repeat is shown in `screenshots/v2-progress-before-tray.png` and
`screenshots/v2-progress-restored.png`.
