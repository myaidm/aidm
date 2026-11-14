# Benchmarks — Ai Download Manager

**Status:** Phase 1 — COMPLETE. Both the 10 GB validation and the
aria2 head-to-head comparison are done; results are in the sections
below. Nothing in this file is still "in progress".
**Date:** 2026-11-13

All benchmarks run against the local deterministic test server
(`DeterministicTestServer`) serving a locally generated file
(seeded xorshift, seed 42). Nothing touches the public internet.
Every result is verified by SHA-256. These are measurements on a
specific machine, not product claims.

## Environment

- Linux VM (Ubuntu 24.04), 4 vCPU (1.5 shared, heavily throttled),
  7.7 GB RAM, main disk (not `/tmp`, which is a 512 MB tmpfs).
- .NET 10 SDK 10.0.404.
- Test server: Unix domain socket for the engine (this sandbox
  blocks .NET TCP); TCP loopback for aria2 (a native binary, so
  loopback TCP works for it).
- Timings vary run to run on this shared VM (the first trial is
  usually slowest — JIT warmup and cold page cache). Medians of 3
  trials are reported; individual trial times are in the JSON
  reports.

## 100 MB — engine, connection counts (3 trials each)

Report: `/home/hatch/tmp/aidm-bench/bench-100mb.json`

| Connections | Median duration | Median throughput | SHA-256 |
|---|---|---|---|
| 1 | 8.62 s | 11.6 MB/s | match |
| 4 | 4.15 s | 24.1 MB/s | match |
| 8 | 4.82 s | 20.7 MB/s | match |
| 16 | 5.33 s | 18.8 MB/s | match |

**Reading:** 4 connections is fastest on this local server; 8 and
16 are slower, and 1 is slowest. More connections are NOT always
faster — the single-threaded test server and the throttled VM
become the bottleneck, not the engine. The engine's default of 8
connections is a reasonable middle ground; no adaptive-policy
change was justified by this measurement (see
`PHASE_1_PROGRESS.md`). Journal writes per 100 MB run: 22–25
(Phase 0 would have done ~400 at 256 KB granularity).

Individual trials (100 MB):
- 1 conn: 10.79 s, 8.62 s, 4.15 s
- 4 conns: 4.72 s, 4.15 s, 3.87 s
- 8 conns: 4.82 s, 4.58 s, 5.33 s
- 16 conns: 5.60 s, 4.41 s, 5.33 s

Peak RSS during the benchmark process (harness + server +
engine): 66–96 MB. Engine CPU per 100 MB run: 1.4–3.5 s.

## 1 GB — before / after the SQLite checkpoint optimization

Same file, same machine, same 8 connections, SHA-256
`2d31d769225afc2e585d29ef245a29fc8cef60f1a71d12e57171b77cd167fc45`
on both sides.

| Run | Duration | Effective throughput | Journal writes |
|---|---|---|---|
| Phase 0 (256 KB flushes) | 98.8 s | 10.4 MB/s | ~4,096 |
| Phase 1 (4 MB coalesced flushes) | 42.2 s | 24.2 MB/s | ~256 |

**Reading:** the journal coalescing made the 1 GB run **2.3×
faster** (98.8 s → 42.2 s). The 24.2 MB/s figure is end-to-end
(download + SHA-256 verification + promotion); the pure transfer
rate during the run averaged ~47 MB/s. Durability was not
weakened: every flush is still a committed transaction with
synchronous FULL and WAL; only the flush frequency changed.
Maximum loss on a hard crash is 4 MB of completed-but-unflushed
progress (plus one in-flight chunk per worker); a graceful cancel
loses at most one 64 KB chunk per worker.

## aria2 comparison (100 MB, medians of 3 trials)

aria2 1.37.0 (`/usr/bin/aria2c`, Ubuntu package). The harness runs
it against a TCP-loopback instance of the same test server serving
the identical file (`--aria2 /usr/bin/aria2c`). Report:
`/home/hatch/tmp/aidm-bench/bench-100mb-aria2.json`. All SHA-256
checks pass on both sides.

| Connections | Engine (median) | aria2 (median) | aria2 ÷ engine |
|---|---|---|---|
| 1 | 5.27 s / 19.0 MB/s | 1.28 s / 78.0 MB/s | 4.1× |
| 4 | 2.98 s / 33.6 MB/s | 0.93 s / 107.4 MB/s | 3.2× |
| 8 | 2.31 s / 43.3 MB/s | 1.02 s / 97.6 MB/s | 2.2× |
| 16 | 2.34 s / 42.7 MB/s | 1.08 s / 92.3 MB/s | 2.2× |

**Reading — stated plainly: aria2 is 2.2–4.1× faster than the
engine on this local server.** This is a measurement, not a
verdict on the product, and it is not hidden. Caveats that matter:

- The engine's timed run **includes** SHA-256 verification of the
  downloaded file, SQLite journal flushes, and file promotion;
  aria2's timed run is the download only (its hash is verified
  after the timer stops). Hashing 100 MB costs ~0.2–0.3 s, so it
  explains only a small part of the gap.
