# Library scan performance: issue #60

The five-second cold-library target is **not met**. This branch reduces unnecessary
decoding and allocations while preserving the complete library projection. Keep
[#60](https://github.com/kc2-io/BotOrNot/issues/60) open.

## Scope and release behavior

Library scans use an explicit summary reader; opening a match still uses Normal.
The summary reader excludes three detail-only groups and enables reader-owned
reusable buffers, transient bunch reuse, and bounded integer fast paths only for
validated release families. Unknown releases use Normal decoding from the start.
Every replay gets a new reader: readers must not be pooled, because the upstream
branch parser can retain its prior release identity across successive reads.

The parser also caches the resolved recorder player instead of repeatedly walking
the entire lobby. Missing mappings preserve the previous owner; channel replacement
changes the owner; Build retains a final full reconciliation. Valid-input full-match
semantics are preserved. Malformed property lengths now fail before copying/skipping
beyond the framed payload.

The shipping worker default remains two, with a shared four-decoder ceiling across
summary and full loads. No input limit, parser completeness rule, or cancellation
lease lifetime is relaxed to improve timings. The cache analysis revision changes
to invalidate older projections.

## Corpus and measurements

Private corpus: 79 files, 1,156,434,527 bytes. Manifest SHA-256:
`2EA97D36D5F68603B3DDF99389740FA4D8584DCAA7A9CEECC8CBF11E7E3D9672`.
All 79 complete summaries match the frozen Normal oracle:
`038891A5CB972631CFEC18CCB32862735C6B24CEAB14C020134A4FC8D682F1F0`.
The newest 50 are a separate UI validation lane, not a substitute for all 79.

Machine: AMD Ryzen 9 9900X, 24 logical processors, approximately 31 GiB available
physical memory, Windows 10.0.26200, .NET 10.0.12, Balanced power plan. The user
reports NVMe storage. Manifest validation reads the inputs before the measured
scan and warms the OS file cache; these are empty-application-cache measurements,
not OS-cold disk measurements.

Diagnostic service measurements (fresh process, all 79, zero failures):

| Reader | Workers | Wall time | CPU time | Allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| Merged-main Normal baseline | 2 | 16.493 s | 31.266 s | 22.508 GB |
| Merged-main Normal baseline | 4 | 10.424 s | 35.250 s | 22.597 GB |
| Summary, reusable buffers/bunches | 4 | 8.014 s | 28.250 s | 10.134 GB |
| Summary, recorder cache added | 4 | 7.766 s | 25.422 s | 10.311 GB |
| Summary, final packed-integer path | 4 | 7.927 s | 26.547 s | 10.256 GB |

These are exploration samples, not a controlled acceptance series or percentile
claims. Agent builds/probes were not excluded from every diagnostic window.
Microbenchmarks established integer-path and recorder-loop improvements, but the
smallest changes are below the variation in whole-corpus wall time.

The offscreen harness found a preexisting bug: the formatted Date grid binding
could write minute-truncated timestamps back into source rows. The binding is now
one-way. Rendered-grid regression tests fail without the fix for both initially
bound rows and subsequently added rows. Earlier UI samples are superseded by the
post-fix comparisons below.

Post-fix Skia offscreen measurements, two workers (the shipping default):

| Profile / application cache | Files | Scan to final frame | Managed entry to frame | Peak working set | Peak private bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Normal / empty | 79 | 19.034 s | 21.776 s | 561.8 MiB | 1488.2 MiB |
| Summary / empty | 79 | 11.795 s | 14.256 s | 578.9 MiB | 1633.5 MiB |
| Summary / populated, autoscan | 79 | 0.755 s | 2.495 s | 126.8 MiB | 70.8 MiB |
| Normal / empty | 50 | 14.587 s | 16.834 s | 566.1 MiB | 1515.8 MiB |
| Summary / empty | 50 | 9.139 s | 11.297 s | 571.4 MiB | 1642.8 MiB |

These compare Normal and Summary in this branch, not native app releases.
All five runs completed without failures. The 79-row Normal, Summary and cached
ordered projections match exactly, as do the two 50-row projections; displayed
aggregates and ordered frequent opponents also match. The 79-file allocated-byte
total fell from 22.579 GB to 10.166 GB. The provisional 1 GiB private-memory target
was exceeded by both cold profiles and remains unresolved.

Each lane is one diagnostic sample. Managed entry excludes OS process creation;
the warm autoscan includes initial view construction after invocation. Native
timing, physical display latency, and controlled percentiles are not established
by these measurements. Sanitized raw lane metrics are in
[`library-scan-measurements.json`](library-scan-measurements.json).

Native desktop evidence and the 20-run five-second gate are separate from these
service/offscreen results. Do not describe this work as meeting the SLA without
that gate. A single run above five seconds is already a failure of the hard target;
there is no reason to repeat a clearly losing configuration 20 times.

## Kept and rejected experiments

- Kept: exact-group exclusions, null logging in the summary parser, owned reusable
  buffers, transient bunch reuse, bounded integer paths, and recorder reconciliation.
- Rejected: whole-block skipping for the three excluded groups (about 0.1 s gain),
  disabled tiered compilation (slower), and eight workers/server GC (higher memory
  use without reaching five seconds). No eight-worker policy ships.
- Historical diagnostic only: skipping every framed export took 6.245 s / 21.047
  CPU-s; opaque content-block skipping took 4.303 s / 13.125 CPU-s. Audit found
  that scratch harness still used parser 3.0.9 without the fast-integer opt-in.
  These are not controlled 3.0.12 comparisons. Both profiles intentionally lose
  required data and can never be used as valid library results.
- Deferred: player-state field pruning needs to preserve creation/name/owner
  callbacks even when all fields in an update are filtered. Cosmetic pruning alone
  is unlikely to close the remaining gap.

A subsequent sampled comparison explicitly restored parser 3.0.12 and enabled all
three optimization hooks in each diagnostic reader. For one representative 41 MB
replay, one fresh process per profile:

| Profile | Wall time | Process CPU time |
| --- | ---: | ---: |
| Valid Summary | 1.324 s | 1.281 s |
| All framed groups rejected (invalid data) | 1.031 s | 1.172 s |
| Opaque content blocks (invalid data) | 0.918 s | 1.094 s |

The fully opaque bypass saved only 113 ms (about 11%) over framed rejection on this
input. A safe shortcut retaining required blocks would recover less. This does not
support pursuing a generic content-block read plan as the next major optimization.
The opaque floor is dominated by packet/bunch framing. In valid Summary, exclusive
worker samples attributed 79 ms to ReadIntPacked, 19 ms to ReadBit, and 16 ms to
ReadSerializedInt; no individual integer primitive can explain the remaining gap.
This is a single-file diagnostic, not a corpus-wide lower bound or SLA result.

Further work should first sample multiple representative files and the all-79
critical path at the four-worker ceiling, then quantify a candidate before changing
framing or introducing a second channel-state implementation. Preserve external
names, actor lifecycle, event observation triggers, unknown-group fallback, and
complete oracle parity. The current evidence does not identify a validated safe
change that closes the five-second gap; neither worker inflation nor incomplete
decoding is an acceptable substitute.

## Reproduction and provenance

See [summary dependencies](library-summary-profile.md),
[service oracle](../tools/ReplayBenchmark/README.md),
[offscreen harness](../BotOrNot.LibraryUiBenchmark/README.md), and
[native harness](../BotOrNot.Avalonia/NativeBenchmark.md).

Parser package chain: `3.0.12-botornot`, rebuilt from upstream
`2fc699e99cf8f6654f13fcc5272ea1de57d89fd7` plus the committed patch. An independent
fresh-source audit applied and built the patch and found no method-body differences
between the fresh build and the packaged Unreal.Core/FortniteReplayReader assemblies.
The service oracle records hashes of all four parser binaries and the GC mode.

Private recordings, raw player identities, cache files, captures, and per-file
oracle projections remain local. Only sanitized timing/provenance evidence belongs
in the repository or PR. Rollback consists of returning the cache scan to the full
reader and incrementing the cache revision; do not reuse caches from a semantically
different profile.
