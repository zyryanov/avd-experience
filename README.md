# AVD Experience

CLI tool that reads Windows Event Logs and analyzes Azure AVD session quality — how long you were connected, and how much time was lost to drops, reconnects, and errors.

## Download

Grab the latest `avd-experience.exe` from [Releases](../../releases/latest). No .NET install required — self-contained single binary.

Run it: the tool will self-relaunch elevated via UAC if not running as admin.

## Requirements

- Windows (Event Log access)
- Admin privileges (optional — without them, the Security log is inaccessible, so workstation lock/unlock events are missing and Paused intervals from locking the screen won't be tracked)

## Build from Source

Requires .NET 10 SDK.

```bash
dotnet run -- [options]
```

### Testing

```bash
dotnet test AvdExperience.UnitTests
dotnet test AvdExperience.IntegrationTests
```

## CLI Options

| Flag | Aliases | Default | Description |
|------|---------|---------|-------------|
| `--start` | `-s`, `--from` | today | Start date, inclusive (`yyyy-MM-dd`) |
| `--end` | `-e`, `--to` | today | End date, inclusive (`yyyy-MM-dd`) |
| `--monitor` | `-m` | — | Watch live event log; print each state transition with timestamp and duration |
| `--csv` | `-c` | off | Export raw events and intervals to CSV files |

## What It Reports

Output: console table grouped by day + period totals.

| Column | Meaning |
|--------|---------|
| Active | Session live and working |
| Connecting | Handshake in progress |
| Paused | User-initiated stop (lock, disconnect) |
| Issue | Unexpected drop (reconnect needed) |
| Issues# | Count of unexpected drops |
| Report | Estimated disruption cost: Connecting time + Issue time + up to 15 min context-switch penalty per post-issue reconnect + 5 min grace on the first connect of the day (profile load overhead) |

**Report** is the key metric for productivity impact — it answers "how much time did AVD problems actually cost me?"

> **Note:** Event log heuristics are best-effort. Some events may be misclassified — for example, a network drop during an active session can look like a user disconnect, inflating Issue time, or a lock event missing admin access skips Paused classification entirely. Treat reported numbers as estimates, not exact measurements.

### CSV Export

Primarily for debugging. Produces two files:
- `avd-events-<from>--<to>.csv` — every relevant event with state machine context
- `avd-events-<from>--<to>-intervals.csv` — typed intervals with durations

## Architecture

Seven F# modules in dependency order:

```
Elevation.fs     — UAC self-relaunch
EventLog.fs      — Windows Event Log I/O (XPath queries)
Events.fs        — Event ID classifiers and domain knowledge
CsvExport.fs     — Write events/intervals to CSV
Stats.fs         — State machine: raw events → typed intervals → DayStats/PeriodStats
Report.fs        — Format PeriodStats for console
Program.fs       — CLI arg parsing (Argu), pipeline orchestration
```

Event sources: `TerminalServices-RDPClient/Operational`, `System` (power), `Security` (lock/unlock).

## Key Types

- `LogEvent` — raw event (Id, TimeCreated, Provider, Message, Properties)
- `IntervalKind` — `Active | Connecting | Paused | Issue`
- `Interval` — typed span with start/end timestamps
- `PeriodStats` — aggregated stats with per-day breakdown
