# avd-experience

## Project Goal

Monitor Azure AVD (Windows 365) session activity on a local machine and analyze it:
- When AVD active
- Time lost to issues (drops, reconnects, auth delays, broker errors)

## Architecture: Service + CLI over a local SQLite DB

An un-elevated CLI cannot read the `Security` event log, so event ingestion runs in a
Windows Service writing to a shared SQLite database; the CLI queries the DB directly.

```
AvdExperience.Service (avd-service.exe, NT AUTHORITY\SYSTEM, auto-start)
    subscribes to RDPClient / System / Security event channels
    → appends RawEvents + channel watermarks (arrival order irrelevant)
    → periodic derive(): fold RawEvents in canonical order (Stats.fs)
      → rewrites Intervals + StateSnapshot in avd.db (%ProgramData%\AvdExperience, WAL mode)
    → Retention.prune (startup + daily): deletes RawEvents older than 90d,
      persisting the exact machine state at the cutoff as the RetentionSeed
      so folds over the retained history continue the full-history fold

AvdExperience (avd-experience.exe, standard user, never elevated for queries)
    reads avd.db read-only via SqlHydra → Spectre.Console report / CSV / monitor
```

Data flow:

```
Windows Event Log
       │
  EventLog.fs  →  LogEvent list
       │
  Events.fs    →  classifiers used by Stats + CsvExport
       │
  ┌────┴─────────┐
  │              │
Stats.fs    AvdExperience.Service (IngestionWorker)
  │              │  append path, one transaction per event:
  │              │  RawEvent + channel watermark (dedup-guarded, order-independent)
  │              │  derive(), periodically: fold all RawEvents canonically →
  │              ▼  rewrite Intervals + StateSnapshot when they differ
PeriodStats    avd.db (SQLite WAL: RawEvents, Intervals, SyncWatermarks, StateSnapshots, ServiceMeta)
  │              │
Report.fs       ▼
            AvdExperience CLI (DbRepository read-only) → Report.fs / CsvExport.fs / MonitorWatcher.fs
```

## Stack

- F# / .NET 10, Windows-only
- Windows Event Log API (`System.Diagnostics.Eventing.Reader`) — Service only
- Microsoft.Data.Sqlite (WAL mode) + SqlHydra.Query 5.x — typed F# query CE, no raw SQL in queries
- Argu — CLI argument parsing; Spectre.Console — colored output
- System.ServiceProcess.ServiceController — CLI-side service management

## Project Layout

Five projects in `AvdExperience.slnx`:

- `AvdExperience.Core/` — shared class library: EventLog, Events, Stats, DateParser,
  DbSchema (SqlHydra-generated, regenerate via `dotnet sqlhydra sqlite` — tool manifest
  in `.config/dotnet-tools.json`, template in `template.db` + `sqlhydra-sqlite.toml`),
  DbRepository (typed queries/inserts, shared by service & CLI)
- `AvdExperience.Service/` — Windows Service host: IngestionWorker (BackgroundService),
  Program (Host.UseWindowsService, service name `AvdExperienceService`), Retention
  (90-day RawEvents pruning)
- `AvdExperience/` (root) — frontend CLI: Elevation, ServiceManager (ServiceController +
  sc.exe install/uninstall + icacls ACL), CsvExport, Report, MonitorWatcher (DB polling),
  Program
- `AvdExperience.UnitTests/`, `AvdExperience.IntegrationTests/`

## Key Types

- `LogEvent` — raw event (`Id`, `TimeCreated`, `Provider`, `Message`, `Properties`)
- `QueryError` — `AccessDenied | ChannelNotFound of string | QueryFailed of string`
- `IntervalKind` — `Active | Connecting | Paused | Issue`
- `ConnectReason` — `Initial | PostIssue | PostPause`; tracks why the current connect started to compute disruption cost
- `FoldSeed` — everything the state machine remembers between events (`State`, `Locked`,
  `Shadow`, `Reason`); `emptySeed` folds from nothing. Retention persists it at the
  pruning cutoff so folds over the retained history equal folds over the full history
- `Interval` — typed span (`Kind`, `Start`, `End`; duration = `End - Start`)
- `DayStats` — per-day aggregates: `ActiveTime`, `ConnectingTime`, `PausedTime`, `IssueTime`, `IssueCount`, `ReportTime`
- `PeriodStats` — `ByDay: DayStats list` + period totals incl. `TotalReport`
- `EventTrace` — single event with state machine context (`StateBefore`/`After`, `ClosedInterval`, `IsRelevant`, `ReportContribution`)
- `IntervalSlice` — interval split at midnight boundary (`Date`, `DurationOnDate`)
- `TraceResult` — `Intervals`, `EventTraces`, `IntervalSlices` from `computeWithTrace`

