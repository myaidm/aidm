# Phase 1 Progress — Ai Download Manager (MVP / Alpha)

**Status:** COMPLETE WITH EXPLICIT BLOCKERS
**Date started:** 2026-11-13
**Date completed:** 2026-11-13
**Repository:** `~/workspace/ai-download-manager/`

**Blockers (explicit, not hidden):** the Windows CI workflow has
never run (no git remote here) and the WPF app has never launched
(needs Windows). Both are BLOCKED, not PASS. See "Known limitations
and Phase 2 tasks".

## Starting baseline (verified, not assumed)

- **10 projects** in `AiDownloadManager.slnx` (verified with
  `dotnet sln AiDownloadManager.slnx list`; 10 .csproj files on disk):
  Core, Engine, Protocols, Infrastructure, Application, Presentation
  (WPF), TestServer, Prototype, Engine.Tests, TestRunner.
- **9 non-Windows projects build clean** (0 warnings, 0 errors) via
  `./build.sh`. Phase 0 tests: **23 passed, 0 failed, 0 skipped**.
- **Phase 0 documentation corrections (this phase):** the Phase 0
  report said "9 projects" and "8 non-Windows projects". The actual
  counts are 10 and 9. `PHASE_0_PROGRESS.md` was corrected in place
  with a correction note; the build listing itself already named all
  9 non-Windows projects — only the counts were wrong.
- **No git repository** exists in this environment and no GitHub
  remote is configured. The Windows CI workflow is authored at
  `.github/workflows/windows-ci.yml` but **cannot run here**; it runs
  once the repo is pushed to GitHub. Until a green run exists, the
  full Windows build and standard `dotnet test` are **BLOCKED**, not
  PASS.
- **WPF compiles on Linux** (new finding): with
  `<EnableWindowsTargeting>true</EnableWindowsTargeting>` the
  Presentation project restores and compiles on this Linux VM — the
  build failed only on a real code bug in the placeholder
  (`App.xaml.cs`: `Application` resolves to the
  `AiDownloadManager.Application` namespace, not
  `System.Windows.Application`). The flag is now set in the csproj.
  Compile verification is possible here; **running the WPF app still
  needs Windows**.
- Environment: Linux VM (Ubuntu 24.04), .NET SDK 10.0.404, 146 GB
  free disk. The sandbox still blocks .NET TCP (`other_tcp` Deny):
  tests run via the in-process TestRunner and the test server uses a
  Unix domain socket, as in Phase 0.

## Work plan (execution order)

1. ✅ Reconcile repo + fix project-count docs (this file).
2. ✅ Windows CI workflow authored (`.github/workflows/windows-ci.yml`).
3. ✅ Single-connection retry and recovery (engine + 6 tests).
4. ✅ Functional WPF shell connected to the engine (compile-verified; runtime needs Windows).
5. ✅ SQLite checkpoint optimization with crash-safety tests (2.3× faster on 1 GB).
6. ✅ HTTPS test environment (local cert, validation, resume, redirects) + 6 tests.
7. ✅ FTP transport (FluentFTP 55.1.0) + local FTP test server + 14 tests.
8. ✅ Adaptive connection allocation (measured; no engine change justified) + 4 tests.
9. ✅ Benchmark harness built; 100 MB + 1 GB results recorded;
   aria2 comparison complete (engine 2.2–4.1× slower — recorded
   honestly; two harness bugs found and fixed).
10. ✅ 10 GB validation — **PASS** (10,240 MB, SHA-256 matches,
    439.6 s, resume after 30% interrupt).
11. ✅ Antivirus hook (opt-in, user-configured scanner) + 10 tests.
12. ✅ JSON/CSV import and export (no credentials ever exported) + 15 tests.
13. ✅ Parity matrix + documentation updated (see below).
14. ✅ Final build/test/regression + report (this file). Final
    regression: 81/81 tests passing; Benchmark and Presentation
    build clean. Phase 1 closure (status, stale-statement cleanup,
    CI readiness, job-handle architecture, fair-benchmark mode,
    Phase 2 plan) completed in the closure task the same day.

## Results

### Tests: 81 passed, 0 failed, 0 skipped (78.9 s, via TestRunner)

23 Phase 0 tests + 58 Phase 1 tests. Full `./build.sh`: all 10
non-Windows projects build clean (0 warnings, 0 errors); the WPF
project builds clean separately (0 warnings, 0 errors).

