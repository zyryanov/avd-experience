module AvdStats.PerfMonitor

open System
open System.Diagnostics

type PerfSample =
    { Time: DateTimeOffset
      CpuPct: float
      RamPct: float
      RamUsedMB: float
      DiskPct: float }

// ── pure helpers (unit-tested) ──────────────────────────────────────────────

let private bars = [| '▁'; '▂'; '▃'; '▄'; '▅'; '▆'; '▇'; '█' |]

/// Map a 0–100 percentage to one sparkline block char. Clamps out-of-range input.
let sparkBar (pct: float) : char =
    let clamped = max 0.0 (min 100.0 pct)
    let idx = int (clamped / 100.0 * float (bars.Length - 1) + 0.5)
    bars.[max 0 (min (bars.Length - 1) idx)]

/// Render a series of percentages as a sparkline string.
let sparkline (values: float list) : string =
    values |> List.map sparkBar |> List.toArray |> String

/// Append a sample, keeping at most `maxLen` most-recent entries.
let pushSample (maxLen: int) (buf: PerfSample list) (s: PerfSample) : PerfSample list =
    let appended = buf @ [ s ]
    let len = List.length appended
    if len > maxLen then appended |> List.skip (len - maxLen) else appended

/// Average of a field over samples. 0.0 for an empty list.
let average (field: PerfSample -> float) (samples: PerfSample list) : float =
    match samples with
    | [] -> 0.0
    | _  -> samples |> List.averageBy field

/// Peak (max) of a field over samples. 0.0 for an empty list.
let peak (field: PerfSample -> float) (samples: PerfSample list) : float =
    match samples with
    | [] -> 0.0
    | _  -> samples |> List.map field |> List.max

// ── counter I/O (thin, Windows-only, not unit-tested) ───────────────────────

type Counters =
    { Cpu: PerformanceCounter
      RamAvailMB: PerformanceCounter
      Disk: PerformanceCounter }

/// Build the whole-VM ("_Total") performance counters.
/// Error if the OS denies access (needs admin / Performance Monitor Users).
let createCounters () : Result<Counters, string> =
    try
        Ok { Cpu        = new PerformanceCounter("Processor", "% Processor Time", "_Total")
             RamAvailMB = new PerformanceCounter("Memory", "Available MBytes")
             Disk       = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total") }
    with ex -> Error ex.Message

/// Take one sample. `totalRamMB` is the host's physical RAM (from SysInfo).
/// Note: counters are "_Total" — whole-VM, not this user's session.
let sample (c: Counters) (totalRamMB: float) : PerfSample =
    let cpu   = float (c.Cpu.NextValue())
    let avail = float (c.RamAvailMB.NextValue())
    let disk  = float (c.Disk.NextValue())
    let usedMB = max 0.0 (totalRamMB - avail)
    { Time = DateTimeOffset.Now
      CpuPct = max 0.0 (min 100.0 cpu)
      RamPct = if totalRamMB > 0.0 then usedMB / totalRamMB * 100.0 else 0.0
      RamUsedMB = usedMB
      DiskPct = max 0.0 (min 100.0 disk) }

let dispose (c: Counters) =
    c.Cpu.Dispose()
    c.RamAvailMB.Dispose()
    c.Disk.Dispose()
