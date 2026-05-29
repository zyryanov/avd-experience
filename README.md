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
| `--perf` | `-p` | — | Live mode: sample whole-VM CPU/RAM/Disk/Network + RDP quality metrics and plot it; pair with `--csv` to dump samples |
| `--share` | `-x` | off | With `--perf`: auto-export metrics to `C:\avd-metrics\` every 30 s for remote SMB access |
| `--teams` | — | off | With `--perf`: POST periodic perf summary to Power Automate webhook (reads `TEAMS_WEBHOOK_URL` from `.env`) |
| `--teams-interval` | `-T` | 10 | Minutes between Teams summary posts |
| `--teams-to` | — | — | Embed recipient (email/UPN) in payload so the receiving flow can route to a 1:1 chat |

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

With `--perf --csv`: `avd-perf-<timestamp>.csv` — one row per 2 s sample with all
15 metric columns (see [Performance Metrics](#performance-metrics--perf) below).

## Performance Metrics (`--perf`)

Separate from the event-log analysis: these read the **host's own hardware and
performance counters**, so run them *inside the AVD session host*.

- `--specs` — one-shot hardware inventory: CPU, logical CPU count, total/available
  RAM, fixed disks.
- `--perf` — samples every 2 s and renders a live table with current / average /
  peak values and a sparkline trend. Ctrl+C to stop.

### Metrics collected

| Metric | Source | Unit |
|--------|--------|------|
| CPU | Processor / % Processor Time / _Total | % |
| RAM | Memory / Available MBytes | % |
| Disk | PhysicalDisk / % Disk Time / _Total | % |
| DiskRead | PhysicalDisk / Disk Read Bytes/sec / _Total | MB/s |
| DiskWrite | PhysicalDisk / Disk Write Bytes/sec / _Total | MB/s |
| NetSent | Network Interface / Bytes Sent/sec (all NICs) | MB/s |
| NetRecv | Network Interface / Bytes Received/sec (all NICs) | MB/s |
| RTT | RemoteFX Network / Current TCP or UDP RTT | ms |
| FPS | RemoteFX Graphics / Output Frames/Second | fps |
| Encoding | RemoteFX Graphics / Average Encoding Time | ms |
| Quality | RemoteFX Graphics / Frame Quality | % |
| Skipped | RemoteFX Graphics / Frames Skipped/Second (sum) | /s |
| Loss | RemoteFX Network / Loss Rate | % |

RDP metrics (RTT through Loss) appear only when running inside an RDP session.
They use per-session RemoteFX counters, not whole-VM totals.

> **Note:** System counters (CPU, RAM, Disk, Network) are Windows `_Total`
> instances — they measure the **whole VM** across all sessions. On a multi-session
> host the numbers reflect everyone on the box. Disk/CPU counters need admin or
> "Performance Monitor Users" membership (the tool already self-elevates via UAC).

### Remote access (`--share`)

```bash
dotnet run -- --perf --share
```

Writes metrics to `C:\avd-metrics\` for remote access via SMB admin share:

| File | Contents | Update frequency |
|------|----------|-----------------|
| `avd-specs.txt` | Hardware spec (CPU, RAM, disk, OS) | Once at start |
| `avd-perf-latest.csv` | Rolling window (last 60 samples) | Every 30 s |
| `avd-perf-full.csv` | Complete session history | On Ctrl+C exit |

Access from another machine:

```
\\<MACHINE>\C$\avd-metrics\avd-perf-latest.csv
```

### Teams notifications (`--teams`)

When SMB to the AVD host is blocked, push a periodic summary card to Teams via a Power
Automate webhook instead. Outbound HTTPS only — no firewall rule, no admin share.

**Setup:**

1. In Power Automate, create a flow with the **"When a Teams webhook request is received"** trigger.
2. In the trigger, set **"Who can trigger the flow?"** to **`Anyone`** — this generates a URL with a `sig=` token used for authentication. The other options (`Any user in my tenant`, `Specific users`) require AAD bearer tokens, which avd-experience does not send.
3. Open the trigger's *Settings* → *Use sample payload to generate schema*, paste the contents of [`trigger-schema-sample.json`](./trigger-schema-sample.json). This makes `triggerBody()?['summary']`, `triggerBody()?['recipient']`, etc. available as dynamic content downstream.
4. Add a **"Post adaptive card in a chat or channel"** action:
   - **Post as** = `Flow bot`
   - **Post in** = `Chat with Flow bot`
   - **Recipient** = expression `coalesce(triggerBody()?['recipient'], 'your.email@example.com')` (fallback when `--teams-to` not passed)
   - **Adaptive Card** = paste the contents of [`adaptive-card.json`](./adaptive-card.json)
5. Save the flow, copy the trigger's **HTTP URL** (now contains `&sig=...`).
6. Put it in `.env` next to the exe (or in the repo root for `dotnet run`):

   ```env
   TEAMS_WEBHOOK_URL=https://<env>.powerplatform.com/.../invoke?api-version=1&sp=...&sig=...
   ```

7. Run:

   ```bash
   dotnet run -- --perf --teams --teams-interval 10 --teams-to me@example.com
   ```

Every 10 minutes the tool POSTs a `MessageCard` with CPU / RAM / Disk / Net / RTT / FPS
avg-and-peak across the window. The live console table keeps updating in parallel.

**Card payload shape (relevant fields):**

```json
{
  "@type": "MessageCard",
  "@context": "https://schema.org/extensions",
  "summary": "AVD perf — UE2S1DWPP-1105",
  "themeColor": "#0078D7",
  "recipient": "me@example.com",
  "sections": [{ "activityTitle": "...", "text": "...", "facts": [ ... ] }]
}
```

**1:1 vs channel routing** — the avd-experience tool just POSTs the payload. Where the
message lands is decided by the receiving Power Automate flow. The setup above uses 1:1 DM
via Flow bot, driven by the payload's `recipient` field. To post to a channel instead, swap
the action's *Post in* to *Channel* and select the channel.

The Flow URL itself is the credential — `.env` is in `.gitignore`. Keep it that way.

**Corporate SSL inspection:** the HttpClient is configured with `DangerousAcceptAnyServerCertificateValidator`
(equivalent of Node's `NODE_TLS_REJECT_UNAUTHORIZED=0`), matching the prior `teams-webhook-mcp` setup.

## Architecture

F# modules in dependency order:

```
Elevation.fs     — UAC self-relaunch
EventLog.fs      — Windows Event Log I/O (XPath queries)
Events.fs        — Event ID classifiers and domain knowledge
Stats.fs         — State machine: raw events → typed intervals → DayStats/PeriodStats
SysInfo.fs       — Static hardware spec (CPU/RAM/disks)
PerfMonitor.fs   — Live CPU/RAM/Disk/Network + RemoteFX RDP perf-counter sampling
CsvExport.fs     — Write events/intervals/perf samples to CSV
Teams.fs         — MessageCard payload builder + .env loader + HttpClient POST to Power Automate webhook
Report.fs        — Format PeriodStats, spec table, and live usage for console
Program.fs       — CLI arg parsing (Argu), pipeline orchestration
```

Event sources: `TerminalServices-RDPClient/Operational`, `System` (power), `Security` (lock/unlock).

## Key Types

- `LogEvent` — raw event (Id, TimeCreated, Provider, Message, Properties)
- `IntervalKind` — `Active | Connecting | Paused | Issue`
- `Interval` — typed span with start/end timestamps
- `PeriodStats` — aggregated stats with per-day breakdown
