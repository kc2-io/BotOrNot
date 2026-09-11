# Replay scan benchmark

Measured 2026-09-11 against commit `b046f92` using the 76 `.replay` files in the
local Fortnite replay directory. The recordings were read without modification. Every run used
a private benchmark cache outside the repository and outside the application's live cache.

The selected newest 50 files totalled 862,127,799 bytes. All 76 files totalled 1,090,354,648
bytes. Every run processed and loaded its complete selected set with zero failures. Normalized
summary fingerprints were identical at each concurrency:

- 50 files: `F4C2A72772A32148FC499954EABF78EDCAF1115EFA4CF86FAB211830A2D5726F`
- 76 files: `D0D872D41DAC4973C273CAF89651FC7B4706564CB569E773FF726B41FBA2E76B`

## Results

Each cell is the median and maximum of five separate processes. With only five trials, the
nearest-rank 95th percentile is the maximum.

| Selected | Workers | Cold total p50/max | Cold first row p50/max | Peak working set p50/max | Warm total p50/max | Warm first row p50/max |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 50 | 1 | 16,223 / 16,468 ms | 1,438 / 1,814 ms | 310.3 / 366.0 MiB | 42 / 46 ms | 42 / 46 ms |
| 50 | 2 | 11,660 / 11,824 ms | 1,295 / 1,459 ms | 418.2 / 465.1 MiB | 43 / 48 ms | 42 / 48 ms |
| 50 | 4 | 7,673 / 8,061 ms | 1,099 / 1,190 ms | 541.5 / 583.1 MiB | 44 / 55 ms | 43 / 55 ms |
| 76 | 1 | 19,808 / 20,103 ms | 1,445 / 1,592 ms | 322.0 / 340.7 MiB | 45 / 46 ms | 45 / 46 ms |
| 76 | 2 | 14,613 / 14,969 ms | 1,314 / 1,403 ms | 424.3 / 439.8 MiB | 43 / 52 ms | 42 / 52 ms |
| 76 | 4 | 9,508 / 10,393 ms | 1,024 / 1,121 ms | 558.0 / 655.3 MiB | 45 / 51 ms | 44 / 51 ms |

Raw cold total milliseconds:

- 50/1: 16,321; 15,434; 16,468; 16,223; 16,200
- 50/2: 11,065; 11,824; 11,720; 11,660; 10,969
- 50/4: 7,673; 8,061; 7,421; 7,682; 7,551
- 76/1: 19,705; 19,808; 20,103; 19,584; 20,015
- 76/2: 14,362; 14,929; 14,398; 14,613; 14,969
- 76/4: 9,508; 9,435; 9,766; 10,393; 9,486

Raw cold first-row milliseconds:

- 50/1: 1,814; 1,217; 1,438; 1,700; 1,348
- 50/2: 1,135; 1,459; 1,295; 1,382; 1,223
- 50/4: 1,099; 1,190; 881; 1,110; 922
- 76/1: 1,451; 1,436; 1,592; 1,306; 1,445
- 76/2: 1,333; 1,313; 1,244; 1,403; 1,314
- 76/4: 773; 940; 1,024; 1,121; 1,057

Raw warm total milliseconds:

- 50/1: 42; 42; 41; 46; 43
- 50/2: 41; 45; 43; 48; 42
- 50/4: 44; 42; 42; 48; 55
- 76/1: 45; 46; 42; 42; 46
- 76/2: 52; 42; 41; 43; 46
- 76/4: 45; 51; 43; 44; 45

Inventory enumeration was typically 11–12 ms. Four-worker summed decode time was approximately
29–32 seconds for 50 files and 37–41 seconds for 76 files, while elapsed completion was 7.4–8.1
seconds and 9.4–10.4 seconds respectively. Replay decoding and projection dominate cold elapsed
time; atomic cache checkpoints and stream delivery are a small part of the measured wall time.

The five-second cold target was not met. The best median was 7.673 seconds for 50 files and 9.508
seconds for 76 files. Four workers improve completion and first-row latency, but increase median
peak working set from about 310–322 MiB at one worker to about 542–558 MiB. The conservative
default remains two workers pending a product decision about this memory/latency tradeoff. A
summary-specific parser path would be the next performance investigation; parse mode was not
lowered because the required displayed-field equivalence has not been established.

## Environment and method

- Windows `10.0.26200`
- AMD64 Family 26 Model 68, 24 logical processors
- Approximately 31.1 GiB available memory
- .NET SDK `10.0.401`, .NET runtime `10.0.12`, x64
- `FortniteReplayReader` `3.0.3-botornot`, `ParseMode.Normal`

The temporary harness is at `work/bench54` in the orchestration workspace. After loading
`work/agent-env.ps1`, a single run is:

```powershell
dotnet .\bench54\bin\Release\net10.0\Bench54.dll `
  'C:\Users\ken\AppData\Local\FortniteGame\Saved\Demos' `
  '.\bench54\private-cache.json' 50 2 cold
```

The harness times inventory-to-start, first streamed summary, total service completion, CPU,
working set, allocations, and each `IReplayService.LoadReplayAsync` call. Decode timings include
the core replay projection. Total time includes cache lookup, summary creation, atomic checkpoint
writes, and stream consumption. This is a headless service benchmark; it does not measure UI
dispatcher batching or rendering.