## DB Notes

- Timestamps are stored UTC-normalized (`ToUniversalTime().ToString("o")`, uniform
  `+00:00` suffix) so lexicographic SQL string comparison matches chronological order.
- WAL mode: readers (CLI) need *write* access to the `-shm` file, so install grants
  `BUILTIN\Users` Modify on `%ProgramData%\AvdExperience` (not just Read).
- Append-only ingestion: the service's live path only appends RawEvents (+ per-channel
  watermarks), transactionally and idempotently. `(Channel, EventId, TimeCreated)` is
  the dedup key (`eventExists` + UNIQUE index `idx_events_dedup`): replay boundary
  events, the warmup→live handover overlap, and watcher re-delivery are all skipped.
  Startup replays events at/after the watermarks (inclusive lower bound, 30-day initial
  backlog) — same-timestamp siblings are not lost across restarts, and backward
  watermarks (clock adjustments) cannot duplicate rows.
- Derived tables: Intervals and StateSnapshots are pure functions of RawEvents,
  recomputed by `derive` in the canonical order (time, then channel rank — Security
  first, matching `advanceState`'s lock-first routing — then event ID). The live append
  path never touches the state machine, so arrival-order skew across watcher channels
  cannot corrupt persisted state; a derive pass folds at warmup and on a periodic tick
  (eventual consistency within ~2s). Restarts need no snapshot restore — the fold over
  RawEvents *is* the state. Interval rows store the connect reason in effect at close
  time; `""` means none (distinct from a genuine `Initial`). `derive` returns the max
  RecordId of the snapshot it folded — the worker's derivation cursor must come from
  that value, never a later max(RecordId) probe (a probe read after the fold can
  include an event appended mid-derive and mark it derived before it is).
  The fold is seeded from the `RetentionSeed` (empty = fold-from-nothing) so a
  pruned DB continues the full-history fold — `stepState` never returns the state
  to `None` once a session has started, so retained folds must be seeded, not
  restarted.
- Retention (90 days): `Retention.prune` runs at warmup (after the replay) and once
  per UTC day on the derive tick. It cuts at the newest event timestamp at/before
  the horizon — a whole same-timestamp batch (canonical order folds siblings
  together, so the seed must follow a complete batch) — by folding everything up
  to that boundary (seeded by the previous seed, chained across prunes), storing
  the result as the `RetentionSeed` meta, deleting `RawEvents WHERE TimeCreated
  <= cutoff`, stamping `RetentionStart`, and re-deriving (deletes don't move
  max(RecordId), so the tick can't notice by itself). This makes
  `fold(retained, seed)` identical to `fold(full history)` from the cutoff onward
  — a left-fold identity — including the connect-reason chain (the first
  post-cutoff reconnect keeps its true PostPause/PostIssue classification) and
  intervals open at the cutoff (true start preserved). The append path drops
  events at/before `RetentionStart` without storing them or advancing the
  watermark: re-arrived old events (replay after a long outage, clock skew) would
  otherwise fold backward on top of the seed and corrupt the derivation; they are
  simply re-read and re-dropped on each restart (bounded by OS log rotation). The
  CLI warns when a report window starts at/before `RetentionStart` or before
  `BackfillStart`, whichever applies.
- Settings: user-facing knobs live as ServiceMeta rows rather than appsettings.json —
  a file beside the exe would be clobbered by "extract the new zip over the same
  folder" upgrades, and the service's frozen command line rules out arguments.
  `Retention.prune` reads `RetentionDays` at prune time and the worker prunes on
  the tick when the value changes, so a shrink applies within seconds without a
  restart. `getRetentionDays` falls back to the built-in default when the stored
  value is invalid (the CLI `config show` surfaces the raw override instead of
  masking it). Shortening is destructive — the CLI confirms — while lengthening
  resurrects nothing: `RetentionStart` stays where the last prune actually reached.
- Warmup → live handover: `EventLogWatcher` (no bookmark) only raises events logged
  after activation, so subscriptions are enabled *before* the backlog replay and
  buffered (`LiveEventBuffer`); buffered events are drained after the replay and the
  dedup guard absorbs the overlap — no events are lost in the handover window.
- Backfill readiness: the service deletes the `BackfillComplete` meta row when warmup
  starts and sets it once caught up (after the first derive); the CLI waits on it
  (bounded, 30s) before reporting so a fresh install is not silently partial. The
  first warmup also records `BackfillStart` (first-warmup time − 30d): events older
  than that horizon were never ingested, and the report warns when a window starts
  before it instead of silently printing a partial report.
- Module/type naming matters for SqlHydra: record types must live in a module whose
  name equals the SQL schema (`main` for SQLite) and be named exactly like the table.

## Testing Conventions

- `DbFlowTests` — repository roundtrips: events (incl. Properties JSON), intervals,
  watermarks, snapshots, meta, the retention seed roundtrip and inclusive cutoff
  deletes (`deleteEventsAtOrBefore`/`getLatestTimeAtOrBefore`), the retention-days
  setting (roundtrip/reset, invalid-value fallback, `tryParseRetentionDays`
  bounds), the `eventExists` dedup guard, canonical event order (equal timestamps →
  channel rank → event ID), monitor record-ID queries, UTC normalization,
  chronological read order (inserts are deliberately scrambled in one test).
- `ServiceFlowTests` — Service↔CLI contract without live OS resources: fixture scenarios
  appended through `Ingestion.appendEvent` (same code the service runs), derived via
  `derive`, read back through the CLI read path, and compared against in-memory
  `computeWithTrace`; derivation order-independence (reversed appends, late-arriving
  events); restart tests (watermark no-duplicate, same-timestamp sibling replay,
  backward-watermark dedup); warmup→live handover (`LiveEventBuffer` with replay
  overlap); DI regression (IHostedService resolves through the factory);
  retention (`pruneWith` with a fixed horizon: cutoff batch deletion, seed capture,
  pruned-vs-unpruned equality over post-cutoff windows incl. PostPause continuity,
  idempotence, pre-cutoff append drop, snapshot preservation); the settings store
  (retention-days roundtrip/reset, invalid-value fallback, `prune` honoring the
  configured days).
- Retention fold tests (`RetentionTests` in UnitTests) prove the core invariant:
  folding the retained events seeded with fold(prefix) equals folding all events —
  same final state tuple and the same intervals that close after the cutoff
  (property test + deterministic same-timestamp-batch and PostPause cases).
- Service logic tests use an injectable `EventLogSource` (`fun ch _ _ -> []` or filtered
  fixtures) — never query real event channels in tests.
- Fake sources must filter events by channel (`channelOf`), matching real per-channel
  logs; a shared source returning all events cross-contaminates watermark replays.
- Each test gets a unique temp DB via `Fixtures.withTempDb` (unique path avoids
  connection-pool file locks; pools cleared before deletion).
- NOT automated (manual/ops only): UAC elevation, sc.exe install/uninstall, real
  service lifecycle, interactive `--monitor` with live session activity.

## Key Modules

```
Elevation.fs       — UAC helpers; isElevated, relaunchElevated (works under dotnet run
                     and published exe), tryRedirectOutput (--elevation-output temp file)
ServiceManager.fs  — ServiceController status/start/stop + sc.exe/icacls install/uninstall
                     (idempotent: re-running install repairs the ACL grant);
                     findServiceExe (published folder or dev build output);
                     ensureServiceReady (install/start on demand, shared by report &
                     --monitor); isBackfillComplete/waitForBackfill (backfill readiness);
                     reportDbReadError (friendly CLI output for failed DB reads)
EventLog.fs        — Windows Event Log I/O; queryByTimeRange, subscribeChannel, makeIdXPath
Events.fs          — Event-ID classifiers (1024/1027/1026/1033/42/107/4800/4801…), marker
Stats.fs           — State machine: stepState + shadowStep + advanceState; nextConnectReason;
                     foldState (history→state) / foldHistory (history→closed intervals
                     + final state — the derivation fold); intervalReportContrib;
                     computeWithTrace → PeriodStats/TraceResult
DateParser.fs      — Human-friendly date parsing; localMidnight / localEndOfDay
DbRepository.fs    — Typed SqlHydra queries & inserts; WAL init; UTC ISO timestamps;
                     channel constants + channelRank → canonical event order used by
                     every reader; write: saveEvent/eventExists (dedup guard)/
                     saveInterval/upsertWatermark/saveSnapshot(+load)/setMeta/
                     tryGetMeta/deleteMeta/clearIntervals; read: getAllEventRows/
                     getAllEvents/getEventsInRange/getEventsBefore/getAllIntervals/
                     getIntervalsInRange/getEventsAfterRecordId/getMaxEventRecordId/
                     counts; backfillCompleteKey/backfillStartKey/getBackfillStart
                     (+ initialBacklogDays) — service-meta markers shared with the CLI,
                     plus the retention queries: retentionSeedKey/retentionStartKey/
                     getRetentionStart/getFoldSeed/saveRetentionSeed (seed storage
                     `Kind@ISO|locked|Kind@ISO|Reason`, hand-rolled — no reflection,
                     trim-safe) / getLatestTimeAtOrBefore / getEventsAtOrBefore /
                     deleteEventsAtOrBefore (+ retentionDays); the settings store:
                     retentionDaysKey/tryParseRetentionDays/getRetentionDays
                     (override from ServiceMeta, validated, else the default)/
                     setRetentionDays/resetRetentionDays — read at prune time by the
                     service and by the CLI `config` verbs, so both see one value
Ingestion.fs       — Service ingestion core (no hosting deps), append-only:
                     appendEvent (dedup-guarded transaction; drops events at/before
                     the retention cutoff without storing or advancing the watermark —
                     they would fold backward on top of the seed; failed live appends
                     are recovered only while the watermark hasn't advanced past them),
                     LiveEventBuffer (lossless warmup→live handover), replayPending(…Passes)
                     (inclusive watermark replay), derive (canonical fold from the
                     RetentionSeed → rewrite Intervals + snapshot; returns the folded
                     max RecordId as the derivation cursor), warmup = replay + derive
                     (EventLogSource is injectable so tests never touch live channels)
Retention.fs       — RawEvents pruning (prune/pruneWith, injectable horizon for
                     tests): prune reads the configured retention-days setting at
                     prune time (user override from the settings store, else the
                     built-in default), cuts at the newest event timestamp at/before
                     the horizon (a whole same-timestamp batch), folds it seeded by
                     the previous seed → RetentionSeed meta, deletes at/before the
                     cutoff, stamps RetentionStart, re-derives; runs at warmup and
                     on the worker tick (first tick of a new UTC day, or immediately
                     when the configured value changes)
IngestionWorker.fs — Thin BackgroundService shell: initDatabase + backfill markers
                     (BackfillComplete readiness, BackfillStart horizon),
                     subscribe (buffered) BEFORE replay, drain, Retention.prune
                     (before the first derive), derive, mark backfill complete,
                     then periodic derive tick (every 2s when new events; the cursor
                     is derive's folded max RecordId) + retention prune on the first
                     tick of a new UTC day or when retention-days changes
Hosting.fs         — configureServices/buildHost; IHostedService registered through the
                     factory (constructor injection cannot supply dbPath)
MonitorWatcher.fs  — --monitor: ensures the service is running, waits for backfill,
                     polls RawEvents every 500ms (each poll batch folded in canonical
                     order; full re-derivation when a batch arrives out of canonical
                     order), prints transitions; Ctrl+C to stop
CsvExport.fs       — writeEventsCsv / writeIntervalsCsv
Report.fs          — Spectre table (printStats), trace log (printTrace), format helpers
Program.fs         - arg parsing; `service <status|start|stop|install|uninstall>` verbs
                     (nested ServiceArgs subcommand, like `config`: listed under
                     SUBCOMMANDS in --help, `service --help` details the verbs, bare
                     `service` defaults to status; elevation on demand); `config` /
                     `config set|reset` settings
                     verbs (write ServiceMeta from the CLI, no elevation; set confirms
                     when shrinking retention — destructive at the next prune); report
                     path reads DB, no elevation; warns when the window starts before
                     the BackfillStart horizon or at/before the RetentionStart
                     horizon (pruned history)
```

## IntervalKind Semantics

| Kind | Meaning | Triggered by |
|------|---------|-------------|
| `Active` | Session live | `isConnected` (1027) |
| `Connecting` | Handshake in progress | `isConnectInitiated` (1024/1102) |
| `Paused` | User-initiated stop | `isPowerDown`, `isWorkstationLocked` (4800), `isUserDisconnected` (1026 reason 1/2/3), `isDisconnected` while workstation locked |
| `Issue` | Unexpected drop | `isDisconnected` (1026) or `isThreadWatchdog` (1033) while workstation **not** locked and reason ≠ 1/2/3 |

Note: `advanceState` routes events by `locked` flag (updated by 4800/4801). While locked, AVD events advance `shadowStep` without generating outward intervals. On unlock, shadow determines new state: `Active` if session survived, `Paused` if dropped (reconnect = `PostPause`), `None` if user-disconnected. Exception: if the lock-induced Paused interval was ≥ 3h, shadow `Issue` or `Connecting` is reset to `Paused` (stale state discarded).

## Report Column (Disruption Cost)

`intervalReportContrib` estimates time lost per closed interval:
- `Connecting` → full connecting duration + 5 min grace for `Initial` connects
- `Issue` → full issue duration
- `Active` after `PostIssue` reconnect → up to 15 min (recovery overhead)
- `Paused` → zero

`ConnectReason = Initial` applies when: no prior session (`None → Connecting`), or previous `Paused` interval was ≥ 3h (long absence = fresh start). `PostIssue` is preserved through short Paused detours (< 3h) so recovery overhead still applies after brief interruptions.

Aggregated into `ReportTime` per day and `TotalReport` for the period.

## Key Domain Concepts

| Term | Meaning |
|------|---------|
| Session | Single AVD connection lifetime (connect → disconnect) |
| Lost time | Gaps from reconnects, errors, broker delays within session |
| Active time | Total session duration minus lost time |
| Report time | Estimated disruption cost: reconnect overhead + issue duration + recovery |

## Event Sources

Windows Event Log channels relevant to AVD (all ingested by the service; the CLI never touches them):
- `Microsoft-Windows-TerminalServices-RDPClient/Operational` (RDP connect/disconnect)
- `System` (power events: sleep 42, resume 107, shutdown 1074/6006/6008)
- `Security` (workstation lock/unlock: 4800/4801; requires SYSTEM/admin)

## CLI Usage

```
avd-experience -s 2026-05-01 -t 2026-05-18 [--csv]   report from the local DB (no elevation)
avd-experience --monitor                             live transitions from avd.db
avd-experience service                               same as 'service status'
avd-experience service status                        service state + DB record counts
avd-experience service start|stop                    start/stop (stop elevates)
avd-experience service install|uninstall             registers/deregisters (elevates)
avd-experience config                                settings + stamped horizons (no elevation)
avd-experience config set retention-days 30          store override (confirms when shrinking)
avd-experience config reset retention-days           back to the built-in default
```

Query flow: if the service is stopped the CLI starts it (elevating only if needed);
if not installed it self-elevates once to install, then queries.

## Conventions

- Functional-first F#: pipelines, discriminated unions, pattern matching
- No mutable state unless interfacing with Windows APIs or the event-driven worker
- CLI output: Spectre.Console ANSI markup, human-readable
- External deps welcome if needed, each addition must be confirmed

## Build, Test & Run

```bash
dotnet build
dotnet run -- -s 2026-05-01 -t 2026-05-18
dotnet run -- --monitor
dotnet test AvdExperience.UnitTests
dotnet test AvdExperience.IntegrationTests
dotnet publish -p:PublishProfile=win-x64                # CLI → publish\win-x64
dotnet publish AvdExperience.Service -c Release -p:PublishProfile=win-x64 -o publish/
```

### Release packaging

GitHub Releases ship `avd-experience-<version>-win-x64.zip` (both exes + `e_sqlite3.dll`) plus
`SHA256SUMS.txt` — see `.github/workflows/release.yml`. `e_sqlite3.dll` must sit beside the exes
(native SQLite is not bundled into single-file apps). The service profile deliberately skips
`PublishTrimmed` (trimmer is unsafe with the Windows Service host). Users extract both exes to a
permanent folder; the CLI finds `avd-service.exe` beside itself and installs it on demand.

The CLI *is* trimmed (`Properties/PublishProfiles/win-x64.pubxml`), which is only safe because of
two guards — do not remove either, or release builds fail at runtime while dev builds and CI pass:
- `TrimmerRoots.xml` preserves `AvdExperience.Core`, `SqlHydra.Query`, `SqlHydra.Domain`,
  `FastExpressionCompiler`, and `FSharp.Core`. SqlHydra turns `select { ... }` quotations into
  LINQ trees at runtime via FSharp.Core's LeafExpressionConverter, which binds the referenced
  methods/properties by reflection; members referenced only from quotation literals are invisible
  to the trimmer, and trimming them makes every DB read die with "Could not bind to method".
- `JsonSerializerIsReflectionEnabledByDefault=true` in `AvdExperience.fsproj`: PublishTrimmed
  silently disables STJ reflection serialization, breaking `toLogEvent`'s
  `JsonSerializer.Deserialize<string list>` for `PropertiesJson`.
```
