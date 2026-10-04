open System
open System.IO
open Argu
open Spectre.Console
open SqlHydra.Query
open AvdStats.Elevation
open AvdStats.CsvExport
open AvdStats.Stats
open AvdStats.Report
open AvdStats.DateParser
open AvdStats.ServiceManager
open AvdStats.MonitorWatcher
open AvdStats.DbRepository

type Args =
    | [<AltCommandLine("-s", "--from")>] Start of string
    | [<AltCommandLine("-t", "--to")>]   End of string
    | [<AltCommandLine("-m")>] Monitor
    | [<AltCommandLine("-c")>] Csv
    // CliPrefix.None makes this a bare subcommand ('service status'), so it shows
    // up in --help next to the flags and is parsed position-independently.
    | [<CliPrefix(CliPrefix.None); Unique>] Service of string
    interface IArgParserTemplate with
        member x.Usage =
            match x with
            | Start _ -> "start date, inclusive (yyyy-MM-dd or relative: today, yesterday, -1d, -2w); alias --from / -s. Default: start of today."
            | End _   -> "end date, inclusive (yyyy-MM-dd or relative: today, yesterday, -1d, -2w); alias --to / -t. Default: end of today."
            | Monitor -> "poll avd.db for live AVD state changes; prints each transition with timestamp and duration"
            | Csv     -> "export events and intervals to CSV files (off by default)"
            | Service _ -> "manage the background AVD service: status | start | stop | install | uninstall (e.g. 'avd-experience service status')"

let private fmt (d: DateTimeOffset) = d.ToString "yyyy-MM-dd"

let private nextAvailablePath (baseName: string) (ext: string) =
    let candidate n =
        if n = 0 then sprintf "%s%s" baseName ext
        else sprintf "%s-%d%s" baseName n ext
    Seq.initInfinite id |> Seq.map candidate |> Seq.find (not << File.Exists)

/// Fold the state machine over all events stored before `at` to derive the state
/// at the start of a report window.
let private deriveStateAt (ctx: QueryContext) (at: DateTimeOffset) : (IntervalKind * DateTimeOffset) option * bool * ConnectReason option =
    getEventsBefore ctx at
    |> foldState None false None None
    |> (fun (st, lk, _, reason) -> st, lk, reason)

let private run (from: DateTimeOffset) (until: DateTimeOffset) (writeCsv: bool) : int =
    let dbPath = defaultDbPath

    if not (ensureServiceReady ()) then 1
    elif not (File.Exists dbPath) then
        AnsiConsole.MarkupLine "[red bold]✗ Database not found[/] — the service may still be starting up. Try again in a moment."
        1
    else
        // Wait for the service to finish backfilling history so the report is not
        // silently partial (fresh install / just-started service). On timeout,
        // warn and report anyway.
        if not (waitForBackfill dbPath backfillWaitTimeout) then
            AnsiConsole.MarkupLine "[yellow]⚠ The service is still backfilling event history — the report below may be incomplete. Re-run shortly for full data.[/]"

        let baseName = sprintf "avd-events-%s--%s" (fmt from) (fmt until)

        // One read context for the window, its pre-history, and the backlog horizon;
        // infrastructure failures (e.g. missing ACL grant after a manual install)
        // surface as a friendly message instead of a crash.
        match tryRead dbPath (fun ctx ->
            let events = getEventsInRange ctx from until
            let initState, initLocked, initReason = deriveStateAt ctx from
            let backfillStart = getBackfillStart ctx
            events, initState, initLocked, initReason, backfillStart) with
        | Error msg ->
            reportDbReadError dbPath msg
            1
        | Ok (events, initState, initLocked, initReason, backfillStart) ->
            // The service only ingests a bounded backlog on first run; a window that
            // starts before that horizon cannot be complete — say so explicitly
            // instead of silently printing a partial report.
            (match backfillStart with
             | Some horizon when from < horizon ->
                 AnsiConsole.MarkupLine(sprintf
                     "[yellow]⚠ No events before %s were ingested (the service's initial backfill reaches ~%d days back) — the report below may be incomplete.[/]"
                     (Markup.Escape (horizon.LocalDateTime.ToString "yyyy-MM-dd HH:mm")) (int initialBacklogDays))
             | _ -> ())
            AnsiConsole.MarkupLine(sprintf "[dim]Found %d events[/] [grey](%s → %s)[/]" events.Length (fmt from) (fmt until))
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

