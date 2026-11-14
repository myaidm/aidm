# Architecture — Ai Download Manager

**Status:** Phase 1 — MVP / Alpha
**Date:** 2026-11-13

## Overview

Ai Download Manager is a layered, modular Windows desktop
application. The dependency rule is strict: **a layer may only
depend on the layers below it, and the download engine never
depends on the UI.** The engine is a plain .NET library with no
WPF, no Win32, and no Windows-only API, so it can be tested on
any platform and so a future macOS/Linux port would only replace
the presentation and platform-integration layers.

```
┌─────────────────────────────────────────────────────────┐
│  Presentation (WPF, net10.0-windows)                    │
│  Main window · download list · categories · scheduler   │
│  settings · history/statistics · tray icon · dialogs   │
│  (MVVM; views + view-models; no business logic in       │
│   code-behind; all strings via RESX)                    │
├─────────────────────────────────────────────────────────┤
│  Application services (net10.0)                         │
│  DownloadService · (Phase 1+) QueueService ·            │
│  SchedulerService · CategoryService · HistoryService ·  │
│  SettingsService · ClipboardMonitor · ExtensionBridge   │
│  UpdateService (opt-in) · AvScannerHook                 │
├─────────────────────────────────────────────────────────┤
│  Download engine (net10.0, UI-independent)             │
│  SegmentedDownloader · WorkQueue (dynamic segmentation) │
│  RetryPolicy · IntegrityVerifier (SHA-256) · DiskWriter │
│  (buffered async I/O, sparse pre-allocation, journal)   │
├─────────────────────────────────────────────────────────┤
│  Protocol layer (net10.0)                               │
│  ITransport (Core) · HttpTransport (HttpClient) ·       │
│  (Phase 1) FtpTransport (FluentFTP) · ProxySelector ·   │
│  AuthHandler (Basic/Digest/NTLM/Negotiate) · CookieJar  │
├─────────────────────────────────────────────────────────┤
│  Persistence (net10.0)                                  │
│  SQLite (jobs, completed ranges, history, settings)     │
│  WAL + synchronous FULL; versioned migrations           │
├─────────────────────────────────────────────────────────┤
│  Platform integration (Windows, in Presentation)        │
│  Tray · jump lists · toast notifications · DPAPI /      │
│  Credential Manager · registry (native-messaging host)  │
│  power management (prevent sleep during transfers)      │
└─────────────────────────────────────────────────────────┘
```

## Projects

| Project | Target | Depends on | Responsibility |
|---|---|---|---|
| `AiDownloadManager.Core` | net10.0 | (none) | Domain models (`DownloadJob`, `ByteRange`, `DownloadOutcome`), pure range planning (`SegmentPlan`), abstractions (`ITransport`, `IDownloadJournal`). No I/O, no packages. |
| `AiDownloadManager.Engine` | net10.0 | Core, Protocols | `SegmentedDownloader`: probe → plan → parallel segmented download → verify → promote. Owns the work queue, retry, journal checkpointing, and hash verification. |
| `AiDownloadManager.Protocols` | net10.0 | Core | `HttpTransport`: `HttpClient`-based HTTP/HTTPS with Range probing and strict response validation. FTP arrives in Phase 1 as a new `ITransport`. |
| `AiDownloadManager.Infrastructure` | net10.0 | Core | `SqliteDownloadJournal`: crash-safe SQLite persistence with versioned migrations (WAL + synchronous FULL). |
| `AiDownloadManager.Application` | net10.0 | Core, Engine, Infrastructure, Protocols | `DownloadService` (thin orchestration) and the composition root (`AddAiDownloadManager`). Queues, scheduler, categories, and the extension bridge are added here in later phases. |
| `AiDownloadManager.Presentation` | net10.0-windows | Application, Core | WPF shell. **Windows-only at runtime.** Phase 1 builds the functional UI (download list, Add URL dialog, speed graph, tray). It compiles on Linux with `<EnableWindowsTargeting>true</EnableWindowsTargeting>`; it cannot run on Linux. |
| `AiDownloadManager.TestServer` | net10.0 | (none) | Deterministic local HTTP/HTTPS server (raw `Socket`) for tests and the prototype. Supports Range, throttling, drops, malformed responses, redirects, and TLS with a self-signed test certificate. Listens on TCP loopback or a Unix domain socket. Never shipped. |
| `AiDownloadManager.Prototype` | net10.0 | Engine, Infrastructure, TestServer, Application | Console acceptance runner: generates a deterministic file, serves it locally, downloads it, verifies SHA-256. |
| `AiDownloadManager.Engine.Tests` | net10.0 | Engine, Infrastructure, TestServer, Core | xUnit unit + integration tests. |

## Key design decisions (Phase 0)

### 1. The journal is the source of truth for resume

The engine never guesses what is on disk. Completed byte ranges
are recorded in SQLite (WAL, synchronous FULL) as they are
downloaded and re-read on resume. A download resumes from the
journal, not from the file's length. A file is only promoted to
its destination after its length and (when supplied) its SHA-256
have been verified, so a corrupt or partial file can never be
presented as complete.

**Checkpoint policy (Phase 1, changed from Phase 0).** Phase 0
flushed the journal on every 256 KB chunk per worker, which spent
most of a 1 GB download in SQLite fsyncs. Phase 1 separates two
granularities:

- *In-memory progress* is recorded every 256 KB per worker (cheap:
  a list entry). A graceful cancel therefore resumes from the last
  chunk — at most one 64 KB in-flight chunk per worker is lost.
- *Journal flushes* are coalesced: one synchronous transaction per
  `JournalCheckpointBytes` (default 4 MB) of newly completed data,
  or every `JournalCheckpointInterval` (default 2 s), whichever
  comes first — plus a forced flush on completion, cancellation,
  and failure. A hard crash (process killed, no graceful cancel)
  therefore loses at most `JournalCheckpointBytes` (4 MB) of
  completed-but-unflushed progress.

