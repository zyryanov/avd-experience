# AVD Experience

Background Windows service + CLI that record and analyze Azure AVD session quality — how long you were connected, and how much time was lost to drops, reconnects, and errors.

## Download

Grab the latest `avd-experience-<version>-win-x64.zip` from [Releases](../../releases/latest). No .NET install required — self-contained binaries.

Extract **both** executables and `e_sqlite3.dll` into a permanent folder (e.g. `Tools\avd-experience`) — the Windows service registration points at this location, so not a temp dir or an in-zip preview.

Run `avd-experience.exe`: on first run it elevates once via UAC to install and start the `avd-service.exe` service (which records AVD activity into a local database); afterwards reports run without elevation. A `SHA256SUMS.txt` next to the zip lets you verify the download.

### Upgrading

1. `avd-experience service stop`
2. Extract the new zip over the same folder
3. `avd-experience service start`

(Or `service uninstall`, replace the files, and let the CLI reinstall on the next run.)

## Requirements

- Windows — the background service reads the event logs as SYSTEM, so the CLI itself needs no special privileges
- Admin rights are only needed once, for the UAC elevation that installs/starts the service (declined elevation = no report, since there is no data source)

## Build from Source

Requires .NET 10 SDK.

```bash
dotnet run -- [options]
```

To produce the release binaries (self-contained, trimmed):

```bash
dotnet publish -p:PublishProfile=win-x64                 # CLI → publish\win-x64
dotnet publish AvdExperience.Service -c Release -p:PublishProfile=win-x64 -o publish/
```

### Testing

```bash
dotnet test AvdExperience.UnitTests
dotnet test AvdExperience.IntegrationTests
```

## CLI Options

| Flag | Aliases | Default | Description |
|------|---------|---------|-------------|
| `--start` | `-s`, `--from` | today | Start date, inclusive (`yyyy-MM-dd` or relative: `today`, `yesterday`, `-1d`, `-2w`, `3 days ago`, `2 months ago`) |
| `--end` | `-t`, `--to` | today | End date, inclusive (same formats) |
| `--monitor` | `-m` | — | Watch the database: poll `avd.db` for live AVD state changes, print each transition with timestamp and duration; Ctrl+C to stop |
| `--csv` | `-c` | off | Export raw events and intervals to CSV files |
| `service <verb>` | — | — | Manage the background service: `status` (no elevation), `start`, `stop`, `install`, `uninstall` (elevate as needed) |
| `config <verb>` | — | — | Show or change settings stored in the database (no elevation): `config`, `config show`, `config set <key> <value>`, `config reset <key>` |

## What It Reports

Output: console table grouped by day + period totals.

| Column | Meaning |
|--------|---------|
| Active | Session live and working |
| Connecting | Handshake in progress |
| Paused | User-initiated stop (lock, disconnect) |
| Issue | Unexpected drop (reconnect needed) |
| Issues# | Count of unexpected drops |
| Report | Estimated disruption cost: Connecting time + Issue time + up to 15 min context-switch penalty per post-issue reconnect + 5 min grace on fresh connects (first of the day or after 3h+ pause) |

**Report** is the key metric for productivity impact — it answers "how much time did AVD problems actually cost me?"

> **Note:** Event log heuristics are best-effort. Some events may be misclassified — for example, a network drop during an active session can look like a user disconnect, inflating Issue time. Treat reported numbers as estimates, not exact measurements.

### CSV Export

Primarily for debugging. Produces two files:
- `avd-events-<from>--<to>.csv` — every relevant event with state machine context
- `avd-events-<from>--<to>-intervals.csv` — typed intervals with durations

## Data Retention

Raw events are kept for **90 days by default** — configurable via `avd-experience config set retention-days <days>` (the setting lives in the database, so it applies at the next prune without a service restart). The service prunes older events once a day, at startup, and immediately when the setting shrinks, cutting at a whole event timestamp and carrying the exact state-machine state (open interval, lock flag, connect reason) forward in a seed, so reports over the retained window are identical to full-history reports — the first reconnect after a prune is still classified by its true cause. Windows starting before the pruned horizon print a warning instead of silently showing partial data.

Note: **shortening** the retention permanently deletes everything below the new horizon (the CLI asks for confirmation); **lengthening** does not resurrect already-pruned events.

## Settings

Settings live in the shared database, which keeps them in one place for both processes and makes them survive upgrades and reinstalls:

```
avd-experience config                        # effective values + their source + stamped horizons
avd-experience config set retention-days 30  # store an override (confirms when shrinking)
avd-experience config reset retention-days   # back to the built-in default
```

## Architecture

Service + CLI over a local SQLite database. The service ingests; the CLI reads — it never touches the Event Log.

Five projects in `AvdExperience.slnx`:

```
AvdExperience.Core/      shared library
  EventLog.fs     — Windows Event Log I/O (XPath queries) — service-side only
  Events.fs       — event ID classifiers and domain knowledge
  Stats.fs        — state machine: raw events → typed intervals → DayStats/PeriodStats
  DateParser.fs   — human-friendly relative and exact date parsing
  DbRepository.fs — SQLite access (WAL mode) via SqlHydra; typed queries/inserts

AvdExperience.Service/   avd-service.exe — Windows service (SYSTEM, auto-start)
  Ingestion.fs / IngestionWorker.fs — subscribe to event channels, append RawEvents
  to avd.db, re-derive Intervals periodically (eventual consistency within ~2s)

(root)                   avd-experience.exe — the CLI
  Elevation.fs     — UAC self-relaunch
  ServiceManager.fs — install/start/stop the service; grants users access to the DB folder
  CsvExport.fs     — write events/intervals to CSV
  Report.fs        — format PeriodStats for console (Spectre.Console)
  MonitorWatcher.fs — --monitor: poll avd.db for state transitions
  Program.fs       — CLI arg parsing (Argu), pipeline orchestration
```

Data flow: the service subscribes to `TerminalServices-RDPClient/Operational`, `System` (power), and `Security` (lock/unlock) and appends raw events to `avd.db` in `%ProgramData%\AvdExperience`; it periodically folds all events through the state machine (`Stats.fs`) and rewrites derived intervals. The CLI reads the database read-only for reports, CSV export, and `--monitor`.

## Key Types

- `LogEvent` — raw event (Id, TimeCreated, Provider, Message, Properties)
- `IntervalKind` — `Active | Connecting | Paused | Issue`
- `ConnectReason` — `Initial | PostIssue | PostPause`; why the current connect started (drives the Report metric)
- `Interval` — typed span with start/end timestamps
- `DayStats` — per-day aggregates: active/connecting/paused/issue times, issue count, report time
- `PeriodStats` — aggregated stats with per-day breakdown
- `EventTrace` — single event with state-machine context (state before/after, closed interval, Report contribution)
