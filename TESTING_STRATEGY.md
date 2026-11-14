# Testing Strategy — Ai Download Manager

**Status:** Phase 1
**Date:** 2026-11-13

## Principles

1. **Never report a test as passed unless it actually ran and
   passed.** A skipped, aborted, or timed-out run is reported as
   such, with the reason.
2. **Never report a benchmark as achieved without actual
   measurements.** A benchmark is a script, a machine, and a
   number — not an adjective.
3. **No test may depend on the public internet.** Every test uses
   the local deterministic test server or a local file. A test
   that needs the network is a broken test.
4. **Failures are visible.** Build errors, warnings, skipped
   tests, and known limitations stay in the progress report;
   they are not cleaned up for presentation.

## Test levels

### Unit tests (fast, no I/O)

- `SegmentPlan`: range allocation (covers the file exactly, no
  overlap, no gaps), minimum-segment limits, `SubtractRanges`
  (including overlapping and out-of-bounds completed ranges),
  `SplitLargest`, `MergeRanges`.
- Journal: round-trips, replacement semantics, missing-job
  behaviour, idempotent initialisation.
- Later: filename sanitisation, category rules, rename
  templates, settings merging, rate limiting.

### Integration tests (local deterministic test servers)

The engine is tested against `DeterministicTestServer`, a
hand-rolled HTTP/1.1 server on a raw `Socket` (TCP loopback or a
Unix domain socket — see "Environment notes"). It serves a
generated file and supports:

- Correct Range responses (206 + exact `Content-Range`).
- `IgnoreRange`: always 200 with the full body (fallback path).
- `InvalidRange`: 200 to a Range request (safe-failure path).
- `InconsistentContentRange`: 206 with a wrong `Content-Range`
  (safe-failure path).
- Dropping: closes after N bytes, for the first K connections
  only (retry path). Dropping is orthogonal to the behavior, so a
  no-Range server can drop connections too.
- Throttling (bytes/second) to make downloads slow enough to
  cancel mid-flight deterministically.
- TLS (HTTPS) with a self-signed test certificate
  (`TestCertificates`), and a 302 redirect path.

Current integration tests:

| Test | What it proves |
|---|---|
| Downloads small file, matches hash | Segmented download reconstructs the file byte-exactly. |
| No-Range server falls back | Single-connection fallback works and still matches the hash. |
| Invalid Range response fails safely | A lying server is detected; no file is promoted. |
| Inconsistent Content-Range fails safely | Same, for a wrong `Content-Range`. |
| Dropped connections are retried | Retry completes the download with a matching hash. |
| Cancelled download resumes from journal | Resume after cancellation; hash matches; nothing promoted before completion. |
| Journal survives a new engine instance | Resume works across processes (new engine + new journal instance). |
| Wrong expected hash fails verification | SHA-256 mismatch fails and does not promote the file. |

### Phase 1 test suites

| Suite | Tests | What it proves |
|---|---|---|
| `SingleConnectionRetryTests` | 6 | No-Range retry: drop/retry success, retry limit, cancel during the retry delay, shrink-on-retry safe failure, restart resume from the journal prefix, retry-limit-then-recover. |
| `JournalCheckpointTests` | 3 | Journal flushes are coalesced (a 20 MB download does ~6 range writes, not ~80); an interrupted journal write fails safely and a resume with a working journal succeeds; crash-like interruption leaves no corrupt file. |
| `HttpsTransportTests` | 6 | HTTPS download + hash; wrong-certificate and untrusted-chain rejection as `CertificateError` (never retried, never bypassed); redirect following; drop/retry over TLS. |
| `FtpTransportTests` | 14 | FTP probe/range/download-to-end + hash; no-REST fallback; wrong credentials; missing file; malformed URL; drop/retry; FTPS success with a pinned certificate and wrong-thumbprint rejection; bad greeting. |
| `AdaptiveAllocationTests` | 4 | Sizes (1/5/20 MB) × connections (1/4/8/16) all succeed with matching hashes; a slow worker does not break the download; a single worker failure is retried; the connection limit is respected and several workers run concurrently. |
| `ImportExportTests` | 15 | JSON/CSV round-trips; malformed input imports nothing; duplicates skipped; invalid URLs/SHA-256/paths rejected; exported output contains no credential-like fields; CSV formula fields stay inert data. |
| `AntivirusScannerTests` | 10 | Clean/infected stub-scanner results; missing scanner is an error; slow scanner times out and its process is killed; disabled/unconfigured scanner never invokes a process; the downloaded file is never executed; DownloadService integration (scan on completion, infected download marked failed, disabled scanner does not scan). |

**Current total: 81 tests, all passing** (23 Phase 0 + 58 Phase 1).
Run with `./build.sh` (Linux) or `dotnet test` (Windows / CI).

### Benchmarks