- aria2 is a mature native C++ binary with a lean event loop; the
  engine is .NET over HttpClient with per-chunk progress and
  journal bookkeeping. The engine also runs in the same process as
  the harness and the Unix-socket server, while aria2 is a separate
  native process on TCP loopback.
- The engine's throughput varies a lot run to run on this throttled
  VM (1 connection measured 11.6, 19.0, and 34.8 MB/s medians in
  three separate runs); aria2 is steadier. Medians are the right
  comparison, and the gap is consistent in every run.
- The engine's absolute throughput (19–43 MB/s on a local server)
  is usable, and its durability (journal + resume + hash) is the
  feature aria2 does not provide in this comparison. But raw
  throughput is a real gap: Phase 2 should profile the engine
  (buffer sizes, per-chunk work, async file I/O) and re-measure
  before claiming parity.

Peak RSS: engine 64–95 MB (harness + server + engine in one
process); aria2 18–32 MB (child process only).

## Apples-to-apples comparison (`--timing split`)

The numbers above compare the engine's **end-to-end** time
(transfer + SHA-256 verification + journal flushes + promotion)
against aria2's **download-only** time (its hash was checked after
the timer stopped). That is not a fair boundary. The harness now
has a `--timing split` mode that reports both boundaries for both
tools, so the comparison is apples-to-apples. The legacy numbers
above are kept unchanged; this section adds the split numbers
alongside them.

**How the boundaries are measured:** engine download-only is the
first progress report where every byte has arrived (verification,
journal flushes, and promotion happen after it, inside
`DownloadAsync`); engine end-to-end is the full `DownloadAsync`
wall time. aria2 download-only is the child-process wall time;
aria2 end-to-end is that plus a timed SHA-256 verification of the
downloaded file. Report: `/home/hatch/tmp/aidm-bench/bench-100mb-fair.json`
(100 MB, medians of 3 trials, same local server, same file).

**Note on variance:** this run happened on a fresh VM after a
deployment, and the machine was faster than in the earlier run
(the engine's end-to-end went from 19.0–43.3 MB/s to 53.0–67.4
MB/s). Timings on this shared VM vary a lot between runs; medians
of 3 trials are the right comparison within a run, and runs are
not directly comparable to each other.

| Connections | Engine download-only | aria2 download-only | aria2 ÷ engine |
|---|---|---|---|
| 1 | 1.44 s / 69.4 MB/s | 1.08 s / 92.6 MB/s | 1.33× |
| 4 | 1.26 s / 79.3 MB/s | 0.92 s / 108.5 MB/s | 1.37× |
| 8 | 1.27 s / 78.4 MB/s | 1.11 s / 89.7 MB/s | 1.14× |
| 16 | 1.04 s / 95.8 MB/s | 1.07 s / 93.3 MB/s | 0.97× (engine faster) |

| Connections | Engine end-to-end | aria2 end-to-end | aria2 ÷ engine |
|---|---|---|---|
| 1 | 1.89 s / 53.0 MB/s | 1.32 s / 75.5 MB/s | 1.42× |
| 4 | 1.68 s / 59.4 MB/s | 1.13 s / 88.6 MB/s | 1.49× |
| 8 | 1.70 s / 58.8 MB/s | 1.35 s / 74.1 MB/s | 1.26× |
| 16 | 1.48 s / 67.4 MB/s | 1.28 s / 78.4 MB/s | 1.16× |

**Reading:** the transfer itself is near parity (0.97–1.37×; at
16 connections the engine's transfer is slightly faster than
aria2's). The end-to-end gap (1.16–1.49×) is dominated by the
post-transfer work: SHA-256 verification + journal flushes +
promotion cost the engine ~0.2–0.45 s on 100 MB (~25% of its
end-to-end time), while aria2's verification costs ~0.2 s. That is
a much more actionable diagnosis than the headline gap: Phase 2
profiling should start with the verification/promotion path, not
the transfer loop. The "within 5% of aria2" target is still **not
met** on end-to-end (16–49% slower) — and it is not claimed here.
The earlier 2.2–4.1× gap and this run's 1.16–1.49× gap are both
real measurements on different VM states; the split mode is what
lets the two be reconciled instead of argued about.

## 10 GB validation

**Status: PASS.** 10,240 MB, 8 connections, `--interrupt-at 30`.

- Source generated locally: 10,240 MB, SHA-256
  `a6d987e2cbb91fbbe575d3ab331654e65e76f96b2346d267c1788c87ea5ccf0c`
  (generation took 317.7 s).
- Interrupted run: cancelled at 30%, **3,221,888,384 bytes
  confirmed written** before the cancel (exactly 30.0% of
  10,737,418,240 bytes).
- Resume run: success, **439.6 s, 23.3 MB/s**, SHA-256
  `a6d987e2cbb91fbbe575d3ab331654e65e76f96b2346d267c1788c87ea5ccf0c`
  — **matches the source**. The downloaded file was promoted.
- Work dir: `/home/hatch/tmp/aidm-10gb`. Exit code 0.

**Scope note:** the Prototype resumes in-process (a new job GUID
per process run), so this run proves interrupt/resume + hash at
10 GB scale in one process. Cross-process restart resume is proven
by the unit tests at 10–20 MB (`JournalSurvivesNewEngineInstance`,
no-Range restart test), not at 10 GB.
