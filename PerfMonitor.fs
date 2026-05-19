module AvdStats.PerfMonitor

open System
open System.Diagnostics

type PerfSample =
    { Time: DateTimeOffset
      CpuPct: float
      RamPct: float
      RamUsedMB: float
      DiskPct: float
      DiskReadBps: float
      DiskWriteBps: float
      NetSentBps: float
      NetRecvBps: float
      RttMs: float
      OutputFps: float
      EncodingTimeMs: float
      FrameQuality: float
      FramesSkippedSec: float
      LossRate: float }

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

/// Map a value in 0..peak to one sparkline block char (for non-% metrics).
let sparkBarScaled (peak: float) (value: float) : char =
    if peak <= 0.0 then bars.[0]
    else sparkBar (value / peak * 100.0)

/// Render a series of absolute values as a sparkline, auto-scaled to peak.
let sparklineScaled (values: float list) : string =
    let pk = match values with [] -> 0.0 | vs -> List.max vs
    values |> List.map (sparkBarScaled pk) |> List.toArray |> String

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

type RdpCounters =
    { TcpRtt: PerformanceCounter list
      UdpRtt: PerformanceCounter list
      OutputFps: PerformanceCounter list
      EncodingTime: PerformanceCounter list
      FrameQuality: PerformanceCounter list
      SkipServer: PerformanceCounter list
      SkipNetwork: PerformanceCounter list
      SkipClient: PerformanceCounter list
      LossRate: PerformanceCounter list }

type Counters =
    { Cpu: PerformanceCounter
      RamAvailMB: PerformanceCounter
      Disk: PerformanceCounter
      DiskRead: PerformanceCounter
      DiskWrite: PerformanceCounter
      NetSent: PerformanceCounter list
      NetRecv: PerformanceCounter list
      Rdp: RdpCounters option }

let private tryCreateRdpCounters () : RdpCounters option =
    try
        let netCat = new PerformanceCounterCategory("RemoteFX Network")
        let gfxCat = new PerformanceCounterCategory("RemoteFX Graphics")
        let netInst = netCat.GetInstanceNames()
        let gfxInst = gfxCat.GetInstanceNames()
        if netInst.Length = 0 && gfxInst.Length = 0 then None
        else
            let mkNet counter = netInst |> Array.map (fun n -> new PerformanceCounter("RemoteFX Network", counter, n)) |> Array.toList
            let mkGfx counter = gfxInst |> Array.map (fun n -> new PerformanceCounter("RemoteFX Graphics", counter, n)) |> Array.toList
            Some { TcpRtt       = mkNet "Current TCP RTT"
                   UdpRtt       = mkNet "Current UDP RTT"
                   OutputFps    = mkGfx "Output Frames/Second"
                   EncodingTime = mkGfx "Average Encoding Time"
                   FrameQuality = mkGfx "Frame Quality"
                   SkipServer   = mkGfx "Frames Skipped/Second - Insufficient Server Resources"
                   SkipNetwork  = mkGfx "Frames Skipped/Second - Insufficient Network Resources"
                   SkipClient   = mkGfx "Frames Skipped/Second - Insufficient Client Resources"
                   LossRate     = mkNet "Loss Rate" }
    with _ -> None

/// Build the whole-VM ("_Total") performance counters.
/// Error if the OS denies access (needs admin / Performance Monitor Users).
let createCounters () : Result<Counters, string> =
    try
        let netCat = new PerformanceCounterCategory("Network Interface")
        let netInstances = netCat.GetInstanceNames()
        let netSent = netInstances |> Array.map (fun n -> new PerformanceCounter("Network Interface", "Bytes Sent/sec", n)) |> Array.toList
        let netRecv = netInstances |> Array.map (fun n -> new PerformanceCounter("Network Interface", "Bytes Received/sec", n)) |> Array.toList
        Ok { Cpu        = new PerformanceCounter("Processor", "% Processor Time", "_Total")
             RamAvailMB = new PerformanceCounter("Memory", "Available MBytes")
             Disk       = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total")
             DiskRead   = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total")
             DiskWrite  = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total")
             NetSent    = netSent
             NetRecv    = netRecv
             Rdp        = tryCreateRdpCounters () }
    with ex -> Error ex.Message

let private readMax (pcs: PerformanceCounter list) =
    match pcs with
    | [] -> 0.0
    | _  -> pcs |> List.map (fun pc -> float (pc.NextValue())) |> List.max

let private readSum (pcs: PerformanceCounter list) =
    pcs |> List.sumBy (fun pc -> float (pc.NextValue()))

/// Take one sample. `totalRamMB` is the host's physical RAM (from SysInfo).
/// Note: counters are "_Total" — whole-VM, not this user's session.
let sample (c: Counters) (totalRamMB: float) : PerfSample =
    let cpu   = float (c.Cpu.NextValue())
    let avail = float (c.RamAvailMB.NextValue())
    let disk  = float (c.Disk.NextValue())
    let usedMB = max 0.0 (totalRamMB - avail)
    let diskRead  = float (c.DiskRead.NextValue())
    let diskWrite = float (c.DiskWrite.NextValue())
    let netSent = c.NetSent |> List.sumBy (fun pc -> float (pc.NextValue()))
    let netRecv = c.NetRecv |> List.sumBy (fun pc -> float (pc.NextValue()))
    let rtt, fps, enc, qual, skip, loss =
        match c.Rdp with
        | None -> 0.0, 0.0, 0.0, 0.0, 0.0, 0.0
        | Some r ->
            let udp = readMax r.UdpRtt
            let tcp = readMax r.TcpRtt
            let rtt = if udp > 0.0 then udp else tcp
            let fps = readSum r.OutputFps
            let enc = readMax r.EncodingTime
            let qual = readMax r.FrameQuality
            let skip = readSum r.SkipServer + readSum r.SkipNetwork + readSum r.SkipClient
            let loss = readMax r.LossRate
            rtt, fps, enc, qual, skip, loss
    { Time = DateTimeOffset.Now
      CpuPct = max 0.0 (min 100.0 cpu)
      RamPct = if totalRamMB > 0.0 then usedMB / totalRamMB * 100.0 else 0.0
      RamUsedMB = usedMB
      DiskPct = max 0.0 (min 100.0 disk)
      DiskReadBps = max 0.0 diskRead
      DiskWriteBps = max 0.0 diskWrite
      NetSentBps = max 0.0 netSent
      NetRecvBps = max 0.0 netRecv
      RttMs = max 0.0 rtt
      OutputFps = max 0.0 fps
      EncodingTimeMs = max 0.0 enc
      FrameQuality = max 0.0 (min 100.0 qual)
      FramesSkippedSec = max 0.0 skip
      LossRate = max 0.0 (min 100.0 loss) }

let private disposeList (pcs: PerformanceCounter list) =
    pcs |> List.iter (fun pc -> pc.Dispose())

let dispose (c: Counters) =
    c.Cpu.Dispose()
    c.RamAvailMB.Dispose()
    c.Disk.Dispose()
    c.DiskRead.Dispose()
    c.DiskWrite.Dispose()
    disposeList c.NetSent
    disposeList c.NetRecv
    c.Rdp |> Option.iter (fun r ->
        disposeList r.TcpRtt
        disposeList r.UdpRtt
        disposeList r.OutputFps
        disposeList r.EncodingTime
        disposeList r.FrameQuality
        disposeList r.SkipServer
        disposeList r.SkipNetwork
        disposeList r.SkipClient
        disposeList r.LossRate)
