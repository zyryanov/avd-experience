# avd-experience

## Project Goal

CLI app reads Windows Event logs, analyzes Azure AVD session records:
- When AVD active
- Time lost to issues (drops, reconnects, auth delays, broker errors)

## Stack

- F# / .NET 10
- Windows Event Log API (`System.Diagnostics.Eventing.Reader`)
- Argu — CLI argument parsing
- Spectre.Console — colored table and ANSI markup output
- Target: `net10.0`, single exe

## Workflow

1. Implement changes
2. Update `CLAUDE.md` if architecture/conventions change

## Architecture

Seven modules, each with single concern. Compile order matches dependency order.

```
Elevation.fs  — UAC elevation helpers; isElevated, relaunchElevated, tryRedirectOutput;
                parent relaunches self as admin passing a temp file path, child redirects
                Console.Out to that file so parent can capture and print output
EventLog.fs   — Windows Event Log I/O; queryEvents, queryByTimeRange, querySystemPowerEvents,
                queryLockEvents, makeIdXPath, subscribeChannel (for monitor mode)
Events.fs     — Event-ID domain knowledge; classifiers (isPowerDown, isPowerResume,
                isConnectInitiated, isConnected, isDisconnected, isUserDisconnected,
                isConnectionCanceled, isWorkstationLocked, isWorkstationUnlocked, isRelevant);
                marker (id→CSV label string); knownNoMarker set
CsvExport.fs  — Serialize traces → events CSV (writeEventsCsv) and intervals → intervals CSV
                (writeIntervalsCsv); returns unknown event IDs for warnings
Stats.fs      — State machine (stepState) builds Active/Connecting/Paused/Issue intervals;
                nextConnectReason tracks ConnectReason across transitions; intervalReportContrib
                computes disruption cost per closed interval; computeWithTrace aggregates into
                DayStats/PeriodStats and splits intervals by day via splitByDay
Report.fs     — Spectre.Console colored table (printStats); state-change trace log (printTrace);
                format helpers (fmtTime, formatDuration, stateMarkup, stateColor)
Program.fs    — CLI arg parsing (Argu); runMonitor for live event subscription (bootstrapState
                seeds initial state from last 24h); run for historical range query + optional CSV
                export; elevation redirect via tryRedirectOutput
```

Data flow:

```
Windows Event Log
       │
  EventLog.fs  →  LogEvent list
       │
  Events.fs    →  classifiers used by Stats + CsvExport
       │
  ┌────┴────┐
  │         │
Stats.fs  CsvExport.fs
  │         │
PeriodStats  CSV file
  │
Report.fs  →  colored console table + trace log
```

Key types:
- `LogEvent` — raw event (`Id`, `TimeCreated`, `Provider`, `Message`, `Properties`)
- `QueryError` — `AccessDenied | ChannelNotFound of string | QueryFailed of string`
- `IntervalKind` — `Active | Connecting | Paused | Issue`
- `ConnectReason` — `Initial | PostIssue | PostPause`; tracks why the current connect started to compute disruption cost
- `Interval` — typed span (`Kind`, `Start`, `End`; duration = `End - Start`)
- `DayStats` — per-day aggregates: `ActiveTime`, `ConnectingTime`, `PausedTime`, `IssueTime`, `IssueCount`, `ReportTime`
- `PeriodStats` — `ByDay: DayStats list` + period totals incl. `TotalReport`
- `EventTrace` — single event with state machine context (`StateBefore`/`After`, `ClosedInterval`, `IsRelevant`, `ReportContribution`)
- `IntervalSlice` — interval split at midnight boundary (`Date`, `DurationOnDate`)
- `TraceResult` — `Intervals`, `EventTraces`, `IntervalSlices` from `computeWithTrace`

## IntervalKind Semantics

| Kind | Meaning | Triggered by |
|------|---------|-------------|
| `Active` | Session live | `isConnected` (1027) |
| `Connecting` | Handshake in progress | `isConnectInitiated` (1024/1102) |
| `Paused` | User-initiated stop | `isPowerDown`, `isWorkstationLocked` (4800), `isUserDisconnected` (1026 reason 1/2), `isDisconnected` while workstation locked |
| `Issue` | Unexpected drop | `isDisconnected` (1026) while workstation **not** locked and reason ≠ 1/2 |

Note: `stepState` carries a `locked: bool` flag (updated by 4800/4801 events). A disconnect while locked → `Paused`; otherwise → `Issue`.

## Report Column (Disruption Cost)

`intervalReportContrib` estimates time lost per closed interval:
- `Connecting` → full connecting duration + 5 min grace for `Initial` connects
- `Issue` → full issue duration
- `Active` after `PostIssue` reconnect → up to 15 min (recovery overhead)
- `Paused` → zero

Aggregated into `ReportTime` per day and `TotalReport` for the period.

## CLI Args

```
--start / -s / --from  <yyyy-MM-dd>   start date inclusive (default: today)
--end   / -e / --to    <yyyy-MM-dd>   end date inclusive   (default: today)
--monitor / -m                        live mode: watch event log, print each transition
--csv   / -c                          export events and intervals to CSV files
```

## Conventions

- Functional-first F#: pipelines, discriminated unions, pattern matching
- No mutable state unless interfacing with Windows APIs
- CLI output: Spectre.Console ANSI markup, human-readable
- External deps welcome if needed, each addition must be confirmed

## Key Domain Concepts

| Term | Meaning |
|------|---------|
| Session | Single AVD connection lifetime (connect → disconnect) |
| Lost time | Gaps from reconnects, errors, broker delays within session |
| Active time | Total session duration minus lost time |
| Report time | Estimated disruption cost: reconnect overhead + issue duration + recovery |

## Event Sources

Windows Event Log channels relevant to AVD:
- `Microsoft-Windows-TerminalServices-RDPClient/Operational` (RDP connect/disconnect)
- `System` (power events: sleep 42, resume 107, shutdown 1074/6006/6008)
- `Security` (workstation lock/unlock: 4800/4801; requires admin)

## Build & Run

```bash
dotnet build
dotnet run
dotnet run -- --start 2026-05-01 --end 2026-05-18
dotnet run -- --monitor
dotnet run -- --csv
```