| Suite | Tests | Result |
|---|---|---|
| SegmentPlanTests | 10 | pass |
| JournalTests | 5 | pass |
| EngineIntegrationTests | 8 | pass |
| SingleConnectionRetryTests | 6 | pass |
| JournalCheckpointTests | 3 | pass |
| HttpsTransportTests | 6 | pass |
| FtpTransportTests | 14 | pass |
| AdaptiveAllocationTests | 4 | pass |
| ImportExportTests | 15 | pass |
| AntivirusScannerTests | 10 | pass |

### Single-connection retry (item 3)

`SegmentedDownloader` now retries the no-Range fallback path with
exponential backoff (same `MaxAttemptsPerRange` / `BaseRetryDelay`
as the segmented path), cancellable during the retry delay, and
resumes from the last safely committed prefix by re-requesting the
whole file and skipping the bytes already on disk. A server that
returns fewer bytes on retry fails permanently (no infinite loop,
no corrupt promotion). Progress is checkpointed to the journal as
the prefix `[0, written-1]`, so a restart resumes it.

**Bug found and fixed by this work:** the cancel path persisted
the journal with the already-cancelled token, so the write threw
and was silently swallowed. Phase 0's frequent flushes masked it;
the coalesced flushing exposed it. The cancel path now persists
with `CancellationToken.None`. (`JournalSurvivesNewEngineInstance`
and the no-Range restart test failed until this was fixed.)

### WPF application (item 4)

A functional MVVM application in `src/AiDownloadManager.Presentation/`:
main window with download list (name, status, progress, %, speed,
size, downloaded, ETA), Start/Pause/Resume/Cancel/Remove, filters
(All/Downloading/Completed/Failed/Queues/Categories/History),
Add URL dialog with validation, settings dialog, live speed graph,
overall statistics, system tray (NotifyIcon), empty states, error
handling. Real composition root in `App.xaml.cs` (journal in
`%LOCALAPPDATA%/AiDownloadManager`, `HttpTransport`,
`SegmentedDownloader`, `DownloadService`). Build: **0 warnings, 0
errors**. Runtime verification is **BLOCKED** (needs Windows) —
the app has never run. A genuine placeholder bug was fixed along
the way (`Application` resolving to the `AiDownloadManager.Application`
namespace).

### SQLite checkpoint optimization (item 5)

Measured before changing anything: Phase 0 flushed the journal on
every 256 KB chunk per worker (~4,096 transactions for 1 GB).
Phase 1 separates in-memory progress (recorded every 256 KB,
cheap) from journal flushes (coalesced: one transaction per 4 MB
of new data, or every 2 s, plus a forced flush on completion /
cancel / failure). Durability unchanged (WAL + synchronous FULL;
nothing reported as saved before its transaction commits).

**Measured result:** 1 GB, 8 connections, same file and machine:
**98.8 s → 42.2 s (2.3× faster)**, SHA-256 matches on both sides.
Journal writes for 100 MB: 22–25 (was ~400). `JournalCheckpointTests`
proves the reduced write count and recovery after an interrupted
journal write.

**Maximum loss of uncommitted progress:** a hard crash loses at
most `JournalCheckpointBytes` (4 MB) of completed-but-unflushed
progress, plus one in-flight chunk (≤64 KB) per worker. A graceful
cancel loses at most one 64 KB chunk per worker (the in-memory
ranges are persisted on cancel).

### HTTPS (item 6)

`DeterministicTestServer` speaks TLS (self-signed test cert from
`TestCertificates`, TLS 1.2/1.3) over the Unix socket, plus a 302
redirect path. `HttpTransport` maps TLS validation failures to a
new dedicated `TransportFailureKind.CertificateError` — never
retried, never bypassed. Tests pin the test certificate by
thumbprint (test-only) or use default validation, which correctly
rejects the self-signed chain. 6 tests, all passing.

### FTP (item 7)

FluentFTP **55.1.0**, MIT (verified from the installed package's
nuspec; latest stable as of 2026-11-14). `FtpTransport :
ITransport` (probe via SIZE/REST, range download via REST+RETR,
FTPS with system-trust validation by default and a test-only
thumbprint pin; passive mode only). Local deterministic FTP test
server (`DeterministicFtpServer`, TCP + Unix socket). 14 tests,
all passing, including FTPS success and wrong-thumbprint
rejection. `DEPENDENCY_AUDIT.md` updated.

### Adaptive allocation (item 8)

