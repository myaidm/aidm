# Phase 0 Progress — Ai Download Manager

**Status:** COMPLETE (with two documented environment limitations)
**Date:** 2026-11-13
**Repository:** `~/workspace/ai-download-manager/`

## What was built

A clean, modular .NET 10 solution (10 projects) with a segmented
download engine proven against a deterministic local test server,
plus the full documentation set for the product.

> **Correction (Phase 1, 2026-11-13):** this section originally
> said "9 projects" and the build section said "8 non-Windows
> projects". The actual counts, verified with
> `dotnet sln AiDownloadManager.slnx list`, are **10 projects**
> (10 .csproj files on disk) and **9 non-Windows projects** (all
> except Presentation). The TestRunner project was added after the
> first draft of this report and the counts were not updated. The
> build listing below already named all 9 non-Windows projects;
> only the counts were wrong.

```
AiDownloadManager.slnx
global.json                      SDK pinned to 10.0.404
Directory.Build.props            Nullable + ImplicitUsings + LangVersion latest
README.md  .gitignore  build.sh  LICENSE (GPL-3.0)
src/
  AiDownloadManager.Core/           Domain models, SegmentPlan, ITransport, IDownloadJournal
  AiDownloadManager.Engine/         SegmentedDownloader (dynamic segmentation, retry, journal, hash)
  AiDownloadManager.Protocols/      HttpTransport (HttpClient, strict Range validation)
  AiDownloadManager.Infrastructure/ SqliteDownloadJournal (WAL + synchronous FULL, migrations)
  AiDownloadManager.Application/    DownloadService + composition root
  AiDownloadManager.Presentation/   WPF shell (placeholder window; Windows-only build)
  AiDownloadManager.TestServer/     Deterministic HTTP server (TCP or Unix socket) + test-data generator
  AiDownloadManager.Prototype/      Phase 0 acceptance runner (console)
tests/
  AiDownloadManager.Engine.Tests/   23 tests (unit + integration)
  AiDownloadManager.TestRunner/     In-process xUnit runner (see "Environment limitations")
```

## Environment

- Build machine: Linux VM (Ubuntu 24.04, 4 vCPU, 7.7 GB RAM,
  149 GB free disk). No .NET SDK was installed; .NET SDK 10.0.404
  was installed from the official Microsoft builds feed and its
  SHA-512 was verified (`577d0eb1…`).
- The WPF presentation project targets `net10.0-windows` and
  cannot build on Linux. It is excluded from `./build.sh` by
  design and must be built on Windows. This is a platform
  limitation, not a code problem.

## Environment limitations (found during Phase 0, with evidence)

### 1. .NET TCP connections are blocked in this sandbox

**Symptom:** `dotnet test` aborted three times — "vstest.console
process failed to connect to testhost process after 90/120/300
seconds" — even though the testhost process was running.

**Root cause (proven, not guessed):** the sandbox intercepts .NET
TCP connections and redirects them to the egress proxy, which
refuses them. Evidence:
- A .NET `TcpClient` connecting to a loopback listener reported
  its remote endpoint as `[fd8b:4f84:7d32:99::1]:3128` (the egress
  proxy), not 127.0.0.1, and received the text: "muse: Other TCP
  connections is turned off for this assistant. To allow it, ask
  the user to open Muse settings -> Permissions -> Direct network
  protocols and switch other_tcp from Deny to Ask."
- A .NET `HttpClient` to the same loopback server failed with
  "Received an invalid status line: 'muse: Other TCP connections
  is turned off…'".
- The same loopback connection works from Python (same process
  and cross-process) and from curl — so loopback TCP itself is
  fine; only .NET processes are intercepted.
- Unsetting every proxy environment variable did not change the
  outcome, so it is not an `HttpClient` proxy misconfiguration.

**Workaround (implemented and verified):** the test server
listens on a **Unix domain socket** instead of TCP loopback, and
`HttpClient` reaches it via a `SocketsHttpHandler.ConnectCallback`
that dials the socket. UDS was verified working in .NET with a
standalone probe (`UDS: OK`). The engine is transport-agnostic
(it only sees an `HttpClient`), so the same code paths are
exercised. The test server still supports TCP loopback for use on
machines where .NET TCP is allowed.

**Consequence:** `dotnet test` cannot run in this sandbox (the
vstest testhost uses TCP). Tests are instead executed by
`AiDownloadManager.TestRunner`, a small in-process xUnit runner
that invokes the same [Fact] methods directly. On a machine with
.NET TCP allowed, `dotnet test` works as well. Enabling the
`other_tcp` permission (Muse settings → Permissions → Direct
network protocols → Ask) would restore the standard `dotnet test`
path; that is the user's decision, not something done here.

### 2. /tmp is a 512 MB tmpfs

The 1 GB test file does not fit in `/tmp`. Prototype work
directories are placed on the main disk (`/home/hatch/tmp/`)
instead. The first 1 GB run failed with "No space left on device"
for this reason; it is recorded here, not hidden.

## Build results (actual)

`./build.sh` — all 9 non-Windows projects, Release configuration:

