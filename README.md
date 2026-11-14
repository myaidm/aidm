# Ai Download Manager

A free, open-source Windows desktop download manager. No
registration, no serial keys, no activation, no subscriptions, no
paid features, no advertisements, no bundled offers, and no
mandatory server infrastructure.

**Status: Phase 1 — MVP / Alpha, complete (see `PHASE_1_PROGRESS.md`).**
The segmented download engine, the deterministic test server, and
the persistence layer are implemented and tested (81 tests pass;
1 GB and 10 GB acceptance runs verified by SHA-256). FTP/FTPS,
HTTPS, single-connection retry, JSON/CSV import/export, and an
opt-in antivirus hook are implemented and tested. The functional
WPF interface is built and compiles cleanly (it needs Windows to
run). Browser extensions, scheduler, and the rest of the IDM
feature-parity roadmap arrive in later phases. See `ROADMAP.md`
and `PHASE_1_PROGRESS.md`.

## Product principles

- Free means free: the full application is the free application.
- Local-first: all state lives on the user's machine.
- Honest performance: speed claims are measured, never marketed.
- Privacy by default: no telemetry, no account, no data leaving
  the machine except the user's own downloads.
- Legal boundaries are features: no DRM circumvention, no
  access-control bypasses.

## Repository layout

```
AiDownloadManager.slnx
src/
  AiDownloadManager.Core/           Domain models, range planning, transport/journal abstractions
  AiDownloadManager.Engine/         Segmented download engine (UI-independent)
  AiDownloadManager.Protocols/      HTTP transport (HttpClient); FTP arrives in Phase 1
  AiDownloadManager.Infrastructure/ SQLite journal, DI composition
  AiDownloadManager.Application/    Application services (DownloadService)
  AiDownloadManager.Presentation/   WPF shell (Windows only — does not build on Linux)
  AiDownloadManager.TestServer/     Deterministic HTTP/HTTPS test server (Unix socket or loopback; tests only)
  AiDownloadManager.Prototype/      Phase 0 acceptance runner (console)
  AiDownloadManager.Benchmark/      Repeatable benchmark harness (engine vs aria2)
tests/
  AiDownloadManager.Engine.Tests/   Unit + integration tests (81 tests, all passing)
  AiDownloadManager.TestRunner/     In-process xUnit runner (used where `dotnet test` cannot run)
```

## Building

Requires the .NET 10 SDK (see `global.json`).

```powershell
# Windows (full solution, including the WPF app):
dotnet build AiDownloadManager.slnx

# Linux (engine, tests, and prototype only — WPF needs Windows):
./build.sh
```

## Running the tests

```sh
# In-process runner (the standard `dotnet test` path needs .NET TCP,
# which this sandbox blocks — see TESTING_STRATEGY.md):
dotnet run --project tests/AiDownloadManager.TestRunner -c Release
```

## Running the Phase 0 acceptance test

```sh
dotnet run --project src/AiDownloadManager.Prototype -- --size-mb 1024 --connections 8
```

This generates a 1 GB deterministic file locally, serves it from
the test server, downloads it with 8 connections, and verifies
the SHA-256. Nothing touches the public internet.

```sh
# No-Range fallback, retry-after-drop, and interruption/resume:
dotnet run --project src/AiDownloadManager.Prototype -- --size-mb 100 --mode norange
dotnet run --project src/AiDownloadManager.Prototype -- --size-mb 100 --mode drop
dotnet run --project src/AiDownloadManager.Prototype -- --size-mb 100 --interrupt-at 30
```

## Documentation

- `DOWNLOAD_MANAGER_MASTER_PLAN.md` (workspace root) — the approved
  master plan, including the IDM feature comparison.
- `ARCHITECTURE.md` — layered architecture and module boundaries.
- `IDM_FEATURE_PARITY_MATRIX.md` — the master checklist for the
  complete product; every feature has a status and a phase.
- `DEPENDENCY_AUDIT.md` — every dependency, its license, and why
  it is needed.
- `TESTING_STRATEGY.md` — how testing works and what is covered.
- `ROADMAP.md` — phases and acceptance criteria.
- `SECURITY.md` — security and privacy policy.
- `PHASE_0_PROGRESS.md` — Phase 0 status and evidence.
- `PHASE_1_PROGRESS.md` — Phase 1 status and evidence.
- `PHASE_2_PLAN.md` — Phase 2 (Beta) plan, prioritized from the parity matrix.
- `BENCHMARKS.md` — benchmark results (100 MB, 1 GB, 10 GB) and the aria2 comparison.

## License

GNU General Public License v3.0 — see `LICENSE`. Browser
extensions use MIT where appropriate. Contributions use the
Developer Certificate of Origin (DCO); there is no CLA and no
relicensing path to a proprietary version.