Measured first. 100 MB benchmark medians: 1 conn 11.6 MB/s,
4 conns 24.1 MB/s (best), 8 conns 20.7 MB/s, 16 conns 18.8 MB/s.
More connections are NOT always faster on this local server, so
no engine change was made — the work queue already splits the
largest remaining range dynamically. The behavior is locked in by
`AdaptiveAllocationTests` (4 tests: sizes × connections, uneven
workers, worker failure, connection limit).

### Benchmarks (item 9)

Harness: `src/AiDownloadManager.Benchmark` (added to the
solution). Results in `BENCHMARKS.md`. aria2 **1.37.0** is
installed (`/usr/bin/aria2c`, Ubuntu package; `apt-get update`
took ~12 min at 100 kB/s). **Head-to-head (100 MB, medians of 3
trials): aria2 is 2.2–4.1× faster than the engine** (engine 19.0–
43.3 MB/s vs aria2 78.0–107.4 MB/s; all SHA-256 OK). The engine's
timed run includes SHA-256 verification + journal flushes +
promotion, aria2's is download-only, which explains only a small
part of the gap. Recorded honestly in `BENCHMARKS.md`; Phase 2
should profile the engine before claiming throughput parity.

**Two bugs found and fixed in the harness during this run:**
(1) `RunAria2TrialAsync` read `process.StartTime` before
`process.Start()` (dead placeholder line — `InvalidOperationException:
No process is associated with this object`); (2) it read
`process.TotalProcessorTime` after `WaitForExitAsync()`, which
throws on Linux once the child is reaped (`Cannot process request
because the process has exited`). The CPU time is now sampled in
the monitor loop while the child is alive. Both fixes are in
`Program.cs`; the full comparison then ran clean.

### 10 GB validation (item 10)

**Status: PASS.** 10,240 MB, 8 connections, `--interrupt-at 30`.
Source SHA-256 `a6d987e2cbb91fbbe575d3ab331654e65e76f96b2346d267c1788c87ea5ccf0c`.
Interrupted run cancelled at 30% with **3,221,888,384 bytes
confirmed** written. Resume run: **success, 439.6 s, 23.3 MB/s**,
SHA-256 matches the source; file promoted; exit code 0. Work dir
`/home/hatch/tmp/aidm-10gb`. Cross-process restart resume is proven
by the unit tests at 10–20 MB, not at 10 GB (the Prototype resumes
in-process).

### Antivirus hook (item 11)

Opt-in `AntivirusScanner` (default OFF, user-configured scanner
executable, `{file}` argument template, configurable timeout,
`UseShellExecute = false` + `ArgumentList`, stdout/stderr
captured, timeout kills the scanner, downloaded file never
executed). Results: Clean / Infected / Error / TimedOut, wired
into `DownloadService` (scan on completion only when configured +
enabled; an infected download is marked failed). 10 tests, all
passing (stub scanner).

### Import / export (item 12)

`JobImportExport` (JSON + CSV): whitelisted export fields only
(url, destination, total bytes, SHA-256, state, created) — the
job model carries no credentials, and tests assert no
credential-like field appears. Whole-batch validation before
persisting anything; duplicates (same URL + destination) skipped
and reported; malformed/incomplete/malicious input rejected
safely; CSV formula fields stay inert data. 15 tests, all
passing.

### Parity matrix (item 13)

`IDM_FEATURE_PARITY_MATRIX.md` updated to Phase 1: **67 features,
19 implemented, 16 tested, 48 not started** (was 13 / 11 / 54).
New in Phase 1: A5 HTTPS tested, A6 FTP implemented + tested, A7
benchmark harness, A8 single-connection retry tested, A9 UI
controls, D14 import/export (jobs), F6 tray (basic), F7 antivirus
hook, F8 main window (partial). A3 adaptive policy and A4
connection pooling remain not implemented by design/measurement.
Browser integration, video, scheduling, proxy, auth, and queues
remain for later phases — none were removed.

## Phase 1 acceptance criteria — status