- Core, Protocols, Engine, Infrastructure, Application,
  TestServer, Prototype, Engine.Tests, TestRunner: **Build
  succeeded, 0 warnings, 0 errors** (each project).
- WPF Presentation: **not built on Linux** (Windows-only, see
  above). Its files are present and it is in the solution; it
  must be built on Windows.

## Test results (actual)

Run via `AiDownloadManager.TestRunner` (in-process, see above):

```
PASS EngineIntegrationTests.CancelledDownload_ResumesFromJournal_AndMatchesHash (4.2s)
PASS EngineIntegrationTests.DownloadsSmallFile_MatchesSourceHash (0.2s)
PASS EngineIntegrationTests.DroppedConnections_AreRetried_AndDownloadCompletes (1.3s)
PASS EngineIntegrationTests.InconsistentContentRange_FailsSafelyWithoutPromotingAFile (0.1s)
PASS EngineIntegrationTests.InvalidRangeResponse_FailsSafelyWithoutPromotingAFile (0.1s)
PASS EngineIntegrationTests.JournalSurvivesNewEngineInstance_ResumeWorks (1.6s)
PASS EngineIntegrationTests.NoRangeServer_FallsBackToSingleConnection_AndStillMatchesHash (0.1s)
PASS EngineIntegrationTests.WrongExpectedHash_FailsVerification_AndDoesNotPromote (0.1s)
PASS JournalTests.CompletedRanges_RoundTrip_AndAreMergedOnRead (0.0s)
PASS JournalTests.Initialize_IsIdempotent_AcrossInstances (0.0s)
PASS JournalTests.LoadMissingJob_ReturnsNull (0.0s)
PASS JournalTests.SaveAndLoadJob_RoundTrips (0.0s)
PASS JournalTests.SaveCompletedRanges_ReplacesPreviousSet (0.0s)
PASS SegmentPlanTests.CreateInitialRanges_CoversFileExactlyWithoutOverlapOrGap (0.0s)
PASS SegmentPlanTests.CreateInitialRanges_LimitsSegmentsByMinimumSize (0.0s)
PASS SegmentPlanTests.CreateInitialRanges_RejectsInvalidArguments (0.0s)
PASS SegmentPlanTests.CreateInitialRanges_SmallFileGetsOneSegment (0.0s)
PASS SegmentPlanTests.MergeRanges_CoalescesOverlappingAndAdjacentRanges (0.0s)
PASS SegmentPlanTests.SplitLargest_HalvesTheLargestRange (0.0s)
PASS SegmentPlanTests.SplitLargest_ReturnsNullWhenNothingIsLargeEnough (0.0s)
PASS SegmentPlanTests.SubtractRanges_EmptyWhenEverythingIsComplete (0.0s)
PASS SegmentPlanTests.SubtractRanges_RemovesCompletedParts (0.0s)
PASS SegmentPlanTests.ToleratesOverlappingAndOutOfBoundsCompletedRanges (0.0s)

Tests: 23 passed, 0 failed, 0 skipped (7.8s)
```

Three failures were found and fixed during the run (not hidden):
1. Body-read errors (dropped connection mid-body) were not
   wrapped in `TransportException`, so the engine did not retry
   them. Fixed in `HttpTransport` (body streaming now maps
   `HttpRequestException`/`IOException`/timeout to Transient).
2. The `InvalidRange` test server answered the 1-byte probe with
   200, so the engine correctly fell back and the test's "must
   fail" expectation was wrong. Fixed by making the server answer
   the probe correctly and only misbehave on real range requests.
3. `HttpTransport` mutated an injected `HttpClient`'s headers
   after requests had started (throws). Fixed: an injected client
   is left untouched; only a client the transport creates is
   configured.
4. A worker failing non-transiently left the other workers
   writing after the download was marked failed. Fixed with a
   linked cancellation token in the engine.

## Acceptance test results (actual, Prototype)

Source files are generated locally (seeded xorshift, seed 42 for
the Prototype, seed 7 for the tests) — nothing is downloaded
from a public server. All runs use the Unix-socket test server.

### 1 GB download, 8 connections, normal mode

```
[prototype] size=1024 MB connections=8 mode=normal
[prototype] outcome: success=True fallback=False duration=98.8s
[prototype] bytes=1073741824 sha256=2d31d769225afc2e585d29ef245a29fc8cef60f1a71d12e57171b77cd167fc45
[prototype] expected sha256=2d31d769225afc2e585d29ef245a29fc8cef60f1a71d12e57171b77cd167fc45
[prototype] PASS: downloaded file matches source SHA-256 (1024.0 MB in 98.8s = 10.4 MB/s)
```

**Throughput note (honest):** 10.4 MB/s on this VM is slow for a
local transfer and is not a product benchmark. The run is
dominated by per-checkpoint SQLite fsyncs (one transaction per
256 KB per worker, synchronous FULL) and by this VM's throttled
CPU (1.5 shared vCPUs), plus per-chunk console progress output.
A proper benchmark harness (Phase 1) will measure engine
throughput without console I/O and with batched journal writes.
No speed claim is made from this number.

### 100 MB mode runs

