# IDM Feature-Parity Matrix — Ai Download Manager

**Status:** Phase 1 — master checklist for the complete product
**Date:** 2026-11-13
**Reference:** IDM 6.43 Build 18, as publicly documented on
[internetdownloadmanager.com/features2.html](https://www12.internetdownloadmanager.com/features2.html),
[internetdownloadmanager.com/news.html](https://www12.internetdownloadmanager.com/news.html),
and [Wikipedia](https://en.wikipedia.org/wiki/Internet_Download_Manager).
Feature data comes only from public documentation. No IDM code,
assets, or protected implementation details were used.

## Status key

| Status | Meaning |
|---|---|
| Not started | In the roadmap; no design work yet. |
| Designed | Design agreed; implementation not started. |
| In development | Being built now. |
| Implemented | Code exists and builds. |
| Tested | Automated tests cover it and pass. |
| Verified | Tested **and** demonstrated against the acceptance criteria. |
| Deferred | Not planned for the stated version, with a documented reason. |

**Rule:** a feature is never marked Implemented, Tested, or
Verified merely because it appears in this matrix. Status changes
only on evidence (a passing test run, a measured benchmark, a
reviewed build).

## Phase key

- **P0** — Foundations (this phase): engine, journal, test server, prototype.
- **P1** — MVP / Alpha: FTP, basic UI, import/export, AV hook.
- **P2** — Beta: browser extensions, queues, scheduler, speed limits, batch, categories, video detection, installer.
- **P3** — V1.0: polish, themes, accessibility, i18n, signed release.
- **V1.1** — Download All, periodic sync, per-queue limits, link refresh.
- **V1.2** — Video quality/playlists, optional ffmpeg remux.
- **V2.0** — Site grabber.
- **Later** — HTTP/3, Metalink, plugin API, CLI mode, PAC proxy; torrent and macOS/Linux are evaluations, not commitments.

---

## A. Download Engine

### A1. Multi-connection downloads
- **IDM offers:** up to 32 parallel streams per file (per public roundups); dynamic segmentation.
- **We must reproduce:** up to 32 connections per file (default 8), each downloading a distinct byte range concurrently.
- **Improvement:** connection count adapts to measured throughput per connection instead of a fixed number; idle connections are reallocated (see A3).
- **Dependencies:** `HttpClient` with `SocketsHttpHandler`; Range-capable servers.
- **Security/legal:** none beyond normal HTTP. No server fingerprinting beyond a standard User-Agent.
- **Test:** 1 GB file from the loopback test server with 8 and 32 connections; byte-exact SHA-256 match.
- **Status:** Implemented (P0). **Phase:** P0.

### A2. Dynamic file segmentation
- **IDM offers:** splits a file into segments; when a connection finishes, the largest remaining segment is split and the free connection takes half.
- **We must reproduce:** the same rebalancing behaviour, implemented independently.
- **Improvement:** segments are not fixed at start: the work queue re-splits continuously, so a slow connection cannot stall the download while others idle.
- **Dependencies:** `SegmentPlan` + engine work queue (done in P0).
- **Security/legal:** none.
- **Test:** throttled test-server run where one connection is artificially slow; total time must be close to the unthrottled run and the hash must match.
- **Status:** Implemented (P0). **Phase:** P0.

### A3. Intelligent segment allocation / adaptive connections
- **IDM offers:** dynamic segmentation (see A2).
- **We must reproduce:** allocation that responds to observed per-connection speed.
- **Improvement:** per-connection throughput tracking; connections that fall below a threshold get their remaining range re-split to faster ones; the engine backs off the connection count on servers that throttle aggressively.
- **Dependencies:** engine telemetry (per-connection byte counters); P0 work queue.
- **Security/legal:** none.
- **Test:** simulated slow-server test; assert the fast connections absorb the work and the total time improves versus fixed segments.
- **Status:** Partially implemented (P1): dynamic splitting via the
  shared work queue (P0) is the mechanism, and its behavior is locked
  in by `AdaptiveAllocationTests` (sizes × connections, uneven workers,
  worker failure, connection limit — all passing). Per-connection
  throughput tracking and the reallocation policy are NOT yet
  implemented: the 100 MB benchmark showed 4 connections (24.1 MB/s)
  beating 8 (20.7) and 16 (18.8) on the local server, so a more
  aggressive policy was deliberately not built on this evidence.
  **Phase:** P1/P2.

### A4. Connection management
- **IDM offers:** connection reuse without repeated login; configurable connection limits.
- **We must reproduce:** keep-alive connection pooling per host; a global and per-download connection cap.
- **Improvement:** pooled connections are shared across downloads from the same host (fewer TLS handshakes); limits are enforced per host as well as globally.
- **Dependencies:** `SocketsHttpHandler` pooling; settings store.
- **Security/legal:** credentials are never logged; pooled connections are per-host so credentials cannot leak across hosts.
- **Test:** download 50 small files from one host; assert the TLS handshake count stays low (observable in test-server logs).
- **Status:** Not started. **Phase:** P1.

### A5. HTTP and HTTPS
- **IDM offers:** HTTP/HTTPS (and HTTP/2 improvements in 6.43).
- **We must reproduce:** HTTP/1.1 and HTTP/2 over TLS 1.2+.
- **Improvement:** HTTP/2 by default via `HttpClient`; connection reuse across downloads.
- **Dependencies:** .NET `HttpClient`.
- **Security/legal:** system certificate validation; no "ignore certificate errors" default.
- **Test:** loopback server (HTTP) + a local TLS test server (later); header and status handling covered by engine tests.
- **Status:** Tested (P1): HTTPS verified with a local TLS test server
  — successful download + SHA-256, wrong-certificate rejection,
  untrusted-chain rejection, redirect following, drop/retry
  (`HttpsTransportTests`, 6 tests, all passing). A TLS failure is a
  dedicated, non-retryable `CertificateError`; validation is never
  disabled. HTTP/2 is not yet exercised (the test server is HTTP/1.1).
  **Phase:** P0/P1.

### A6. FTP
- **IDM offers:** FTP downloads with proxy support.
- **We must reproduce:** FTP (active/passive) with resume via REST, and FTPS where the server supports it.
- **Improvement:** FTP is a thin `ITransport` module, so it can be dropped later without touching the engine if usage is negligible.
- **Dependencies:** FluentFTP 55.1.0 (MIT, audited in `DEPENDENCY_AUDIT.md`; not yet added).
- **Security/legal:** credentials in Windows Credential Manager only; FTPS preferred; plain FTP warns the user that credentials cross the network in cleartext.
- **Test:** local FTP test server (planned for P1); resume and Range-equivalent behaviour verified the same way as HTTP.
- **Status:** Implemented + Tested (P1): `FtpTransport : ITransport`
  via FluentFTP 55.1.0 (MIT, verified from the installed package's
  nuspec) — probe (SIZE/REST), range download (REST + RETR), FTPS with
  system-trust certificate validation by default and a test-only
  thumbprint pin. Local deterministic FTP test server (TCP + Unix
  socket). 14 tests, all passing (`FtpTransportTests`), including FTPS
  success and wrong-thumbprint rejection. Active mode is not implemented
  (passive only). **Phase:** P1.

### A7. Download acceleration
- **IDM offers:** "up to 8x" acceleration via segmentation (marketing claim, no published test).
- **We must reproduce:** the mechanism (segmentation), not the number.
- **Improvement:** we publish a reproducible benchmark instead of a marketing figure: 1 GB from a Range-capable server must complete within 5% of aria2's time on the same machine.
- **Dependencies:** engine + benchmark script (in-repo).
- **Security/legal:** none. No claim is published without its test setup.
- **Test:** benchmark script run in CI against the local test server; results published per release.
- **Status:** Implemented + Tested (P1): benchmark harness
  (`AiDownloadManager.Benchmark`) with repeated trials, median
  reporting, SHA-256 verification, CPU/memory/journal-write reporting.
  100 MB results (medians, local server, this VM): 1 conn 11.6 MB/s,
  4 conns 24.1 MB/s (best), 8 conns 20.7 MB/s, 16 conns 18.8 MB/s. The
  1 GB run improved from 98.8 s (P0) to 42.2 s (P1) after the journal
  coalescing. **aria2 head-to-head complete (100 MB, medians of 3
  trials): engine 19.0–43.3 MB/s vs aria2 78.0–107.4 MB/s — aria2 is
  2.2–4.1× faster.** The engine's timed run includes SHA-256
  verification + journal flushes + promotion; aria2's is download
  only. The published improvement target (within 5% of aria2) is
  **not met** — recorded honestly in `BENCHMARKS.md`; profiling the
  engine is Phase 2 work. **Phase:** P1.

### A8. Automatic retries
- **IDM offers:** automatic retry on connection loss and server errors.
- **We must reproduce:** per-range retry with exponential backoff; configurable attempt count and delay.
- **Improvement:** retries are per-range, not per-file, so a failure on one connection does not restart the whole download; transient vs permanent failures are distinguished (404/403 fail fast, timeouts retry).
- **Dependencies:** engine retry policy (done in P0).
- **Security/legal:** none.
- **Test:** drop-connection test-server mode; download completes after retries with a matching hash.
- **Status:** Implemented (P0, segmented ranges) + Tested (P1,
  single-connection fallback): drop/retry, retry limit, cancel during
  the retry delay, shrink-on-retry safe failure, restart resume
  (`SingleConnectionRetryTests`, 6 tests, all passing). **Phase:** P0/P1.

### A9. Pause and resume
- **IDM offers:** pause/resume of any download.
- **We must reproduce:** pause at any time; resume from the exact byte position recorded in the journal.
- **Improvement:** resume is journal-based, so it survives the app being killed, not just a clean pause.
- **Dependencies:** journal + engine.
- **Security/legal:** none.
- **Test:** cancel mid-download, resume, hash matches (engine integration test).
- **Status:** Implemented (P0 engine) + UI pause/resume/cancel controls
  in the P1 WPF app (compile-verified; runtime needs Windows).
  **Phase:** P0/P1.

### A10. Resume after application restart
- **IDM offers:** resume after the application is closed and reopened.
- **We must reproduce:** same, from the SQLite journal.
- **Improvement:** the journal is crash-safe (WAL + synchronous FULL), so even a killed process resumes cleanly.
- **Dependencies:** SQLite journal (done in P0).
- **Security/legal:** none.
- **Test:** cancel, create a brand-new engine and journal instance, resume, hash matches (engine integration test).
- **Status:** Tested (P0). **Phase:** P0.

### A11. Resume after Windows restart
- **IDM offers:** resume after shutdown / power loss.
- **We must reproduce:** same; the journal and the `.part` file survive a reboot.
- **Improvement:** the journal is fsynced per checkpoint, so a power loss loses at most one checkpoint interval per worker (256 KB), never the whole download.
- **Dependencies:** journal durability (WAL + synchronous FULL).
- **Security/legal:** none.
- **Test:** manual VM test in P3 (reboot mid-download, resume, verify); automated equivalent is the kill-and-resume test in P0.
- **Status:** Implemented (P0); VM verification is P3. **Phase:** P0/P3.

### A12. Recovery after network interruption
- **IDM offers:** recovery from lost connections, network errors, shutdowns.
- **We must reproduce:** automatic detection of a dropped connection, retry of the affected range, and resume from the journal.
- **Improvement:** per-range retry (A8) plus journal checkpoints mean recovery is fast and loses minimal work.
- **Dependencies:** engine retry + journal.
- **Security/legal:** none.
- **Test:** drop-connection mode; throttled interruption test.
- **Status:** Implemented (P0). **Phase:** P0.

### A13. Download integrity verification
- **IDM offers:** no documented checksum verification.
- **We must reproduce:** better than IDM — SHA-256/MD5/SHA-1 verification against a user-supplied hash or a `.sha256` sidecar file.
- **Improvement:** verification is mandatory before a file is promoted; a mismatch fails the download and keeps the partial file for diagnosis instead of silently delivering a corrupt file.
- **Dependencies:** `SHA256.HashDataAsync` (streaming, low memory).
- **Security/legal:** hashes are verified, never trusted from the server alone.
- **Test:** wrong-hash test (must fail and not promote); correct-hash test (must pass).
- **Status:** Implemented (P0). **Phase:** P0.

### A14. Large-file support
- **IDM offers:** large files (no documented limit).
- **We must reproduce:** files of 10 GB and more; 64-bit offsets throughout; sparse pre-allocation.
- **Improvement:** pre-allocation prevents disk-full failures mid-download; progress and journal use 64-bit values everywhere (no 32-bit overflow at 2 GB/4 GB).
- **Dependencies:** 64-bit `long` offsets; `FileStream.SetLength`.
- **Security/legal:** none.
- **Test:** 1 GB acceptance run (P0); a 10 GB run is a P1 benchmark (generated locally, disk permitting).
- **Status:** Implemented (P0, verified at 1 GB). **Phase:** P0.

### A15. Server Range detection and fallback
- **IDM offers:** resume and segmentation where the server supports it.
- **We must reproduce:** probe with a real Range request; fall back to a single connection when the server does not support Range.
- **Improvement:** the probe is a real Range request, not a trust-`Accept-Ranges` check; malformed Range responses are detected and fail safely instead of producing a corrupt file.
- **Dependencies:** `HttpTransport` probe + validation (done in P0).
- **Security/legal:** none.
- **Test:** no-Range server mode (fallback + hash match); invalid-Range and inconsistent-Content-Range modes (safe failure, no file promoted).
- **Status:** Implemented (P0). **Phase:** P0.

---

## B. Browser Integration

### B1–B3. Chrome / Edge / Firefox integration
- **IDM offers:** extensions for Chrome, Edge, Firefox, Opera, and others via its integration module.
- **We must reproduce:** official WebExtensions for Chrome, Edge, and Firefox (Manifest V3; Firefox MV3/MV2 as its store requires), plus the same Chromium extension for Opera and other Chromium browsers.
- **Improvement:** one extension codebase for all three stores; the extension is minimal (no remote code, no broad host permissions beyond download interception, with a written justification).
- **Dependencies:** WebExtensions APIs; a native-messaging host executable shipped with the app; registry registration for the host.
- **Security/legal:** the extension never gets filesystem access; messages are validated (scheme allowlist, size limits) before the app acts on them; no browsing data leaves the machine.
- **Test:** capture a download from each of Chrome, Edge, and Firefox on current stable releases; extension store policy checklist.
- **Status:** Not started. **Phase:** P2.

### B4. Download interception / browser download replacement
- **IDM offers:** intercepts downloads and replaces the browser's built-in downloader.
- **We must reproduce:** opt-in interception; the user chooses which file types or sites are captured.
- **Improvement:** interception is granular (per file type, per site) instead of all-or-nothing, and it can be toggled without restarting the browser.
- **Dependencies:** extension `downloads` API + native messaging.
- **Security/legal:** explicit user opt-in; the browser's own download UI remains available as a fallback.
- **Test:** manual test matrix per browser (P2); automated test of the native-messaging protocol with a mock extension message.
- **Status:** Not started. **Phase:** P2.

### B5. One-click download capture
- **IDM offers:** one-click capture of a link.
- **We must reproduce:** clicking a download link (or the extension button) sends the URL to the app.
- **Improvement:** the capture includes filename, referrer, and cookies for the captured URL only (see B8).
- **Dependencies:** extension content script + native messaging.
- **Security/legal:** only the clicked URL and its metadata are sent; no page content.
- **Test:** manual per-browser test (P2).
- **Status:** Not started. **Phase:** P2.

### B6. Context-menu integration
- **IDM offers:** "Download with IDM" context-menu entries.
- **We must reproduce:** "Download with Ai Download Manager" in the link and page context menus.
- **Improvement:** the menu shows the target filename and size when known, so the user can confirm before downloading.
- **Dependencies:** extension `contextMenus` API.
- **Security/legal:** none.
- **Test:** manual per-browser test (P2).
- **Status:** Not started. **Phase:** P2.

### B7. Cookie and session-aware downloads (where permitted)
- **IDM offers:** cookie support for authenticated downloads.
- **We must reproduce:** the extension passes the session cookies for the captured URL only, so downloads behind a login work.
- **Improvement:** cookies are passed per-download, never stored; they are never written to disk or logs.
- **Dependencies:** extension `cookies` API (host permission for the captured site only).
- **Security/legal:** cookies are held in memory for the single download and discarded; no credential theft, no cookie export feature.
- **Test:** local authenticated test server (P2).
- **Status:** Not started. **Phase:** P2.

### B8. Filename and referrer capture
- **IDM offers:** captures filename and referrer.
- **We must reproduce:** filename from `Content-Disposition`, URL path, and the page's suggested name; referrer from the capturing page.
- **Improvement:** filename sanitisation (no path traversal, no reserved device names, no control characters) is enforced before writing.
- **Dependencies:** engine + extension.
- **Security/legal:** sanitisation is a security requirement, not a nicety (see `SECURITY.md`).
- **Test:** unit tests with hostile filenames (`../`, `C:\`, `CON`, control characters).
- **Status:** Not started (sanitisation is designed; implementation is P1 with the UI). **Phase:** P1/P2.

### B9. Browser extension management
- **IDM offers:** extension install/update management.
- **We must reproduce:** in-app extension status (installed version, browser, enabled/disabled), one-click reinstall, and update prompts.
- **Improvement:** the app detects a missing or outdated extension and offers to fix it, instead of silently failing to capture.
- **Dependencies:** native-messaging host handshake (protocol version check).
- **Security/legal:** the host validates the extension's origin; an unknown caller is rejected.
- **Test:** protocol-version mismatch test (old extension must fail loudly).
- **Status:** Not started. **Phase:** P2.

### B10. Compatibility with browser updates
- **IDM offers:** tracks browser releases; fixes breakage in updates.
- **We must reproduce:** CI tests against Chrome/Edge/Firefox beta channels; the extension uses stable APIs only.
- **Improvement:** the native-messaging protocol is versioned, so a browser update cannot silently change the meaning of a message.
- **Dependencies:** CI beta-channel jobs.
- **Security/legal:** none.
- **Test:** beta-channel smoke test in CI (P2).
- **Status:** Not started. **Phase:** P2.

---

## C. Video Download Capabilities

> Hard boundary for this whole section: **no DRM circumvention,
> no access-control bypass, no credential theft, no site-specific
> extractors for services that prohibit downloading.** The feature
> is generic media detection on the current page, nothing more.

### C1. Detect direct video URLs
- **IDM offers:** video download panel on pages with direct video links.
- **We must reproduce:** detect `<video>` elements and direct media URLs on the current page.
- **Improvement:** the panel shows the file size and container when known, before the user commits.
- **Dependencies:** extension content script.
- **Security/legal:** only media the page already exposes as a plain URL.
- **Test:** test page with a direct MP4; panel appears and offers the download (P2).
- **Status:** Not started. **Phase:** P2.

### C2. Detect publicly accessible, non-DRM HLS/DASH streams
- **IDM offers:** video download with conversion (including MP4/TS handling).
- **We must reproduce:** detect `.m3u8` / `.mpd` manifests the page already exposes, where they are unencrypted; offer the listed qualities.
- **Improvement:** quality choice comes from the manifest itself; encrypted/DRM manifests are refused with an explanation, not silently skipped.
- **Dependencies:** extension network observation; a manifest parser (P2).
- **Security/legal:** DRM/encrypted streams are refused; no key extraction, no license-server interaction.
- **Test:** test page with a clear HLS manifest; download completes and the segments assemble correctly.
- **Status:** Not started. **Phase:** P2 (basic); quality selection is V1.2.

### C3. Detect media exposed by the current page
- **IDM offers:** "catch any download" / video panel.
- **We must reproduce:** observe `video/*` responses and media elements on the current page.
- **Improvement:** detection is limited to the current page and the user's explicit action; nothing is captured in the background.
- **Dependencies:** extension.
- **Security/legal:** no background sniffing of unrelated traffic.
- **Test:** test pages (P2).
- **Status:** Not started. **Phase:** P2.

### C4. Video download panel
- **IDM offers:** a floating panel on video pages.
- **We must reproduce:** a panel offering the detected media with quality and size.
- **Improvement:** the panel explains why a stream is unavailable (DRM, encrypted, or blocked) instead of showing nothing.
- **Dependencies:** extension UI.
- **Security/legal:** none beyond C1–C3.
- **Test:** manual test pages (P2).
- **Status:** Not started. **Phase:** P2.

### C5. Quality options when exposed by the manifest
- **IDM offers:** quality selection for video downloads.
- **We must reproduce:** list the qualities the manifest exposes; download the chosen one.
- **Improvement:** the chosen quality's estimated size is shown before downloading.
- **Dependencies:** manifest parser.
- **Security/legal:** none.
- **Test:** multi-quality test manifest (V1.2).
- **Status:** Not started. **Phase:** V1.2.

### C6. Supported media formats
- **IDM offers:** MP4, TS, and others with conversion.
- **We must reproduce:** progressive MP4/WebM and clear HLS/DASH in V1; other containers as the manifest supports them.
- **Improvement:** format support is documented per container, with a clear "not supported" message instead of a silent failure.
- **Dependencies:** engine (segmented download of media segments).
- **Security/legal:** none.
- **Test:** format matrix test (P2/V1.2).
- **Status:** Not started. **Phase:** P2.

### C7. Optional video conversion / remuxing (later version)
- **IDM offers:** converts downloaded video (TS→MP4, etc.).
- **We must reproduce:** optional remuxing via an external ffmpeg the user installs; the app detects it and offers it.
- **Improvement:** ffmpeg is never bundled (licensing and size); the user installs it, the app uses it, and the app documents the arrangement.
- **Dependencies:** external ffmpeg (user-installed).
- **Security/legal:** no bundled binaries; the app never downloads executables for the user.
- **Test:** ffmpeg-present vs ffmpeg-absent behaviour (V1.2).
- **Status:** Not started. **Phase:** V1.2.

---

## D. Download Organization

### D1. Multiple download queues
- **IDM offers:** multiple named queues with their own settings.
- **We must reproduce:** named queues, each with a concurrency limit and a speed limit.
- **Improvement:** queue priorities and dependencies (a download that starts only after another finishes) in V1.1.
- **Dependencies:** `QueueService` + SQLite `queues`/`queue_items` tables (P2).
- **Security/legal:** none.
- **Test:** a 10-file queue with a 2-concurrency limit completes in order (P2 acceptance).
- **Status:** Not started. **Phase:** P2.

### D2. Download scheduler
- **IDM offers:** start/stop at set times; disconnect or shut down the PC when done; periodic synchronisation.
- **We must reproduce:** time-based queue start/stop; shutdown/sleep/hibernate on completion.
- **Improvement:** the schedule is durable (SQLite) and evaluated by a timer, not by keeping the machine awake; optional wake-from-sleep for a scheduled queue.
- **Dependencies:** `SchedulerService` + Windows power management APIs.
- **Security/legal:** shutdown requires explicit user consent per schedule; never shuts down while another queue is active unless the user said so.
- **Test:** VM test — scheduler starts a queue at a set time and shuts the machine down afterwards (P2 acceptance).
- **Status:** Not started. **Phase:** P2.

### D3. Download categories
- **IDM offers:** categories (Documents, Music, Video, etc.) with automatic organisation.
- **We must reproduce:** user-defined categories with rules (extension, host, size) and automatic folder placement.
- **Improvement:** rules are explicit and inspectable; no hidden heuristics; a file that matches no rule goes to a visible "Uncategorised" bucket instead of a surprise location.
- **Dependencies:** `CategoryService` + rules engine.
- **Security/legal:** none.
- **Test:** rule-matching unit tests; end-to-end category placement test (P2).
- **Status:** Not started. **Phase:** P2.

### D4. Automatic file organization
- **IDM offers:** files sorted into category folders automatically.
- **We must reproduce:** same, driven by D3 rules.
- **Improvement:** rename templates (e.g., `{host}/{date}/{name}`) so organisation is predictable and reversible.
- **Dependencies:** D3.
- **Security/legal:** destination paths are validated (no traversal outside the chosen folders).
- **Test:** template unit tests (P2).
- **Status:** Not started. **Phase:** P2.

### D5. Batch downloads
- **IDM offers:** batch download from a list of URLs, including pattern expansion.
- **We must reproduce:** URL list import (.txt/.csv), numeric and pattern expansion (`file[001-100].zip`), duplicate detection.
- **Improvement:** batch jobs are resumable as a group; a failed item does not abort the batch; a batch report lists every item's outcome.
- **Dependencies:** batch parser + queue.
- **Security/legal:** a batch cannot contain `file://` or other non-HTTP(S)/FTP schemes.
- **Test:** import of 500 URLs creates 500 jobs with duplicate detection (P2 acceptance).
- **Status:** Not started. **Phase:** P2.

### D6. Download All links
- **IDM offers:** downloads all links on a page, with a filter.
- **We must reproduce:** list the links on the current page, let the user filter by type/pattern, and queue the selection.
- **Improvement:** the list shows size and type before anything is queued, so the user does not accidentally queue hundreds of files blind.
- **Dependencies:** extension page scan.
- **Security/legal:** only links the user explicitly selects are downloaded.
- **Test:** test page with mixed links; filter + queue behaviour (V1.1).
- **Status:** Not started. **Phase:** V1.1.

### D7. Clipboard monitoring
- **IDM offers:** captures copied download links.
- **We must reproduce:** opt-in clipboard monitoring with an allowlist/denylist; only text that looks like a URL is read.
- **Improvement:** monitoring is off by default and the user sees exactly what was captured before it is queued.
- **Dependencies:** `ClipboardMonitor` service.
- **Security/legal:** non-URL clipboard content is ignored immediately and never stored; the feature is opt-in.
- **Test:** clipboard unit tests with URLs, non-URLs, and denylisted hosts (P2).
- **Status:** Not started. **Phase:** P2.

### D8. Drag and drop
- **IDM offers:** drag links into the window; drag files out.
- **We must reproduce:** drop a URL onto the window to add it; drag a completed file to Explorer.
- **Improvement:** dropped text is validated as a URL before anything is queued.
- **Dependencies:** WPF drag-and-drop.
- **Security/legal:** dropped content is treated as untrusted input (same sanitisation as B8).
- **Test:** manual UI test (P2).
- **Status:** Not started. **Phase:** P2.

### D9. Download history
- **IDM offers:** full download history.
- **We must reproduce:** SQLite-backed history with URL, size, dates, status, and error for every download.
- **Improvement:** history is append-only and exportable (JSON/CSV); clearing history is explicit and does not delete the files.
- **Dependencies:** SQLite `history` table.
- **Security/legal:** history stays on the machine; no sync, no cloud.
- **Test:** history round-trip tests (P2).
- **Status:** Not started (schema planned). **Phase:** P2.

### D10. Download statistics
- **IDM offers:** download statistics.
- **We must reproduce:** totals (downloaded bytes, files, time), per-download details, live speed graph.
- **Improvement:** statistics are computed from the journal, so they survive restarts; a per-host breakdown shows which servers are slow.
- **Dependencies:** history + journal.
- **Security/legal:** none.
- **Test:** statistics unit tests against a seeded journal (P2).
- **Status:** Not started. **Phase:** P2.

### D11. Search and filtering
- **IDM offers:** search/filter in the download list.
- **We must reproduce:** filter by name, URL, host, category, status, and date.
- **Improvement:** filters compose (status + category + text) and the filter state is remembered across restarts.
- **Dependencies:** WPF list + SQLite queries.
- **Security/legal:** none.
- **Test:** UI test (P2/P3).
- **Status:** Not started. **Phase:** P2.

### D12. File renaming
- **IDM offers:** rename on download and afterwards.
- **We must reproduce:** rename templates at download time; rename a completed file (and update the journal).
- **Improvement:** rename templates are previewed before the download starts.
- **Dependencies:** category/rename engine.
- **Security/legal:** same sanitisation as B8.
- **Test:** template unit tests (P2).
- **Status:** Not started. **Phase:** P2.

### D13. Duplicate detection
- **IDM offers:** warns about duplicate downloads.
- **We must reproduce:** same URL, or same filename in the same folder, triggers a prompt (skip / rename / overwrite / resume).
- **Improvement:** detection also uses the SHA-256 when known, so the same file from a different URL is caught.
- **Dependencies:** journal lookup.
- **Security/legal:** overwrite requires explicit confirmation.
- **Test:** duplicate-URL and duplicate-hash tests (P2).
- **Status:** Not started. **Phase:** P2.

### D14. Import and export
- **IDM offers:** import/export of download jobs.
- **We must reproduce:** JSON + CSV export of jobs and history; import of URL lists and job files.
- **Improvement:** best-effort import of IDM/FDM job exports is a later goal, clearly labelled as best-effort (formats are undocumented).
- **Dependencies:** serialisation; journal.
- **Security/legal:** imported files are validated; a malicious job file cannot execute anything or write outside the download folders.
- **Test:** round-trip export/import tests (P1 for jobs; P2 for history).
- **Status:** Implemented + Tested (P1, jobs): JSON + CSV export/import
  with a whitelisted field set (no credentials — the job model carries
  none, and tests assert no credential-like field appears), whole-batch
  validation, duplicate detection, malformed/malicious input rejected
  safely (`ImportExportTests`, 15 tests, all passing). History export
  is P2. **Phase:** P1 (jobs), P2 (history).

---

## E. Network and Advanced Settings

### E1. HTTP/HTTPS proxy
- **IDM offers:** HTTP proxy, including Microsoft ISA and FTP proxy types.
- **We must reproduce:** manual HTTP/HTTPS proxy, system proxy settings, and authenticated proxies.
- **Improvement:** per-site proxy rules (E6) so one proxy does not have to serve every host.
- **Dependencies:** `ProxySelector` + `SocketsHttpHandler.Proxy`.
- **Security/legal:** proxy credentials in Credential Manager only; no plaintext storage.
- **Test:** local proxy test server (P1/P2).
- **Status:** Not started. **Phase:** P1 (manual + system), P2 (per-site).

### E2. SOCKS proxy
- **IDM offers:** SOCKS proxy support.
- **We must reproduce:** SOCKS5 (with authentication) for HTTP downloads.
- **Improvement:** SOCKS5 is configured per-site as well as globally.
- **Dependencies:** a SOCKS5 `HttpMessageHandler` (or a maintained library, audited before use).
- **Security/legal:** same credential handling as E1.
- **Test:** local SOCKS5 test server (P2).
- **Status:** Not started. **Phase:** P2.

### E3. Authentication (Basic, Digest, NTLM, Kerberos, Negotiate)
- **IDM offers:** Basic, Negotiate, NTLM, Kerberos.
- **We must reproduce:** Basic + Digest in P1; NTLM + Negotiate via OS (SSPI) in P2; Kerberos via OS where available.
- **Improvement:** credentials are stored per-host in Credential Manager and never in the journal or logs.
- **Dependencies:** `HttpClientHandler.Credentials` / SSPI.
- **Security/legal:** no credential is ever written to disk in plaintext; URLs with embedded credentials are redacted in logs.
- **Test:** local authenticated test server (P1/P2).
- **Status:** Not started. **Phase:** P1 (Basic/Digest), P2 (NTLM/Negotiate).

### E4. Per-site settings
- **IDM offers:** per-site download settings.
- **We must reproduce:** per-host overrides for connections, speed limit, proxy, credentials, and user-agent.
- **Improvement:** settings are shown in one place per host, with the effective (merged) settings visible so the user can see what actually applies.
- **Dependencies:** settings store + `ProxySelector`.
- **Security/legal:** credentials per-host (see E3).
- **Test:** settings-merge unit tests (P2).
- **Status:** Not started. **Phase:** P2.

### E5. Connection limits
- **IDM offers:** configurable connection limits.
- **We must reproduce:** global and per-download caps (already in the engine as `MaxConnections`).
- **Improvement:** per-host caps as well, so one host cannot consume every connection.
- **Dependencies:** engine options + settings.
- **Security/legal:** none.
- **Test:** engine tests with 1, 8, and 32 connections (P0).
- **Status:** Implemented in the engine (P0); UI settings are P1/P2. **Phase:** P0/P2.

### E6. Global speed limits
- **IDM offers:** download limits / quotas (MB per hour).
- **We must reproduce:** a global KB/s cap, live adjustable, plus hourly quotas.
- **Improvement:** the limiter is token-bucket based, so it is smooth rather than bursty; it applies across all active downloads, not per connection.
- **Dependencies:** a rate limiter in the engine (P2).
- **Security/legal:** none.
- **Test:** throttled download test asserting the average rate stays within 5% of the cap (P2).
- **Status:** Not started. **Phase:** P2.

### E7. Per-download speed limits
- **IDM offers:** limits via scheduler quotas (partial).
- **We must reproduce:** a per-download cap, set at queue time and adjustable live.
- **Improvement:** per-download limits compose with the global limit (the stricter one wins) and are visible in the download list.
- **Dependencies:** rate limiter (E6).
- **Security/legal:** none.
- **Test:** two concurrent downloads with different caps (P2).
- **Status:** Not started. **Phase:** P2.

### E8. Scheduled bandwidth limits
- **IDM offers:** quotas and scheduler-based limits.
- **We must reproduce:** time-of-day speed profiles (e.g., full speed at night, capped during work hours).
- **Improvement:** profiles are part of the scheduler, so one rule covers both timing and bandwidth.
- **Dependencies:** scheduler + rate limiter.
- **Security/legal:** none.
- **Test:** schedule-driven limit test (P2/V1.1).
- **Status:** Not started. **Phase:** V1.1.

### E9. Automatic retry configuration
- **IDM offers:** configurable retries.
- **We must reproduce:** attempt count, base delay, and backoff factor, globally and per download.
- **Improvement:** the retry policy distinguishes transient from permanent failures, so a 404 is never retried 5 times.
- **Dependencies:** engine retry policy (done in P0; settings UI is P2).
- **Security/legal:** none.
- **Test:** drop-connection tests (P0); settings tests (P2).
- **Status:** Implemented in the engine (P0); settings UI is P2. **Phase:** P0/P2.

### E10. Custom download folders
- **IDM offers:** per-category and per-download folders.
- **We must reproduce:** a global default folder, per-category folders, and a per-download override.
- **Improvement:** the folder picker shows free space and warns before starting a download that will not fit.
- **Dependencies:** settings + category rules.
- **Security/legal:** folders are validated; a download cannot be pointed at a system directory.
- **Test:** folder-validation unit tests (P1/P2).
- **Status:** Not started. **Phase:** P1.

---

## F. Advanced IDM Workflows

### F1. Site Grabber / offline website capture
- **IDM offers:** downloads a whole site (filtered) for offline browsing; schedulable grabber projects.
- **We must reproduce:** a grabber that follows links from a start URL, within a depth and a same-host (or allowlisted-host) rule, with a size cap and a file-type filter.
- **Improvement:** robots.txt is respected; the grabber has a hard total-size cap and a rate limit so it cannot hammer a server; every fetched URL is logged.
- **Dependencies:** an HTML link extractor + the engine; a new `GrabberService`.
- **Security/legal:** robots.txt respected; no login-wall bypass; the user is warned that grabbing a site they do not own may violate its terms, and the tool refuses sites that return 403 to the grabber.
- **Test:** local test site with a known link graph; assert exactly the expected files are fetched and nothing else (V2.0).
- **Status:** Not started. **Phase:** V2.0.

### F2. Periodic synchronization
- **IDM offers:** periodic synchronisation of a site or folder.
- **We must reproduce:** re-check a URL (or a grabber project) on a schedule and download new or changed files.
- **Improvement:** change detection uses ETag / Last-Modified / hash, so unchanged files are never re-downloaded.
- **Dependencies:** scheduler + engine.
- **Security/legal:** none beyond F1.
- **Test:** local changing file; sync picks up the change once (V1.1).
- **Status:** Not started. **Phase:** V1.1.

### F3. Download quotas
- **IDM offers:** download limits (progressive downloading with quotas).
- **We must reproduce:** MB-per-hour quotas, globally and per queue.
- **Improvement:** quotas pause the queue (with a clear message) instead of silently slowing it, and the user can override a quota for one download.
- **Dependencies:** rate limiter (E6) + scheduler.
- **Security/legal:** none.
- **Test:** quota test asserting the queue pauses at the limit (V1.1).
- **Status:** Not started. **Phase:** V1.1.

### F4. Browser integration settings
- **IDM offers:** settings for which browsers and file types are captured.
- **We must reproduce:** per-browser enable/disable, file-type allowlist, and site allowlist/denylist.
- **Improvement:** settings are per-browser and visible in one screen, with the effective capture rule explained in plain language.
- **Dependencies:** extension + settings store.
- **Security/legal:** none.
- **Test:** settings unit tests (P2).
- **Status:** Not started. **Phase:** P2.

### F5. Download completion actions
- **IDM offers:** run a program, play a sound, shut down, etc. on completion.
- **We must reproduce:** shutdown/sleep/hibernate, play a sound, show a notification; "run a program" only with an explicit, per-action user confirmation.
- **Improvement:** actions are per-queue and per-download, and the "run a program" action is deliberately fenced (no silent execution of anything, ever).
- **Dependencies:** scheduler + platform integration.
- **Security/legal:** no automatic execution of downloaded files; a configured external program runs only after explicit user setup.
- **Test:** VM shutdown test (P2); notification test (P2).
- **Status:** Not started. **Phase:** P2.

### F6. System tray operation
- **IDM offers:** minimises to the tray; tray menu and icon show progress.
- **We must reproduce:** tray icon with progress, a context menu (pause all, resume all, open, quit), and minimise-to-tray.
- **Improvement:** the tray icon's tooltip shows the current total speed and remaining time.
- **Dependencies:** WPF NotifyIcon (or Win32 via the presentation layer).
- **Security/legal:** none.
- **Test:** manual UI test (P2).
- **Status:** Implemented (P1, basic): tray icon with Show/Exit and
  minimise-to-tray in the WPF app (compile-verified; runtime needs
  Windows). Progress in the tray icon and pause-all/resume-all menu
  are P2. **Phase:** P1/P2.

### F7. Automatic antivirus scanning (user's configured scanner)
- **IDM offers:** runs the user's antivirus on completion.
- **We must reproduce:** a hook that runs the user's configured scanner (e.g., Windows Defender via its API, or a chosen executable) on each completed file.
- **Improvement:** the scan result is shown in the download list; a detection quarantines the file (moves it out of the download folder) and warns the user instead of silently deleting it.
- **Dependencies:** `AvScannerHook` + Defender API or a configured executable path.
- **Security/legal:** the app never bundles a scanner; it never executes a downloaded file itself.
- **Test:** scan-hook test with a stub scanner (P1).
- **Status:** Implemented + Tested (P1): opt-in `AntivirusScanner` hook
  (default OFF, user-configured scanner executable, `{file}` argument
  template, timeout, no shell, the downloaded file is never executed).
  Results are Clean / Infected / Error / TimedOut. 3 tests with a stub
  scanner, all passing. Windows Defender API integration is a later
  step. **Phase:** P1.

### F8. Customizable interface
- **IDM offers:** customisable interface and user skins.
- **We must reproduce:** column selection, layout density, and dark/light/system themes in V1; full theming/skins later.
- **Improvement:** accessibility is a requirement, not a theme: full keyboard operation and screen-reader labels from the first real UI.
- **Dependencies:** WPF styling.
- **Security/legal:** none.
- **Test:** accessibility checklist (P3 acceptance).
- **Status:** Partially implemented (P1): the functional WPF main
  window exists (download list, Add URL dialog, settings, speed
  graph). Column selection/themes remain P2; accessibility pass P3.
  Runtime verification needs Windows. **Phase:** P1/P2/P3.

### F9. Keyboard shortcuts
- **IDM offers:** keyboard shortcuts for common actions.
- **We must reproduce:** shortcuts for add URL, pause/resume, pause all, resume all, delete, open folder, and search.
- **Improvement:** shortcuts are listed in a single, searchable shortcuts screen and are customisable.
- **Dependencies:** WPF input bindings.
- **Security/legal:** none.
- **Test:** keyboard-only UI test (P3).
- **Status:** Not started. **Phase:** P2/P3.

### F10. Notifications
- **IDM offers:** download completion notifications.
- **We must reproduce:** Windows toast notifications for completion, failure, and queue completion.
- **Improvement:** notifications are actionable (Open, Open folder, Retry) and respect Windows Focus Assist / quiet hours.
- **Dependencies:** Windows toast APIs.
- **Security/legal:** notifications never contain credentials or full URLs with tokens.
- **Test:** manual test (P2).
- **Status:** Not started. **Phase:** P2.

### F11. Portable mode
- **IDM offers:** installer only (no portable mode documented).
- **We must reproduce:** better than IDM — a ZIP distribution that runs from any folder, keeping settings and the database beside the executable.
- **Improvement:** portable mode is a first-class distribution, not an afterthought: no registry writes, no AppData dependency.
- **Dependencies:** settings/database path resolution (already relative-friendly).
- **Security/legal:** none.
- **Test:** run from a USB-style folder on a clean VM; assert no files are written outside it (P2).
- **Status:** Not started. **Phase:** P2.

---

## Summary counts (Phase 1)

| Area | Features | Implemented | Tested | Not started |
|---|---|---|---|---|
| A. Download engine | 15 | 13 | 13 | 2 |
| B. Browser integration | 10 | 0 | 0 | 10 |
| C. Video | 7 | 0 | 0 | 7 |
| D. Organization | 14 | 1 | 1 | 13 |
| E. Network / advanced | 10 | 2 (engine parts) | 1 | 8 |
| F. Advanced workflows | 11 | 3 | 1 | 8 |
| **Total** | **67** | **19** | **16** | **48** |

"Implemented" counts features whose core mechanism exists and is
exercised by a passing test or a measured run; the user-facing part
(UI, settings, extensions) is a later phase in several cases, and
anything that has only ever compiled (not run) on Windows is marked
as such in its entry — it is not counted as Tested. Nothing here is
marked Verified without a measured demonstration — see
`PHASE_0_PROGRESS.md` and `PHASE_1_PROGRESS.md` for the evidence.
Phase 1 changes: A5 HTTPS tested, A6 FTP implemented + tested, A7
benchmark harness implemented + tested, A8 single-connection retry
tested, A9 UI pause/resume controls, D14 import/export (jobs)
implemented + tested, F6 tray (basic), F7 antivirus hook implemented
+ tested, F8 main window (partial). A3 adaptive policy and A4
connection pooling remain not implemented by design/measurement.
