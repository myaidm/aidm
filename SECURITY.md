# Security Policy — Ai Download Manager

**Status:** Phase 1
**Date:** 2026-11-13

## Scope

This policy covers the application, its browser extensions, its
installer, and this repository. It is a living document: every
phase revisits it before release.

## Security requirements (enforced in code)

1. **TLS 1.2+ only.** System certificate validation. No custom
   CA, no certificate-pinning bypass, and no "ignore certificate
   errors" option except an explicit, per-download, warned
   override that is never the default.
2. **Downloaded files are never executed by the app.** "Open"
   hands the file to the shell only after explicit user action;
   executables show a confirmation dialog first.
3. **Filename sanitisation.** No path traversal (`../`, absolute
   paths, reserved device names such as `CON`/`NUL`), no control
   characters, and length limits enforced before writing.
4. **Credentials live in Windows Credential Manager / DPAPI
   only.** Never in SQLite, never in logs, never in crash dumps.
5. **Logs never contain URLs with credentials, cookies, or
   authorization headers.** URLs are redacted in logs by default.
6. **The browser extension runs with least privilege.** No
   filesystem access; no access to page content beyond media
   detection; every message is validated (scheme allowlist, size
   limits) before the app acts on it.
7. **No auto-run of downloaded installers.** No silent execution
   of anything, ever.
8. **A file is only promoted to its destination after its length
   and (when supplied) its SHA-256 have been verified.** A
   mismatch fails the download; the partial file is kept for
   diagnosis, not delivered.
9. **Range responses are validated.** A server that lies about
   Range support is detected (see `ARCHITECTURE.md`); a corrupt
   file is never promoted.
10. **No remote code.** The extension contains no remote code;
    updates come only through the browser stores and GitHub
    Releases.

## Privacy requirements (enforced in code)

1. **No account, no registration, no device ID, no telemetry by
   default.** There is no telemetry code in Phase 0 at all.
2. **No browsing history is sent anywhere.** The extension sees
   only the downloads the user asks it to capture.
3. **Update check (when added) is opt-in and fetches a static
   JSON file from GitHub Releases.** It sends no identifier.
4. **Clipboard monitoring is opt-in** and reads only text that
   looks like a URL; everything else is ignored immediately.
5. **Crash reports, if ever added, are opt-in, local-first**
   (written to disk for the user to inspect), and never
   auto-uploaded.
6. **Network audit:** a release build is run under a packet
   capture during a test session; with the update check off, the
   capture must show no non-download traffic.

## Video and legal boundaries

- No DRM circumvention, no bypassing of access controls, no
  downloading from services whose terms prohibit it when the app
  can detect that, no site-specific extractors designed to defeat
  a service's restrictions, and no credential theft.
- Video detection is generic: direct media URLs and clear
  (unencrypted) HLS/DASH manifests the page already exposes.
  Encrypted/DRM streams are refused with an explanation.
- These are product requirements, not suggestions. A feature
  request that crosses them is declined and the reason is
  recorded.

## Dependency security

- Every dependency is audited in `DEPENDENCY_AUDIT.md` before
  use: maintenance status, license compatibility with GPL-3.0,
  and necessity.
- `dotnet list package --vulnerable` and the CI dependency scan
  are the standing checks for known vulnerabilities.
- No dependency may phone home, require an account, or impose a
  usage limit at runtime.

## Reporting a vulnerability

- **Do not open a public issue for a security vulnerability.**
- Contact the maintainers through the address listed in the
  repository's `SECURITY.md` contact section (to be added at the
  first public release; during pre-release development, report
  directly to the project owner).
- We follow a 90-day disclosure policy: we will fix the issue
  and publish a release before public disclosure, and we will
  credit the reporter unless they ask not to be.
- Please include: the version, a description, steps to
  reproduce, and the impact you believe it has. A proof of
  concept is welcome but not required.

## Known limitations (Phase 0)

- The WPF presentation project cannot be built or tested on
  Linux; it is built and tested on Windows. This is a platform
  limitation, not a security gap, but it means the UI has had no
  automated testing yet.
- Single-connection (non-Range) downloads do not yet retry on
  transient failure; the segmented path does. This is a
  reliability gap, not a security gap, and it is tracked in
  `PHASE_0_PROGRESS.md`.
- The update check does not exist yet, so there is nothing to
  audit there; the opt-in requirement above binds its future
  implementation.
