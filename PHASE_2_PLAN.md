# Phase 2 Plan — Ai Download Manager (Beta)

**Status:** Planned (2026-11-13)
**Based on:** `DOWNLOAD_MANAGER_MASTER_PLAN.md` §6 (Phase 2 — Beta),
`ROADMAP.md` (Phase 2 — Beta), and
`IDM_FEATURE_PARITY_MATRIX.md` (67 features; Phase 1: 19
implemented, 16 tested, 48 not started).
**Precondition:** Phase 1 is COMPLETE WITH EXPLICIT BLOCKERS (see
`PHASE_1_PROGRESS.md`). The two blockers — Windows CI never run,
WPF app never launched — are the first items below, not background
assumptions.

## Principles

- **No feature is started without its acceptance test written
  first** (the Phase 0/1 rule that kept every claim honest).
- **Nothing is marked Tested without a measured demonstration**
  on Windows or the local deterministic server — compile-only is
  not Tested.
- **The parity matrix stays the single source of truth.** All 67
  entries are preserved; Phase 2 moves entries from Not started to
  Implemented/Tested and records the evidence in the entry. No
  entry is removed, merged, or silently re-scoped.
- **No paid service, cloud dependency, or paid API** (standing
  rule). No DRM circumvention, ever.

## Workstreams (in priority order)

### 1. Windows verification (unblocks everything else)

The repo has never been built or run on Windows. Until it has,
every WPF and `dotnet test` claim is provisional.

| # | Task | Acceptance |
|---|---|---|
| 1.1 | Push the repo to GitHub; get one green Windows CI run (`.github/workflows/windows-ci.yml`) | Full solution builds incl. WPF; `dotnet test` green; test-results artifact uploaded; 100 MB Prototype smoke run passes with matching SHA-256 |
| 1.2 | Launch the WPF app on Windows 10 and 11; exercise Add URL, pause/resume/cancel, tray, settings, dialogs | App launches; a real download completes; pause/resume works from the UI; no unhandled exceptions |
| 1.3 | Fix whatever 1.1–1.2 finds (expected: UDS path-length edge cases, WinForms/WPF interop nits, first-run folder creation) | Fixes land with a regression test where the failure is reproducible on Linux |

**User action required:** create a GitHub repository and provide
the remote URL (or push it yourself). No remote or credential is
invented by the agent. See `PHASE_1_PROGRESS.md` for the exact
steps.

### 2. Engine profiling (close the aria2 gap)

Phase 1 measured the gap honestly: aria2 is 2.2–4.1× faster than
the engine on a 100 MB local-server download (end-to-end), and the
fair `--timing split` mode shows how much of that is verification
overhead vs transfer. The published target is "within 5% of aria2".

| # | Task | Acceptance |
|---|---|---|
| 2.1 | Profile the engine on a 1 GB local download (CPU, allocations, per-chunk work, journal flushes, HttpClient overhead) | A profile with the top 3 costs named, with numbers |
| 2.2 | Address the top costs (candidates: larger I/O buffers, fewer per-chunk allocations, async file I/O tuning, batching journal writes) — one change at a time, re-measured after each | Each change is kept only if the split-mode benchmark improves; no change is kept on a hunch |
| 2.3 | Re-run the fair comparison (`--timing split`, 100 MB + 1 GB, 3 trials) and record the new gap in `BENCHMARKS.md` | Gap reported honestly whether or not the 5% target is met; the target is not claimed until measured |

**Not in scope:** changing the journal durability guarantees to
buy speed (WAL + synchronous FULL stays); removing SHA-256
verification from the default path.

### 3. Job lifecycle architecture (finish the closure work)

Phase 1 closure gave `DownloadService` a job-handle API
(`StartDownloadAsync` / `ResumeDownloadAsync` → `DownloadHandle`;
`PauseAsync` / `CancelAsync` by id; `ListJobsAsync` /
`DeleteJobAsync`) and the journal `ListJobsAsync` /
`DeleteJobAsync`. The UI now goes through the service. Two
follow-ups remain:

| # | Task | Acceptance |
|---|---|---|
| 3.1 | Switch startup restore from Presentation-owned `history.json` to the journal `ListJobsAsync` (the journal is the source of truth; `history.json` becomes a display cache or is removed) | Kill the app mid-download, relaunch, and the job list shows the interrupted job as Paused with its downloaded bytes; resume completes it with a matching hash |
| 3.2 | Wire the settings scanner path into `DownloadService` (the Settings dialog already collects it; App does not pass it yet) | With a scanner configured and enabled, a completed UI download is scanned; an infected stub is reported and the job marked Failed |
| 3.3 | Transport selection by URL scheme (HTTP/HTTPS → `HttpTransport`, FTP/FTPS → `FtpTransport`) so the UI's FTP fail-fast can be removed | An FTP URL added in the UI downloads through the FTP transport; an unsupported scheme still fails fast with a clear message |