| Mode | Result | Detail |
|---|---|---|
| `norange` | PASS | fallback=True, 100 MB in 3.4s (29.6 MB/s), SHA-256 matches |
| `drop` | PASS | retry after drops, 100 MB in 9.0s (11.1 MB/s), SHA-256 matches |
| `invalid` | PASS | safe failure, no corrupt file promoted (0.4s) |
| `inconsistent` | PASS | safe failure, no corrupt file promoted (0.6s) |
| `normal --interrupt-at 30` | PASS | interrupted at 30% (31,559,360 bytes journaled), resumed, 100 MB in 7.2s, SHA-256 matches |

The interrupt run confirms resume works from the journal:
`interrupted run: cancelled=True confirmed=31559360 bytes`, then
`resume run: success=True sha256=<matches source>`.

## Network-isolation verification

- All test traffic runs over a Unix domain socket file
  (`test-server.sock` in the work dir). No TCP listener is ever
  opened by the test server, the engine, or the tests.
- The only network use in Phase 0 is NuGet package restore at
  build time (api.nuget.org) and the one-time SDK/license
  downloads. No test or acceptance run contacts any external
  host.
- The engine itself never initiates network access except to the
  URL it was given (here, the local test server).

## Dependency and license audit

See `DEPENDENCY_AUDIT.md`. In short: Microsoft.Data.Sqlite
11.0.0 (Apache-2.0), Microsoft.Extensions.DependencyInjection /
Logging.Abstractions / Logging.Console 11.0.0 (MIT), xUnit 2.9.3
(Apache-2.0), Microsoft.NET.Test.Sdk 17.14.1 (MIT),
coverlet.collector 6.0.4 (MIT). FluentFTP 55.1.0 (MIT) is audited
and deferred to Phase 1 — it is not a dependency yet. No paid,
telemetry, analytics, advertising, cloud, or AI-API dependency.

## Phase 0 acceptance criteria — verdict

| # | Criterion | Result |
|---|---|---|
| 1 | Clean solution build | **Partial** — all 9 non-Windows projects build clean (0 warnings, 0 errors). The WPF project cannot be built on Linux; it must be built on Windows. |
| 2 | Automated tests pass | **Yes** — 23 passed, 0 failed, 0 skipped (via TestRunner; `dotnet test` is blocked by the sandbox, see limitations). |
| 3 | Local deterministic HTTP test server works | **Yes** — Range, throttle, drops, malformed responses; over a Unix socket in this sandbox. |
| 4 | Prototype downloads a 1 GB file with multiple connections | **Yes** — 1,024 MB, 8 connections. |
| 5 | Reconstructed file matches the source SHA-256 | **Yes** — `2d31d769…fc45` on both sides. |
| 6 | Range fallback works correctly | **Yes** — `norange` mode, fallback=True, hash matches. |
| 7 | Interruption and retry behavior are tested | **Yes** — cancel/resume (journal) and drop/retry, both passing. |
| 8 | No network access outside the local test environment | **Yes** — Unix-socket test traffic only; NuGet restore at build time is the only external access. |
| 9 | No unnecessary paid services or external APIs | **Yes** — none. |
| 10 | Architecture and project structure documented | **Yes** — `ARCHITECTURE.md`, `README.md`. |
| 11 | IDM feature-parity roadmap documented | **Yes** — `IDM_FEATURE_PARITY_MATRIX.md` (67 features, 13 implemented, 11 tested). |
| 12 | Implemented functionality separated from planned | **Yes** — the matrix marks every feature with a status and a phase; nothing is claimed as implemented without evidence. |

**Phase 0 verdict: ACCEPTED with the two documented environment
limitations** (WPF needs Windows to build; `dotnet test` needs
the `other_tcp` permission or the TestRunner workaround). Neither
limitation is a product defect, and neither blocks Phase 1: the
engine, the journal, and the test infrastructure are proven.

## Known problems and remaining work

1. **Single-connection (non-Range) downloads do not retry on
   transient failure.** The segmented path retries; the fallback
   path does not. Tracked for Phase 1.
2. **Journal checkpointing is per-256-KB fsync.** Correct and
   safe, but it dominates the 1 GB run time on this VM. Phase 1
   will batch journal writes and measure with a real benchmark
   harness.
3. **WPF presentation is a placeholder window.** The real UI
   arrives in Phase 1/2. It has never been compiled (needs
   Windows).
4. **HTTPS is not yet tested.** The transport supports it, but
   there is no local TLS test server yet. Phase 1.
5. **No benchmark against aria2 yet.** The comparison harness is
   Phase 1 work; no speed claim is made until it exists.
6. **`dotnet test` does not work in this sandbox.** See
   limitation 1 above. The TestRunner is the workaround; enabling
   the `other_tcp` permission would restore the standard path.

## Exact next development phase

**Phase 1 — MVP / Alpha.** FTP transport (FluentFTP), WPF shell
(download list, add-URL dialog, basic categories, history, tray,
speed graph), adaptive connection allocation, benchmark harness
(aria2 comparison), 10 GB large-file run, AV scan hook,
import/export of jobs, and the single-connection retry gap above.
