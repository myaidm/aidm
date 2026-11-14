# Roadmap — Ai Download Manager

**Status:** Phase 1 complete (2026-11-13); Phase 2 planning in
`PHASE_2_PLAN.md`
**Rule:** the V1 scope is frozen. A feature is only added to a
phase when it has an owner and acceptance criteria. Nothing in
`IDM_FEATURE_PARITY_MATRIX.md` is permanently deferred without a
documented technical reason.

## Phase 0 — Foundations (complete)

**Goal:** a clean, modular repository; a segmented download engine
proven against a deterministic local test server; the full
IDM-parity roadmap documented.

- Solution and layered projects (Core, Engine, Protocols,
  Infrastructure, Application, Presentation, TestServer,
  Prototype, Tests).
- SQLite journal with versioned migrations.
- Segmented engine: dynamic segmentation, resume, retry, hash
  verification, Range fallback.
- Deterministic loopback HTTP test server (Range, throttle,
  drops, malformed responses).
- Automated tests (unit + integration).
- Documentation: master plan, architecture, parity matrix,
  dependency audit, testing strategy, security policy, progress.

**Acceptance criteria (all must be demonstrated, not claimed):**
1. Clean build of every non-Windows project on Linux; the WPF
   project builds on Windows (documented limitation).
2. Automated tests pass (see `PHASE_0_PROGRESS.md` for the run).
3. Local test server works (loopback only).
4. Prototype downloads a 1 GB file with multiple connections.
5. Reconstructed file matches the source SHA-256.
6. Range fallback works (no-Range server mode).
7. Interruption and retry are tested (cancel/resume + drop mode).
8. No network access outside the local test environment.
9. No paid services or external APIs.
10. Architecture and project structure documented.
11. IDM feature-parity roadmap documented.
12. Implemented functionality is clearly separated from planned
    functionality.

## Phase 1 — MVP / Alpha (8–10 weeks; executed scope complete 2026-11-13 — see `PHASE_1_PROGRESS.md`)

- FTP transport (FluentFTP, already audited).
- WPF shell: download list, add-URL dialog, categories (basic),
  history view, tray icon, speed graph.
- Engine: adaptive connection allocation (A3), benchmark harness
  (aria2 comparison), 10 GB large-file run.
- AV scan hook (user's scanner).
- Import/export of jobs (JSON/CSV).
- Error-message review: every error has a concrete next action.
- Installer (Inno Setup) + portable ZIP. **Not done in Phase 1 —
  deferred to Phase 2** (no Windows machine was available to build
  or test an installer; the rest of the Phase 1 scope is complete).

**Acceptance:** 1 GB download and verify; kill-and-resume with
zero re-download beyond one checkpoint interval per worker;
same after a VM reboot; 100 consecutive small downloads with zero
losses; idle RAM ≤ 80 MB; UI never blocks > 100 ms on the main
thread during a download.

## Phase 2 — Beta (8–10 weeks)

- Browser extensions (Chrome, Edge, Firefox) + native-messaging
  host; clipboard monitoring; one-click capture; context menu.
- Queues (multiple, with concurrency limits); scheduler
  (time-based, shutdown on completion); speed limits (global +
  per-download); batch import; category rules; basic video
  detection (direct media + clear HLS/DASH).
- Proxy (manual, system, per-site), SOCKS5, auth (Basic/Digest/
  NTLM/Negotiate), per-site settings.
- Download All (page link scan) — or V1.1 if the extension work
  overruns; the decision is recorded, not silently slipped.
- WinGet manifest; portable mode verified on a clean VM.

**Acceptance:** extension captures from each of Chrome, Edge, and
Firefox on current stable releases; a 10-file queue with a
2-concurrency limit completes in order; scheduler starts a queue
and shuts the machine down (VM test); batch import of 500 URLs
with duplicate detection; video panel appears on a test page with
a direct MP4; no extension permission beyond what the feature
needs.

## Phase 3 — V1.0 (6–8 weeks)

- Polish: dark/light/system themes, accessibility pass (keyboard
  navigation, screen-reader labels), i18n framework, documentation,
  release signing, public launch (GitHub release + WinGet).
- Full manual test pass on Windows 10 and 11 (clean VMs).
- Upgrade from Beta preserves jobs and history.

**Acceptance:** installer installs and uninstalls cleanly (no
files or registry keys left except user data, with an option to
remove that too); accessibility checklist passes; signed release
with published SHA-256 hashes; zero known crashers in the tracker.

## V1.1 — Quality of life

Download All links on a page; periodic synchronisation;
per-queue speed limits and time-based speed schedules;
auto-fetch of checksum sidecars; "refresh expired link" from the
originating page; download quotas.

## V1.2 — Video

Quality selection and playlist handling for clear HLS/DASH;
optional external ffmpeg remux (never bundled).

## V2.0 — Site grabber

Filtered site download for offline browsing, with robots.txt
respect, depth and rate limits, and a hard cap on total size;
schedulable grabber projects.

## Future evaluation (not committed)

- HTTP/3 (when .NET support is stable); Metalink; a plugin API
  (sandboxed, reviewed, opt-in per plugin); a headless/CLI mode
  using the same engine; PAC file proxy support.
- Torrent support via a separate, optional module — only if users
  demand it (FDM already does this well).
- macOS/Linux ports — requires a UI-framework decision; not
  promised.

## Deliberately never planned

DRM circumvention, ad injection, bundled offers, paid tiers,
accounts, cloud storage, or any "download from service X" feature
that exists to defeat that service's terms.