/// Elevated (or non-elevating) execution of a service command.
let private performServiceCommand (verb: string) : int =
    match verb with
    | "start" ->
        (match tryStart () with
         | Ok () -> AnsiConsole.MarkupLine "[green]✓ AVD Service started.[/]"; 0
         | Error e -> AnsiConsole.MarkupLine(sprintf "[red]✗ Start failed:[/] %s" (Markup.Escape e)); 1)
    | "stop" ->
        (match tryStop () with
         | Ok () -> AnsiConsole.MarkupLine "[green]✓ AVD Service stopped.[/]"; 0
         | Error e -> AnsiConsole.MarkupLine(sprintf "[red]✗ Stop failed:[/] %s" (Markup.Escape e)); 1)
    | "install" ->
        (match install () with
         | Ok () -> AnsiConsole.MarkupLine "[green]✓ AVD Service installed and started.[/]"; 0
         | Error e -> AnsiConsole.MarkupLine(sprintf "[red]✗ Install failed:[/] %s" (Markup.Escape e)); 1)
    | "uninstall" ->
        (match uninstall () with
         | Ok () -> AnsiConsole.MarkupLine "[green]✓ AVD Service uninstalled.[/]"; 0
         | Error e -> AnsiConsole.MarkupLine(sprintf "[red]✗ Uninstall failed:[/] %s" (Markup.Escape e)); 1)
    | _ -> 1

let private runServiceCommand (verb: string) : int =
    match verb with
    | "status" ->
        (match getStatus () with
         | NotInstalled -> AnsiConsole.MarkupLine "[yellow]AVD Service is not installed.[/]"
         | Stopped      -> AnsiConsole.MarkupLine "[yellow]AVD Service is stopped.[/]"
         | StartPending -> AnsiConsole.MarkupLine "[yellow]AVD Service is starting…[/]"
         | StopPending  -> AnsiConsole.MarkupLine "[yellow]AVD Service is stopping…[/]"
         | Running      -> AnsiConsole.MarkupLine "[green]AVD Service is running.[/]"
         | Other s      -> AnsiConsole.MarkupLine(sprintf "[yellow]AVD Service state:[/] %s" (Markup.Escape s)))
        if File.Exists defaultDbPath then
            match tryRead defaultDbPath (fun ctx ->
                countEvents ctx, countIntervals ctx,
                tryGetMeta ctx backfillCompleteKey |> Option.isSome) with
            | Ok (events, intervals, complete) ->
                AnsiConsole.MarkupLine(sprintf "[dim]DB:[/] %d events, %d intervals" events intervals)
                if complete then
                    AnsiConsole.MarkupLine "[dim]Backfill:[/] complete"
                else
                    AnsiConsole.MarkupLine "[yellow]Backfill in progress — DB counts are partial…[/]"
            | Error msg ->
                reportDbReadError defaultDbPath msg
        0
    | "start" ->
        match tryStart () with
        | Ok () -> AnsiConsole.MarkupLine "[green]✓ AVD Service started.[/]"; 0
        | Error _ when not (isElevated ()) ->
            AnsiConsole.MarkupLine "[yellow]Starting the service requires elevation…[/]"
            match relaunchElevated [| "service"; verb |] with
            | Some code -> code
            | None -> AnsiConsole.MarkupLine "[yellow]⚠ Elevation declined[/]"; 1
        | Error e -> AnsiConsole.MarkupLine(sprintf "[red]✗ Start failed:[/] %s" (Markup.Escape e)); 1
    | "stop" | "install" | "uninstall" ->
        if isElevated () then performServiceCommand verb
        else
            AnsiConsole.MarkupLine(sprintf "[yellow]Elevating for 'service %s'…[/]" (Markup.Escape verb))
            match relaunchElevated [| "service"; verb |] with
            | Some code -> code
            | None -> AnsiConsole.MarkupLine "[yellow]⚠ Elevation declined[/]"; 1
    | v ->
        AnsiConsole.MarkupLine(sprintf "[red]Unknown service command:[/] %s [grey](expected: status | start | stop | install | uninstall)[/]" (Markup.Escape v))
        1

let private runParsed (argv: string[]) =
    let parser = ArgumentParser.Create<Args>(programName = "avd-experience")
    let parsed =
        try Ok (parser.ParseCommandLine argv)
        with :? ArguParseException as ex ->
            AnsiConsole.MarkupLine(sprintf "[red]%s[/]" (Markup.Escape ex.Message))
            Error 1
    match parsed with
    | Error code -> code
    | Ok args when args.Contains Service -> runServiceCommand (args.GetResult Service)
    | Ok args when args.Contains Monitor -> AvdStats.MonitorWatcher.run ()
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
    runParsed (tryRedirectOutput argv)