| # | Criterion | Status |
|---|---|---|
| 1 | Phase 0 functionality intact | ✅ 23 Phase 0 tests pass in the 81-test suite |
| 2 | Full Windows solution builds, incl. WPF | BLOCKED — no CI run yet (no git remote); WPF compiles on Linux with EnableWindowsTargeting (0 warnings, 0 errors), which is compile-only evidence |
| 3 | Standard `dotnet test` succeeds in Windows CI | BLOCKED — no CI run yet |
| 4 | WPF application launches; primary controls work | BLOCKED (runtime) — app built, compiles clean, never run; needs Windows |
| 5 | Single-connection retry + recovery tested | ✅ 6 tests, all passing |
| 6 | SQLite optimization: measurable results + crash-safety evidence | ✅ 98.8 s → 42.2 s on 1 GB; 3 crash-safety tests passing; max loss documented (4 MB hard crash, 64 KB graceful cancel) |
| 7 | HTTPS tests pass with proper certificate validation | ✅ 6 tests, all passing |
| 8 | FTP implemented + tested, or exact blocker documented | ✅ 14 tests, all passing (FluentFTP 55.1.0, MIT) |
| 9 | Adaptive connection behavior has automated tests | ✅ 4 tests, all passing |
| 10 | Performance results repeatable and accurately reported | ✅ Benchmark harness + `BENCHMARKS.md` (medians of 3 trials); aria2 comparison done — engine is 2.2–4.1× slower, recorded honestly |
| 11 | 10 GB test passes with matching SHA-256, or blocker documented | ✅ PASS — 10,240 MB, resume after 30% interrupt, SHA-256 matches, 439.6 s / 23.3 MB/s |
| 12 | Import/export works and exposes no credentials | ✅ 15 tests, all passing; whitelist enforced by tests |
| 13 | Parity matrix complete and updated | ✅ 67 features, 19 implemented, 16 tested, 48 not started |
| 14 | No paid service / cloud / paid API introduced | ✅ None (aria2 is a free local binary; FluentFTP is MIT) |
| 15 | No major feature silently removed | ✅ None removed; future work stays visible in the matrix |
| 16 | No critical regression unresolved | ✅ 81/81 tests passing; one pre-existing cancel-path journal bug found and fixed (see above) |

## Known limitations and Phase 2 tasks

- **Windows CI has never run.** The workflow is authored but the
  repo has no git remote here, so the full Windows build and
  standard `dotnet test` are BLOCKED, not PASS. Pushing to GitHub
  and getting a green run is the first Phase 2 action.
- **The WPF app has never run.** It compiles cleanly (0 warnings)
  but needs Windows to launch. All UI behavior is unverified at
  runtime.
- **`DownloadService.DownloadAsync` does not return the job id** —
  **fixed in the Phase 1 closure task.** `DownloadService` now
  exposes a job-handle API (`StartDownloadAsync` /
  `ResumeDownloadAsync` return a `DownloadHandle` with the job id
  and a completion task; `PauseAsync` / `CancelAsync` address a
  running download by id; `ListJobsAsync` / `DeleteJobAsync` expose
  the journal). `MainViewModel` and `DownloadItemViewModel` no
  longer touch the engine or the journal directly — all downloads
  go through the service, which also means UI downloads now get
  the post-completion antivirus scan. The journal gained
  `ListJobsAsync` / `DeleteJobAsync` (so history survives a
  restart at the data layer). 9 new tests (6 service, 3 journal),
  all passing; suite is 90/90. The Presentation-owned
  `history.json` restore is unchanged for now; switching the
  startup restore to the journal `ListJobsAsync` is a Phase 2
  step.
- **10 GB cross-process restart** is proven at 10–20 MB by unit
  tests, not at 10 GB (the Prototype resumes in-process).
- **HTTP/2 not exercised** (the test server is HTTP/1.1). FTP
  active mode not implemented (passive only). Windows Defender API
  integration not done (the AV hook takes a scanner executable).
- **Single-connection fallback has no speed limit yet**, and the
  global/per-download rate limiter is still P2 work.

- 2026-11-13: Baseline established; Phase 0 counts corrected;
  Windows CI workflow authored; `<EnableWindowsTargeting>` added to
  the Presentation project (WPF now compiles on Linux; runtime still
  needs Windows).
- 2026-11-13: Engine work complete (single-connection retry, SQLite
  coalescing, HTTPS, adaptive tests) — 81/81 tests passing. WPF,
  FTP, and import/export + AV workstreams delivered by subagents and
  verified (build + tests + code review). Benchmark harness built;
  100 MB + 1 GB results recorded. (At this point in the day the
  10 GB validation and the aria2 comparison were still running;
  both completed later the same day — see the entry below.)
