module AvdStats.MonitorWatcher

open System
open System.IO
open System.Threading
open Spectre.Console
open AvdStats.EventLog
open AvdStats.Stats
open AvdStats.Report
open AvdStats.ServiceManager
open AvdStats.DbRepository
open SqlHydra.Query

let private pollIntervalMs = 500

/// Canonical order key: time, then channel rank, then event id — must match
/// DbRepository's canonical event order.
let private keyOf (channel: string) (e: LogEvent) : DateTimeOffset * int * int =
    e.TimeCreated, channelRank channel, e.Id

/// The monitor's state definition matches the service's derivation exactly: fold
/// ALL stored events in canonical order (no wall-clock cutoff — canonical order is
/// by stored timestamp, so a clock-skewed future event simply sorts last).
/// Also returns the highest folded RecordId and the canonical key of the last
/// folded event:
///  - the RecordId cursor comes from the same read as the state, so an event
///    committed during the read is picked up by the next poll instead of being
///    skipped forever by a later max(RecordId) probe;
///  - the last key detects poll batches that arrive out of canonical order.
let private currentState (ctx: QueryContext)
    : (IntervalKind * DateTimeOffset) option * bool * (IntervalKind * DateTimeOffset) option * ConnectReason option * int64 * (DateTimeOffset * int * int) option =
    let rows = getAllEventRows ctx
    let state, locked, shadow, reason = rows |> List.map (fun (_, _, e) -> e) |> foldState None false None None
    let maxRecordId = rows |> List.fold (fun acc (rid, _, _) -> max acc rid) 0L
    let lastKey = rows |> List.tryLast |> Option.map (fun (_, ch, e) -> keyOf ch e)
    state, locked, shadow, reason, maxRecordId, lastKey

/// Poll avd.db for new state transitions and print them live.
let run () : int =
    let dbPath = defaultDbPath

    // Match the report path: with the service stopped the monitor would silently
    // watch a stale DB, so make sure it is installed and running first.
    if not (ensureServiceReady ()) then 1
    elif not (File.Exists dbPath) then
        AnsiConsole.MarkupLine "[red bold]✗ Database not found[/] — the service has not run yet. Run 'avd-experience service install' first."
        1
    else
        // Wait for the backlog replay so the derived initial state is not based on
        // partial history; live polling below is unaffected either way.
        if not (waitForBackfill dbPath backfillWaitTimeout) then
            AnsiConsole.MarkupLine "[yellow]⚠ The service is still backfilling event history — the initial state below may be incomplete; live transitions are unaffected.[/]"

        match tryRead dbPath currentState with
        | Error msg ->
            reportDbReadError dbPath msg
            1
        | Ok (initState, initLocked, initShadow, initReason, maxRecordId, initKey) ->
            AnsiConsole.MarkupLine(sprintf "[dim]Initial state:[/] %s" (stateMarkup initState))
            AnsiConsole.MarkupLine "[dim italic]Monitoring AVD events… (Ctrl+C to stop)[/]"

            let state = ref initState
            let wsLocked = ref initLocked
            let shadowState = ref initShadow
            let connectReason = ref initReason
            let lastSeenId = ref maxRecordId
            let lastKey = ref initKey

            let onEvent (e: LogEvent) =
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
                        ts e.Id (stateMarkup before) (stateMarkup after) durStr reportStr)

            let done_ = new ManualResetEventSlim false
            Console.CancelKeyPress.Add(fun e ->
                e.Cancel <- true
                done_.Set())

            // Re-derive the whole state from the DB (identical to the service's
            // derivation). Used when a poll batch arrives out of canonical order.
            let refold () =
                match tryRead dbPath currentState with
                | Error msg -> Error msg
                | Ok (st, lk, sh, rs, maxId, lastK) ->
                    state.Value <- st
                    wsLocked.Value <- lk
                    shadowState.Value <- sh
                    connectReason.Value <- rs
                    lastSeenId.Value <- max lastSeenId.Value maxId
                    lastKey.Value <- lastK
                    Ok ()

            // A mid-run read failure (DB deleted, permissions changed) ends the
            // monitor with a message instead of a stack trace.
            let rec poll () : int =
                if done_.IsSet then 0
                else
                    match tryRead dbPath (fun ctx -> getEventsAfterRecordId ctx lastSeenId.Value) with
                    | Error msg ->
                        reportDbReadError dbPath msg
                        1
                    | Ok records ->
                        // Rows arrive in RecordId (arrival) order; fold each poll batch
                        // in the canonical order. If a batch contains an event that
                        // canonically belongs BEFORE something already folded (cross-
                        // channel delivery skew), the incremental fold would diverge
                        // from the canonical order — re-derive the whole state instead.
                        let batch = records |> List.sortBy (fun (_, ch, e) -> keyOf ch e)
                        let disordered =
                            match lastKey.Value with
                            | Some k -> batch |> List.exists (fun (_, ch, e) -> keyOf ch e < k)
                            | None -> false
                        if disordered then
                            AnsiConsole.MarkupLine "[dim italic]Out-of-order event — state re-derived from full history.[/]"
                            match refold () with
                            | Error msg ->
                                reportDbReadError dbPath msg
                                1
                            | Ok () ->
                                if records.IsEmpty then Thread.Sleep pollIntervalMs
                                poll ()
                        else
                            for (rid, channel, e) in batch do
                                lastSeenId.Value <- max lastSeenId.Value rid
                                lastKey.Value <- Some (keyOf channel e)
                                onEvent e
                            if records.IsEmpty then Thread.Sleep pollIntervalMs
                            poll ()
            poll ()