### 4. Browser integration (B1–B10, 0/10 in Phase 1)

The largest untouched area. Build the extension + native-messaging
host first, then the capture features on top of it.

| # | Task | Parity entries |
|---|---|---|
| 4.1 | Native-messaging host (console app, stdio JSON protocol) + extension skeleton for Chrome/Edge (MV3) and Firefox; install/uninstall wiring from the app | B1–B3, B9 |
| 4.2 | Download interception: extension hands the URL (+ cookies where the user permits) to the host, which creates a job in the app | B4, B7 |
| 4.3 | One-click capture, context menu, filename/referrer capture | B5, B6, B8 |
| 4.4 | Compatibility: extension works on current stable releases of all three browsers | B10 |

**Acceptance:** the extension captures a download from each of
Chrome, Edge, and Firefox on current stable releases; no extension
permission beyond what the feature needs; no browsing data leaves
the machine (the host is local-only).

### 5. Core IDM workflows (D + E + F, highest user value first)

| # | Task | Parity entries |
|---|---|---|
| 5.1 | Queues (multiple, with concurrency limits) + scheduler (time-based start, shutdown on completion) | D1, D2 |
| 5.2 | Speed limits (global + per-download) + rate limiter in the engine (the single-connection fallback has no limit yet) | E6, E7, E5 |
| 5.3 | Batch import (500 URLs, duplicate detection) + clipboard monitoring + categories with rules | D5, D7, D3 |
| 5.4 | Download history view + statistics + search/filter (build on the journal List API) | D9, D10, D11 |
| 5.5 | Proxy (manual, system, per-site), SOCKS5, auth (Basic/Digest/NTLM/Negotiate), per-site settings | E1–E4 |
| 5.6 | Basic video detection (direct media + clear HLS/DASH) + video panel | C1–C4 |
| 5.7 | Download All (page link scan) — or V1.1 if the extension work overruns; the decision is recorded, not silently slipped | D6 |
| 5.8 | Installer (Inno Setup) + portable ZIP + WinGet manifest; portable mode verified on a clean VM | F11, Roadmap Phase 1 carry-over |

**Acceptance (from the roadmap):** a 10-file queue with a
2-concurrency limit completes in order; the scheduler starts a
queue and shuts the machine down (VM test); batch import of 500
URLs with duplicate detection; the video panel appears on a test
page with a direct MP4; the installer installs and uninstalls
cleanly.

### 6. Deferred to V1.1 / V1.2 / V2.0 (not Phase 2)

- Download All if it slips (V1.1), periodic synchronisation,
  per-queue speed limits, checksum sidecars, download quotas (V1.1).
- Quality selection and playlists for clear HLS/DASH, optional
  external ffmpeg remux (V1.2).
- Site Grabber (V2.0).
- HTTP/3, Metalink, plugin API, headless/CLI mode, PAC proxy,
  torrent module, macOS/Linux ports: future evaluation only.

## Phase 2 acceptance criteria (all must be demonstrated)

1. Windows CI green; WPF app launched and exercised on Windows.
2. Engine profiling published; the aria2 gap re-measured and
   reported (target: within 5% — claimed only if measured).
3. Startup restore reads the journal; a killed download resumes
   after an app restart and after a VM reboot.
4. Browser extension captures from Chrome, Edge, and Firefox.
5. Queues + scheduler + speed limits work end to end on Windows.
6. Installer installs/uninstalls cleanly on a clean VM.
7. No regression: the full test suite (90 tests at Phase 1 close)
   stays green, and the 10 GB validation still passes.
8. Parity matrix updated: every Phase 2 entry moved with evidence;
   the summary counts reflect the new totals.

## Risks

- **Windows-only surprises** (the biggest): UDS path lengths,
  WinForms/WPF interop, Defender SmartScreen on an unsigned
  installer. Mitigation: do workstream 1 first, before building
  on untested assumptions.
- **Extension store policies** (MV3, host permissions): keep the
  host local-only and the permission set minimal; the extension
  must work unloaded (developer mode) even if store review is slow.
- **Throughput target may not be reachable** without a native
  transport. If profiling shows the gap is structural (managed
  HttpClient + journaling vs native aria2), report it and reset
  the target with evidence rather than weakening durability.
- **Scope creep from the 48 unstarted features:** the priority
  order above is the plan; anything else waits for Phase 3 or a
  recorded decision.

## Out of scope for Phase 2

Anything in "Deliberately never planned" (DRM circumvention, ad
injection, bundled offers, paid tiers, accounts, cloud storage),
and any feature whose acceptance test cannot be written before
the implementation starts.