- 2026-11-13: Cancel-path journal bug found and fixed (cancel
  persisted with the cancelled token, so the write threw silently;
  Phase 0's frequent flushes had masked it).
- 2026-11-13: 10 GB validation **PASS** (10,240 MB, SHA-256
  matches, 439.6 s, resume after 30% interrupt). aria2 comparison
  complete (engine 2.2–4.1× slower, recorded honestly in
  `BENCHMARKS.md`); two harness bugs found and fixed
  (`StartTime` before `Start()`; `TotalProcessorTime` after exit
  on Linux).

## Phase 1 closure (2026-11-13, same day)

The engineering work was complete; this task closed the phase
without reimplementing or rerunning successful work.

- **Documentation cleanup.** Status changed to COMPLETE WITH
  EXPLICIT BLOCKERS. Removed the stale "aria2 comparison pending"
  and "10 GB validation running" statements; reconciled the
  status lines in `ROADMAP.md` (`ARCHITECTURE.md`,
  `IDM_FEATURE_PARITY_MATRIX.md`, `SECURITY.md`,
  `DEPENDENCY_AUDIT.md` all said Phase 0 — they now say Phase 1).
  Found and recorded one roadmap gap: the installer (Inno Setup)
  listed under Phase 1 was not built — deferred to Phase 2, with
  the reason (no Windows machine to build or test it). Historical
  results were not changed.
- **Windows CI readiness (verified, not run).** The workflow
  (`.github/workflows/windows-ci.yml`) was inspected line by line:
  checkout@v4, setup-dotnet@v4 (10.0.x, matches the net10.0 /
  net10.0-windows targets), restore + build of the full `.slnx`
  (incl. WPF), `dotnet test` with a trx logger, artifact upload
  (`if: always()`), and a 100 MB Prototype smoke run. Two small
  robustness fixes were made: a job-level `timeout-minutes: 30`,
  and `TEMP`/`TMP` pointed at `runner.temp` so the tests' Unix
  socket paths stay well under Windows' ~108-character limit
  (they would land near 99 characters under the default `%TEMP%`).
  The repo is push-ready (724 KB of source, no test data, `.gitignore`
  covers bin/obj/artifacts; no `.db`/`.part` files in the tree).
  **It still has never run: there is no git repository and no
  remote here.** The user must create a GitHub repository and
  provide the remote URL (or push it themselves). No remote or
  credential was invented.
- **WPF architecture (job handle).** The `DownloadService`
  job-id gap is fixed: the service now exposes a `DownloadHandle`
  (job id + completion task) via `StartDownloadAsync` /
  `ResumeDownloadAsync`, `PauseAsync` / `CancelAsync` by id, and
  `ListJobsAsync` / `DeleteJobAsync`; the journal gained
  `ListJobsAsync` / `DeleteJobAsync`. `MainViewModel` and
  `DownloadItemViewModel` no longer touch the engine or the
  journal — all downloads go through the service (which also
  means UI downloads now get the post-completion antivirus scan).
  The service builds one downloader per download from a factory
  so per-download connection counts keep working. 9 new tests
  (6 service, 3 journal), all passing; suite is 90/90;
  Presentation builds with 0 warnings. The startup restore still
  reads Presentation-owned `history.json`; switching it to the
  journal is a Phase 2 step.
- **Benchmark fairness.** A `--timing split` mode records
  download-only and end-to-end times separately for both tools.
  The legacy numbers are unchanged. Split results (100 MB,
  medians of 3): download-only aria2 ÷ engine is 0.97–1.37×
  (the transfer is near parity; at 16 connections the engine is
  slightly faster), while end-to-end it is 1.16–1.49× — the gap
  is dominated by post-transfer verification + journal +
  promotion (~0.2–0.45 s on 100 MB). The "within 5% of aria2"
  target is still not met and is not claimed. Note: aria2 had
  to be reinstalled (1.37.0) for this run — the VM was replaced
  after Phase 1 and apt-installed packages do not survive a
  deployment; the dotnet SDK in `$HOME/.dotnet` does.
- **Phase 2 plan.** `PHASE_2_PLAN.md` created from the master
  plan and the parity matrix (all 67 entries preserved).
  Priority: Windows verification, engine profiling, job-lifecycle
  follow-ups, browser integration, core IDM workflows.
- **Flaky test observed (not a regression).** During the closure
  final regression, `FtpTransportTests.DroppedDataConnection_MapsToTransient_AndRetryCompletes`
  failed once (`Assert.Throws() Failure: No exception was thrown`
  — the drop did not land while the client was still reading, so
  the transfer completed). An immediate rerun passed: 90/90.
  The test is timing-sensitive by construction (it depends on a
  connection drop landing mid-transfer); on a fast VM the transfer
  can finish first. No code was changed between the two runs. If
  it flakes again, the fix belongs in the test (e.g. a slower
  throttle so the drop reliably lands mid-transfer), not in the
  transport.
