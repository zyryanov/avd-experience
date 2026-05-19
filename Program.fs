open System
open System.IO
open Argu
open Spectre.Console
open AvdStats.Elevation
open AvdStats.EventLog
open AvdStats.Events
open AvdStats.CsvExport
open AvdStats.Stats
open AvdStats.SysInfo
open AvdStats.PerfMonitor
open AvdStats.Report

type Args =
    | [<AltCommandLine("-s", "--from")>] Start of string
    | [<AltCommandLine("-t", "--to")>]   End of string
    | [<AltCommandLine("-m")>] Monitor
    | [<AltCommandLine("-c")>] Csv
    | [<AltCommandLine("-i")>] Specs
    | [<AltCommandLine("-p")>] Perf
    | [<AltCommandLine("-x")>] Share
    interface IArgParserTemplate with
        member x.Usage =
            match x with
            | Start _ -> "start date, inclusive (yyyy-MM-dd); alias --from / -s. Default: start of today."
            | End _   -> "end date, inclusive (yyyy-MM-dd); alias --to / -t. Default: end of today."
            | Monitor -> "watch live event log for AVD state changes; prints each transition with timestamp and duration"
            | Csv     -> "export to CSV files (events+intervals, or perf samples with --perf); off by default"
            | Specs   -> "print the host hardware spec (CPU, RAM, disks) and exit"
            | Perf    -> "live mode: sample whole-VM CPU/RAM/Disk usage and plot it; pair with --csv to also dump samples"
            | Share   -> "with --perf: auto-export metrics to C:\\avd-metrics\\ every 30s for remote access via SMB admin share"

let private fmt (d: DateTimeOffset) = d.ToString "yyyy-MM-dd"

let private localMidnight (d: DateTime) =
    let offset = TimeZoneInfo.Local.GetUtcOffset(d)
    DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, offset)

let private localEndOfDay (d: DateTime) =
    localMidnight(d.AddDays 1.0).AddTicks -1L

let parseDate (s: string) =
    match DateTime.TryParseExact(s, "yyyy-MM-dd", null, Globalization.DateTimeStyles.None) with
    | true, d  -> Ok d
    | false, _ -> Error (sprintf "Invalid date '%s' — expected yyyy-MM-dd" s)

let channel = "Microsoft-Windows-TerminalServices-RDPClient/Operational"

let private nextAvailablePath (baseName: string) (ext: string) =
    let candidate n =
        if n = 0 then sprintf "%s%s" baseName ext
        else sprintf "%s-%d%s" baseName n ext
    Seq.initInfinite id |> Seq.map candidate |> Seq.find (not << File.Exists)



let private softQuery label result =
    match result with
    | Ok evs -> evs
    | Error e ->
        AnsiConsole.MarkupLine(sprintf "[yellow]⚠ Warning:[/] could not read %s events ([dim]%s[/])" (Markup.Escape label) (Markup.Escape (sprintf "%A" e)))
        []

let private deriveStateAt (at: DateTimeOffset) : (IntervalKind * DateTimeOffset) option * bool * ConnectReason option =
    let from = at.AddHours(-24.0)
    let rdp  = match queryByTimeRange channel [] from at with Ok evs -> evs | Error _ -> []
    let sys  = match querySystemPowerEvents from at       with Ok evs -> evs | Error _ -> []
    let sec  = match queryLockEvents from at              with Ok evs -> evs | Error _ -> []
    let events = rdp @ sys @ sec |> List.sortBy (fun e -> e.TimeCreated)
    events |> List.fold
        (fun (st, lk, shadow, reason) e ->
            let st', lk', shadow', closed = advanceState st lk shadow e
            let reason' = nextConnectReason st st' reason closed
            st', lk', shadow', reason')
        (None, false, None, None)
    |> (fun (st, lk, _, reason) -> st, lk, reason)