`src/AiDownloadManager.Benchmark` is a repeatable harness (not a
unit test): it runs the engine against the local deterministic
server across connection counts and file sizes, with repeated
trials and median reporting, SHA-256 verification, and CPU /
memory / journal-write reporting. With `--aria2 /usr/bin/aria2c`
it runs aria2 against a TCP-loopback instance of the same server
serving the identical file. See `PHASE_1_PROGRESS.md` for the
latest numbers. Benchmarks are measurements on a specific machine,
not product claims.

### Acceptance tests (Prototype console runner)

`src/AiDownloadManager.Prototype` is the Phase 0 acceptance
runner. It generates a deterministic 1 GB file locally (seeded
xorshift, so the SHA-256 is stable), serves it from the loopback
server, downloads it with N connections, and verifies the hash.
Modes: `normal`, `norange`, `drop`, `invalid`, `inconsistent`,
plus `--interrupt-at PERCENT` (cancel, then resume and verify).
Nothing touches the public internet; the server binds
127.0.0.1 only. Results are recorded in `PHASE_0_PROGRESS.md`
with the actual output, not a summary claim.

### Manual / VM tests (later phases)

- Windows 10 and 11 clean VMs: installer, uninstall, upgrade,
  reboot-mid-download resume, scheduler shutdown, extension
  capture in Chrome/Edge/Firefox.
- Accessibility checklist (keyboard-only operation,
  screen-reader labels) in Phase 3.
- Network audit: release build under packet capture; with the
  update check off, no non-download traffic may appear.

## Deterministic test data

Test files are generated locally with a seeded xorshift generator
(`TestDataGenerator`), never downloaded. The same seed always
produces the same bytes and the same SHA-256 on any machine, so a
hash mismatch always means a real bug, never a flaky fixture.

## Running the tests

```sh
# Linux (all non-Windows projects + the in-process test runner):
./build.sh

# Tests only (in-process runner; `dotnet test` needs .NET TCP, which
# this sandbox blocks — see "Environment notes"):
dotnet run --project tests/AiDownloadManager.TestRunner -c Release

# Windows / CI (standard path, including the WPF project):
dotnet test tests/AiDownloadManager.Engine.Tests -c Release

# Acceptance run (1 GB):
dotnet run --project src/AiDownloadManager.Prototype -- --size-mb 1024 --connections 8

# Benchmarks (100 MB, connections 1/4/8/16, 3 trials):
dotnet run --project src/AiDownloadManager.Benchmark -c Release -- --size-mb 100 --connections 1,4,8,16 --trials 3 --work-dir /home/hatch/tmp/aidm-bench

# Benchmarks with aria2 comparison (aria2c must be installed):
dotnet run --project src/AiDownloadManager.Benchmark -c Release -- --size-mb 100 --connections 1,4,8,16 --trials 3 --aria2 /usr/bin/aria2c --work-dir /home/hatch/tmp/aidm-bench
```

## Environment notes (this VM)

- The build machine is a Linux VM (Ubuntu 24.04) with limited
  CPU and memory. `/tmp` is a 512 MB tmpfs, so 1 GB test files
  must live on the main disk (`/home/hatch/tmp/`), not in
  `/tmp`.
- **.NET TCP is blocked in this sandbox.** The `other_tcp`
  permission is Deny, and the sandbox redirects .NET TCP
  connections to the egress proxy, which refuses them with:
  "muse: Other TCP connections is turned off for this assistant."
  This was proven with a standalone probe: a .NET `TcpClient`
  to a loopback listener reported its remote endpoint as the
  egress proxy (`[fd8b:4f84:7d32:99::1]:3128`), while the same
  connection from Python and curl works fine. Unsetting the
  proxy environment variables does not change it.
  Consequences and workarounds:
  - The test server listens on a **Unix domain socket** instead
    of TCP loopback; `HttpClient` reaches it via a
    `SocketsHttpHandler.ConnectCallback`. UDS was verified
    working in .NET. The engine is transport-agnostic, so the
    same code paths are exercised. TCP loopback remains
    supported for machines where .NET TCP is allowed.
  - `dotnet test` cannot run here (the vstest testhost uses
    TCP). Tests run through `AiDownloadManager.TestRunner`, an
    in-process xUnit runner that invokes the same [Fact] methods
    directly. On a machine with .NET TCP allowed, `dotnet test`
    works as well. Enabling the `other_tcp` permission (Muse
    settings → Permissions → Direct network protocols → Ask)
    would restore the standard path; that is the user's decision.
- The WPF presentation project compiles on Linux with
  `<EnableWindowsTargeting>true</EnableWindowsTargeting>` (verified:
  `dotnet build src/AiDownloadManager.Presentation -c Release`
  succeeds with 0 warnings), so `build.sh`'s exclusion is about
  *running* the UI, not compiling it. Running the WPF app still
  needs Windows. This is stated in `README.md`, `ARCHITECTURE.md`,
  and `PHASE_1_PROGRESS.md`.
