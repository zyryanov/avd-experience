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
| `--end` | `-t`, `--to` | today | End date, inclusive (`yyyy-MM-dd`) |
| `--monitor` | `-m` | — | Watch live event log; print each state transition with timestamp and duration |
| `--csv` | `-c` | off | Export to CSV — events+intervals, or perf samples when paired with `--perf` |
| `--specs` | `-i` | — | Print the host hardware spec (CPU, RAM, disks) and exit |
| `--perf` | `-p` | — | Live mode: sample whole-VM CPU/RAM/Disk usage and plot it; pair with `--csv` to dump samples |

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

> **Note:** Event log heuristics are best-effort. Some events may be misclassified — for example, a network drop during an active session can look like a user disconnect, inflating Issue time, or a lock event missing admin access skips Paused classification entirely. Treat reported numbers as estimates, not exact measurements.

### CSV Export

Primarily for debugging. Produces two files:
- `avd-events-<from>--<to>.csv` — every relevant event with state machine context
- `avd-events-<from>--<to>-intervals.csv` — typed intervals with durations

With `--perf --csv`: `avd-perf-<timestamp>.csv` — one row per sample
(`Timestamp,CpuPct,RamPct,RamUsedMB,DiskPct`).

## Resource Performance (`--specs` / `--perf`)

Separate from the event-log analysis: these read the **host's own hardware and
performance counters**, so run them *inside the AVD session host*.

- `--specs` — one-shot hardware inventory: CPU, logical CPU count, total/available
  RAM, fixed disks.
- `--perf` — samples CPU / RAM / Disk every 2 s and renders a live table with
  current / average / peak values and a sparkline trend. Ctrl+C to stop.

> **Note:** `--perf` counters are Windows `_Total` instances — they measure the
> **whole VM** across all sessions, not just your user session. On a multi-session
> host the numbers reflect everyone on the box. Disk/CPU counters need admin or
> "Performance Monitor Users" membership (the tool already self-elevates via UAC).

## Architecture

F# modules in dependency order:

```
Elevation.fs     — UAC self-relaunch
EventLog.fs      — Windows Event Log I/O (XPath queries)
Events.fs        — Event ID classifiers and domain knowledge
Stats.fs         — State machine: raw events → typed intervals → DayStats/PeriodStats
SysInfo.fs       — Static hardware spec (CPU/RAM/disks)
PerfMonitor.fs   — Live CPU/RAM/Disk perf-counter sampling
CsvExport.fs     — Write events/intervals/perf samples to CSV
Report.fs        — Format PeriodStats, spec table, and live usage for console
Program.fs       — CLI arg parsing (Argu), pipeline orchestration
```

Event sources: `TerminalServices-RDPClient/Operational`, `System` (power), `Security` (lock/unlock).

## Key Types

- `LogEvent` — raw event (Id, TimeCreated, Provider, Message, Properties)
- `IntervalKind` — `Active | Connecting | Paused | Issue`
- `Interval` — typed span with start/end timestamps
- `PeriodStats` — aggregated stats with per-day breakdown