let private runMonitor () : int =
    let lockObj = obj()
    let initState, initLocked, _ = deriveStateAt DateTimeOffset.Now
    let state = ref initState
    let wsLocked = ref initLocked
    let shadowState : (IntervalKind * DateTimeOffset) option ref = ref (if initLocked then initState else None)
    let connectReason : ConnectReason option ref = ref None
    AnsiConsole.MarkupLine(sprintf "[dim]Initial state:[/] %s" (stateMarkup initState))
    let done_ = new Threading.ManualResetEventSlim false

    let onEvent (e: LogEvent) =
        lock lockObj (fun () ->
            let before = state.Value
            let after, lk', shadow', closed = advanceState before wsLocked.Value shadowState.Value e
            state.Value <- after
            wsLocked.Value <- lk'
            shadowState.Value <- shadow'
            let contrib =
                match closed with
                | Some iv -> intervalReportContrib iv.Kind connectReason.Value (iv.End - iv.Start)
                | None    -> TimeSpan.Zero
            connectReason.Value <- nextConnectReason before after connectReason.Value closed
            if after <> before then
                let ts = e.TimeCreated.LocalDateTime.ToString("HH:mm:ss")
                let durStr =
                    match closed with
                    | Some iv -> sprintf "  [dim](%s: %s)[/]" (formatKind (Some (iv.Kind, iv.Start))) (formatDuration (iv.End - iv.Start))
                    | None -> ""
                let reportStr =
                    if contrib > TimeSpan.Zero
                    then sprintf "  [magenta]+%s[/]" (formatDuration contrib)
                    else ""
                AnsiConsole.MarkupLine(sprintf "[grey]%s[/] [dim]#%-4d[/]  %s [grey]→[/] %s%s%s"
                    ts e.Id (stateMarkup before) (stateMarkup after) durStr reportStr))

    let onError (ex: exn) =
        AnsiConsole.MarkupLine(sprintf "[yellow]⚠ Warning:[/] subscription error — %s" (Markup.Escape ex.Message))

    use _rdp = subscribeChannel channel "*" onEvent onError
    use _sys = subscribeChannel "System"   (makeIdXPath [42; 107; 1074; 6006; 6008]) onEvent onError
    use _sec = subscribeChannel "Security" (makeIdXPath [4800; 4801]) onEvent onError
    AnsiConsole.MarkupLine "[dim italic]Monitoring AVD events… (Ctrl+C to stop)[/]"
    Console.CancelKeyPress.Add(fun e ->
        e.Cancel <- true
        done_.Set())
    done_.Wait()
    0

let private run (from: DateTimeOffset) (until: DateTimeOffset) (writeCsv: bool) : int =
    let baseName = sprintf "avd-events-%s--%s" (fmt from) (fmt until)

    match queryByTimeRange channel [] from until with
    | Error AccessDenied ->
        AnsiConsole.MarkupLine "[red bold]✗ Access denied[/] — run as administrator"
        1
    | Error (ChannelNotFound ch) ->
        AnsiConsole.MarkupLine(sprintf "[red]Channel not found:[/] %s" (Markup.Escape ch))
        1
    | Error (QueryFailed msg) ->
        AnsiConsole.MarkupLine(sprintf "[red]Query failed:[/] %s" (Markup.Escape msg))
        1
    | Ok rdpEvents ->
        let events =
            rdpEvents
            @ softQuery "System power" (querySystemPowerEvents from until)
            @ softQuery "Security lock" (queryLockEvents from until)
            |> List.sortBy (fun e -> e.TimeCreated)
        AnsiConsole.MarkupLine(sprintf "[dim]Found %d events[/] [grey](%s → %s)[/]" events.Length (fmt from) (fmt until))
        let initState, initLocked, initReason = deriveStateAt from
        let initStateClamped = initState |> Option.map (fun (kind, t) -> kind, max t from)
        let stats, trace = computeWithTrace initStateClamped initLocked initReason until events
        if writeCsv then
            let eventsPath = nextAvailablePath baseName ".csv"
            let unknownIds = writeEventsCsv eventsPath trace.EventTraces
            AnsiConsole.MarkupLine(sprintf "[green]✓ Saved to[/] %s" (Markup.Escape eventsPath))
            if not unknownIds.IsEmpty then
                AnsiConsole.MarkupLine(sprintf "[yellow]⚠ Warning:[/] %d unknown event ID(s) in export (no marker): %s"
                    unknownIds.Count
                    (unknownIds |> Seq.map string |> String.concat ", "))
            let intervalsPath = nextAvailablePath baseName "-intervals.csv"
            writeIntervalsCsv intervalsPath trace.IntervalSlices trace.EventTraces
            AnsiConsole.MarkupLine(sprintf "[green]✓ Intervals CSV:[/] %s" (Markup.Escape intervalsPath))
        printTrace trace.EventTraces
        printStats stats
        0

let private perfIntervalMs = 2000
let private perfWindow = 60

let private runSpecs () : int =
    printSpecs (collect ())
    0

let private shareDir = @"C:\avd-metrics"
let private shareFlushInterval = 15

