# Dependency Audit — Ai Download Manager

**Date:** 2026-11-13 (Phase 1; FluentFTP 55.1.0 added this phase)
**Policy:** every dependency must be (1) actively maintained,
(2) license-compatible with GPL-3.0, (3) necessary — no dependency
is added "just in case", and (4) free, with no paid tier, account,
or usage limit. Transitive dependencies are listed where they
matter (native binaries). Versions are pinned in the `.csproj`
files; this document records what was actually restored.

## Runtime dependencies (shipped with the application)

| Package | Version | License | Used by | Why it is needed | Alternatives considered |
|---|---|---|---|---|---|
| Microsoft.Data.Sqlite | 11.0.0 | Apache-2.0 | Infrastructure | SQLite journal: crash-safe persistence of jobs and completed byte ranges. The only local database option that is a single file, needs no server, and is maintained by Microsoft. | System.Data.SQLite (heavier, separate native builds); LiteDB (less standard SQL, smaller community); hand-rolled JSON journal (not crash-safe, would need fsync discipline of its own). |
| Microsoft.Extensions.DependencyInjection | 11.0.0 | MIT | Infrastructure, Application | Composition root. The standard .NET DI container; zero learning curve for contributors. | Hand-rolled service locator (worse); Autofac (heavier, no benefit at this scale). |
| Microsoft.Extensions.Logging.Abstractions | 11.0.0 | MIT | Engine, Infrastructure, Application | `ILogger<T>` abstractions only. Lets the engine log without depending on a concrete logger. | A custom logging interface (reinventing a standard). |
| Microsoft.Extensions.Logging.Console | 11.0.0 | MIT | Prototype | Console logging for the Phase 0 acceptance runner only. Not a dependency of the shipped application yet; the WPF app will choose its own providers. | None — trivial, standard. |
| FluentFTP | **55.1.0** | MIT | Protocols | FTP/FTPS transport (`FtpTransport : ITransport`): connection handling, authentication, SIZE/REST/RETR, passive data connections, explicit FTPS with system-trust certificate validation (plus a test-only thumbprint pin for the local test server). Added in Phase 1, when the FTP transport was first implemented. | `FtpWebRequest` (obsolete, removed from modern guidance); writing an FTP client from scratch (wasted effort, easy to get wrong). |

**FluentFTP 55.1.0 — what was checked (Phase 1):**
- **Version:** 55.1.0 is the latest stable release (222 versions published; the flat-container index lists 55.1.0 as newest as of 2026-11-14). Installed via `dotnet add package FluentFTP --version 55.1.0`.
- **License:** MIT, verified from the installed package's nuspec (`~/.nuget/packages/fluentftp/55.1.0/fluentftp.nuspec`: `<license type="expression">MIT</license>`), project `https://github.com/robinrodricks/FluentFTP`, copyright Robin Rodricks and contributors. MIT is GPL-compatible, so no legal review is needed beyond this note.
- **Maintenance:** actively maintained (v55.x release line current in November 2026; 222 published versions). Not abandoned, not a single-person experiment.
- **Transitive dependencies:** none — every `<group>` in the nuspec `<dependencies>` section is empty. No native binaries.
- **Vulnerabilities:** `dotnet list package --vulnerable` on the Protocols project reports "no vulnerable packages".
- **Security review:** the library is used only as an FTP protocol client. It never phones home and needs no account or key. Two behaviours were verified by reading its source and by probing: (1) it always connects over plain TCP sockets and offers no custom-socket hook — this is why the local FTP test server listens on IPv6 loopback (::1), the only loopback family .NET TCP can use in the Muse sandbox; (2) its `CustomStream` hook is TLS-only (GnuTLS), not a transport replacement. Certificate validation defaults to rejecting on any `SslPolicyErrors`; the transport attaches an explicit handler that accepts only on `PolicyErrors == None`, or — test-only — on an exact thumbprint pin. There is no accept-all path.

**Transitive (native) dependencies:** `Microsoft.Data.Sqlite`
pulls `SQLitePCLRaw.bundle_e_sqlite3` (and its platform packages),
which bundles the SQLite C library (public domain) as a native
binary per platform (`runtimes/linux-x64/native/e_sqlite3.so`,
`win-x64`, etc.). The Windows build will include the win-x64
native library automatically. No action is needed; this is
recorded so a future audit knows where the native binaries come
from.

## Test-only dependencies (never shipped)

| Package | Version | License | Why |
|---|---|---|---|
| xunit | 2.9.3 | Apache-2.0 | The standard .NET test framework. |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | Runs xUnit tests under `dotnet test` / VS Test. |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | Test host for `dotnet test`. |
| coverlet.collector | 6.0.4 | MIT | Code-coverage collection (used in CI later). |

## Framework / SDK (not a package dependency)

| Item | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.404 | Pinned in `global.json`. LTS. Installed from the official Microsoft builds feed; SHA-512 verified at install time (`577d0eb1…`). |
| Target framework | net10.0 (libraries, engine, tests); net10.0-windows (WPF presentation only) | The WPF project is the only Windows-only piece. |
| C# | 13 (`LangVersion: latest`) | |

## License compatibility

- The application is GPL-3.0. MIT and Apache-2.0 dependencies
  are both GPL-compatible (per the FSF's license list): they may
  be combined with GPL-3.0 code and the combined work stays
  GPL-3.0. No dependency is copyleft in a way that conflicts, and
  none requires a paid license, a key, or an account.
- FluentFTP is MIT and therefore GPL-compatible; its inclusion in
  Phase 1 needs no legal review beyond this note.
- Every dependency's license is recorded in the release SBOM
  (planned for the CI workflow; see `ROADMAP.md`).

## Maintenance and security posture

- All packages above are the latest stable versions as of
  2026-11-13, from Microsoft or from widely used community
  projects (xUnit, FluentFTP, coverlet) with active maintenance.
- No package is abandoned, no package is a single-person
  experiment, and no package phones home or requires network
  access at runtime.
- `dotnet list package --vulnerable` (and the CI dependency scan
  planned in `ROADMAP.md`) is the standing check for known
  vulnerabilities. No vulnerable package was knowingly included.
- The repository intentionally has **no** dependency on: any AI
  API, any analytics/telemetry SDK, any advertising SDK, any
  cloud SDK, or any package with a "free for non-commercial use"
  or per-seat license.

## Rejected dependencies (for the record)

- **aria2 (as an engine):** proven and fast, but it would make
  the product a GUI over someone else's engine, limit control
  over resume semantics, and add a native binary to ship and
  sign. It remains the performance benchmark, not a dependency.
- **Electron / Tauri:** runtime footprint and a second language
  for the UI; rejected in the master plan.
- **Serilog / NLog:** good loggers, but `Microsoft.Extensions.Logging`
  covers Phase 0 needs with zero extra packages. Revisit only if
  structured file logging is required.
- **Entity Framework Core:** far heavier than needed for two
  tables and a versioned migration runner; raw SQL via
  `Microsoft.Data.Sqlite` is simpler, auditable, and has no
  hidden queries.
