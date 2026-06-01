module AvdStats.CsvExport

open System
open System.IO
open AvdStats.EventLog
open AvdStats.Events
open AvdStats.Stats
open AvdStats.SysInfo
open AvdStats.PerfMonitor

let private escape (s: string) =
    if s.Contains ',' || s.Contains '"' || s.Contains '\n' then
        sprintf "\"%s\"" (s.Replace("\"", "\"\""))
    else
        s

let private fmtKindName = function
    | Active -> "Active"
    | Connecting -> "Connecting"
    | Paused -> "Paused"
    | Issue -> "Issue"

let private fmtState (state: (IntervalKind * DateTimeOffset) option) =
    match state with
    | None -> ""
    | Some (kind, t) -> fmtKindName kind + "@" + t.ToString "o"

let private traceToCsvRow (t: EventTrace) =
    let e = t.Event
    let msg = e.Message |> Option.defaultValue ""
    let props = e.Properties |> String.concat "|"
    let closedKind, closedDuration =
        match t.ClosedInterval with
        | None -> "", ""
        | Some i -> fmtKindName i.Kind, (i.End - i.Start).ToString()
    let reportContrib =
        if t.ReportContribution > TimeSpan.Zero then t.ReportContribution.ToString() else ""
    [ string e.Id
      e.TimeCreated.ToString "o"
      e.Provider
      marker e.Id |> Option.defaultValue ""
      string t.IsRelevant
      fmtState t.StateBefore
      fmtState t.StateAfter
      closedKind
      closedDuration
      reportContrib
      msg
      props ]
    |> List.map escape
    |> String.concat ","

let writeEventsCsv (path: string) (traces: EventTrace list) : Set<int> =
    use writer = new StreamWriter(path, append = false, encoding = Text.Encoding.UTF8)
    writer.WriteLine "EventId,TimeCreated,Provider,Marker,IsRelevant,StateBefore,StateAfter,ClosedKind,ClosedDuration,ReportContribution,Message,Properties"
    (Set.empty, traces)
    ||> List.fold (fun unknowns t ->
        writer.WriteLine (traceToCsvRow t)
        let e = t.Event
        if marker e.Id |> Option.isNone && not (Set.contains e.Id knownNoMarker) then Set.add e.Id unknowns
        else unknowns)

let writeIntervalsCsv (path: string) (slices: IntervalSlice list) (traces: EventTrace list) : unit =
    let openerMap =
        traces
        |> List.choose (fun t ->
            match t.StateAfter with
            | Some (kind, start) -> Some ((kind, start), t.Event.Id)
            | None -> None)
        |> Map.ofList

    let closerMap =
        traces
        |> List.choose (fun t ->
            match t.ClosedInterval with
            | Some i -> Some ((i.Kind, i.Start), t.Event.Id)
            | None -> None)
        |> Map.ofList

    let lookupId map kind start =
        Map.tryFind (kind, start) map |> Option.map string |> Option.defaultValue ""

    use writer = new StreamWriter(path, append = false, encoding = Text.Encoding.UTF8)
    writer.WriteLine "Kind,IntervalStart,IntervalEnd,IntervalDuration,Date,DurationOnDate,OpenedByEventId,ClosedByEventId"
    for s in slices do
        let i = s.Interval
        let row =
            [ fmtKindName i.Kind
              i.Start.ToString "o"
              i.End.ToString "o"
              (i.End - i.Start).ToString()
              s.Date.ToString "yyyy-MM-dd"
              s.DurationOnDate.ToString()
              lookupId openerMap i.Kind i.Start
              lookupId closerMap i.Kind i.Start ]
            |> List.map escape
            |> String.concat ","
        writer.WriteLine row

let writeSpecsTxt (path: string) (spec: SystemSpec) : unit =
    use writer = new StreamWriter(path, append = false, encoding = Text.Encoding.UTF8)
    writer.WriteLine(sprintf "Machine,%s" spec.MachineName)
    writer.WriteLine(sprintf "OS,%s" spec.OsDescription)
    writer.WriteLine(sprintf "CPU,%s" spec.CpuModel)
    writer.WriteLine(sprintf "LogicalCPUs,%d" spec.LogicalCpus)
    writer.WriteLine(sprintf "TotalRAM,%s" (formatBytes (float spec.TotalRamBytes)))
    writer.WriteLine(sprintf "AvailableRAM,%s" (formatBytes (float spec.AvailRamBytes)))
    for d in spec.Disks do
        writer.WriteLine(sprintf "Disk %s,%s free of %s" d.Name (formatBytes (float d.FreeBytes)) (formatBytes (float d.TotalBytes)))
    writer.WriteLine(sprintf "Timestamp,%s" (DateTimeOffset.Now.ToString "o"))

let writePerfCsv (path: string) (samples: PerfSample list) : unit =
    use writer = new StreamWriter(path, append = false, encoding = Text.Encoding.UTF8)
    writer.WriteLine "Timestamp,CpuPct,RamPct,RamUsedMB,DiskPct,DiskReadBps,DiskWriteBps,NetSentBps,NetRecvBps,RttMs,OutputFps,EncodingTimeMs,FrameQuality,FramesSkippedSec,LossRate"
    for s in samples do
        writer.WriteLine(
            sprintf "%s,%.2f,%.2f,%.1f,%.2f,%.2f,%.2f,%.2f,%.2f,%.1f,%.2f,%.1f,%.1f,%.2f,%.2f"
                (s.Time.ToString "o") s.CpuPct s.RamPct s.RamUsedMB s.DiskPct
                s.DiskReadBps s.DiskWriteBps s.NetSentBps s.NetRecvBps
                s.RttMs s.OutputFps s.EncodingTimeMs s.FrameQuality s.FramesSkippedSec s.LossRate)