let private runPerf (writeCsv: bool) (share: bool) : int =
    let spec = collect ()
    printSpecs spec
    if share then
        Directory.CreateDirectory shareDir |> ignore
        writeSpecsTxt (Path.Combine(shareDir, "avd-specs.txt")) spec
        let unc = sprintf @"\\%s\C$\avd-metrics\" spec.MachineName
        AnsiConsole.MarkupLine(sprintf "[green]✓ Share:[/] %s" (Markup.Escape shareDir))
        AnsiConsole.MarkupLine(sprintf "[dim]Remote:[/] %s" (Markup.Escape unc))
    match createCounters () with
    | Error msg ->
        AnsiConsole.MarkupLine(sprintf "[red bold]✗ Cannot read performance counters:[/] %s" (Markup.Escape msg))
        AnsiConsole.MarkupLine "[dim]Disk/CPU counters need admin or 'Performance Monitor Users' membership.[/]"
        1
    | Ok counters ->
        let totalRamMB = float spec.TotalRamBytes / 1048576.0
        let all = ResizeArray<PerfSample>()
        let mutable tick = 0
        use stop = new Threading.ManualResetEventSlim(false)
        Console.CancelKeyPress.Add(fun e ->
            e.Cancel <- true
            stop.Set())
        sample counters totalRamMB |> ignore
        AnsiConsole.MarkupLine "[dim italic]Sampling every 2s… (Ctrl+C to stop)[/]"
        AnsiConsole.Live(perfRenderable []).Start(fun ctx ->
            while not stop.IsSet do
                let s = sample counters totalRamMB
                all.Add s
                tick <- tick + 1
                let window =
                    let n = all.Count
                    if n > perfWindow then List.ofSeq (Seq.skip (n - perfWindow) all)
                    else List.ofSeq all
                ctx.UpdateTarget(perfRenderable window)
                ctx.Refresh()
                if share && tick % shareFlushInterval = 0 then
                    writePerfCsv (Path.Combine(shareDir, "avd-perf-latest.csv")) window
                stop.Wait perfIntervalMs |> ignore)
        dispose counters
        if share && all.Count > 0 then
            writePerfCsv (Path.Combine(shareDir, "avd-perf-full.csv")) (List.ofSeq all)
            AnsiConsole.MarkupLine(sprintf "[green]✓ Full export:[/] %s" (Markup.Escape (Path.Combine(shareDir, "avd-perf-full.csv"))))
        if writeCsv && all.Count > 0 then
            let path = nextAvailablePath (sprintf "avd-perf-%s" (DateTime.Now.ToString "yyyyMMdd-HHmmss")) ".csv"
            writePerfCsv path (List.ofSeq all)
            AnsiConsole.MarkupLine(sprintf "[green]✓ Perf CSV:[/] %s" (Markup.Escape path))
        0

let private runParsed (argv: string[]) =
    let parser = ArgumentParser.Create<Args>(programName = "avd-experience")
    let parsed =
        try Ok (parser.ParseCommandLine argv)
        with :? ArguParseException as ex ->
            AnsiConsole.MarkupLine(sprintf "[red]%s[/]" (Markup.Escape ex.Message))
            Error 1
    match parsed with
    | Error code -> code
    | Ok args when args.Contains Monitor -> runMonitor ()
    | Ok args when args.Contains Specs -> runSpecs ()
    | Ok args when args.Contains Perf -> runPerf (args.Contains Csv) (args.Contains Share)
    | Ok args ->
        let from =
            match args.TryGetResult Start with
            | Some s -> parseDate s |> Result.map localMidnight
            | None   -> Ok (localMidnight DateTime.Today)
        let until =
            match args.TryGetResult End with
            | Some s -> parseDate s |> Result.map localEndOfDay
            | None   -> Ok (localEndOfDay DateTime.Today)
        match from, until with
        | Error msg, _ | _, Error msg ->
            AnsiConsole.MarkupLine(sprintf "[red]Error:[/] %s" (Markup.Escape msg))
            1
        | Ok from, Ok until -> run from until (args.Contains Csv)

[<EntryPoint>]
let main argv =
    Console.OutputEncoding <- Text.Encoding.UTF8
    if isElevated () then
        let cleanArgv = tryRedirectOutput argv
        runParsed cleanArgv
    else
        AnsiConsole.MarkupLine "[yellow]Requesting elevation to read Security lock events…[/]"
        match relaunchElevated argv with
        | Some code -> code
        | None ->
            AnsiConsole.MarkupLine "[yellow]⚠ Elevation declined[/] — Security lock events will be skipped, stats will be incorrect."
            runParsed argv