Durability was NOT weakened to buy this speed: every flush is
still a committed transaction with synchronous FULL and WAL, and
nothing is ever reported as saved before its transaction commits.
The only change is how often a transaction is committed. The
maximum loss is documented here and in `PHASE_1_PROGRESS.md`,
and `JournalCheckpointTests` proves both the reduced write count
and recovery after an interrupted journal write.

### 2. Dynamic segmentation via a shared work queue

The engine does not assign fixed segments to fixed workers.
Instead, all remaining byte ranges live in a locked work queue.
A worker claims the largest remaining range and, if it is at
least twice the minimum segment size (1 MB), splits it in half:
the first half goes back in the queue and the worker takes the
second half. This is the same idea as IDM's dynamic segmentation
(split the largest segment when a connection frees up),
implemented independently. It guarantees that a slow connection
cannot hold up the download while fast connections sit idle, and
it makes resume trivial: the remaining ranges are simply whatever
the journal does not already cover.

### 3. Strict Range validation

`HttpTransport` probes with a real Range request (`bytes=0-0`)
instead of trusting `Accept-Ranges`, and validates every Range
response: status must be 206 and `Content-Range` must match the
requested range exactly. A server that lies about Range support
is detected and either falls back to a single connection (when
the probe shows no Range support) or fails safely with a clear
error (when a download response is malformed). The engine never
writes bytes it did not ask for.

### 4. Transports are pluggable

All protocol knowledge lives behind `ITransport`. Adding FTP is
a new `ITransport` implementation, not a branch inside the
engine. The engine calls `ProbeAsync`, `DownloadRangeAsync`, and
`DownloadToEndAsync`; it never sees a URL scheme.

### 5. Single-connection fallback retries and resumes (Phase 1)

A server without Range support is downloaded with one connection,
but that path now has the same retry discipline as the segmented
path: transient failures are retried with exponential backoff
(`MaxAttemptsPerRange`, cancellable during the delay), and progress
is checkpointed to the journal as the prefix `[0, written-1]`. On
retry — and on resume after a restart — the engine re-requests the
whole file and skips the bytes already safely on disk, then
appends. The final length/hash check still applies, so a server
that returns different (e.g. shorter) content on a retry fails
safely instead of promoting a corrupt file. Only a prefix starting
at 0 is resumable without Range support; non-prefix ranges are
discarded with a warning.

### 6. TLS failures are security failures, not network glitches (Phase 1)

`HttpTransport` maps a TLS certificate validation failure to the
dedicated `TransportFailureKind.CertificateError`. The engine
never retries it, and validation is never disabled or bypassed in
production code. Tests use a local self-signed certificate that
clients either pin by thumbprint (test-only) or reject via default
validation. See `TESTING_STRATEGY.md`.

### 7. No UI types below the presentation layer

Core, Engine, Protocols, and Infrastructure contain no WPF, no
`System.Windows`, and no Win32 calls. The engine reports progress
through `IProgress<DownloadProgress>` and results through
`DownloadOutcome`. This is what makes the engine testable on
Linux and keeps the future UI work from destabilising it.

### 8. Migrations are versioned and explicit

`SqliteDownloadJournal` applies a numbered list of migrations in
order and records each in a `schema_version` table. There is no
ORM and no auto-migration: a schema change is a new, reviewed
migration, and `InitializeAsync` is idempotent.

## Data model (SQLite, Phase 0)

```sql
downloads(id TEXT PK, url TEXT, destination TEXT,
          total_bytes INTEGER NULL, expected_sha256 TEXT NULL,
          state TEXT, created_at TEXT, error TEXT NULL)
completed_ranges(download_id TEXT, start_byte INTEGER,
                 end_byte INTEGER,
                 PRIMARY KEY (download_id, start_byte))
schema_version(version INTEGER PK, applied_at TEXT)
```

Later phases add `queues`, `queue_items`, `categories`,
`history`, and `settings` tables; each arrives with its own
migration, never by editing migration v1.

## Modularity rules

- Each feature is a module behind an interface in Application
  services; the engine exposes no UI types.
- New protocols are added as a new `ITransport`, not by branching
  inside the engine.
- The browser-extension protocol is versioned (`protocol: 1`);
  old extensions fail loudly instead of silently misbehaving.
- No module reads another module's tables directly; all
  cross-module access goes through the owning service.
- The composition root lives in `AiDownloadManager.Application`
  (`ServiceCollectionExtensions`), not in Infrastructure, so the
  layering stays clean: Infrastructure knows nothing about the
  engine or the transports.

## What Phase 0 deliberately does not build

- The full WPF interface (placeholder window only).
- Browser extensions and the native-messaging host.
- Queues, scheduler, categories, clipboard monitoring.
- FTP transport (audited, deferred to Phase 1).
- Site grabber, Download All, video conversion, torrents.
- Installer (Inno Setup), WinGet manifest, code signing.

Each is preserved in `IDM_FEATURE_PARITY_MATRIX.md` and
`ROADMAP.md` with its planned phase. Nothing in the Phase 0
foundation makes any of them harder: the engine, the transport
abstraction, the journal schema, and the service boundary were
all designed with the full roadmap in mind.

## Build notes

- SDK pinned in `global.json` (10.0.404). `dotnet build` on
  Windows builds the full solution; on Linux, `./build.sh`
  builds everything except the WPF presentation project, which
  requires Windows. This is a platform limitation, not a code
  problem, and it is stated in every build document so nobody
  mistakes it for a failure.
- Release configuration is the default for verification
  (`-c Release`); Debug builds are for development only.
