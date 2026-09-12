# Death-cause event coverage

The original audit uses the five committed BR/Reload replay fixtures and parser `3.0.4-botornot`. Its denominator is **finish events** from `PlayerElimination`, not final player records and not every kill-feed update.

The current application uses parser `3.0.5-botornot`, retaining the same event API and the storm channel-lifecycle fix from #56.

An event/kill-feed correlation requires the same stable victim, compatible DBNO kind, compatible actor when the actor resolves, and a unique match in both directions within 1.1 seconds. The bound is empirical: the largest nearest valid offset in the inspected BR/Reload/F1 corpus was 1.004 seconds. It is a bounded correlation heuristic, not universal event identity. Ambiguous candidates remain unmatched.

“Tag” below means a specifically resolved cause tag, not merely a nonempty `DeathTags` container. Event code `0` is the meaningful Storm code. Codes `50` (`Unspecified`), `51` (`MAX`), and codes outside the maintained table are raw evidence but unresolved.

| Fixture | Finishes | Correlated | Unmatchable | Code only | Tag only | Both agree | Conflict | Neither |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Blitz BR Squad | 33 | 33 | 0 | 30 | 0 | 0 | 0 | 3 |
| Blitz Zero Build BR Squad | 31 | 31 | 0 | 28 | 0 | 0 | 0 | 3 |
| Reload Build Duos | 76 | 75 | 1 | 74 | 0 | 0 | 0 | 2 |
| No-Build BR Trios (2026-01-31) | 103 | 101 | 2 | 95 | 2 | 2 | 1 | 3 |
| No-Build BR Solo (2026-06-10) | 96 | 96 | 0 | 95 | 0 | 0 | 0 | 1 |
| **Total** | **339** | **336** | **3** | **322** | **2** | **2** | **1** | **12** |

The five resolution columns are mutually exclusive and sum to 339. “Unmatchable” is an independent correlation count: those events still retain their event-chunk code, but no kill-feed tags/code are attached.

## Verified specific mappings

- `Area51Gun` maps to **Arc Gun** only when it is an exact dot-delimited segment under `Item.Weapon.*`. Five finish events contain this evidence: two tag-only events with event code 50, two events where the broad weapon-code category agrees, and one conflict with code 17. The tag label wins while the raw code and `Conflicting` status remain visible. The corpus also contains `Gameplay.Damage.Area51Gun.Beam`, but it does not distinguish direct from chained damage. **Arc Gun direct/chain remains unresolved and is not displayed.**
- Exact tag `Gameplay.Damage.DeployableTurret.Shot` maps to **Turret**. It occurs in the private F1 trace on an event whose raw event code is 50. Prefix/suffix lookalikes do not match.

Numeric codes remain the broad compatibility fallback. A specific verified tag refines a broad numeric label. Distinct resolved numeric labels, such as Rifle versus Shotgun, are retained with deterministic event-code display precedence and `Conflicting` status. Two different recognized specific tags on one event resolve to Unknown/Conflicting rather than depending on tag order.

## F1 owner-credit evidence

The private issue attachment is not committed. An always-running anonymized trace regression records its measured clocks and raw nullable DBNO values. The owner knocks P6 at event 1089.590/frame 1089.996 (`IsDbno=true`); P6 emits unmatched raw `IsDbno=false` at frame 1096.994; P6 later finishes the owner and is eventually finished by P94 at event 1124.290/frame 1124.991. The explicit recovery clears the owner knock, leaving the two earlier credits at 633.210 and 657.760. The optional local binary test confirms `OwnerKills=2`, two derived rows, and no stale 18:44 row.

The five committed fixtures retain matching authoritative/derived owner counts of **1, 1, 5, 8, and 3** in the table order above. F1 is the demonstrated behavior change: derived rows move from 3 to the authoritative 2 after the recovery invalidates the stale knock.

No timeout is used. Initial reboot counters are baselines; only a later increase resets a life. Missing DBNO fields, cause 50 by itself, arbitrary later attacks, non-finite/unknown timestamps, and same-frame contradictory lifecycle evidence never create a recovery or inferred credit.
