module AvdStats.IntegrationTests.ServiceFlowTests

// Verifies the Service↔CLI contract without touching live event logs or services:
// fixture scenarios are appended through the ingestion write path (the exact code
// the Windows service runs — append-only, arrival-order-independent), then read
// back through the CLI's DB read path and compared against in-memory computation.
// Derived tables (Intervals, StateSnapshot) are recomputed by `derive` from the
// stored events in canonical order — the tests below pin that order-independence.

open System
open Xunit
open FsUnit.Xunit
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open AvdStats.EventLog
open AvdStats.Stats
open AvdStats.DbRepository
open AvdStats.IntegrationTests.Fixtures
open AvdStats.Service.Ingestion
open AvdStats.Service.IngestionWorker
open AvdStats.Service.Retention

let private logger = NullLogger.Instance :> ILogger

/// Channel classification matching the service's subscriptions.
let private channelOf (e: LogEvent) =
    if e.Id = 4800 || e.Id = 4801 then securityChannel
    elif List.contains e.Id [ 42; 107; 1074; 6006; 6008 ] then systemChannel
    else rdpChannel

let private appendAll dbPath gate (events: LogEvent list) =
    events |> List.iter (fun e -> appendEvent dbPath gate logger (channelOf e) e)

/// The CLI read path: derive init state from pre-window history (continued from
/// the retention seed), then compute over the window.
let private computeViaCli dbPath (from: DateTimeOffset) (until: DateTimeOffset) =
    use ro = openReadContext dbPath
    let seed = getFoldSeed ro
    let initState, initLocked, initReason =
        getEventsBefore ro from
        |> foldState seed.State seed.Locked seed.Shadow seed.Reason
        |> (fun (st, lk, _, r) -> st, lk, r)
    let events = getEventsInRange ro from until
    computeWithTrace initState initLocked initReason until events |> fst

/// Push events through the service append path, derive the tables, read back via the
/// CLI path, and assert the stats are identical to a pure in-memory computation.
let private assertDbStatsMatchInMemory (periodEnd: DateTimeOffset) (events: LogEvent list) =
    let inMemory, _ = computeWithTrace None false None periodEnd events
    withTempDb <| fun dbPath ->
        let gate = obj()
        appendAll dbPath gate events
        derive dbPath gate logger |> ignore
        let from = (events |> List.minBy (fun e -> e.TimeCreated)).TimeCreated.AddMinutes -1.0
        let viaDb = computeViaCli dbPath from periodEnd
        viaDb |> should equal inMemory
    inMemory

/// All derived interval rows as plain tuples (the same shape derive compares).
let private intervalRows dbPath =
    use ro = openReadContext dbPath
    getAllIntervals ro
    |> List.map (fun i -> i.Kind, i.StartTime, i.EndTime, i.ConnectReason, i.DurationMs, i.ReportContribMs)

// ── Service→CLI contract ─────────────────────────────────────────────────────

[<Fact>]
let ``clean workday: db write path reproduces in-memory stats`` () =
    let stats = assertDbStatsMatchInMemory cleanWorkdayEnd cleanWorkdayEvents
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 6.0)
    stats.TotalActive |> should equal (TimeSpan.FromHours 7.0 + TimeSpan.FromMinutes 59.0)

[<Fact>]
let ``drop and reconnect: db write path reproduces in-memory stats`` () =
    let stats = assertDbStatsMatchInMemory issueReconnectEnd issueReconnectEvents
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 28.0)
    stats.TotalIssueCount |> should equal 1

[<Fact>]
let ``lock unlock: db write path reproduces in-memory stats`` () =
    let stats = assertDbStatsMatchInMemory lockUnlockEnd lockUnlockEvents
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 7.0)
    stats.TotalPaused |> should equal (TimeSpan.FromMinutes 90.0)

[<Fact>]
let ``multi-day: db write path reproduces in-memory stats`` () =
    assertDbStatsMatchInMemory (multiDayEnd()) (multiDayEvents())
    |> ignore

// ── Derivation: order-independence ───────────────────────────────────────────

[<Fact>]
let ``derive is order-independent: reversed ingestion yields identical intervals`` () =
    // The headline property of the append-only design: append order cannot affect
    // the derived tables — they are recomputed from stored events in canonical order.
    withTempDb <| fun forwardDb ->
        let gate = obj()
        appendAll forwardDb gate issueReconnectEvents
        derive forwardDb gate logger |> ignore
        let expected = intervalRows forwardDb
        (expected |> List.length) |> should equal 5        // C1, A1, Issue, C2, A2

        withTempDb <| fun reversedDb ->
            appendAll reversedDb (obj()) (issueReconnectEvents |> List.rev)
            derive reversedDb (obj()) logger |> ignore
            intervalRows reversedDb |> should equal expected

[<Fact>]
let ``derive recomputes intervals after late-arriving events`` () =
    // part1 is ingested and derived; part2 arrives afterwards (service restart,
    // delayed delivery) — derive folds the union in canonical order, and the result
    // matches a straight full-history run.
    let part1, part2 = issueReconnectEvents |> List.sortBy (fun e -> e.TimeCreated) |> List.splitAt 2
    withTempDb <| fun splitDb ->
        let gate = obj()
        appendAll splitDb gate part1
        derive splitDb gate logger |> ignore
        (intervalRows splitDb |> List.length) |> should equal 1    // only C1 closed so far

        appendAll splitDb gate part2
        derive splitDb gate logger |> ignore

        withTempDb <| fun wholeDb ->
            appendAll wholeDb (obj()) issueReconnectEvents
            derive wholeDb (obj()) logger |> ignore
            intervalRows splitDb |> should equal (intervalRows wholeDb)

[<Fact>]
let ``derive rebuilds the snapshot from stored events`` () =
    withTempDb <| fun dbPath ->
        let gate = obj()
        appendAll dbPath gate lockUnlockEvents

        // no derive yet: snapshot is still empty
        use before = openReadContext dbPath
        let st0, lk0, _, _ = loadSnapshot before
        st0 |> should equal None
        lk0 |> should equal false

        derive dbPath gate logger |> ignore
        use after = openReadContext dbPath
        let expectedState, expectedLocked, _, expectedReason = foldState None false None None lockUnlockEvents
        let st, lk, sh, rs = loadSnapshot after
        st |> should equal expectedState
        lk |> should equal expectedLocked
        sh |> should equal None
        rs |> should equal expectedReason

// ── Restart safety ───────────────────────────────────────────────────────────

[<Fact>]
let ``replay after restart picks up a same-timestamp sibling skipped before the restart`` () =
    // Two events in one channel share an exact timestamp. The "crash" happens after
    // the first is committed; the inclusive watermark replay must append the second
    // without duplicating the first.
    withTempDb <| fun dbPath ->
        let gate = obj()
        let t = DateTimeOffset(2026, 1, 17, 9, 0, 0, TimeSpan.Zero)
        let evA, evB = rdp 1024 t, rdp 1027 t
        appendEvent dbPath gate logger rdpChannel evA

        use ro = openReadContext dbPath
        countEvents ro |> should equal 1

        let st, _, _, _, _ = warmup (fun ch _ _ -> if ch = rdpChannel then [evA; evB] else []) dbPath gate logger

        use ro2 = openReadContext dbPath
        countEvents ro2 |> should equal 2
        st |> should equal (Some (Active, t))

[<Fact>]
let ``backward watermark re-reads stored events without duplicating`` () =
    // Simulate a watermark that moved backwards (e.g. NTP clock adjustment): replay
    // re-reads the entire already-stored history; the dedup guard must skip it all
    // and the derived tables must stay identical.
    withTempDb <| fun dbPath ->
        let gate = obj()
        appendAll dbPath gate issueReconnectEvents
        derive dbPath gate logger |> ignore
        let expectedRows = intervalRows dbPath
        (expectedRows |> List.length) |> should equal 5

        use ctx = openContext dbPath
        let early = DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        for ch in [ rdpChannel; systemChannel; securityChannel ] do
            upsertWatermark ctx ch early

        replayPending (fun ch _ _ -> issueReconnectEvents |> List.filter (fun e -> channelOf e = ch)) dbPath gate logger |> ignore
        derive dbPath gate logger |> ignore

        use after = openReadContext dbPath
        countEvents after |> should equal issueReconnectEvents.Length
        intervalRows dbPath |> should equal expectedRows

[<Fact>]
let ``derive returns the max RecordId of the snapshot it folded`` () =
    // The worker's derivation cursor comes from this value; it must equal the
    // table max right after a derive with no concurrent appends, so the periodic
    // tick cannot mark a mid-derive append as already derived.
    withTempDb <| fun dbPath ->
        let gate = obj()
        appendAll dbPath gate lockUnlockEvents
        let _, _, _, _, derivedId = derive dbPath gate logger
        use ro = openReadContext dbPath
        derivedId |> should equal (getMaxEventRecordId ro)

[<Fact>]
let ``restart with an unchanged event log does not duplicate data`` () =
    withTempDb <| fun dbPath ->
        let gate = obj()
        appendAll dbPath gate lockUnlockEvents
        derive dbPath gate logger |> ignore
        let expectedRows = intervalRows dbPath
        (expectedRows |> List.length) |> should equal 6

        // "Restart": each channel's log still contains its own events.
        warmup (fun ch _ _ -> lockUnlockEvents |> List.filter (fun e -> channelOf e = ch)) dbPath gate logger
        |> ignore

        use ro = openReadContext dbPath
        countEvents ro |> should equal lockUnlockEvents.Length
        intervalRows dbPath |> should equal expectedRows

// ── Warmup → live handover (subscribe first, buffer, replay, drain) ───────────

[<Fact>]
let ``LiveEventBuffer buffers until StopBuffering, then passes through`` () =
    let processed = ref []
    let buffer = new LiveEventBuffer(obj(), fun ch e -> processed.Value <- (ch, e) :: processed.Value)
    let t = DateTimeOffset(2026, 1, 17, 9, 0, 0, TimeSpan.Zero)

    buffer.Submit securityChannel (rdp 1024 t)
    buffer.Submit systemChannel   (rdp 1027 t)
    processed.Value |> List.length |> should equal 0       // still buffered

    buffer.StopBuffering()                                // drains in FIFO order
    processed.Value |> List.rev |> should equal [ (securityChannel, rdp 1024 t); (systemChannel, rdp 1027 t) ]

    buffer.Submit securityChannel (rdp 1026 t)             // direct pass-through now
    (processed.Value |> List.head |> fst) |> should equal securityChannel
    processed.Value |> List.length |> should equal 3

[<Fact>]
let ``warmup-to-live handover loses no events and duplicates none`` () =
    // The event log at replay time holds only the first 4 events; the live
    // subscription re-delivers those 4 (the overlap window) plus 2 events that
    // arrived after the replay's query window. All 6 must be appended exactly once.
    withTempDb <| fun dbPath ->
        let gate = obj()
        let sorted = issueReconnectEvents |> List.sortBy (fun e -> e.TimeCreated)
        let backlog, _liveOnly = sorted |> List.splitAt 4

        let source (ch: string) (_: DateTimeOffset) (_: DateTimeOffset) =
            backlog |> List.filter (fun e -> channelOf e = ch)

        let buffer = new LiveEventBuffer(gate, appendEvent dbPath gate logger)
        sorted |> List.iter (fun e -> buffer.Submit (channelOf e) e)

        replayPendingPasses source dbPath gate logger
        buffer.StopBuffering()
        derive dbPath gate logger |> ignore

        use ro = openReadContext dbPath
        countEvents ro |> should equal sorted.Length
        countIntervals ro |> should equal 5               // C1, A1, Issue, C2, A2

// ── DI registration regression ───────────────────────────────────────────────

[<Fact>]
let ``host resolves the ingestion worker as IHostedService through the factory`` () =
    // Regression: AddHostedService<IngestionWorker>() activates by constructor injection,
    // which cannot supply the dbPath string — the service crashed at startup.
    use host = AvdStats.Service.Hosting.buildHost [||] "unused.db"
    let worker = host.Services.GetRequiredService<IngestionWorker>()
    let hosted = host.Services.GetRequiredService<IHostedService>()
    System.Object.ReferenceEquals(worker, hosted) |> should equal true
// ── Retention: pruning keeps the fold exact ─────────────────────────────────

/// Day-1 session, 12:00 user disconnect; reconnect 14:30 the same day — pause
/// 2.5h < 3h, so the full history classifies the reconnect PostPause.
let private retentionEvents =
    let d1 = DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)
    [ rdp 1024 (d1.AddHours 9.0)
      rdp 1027 (d1.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
      userDisc (d1.AddHours 12.0)
      rdp 1024 (d1.AddHours 14.5)
      rdp 1027 (d1.AddHours 14.5 + TimeSpan.FromMinutes 1.0)
      userDisc (d1.AddHours 17.0) ]

let private retentionHorizon = DateTimeOffset(2026, 1, 15, 13, 0, 0, TimeSpan.Zero)   // between 12:00 and 14:30

[<Fact>]
let ``prune cuts at the newest event at/before the horizon and records the seed`` () =
    withTempDb <| fun dbPath ->
        let gate = obj()
        appendAll dbPath gate retentionEvents
        pruneWith retentionHorizon dbPath gate logger

        use ro = openReadContext dbPath
        // everything at/before the 12:00 disconnect is gone; day-2 events survive
        countEvents ro |> should equal 3
        getAllEvents ro |> List.map (fun e -> e.Id) |> should equal [ 1024; 1027; 1026 ]

        getRetentionStart ro |> should equal (Some (DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)))

        // the seed captured the machine state at the cutoff: the disconnect left an
        // open Paused interval (user-initiated → Initial reason so far)
        let seed = getFoldSeed ro
        seed.State |> should equal (Some (Paused, DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)))
        seed.Locked |> should equal false
        seed.Reason |> should equal (Some Initial)

[<Fact>]
let ``pruned db reproduces the unpruned db for post-cutoff windows`` () =
    // The invariant that makes retention safe: for a window starting after the
    // cutoff, the pruned DB's CLI read path yields identical stats to the
    // unpruned DB's — including the PostPause classification of the first
    // reconnect (a naive prune would restart from nothing, classify it Initial
    // and charge the 5-minute fresh-connect grace).
    withTempDb <| fun prunedDb ->
        withTempDb <| fun fullDb ->
            let gate = obj()
            appendAll prunedDb gate retentionEvents
            appendAll fullDb gate retentionEvents
            pruneWith retentionHorizon prunedDb gate logger
            derive fullDb gate logger |> ignore

            let from   = DateTimeOffset(2026, 1, 15, 14, 0, 0, TimeSpan.Zero)
            let until  = DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero)
            computeViaCli prunedDb from until |> should equal (computeViaCli fullDb from until)

            // and the derived Intervals keep the PostPause connect reason
            intervalRows prunedDb
            |> List.filter (fun (kind, _, _, _, _, _) -> kind = "Connecting")
            |> List.map (fun (_, _, _, reason, _, _) -> reason)
            |> should equal [ "PostPause" ]

[<Fact>]
let ``prune is idempotent and the append path drops pre-cutoff events`` () =
    withTempDb <| fun dbPath ->
        let gate = obj()
        appendAll dbPath gate retentionEvents
        pruneWith retentionHorizon dbPath gate logger
        use ro = openReadContext dbPath
        countEvents ro |> should equal 3

        // second run: cutoff unchanged, nothing left to delete
        pruneWith retentionHorizon dbPath gate logger
        use ro2 = openReadContext dbPath
        countEvents ro2 |> should equal 3

        // a re-arrived PRE-cutoff event (backlog replay after an outage) is dropped
        // by the append guard instead of folding backward on top of the seed
        appendAll dbPath gate [ rdp 1027 (DateTimeOffset(2026, 1, 10, 10, 0, 0, TimeSpan.Zero)) ]
        use ro3 = openReadContext dbPath
        countEvents ro3 |> should equal 3

        // …while an event between the cutoff and the horizon is stored, then pruned
        appendAll dbPath gate [ rdp 1027 (DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero)) ]
        use ro4 = openReadContext dbPath
        countEvents ro4 |> should equal 4
        pruneWith retentionHorizon dbPath gate logger
        use ro5 = openReadContext dbPath
        countEvents ro5 |> should equal 3
        // the seed advanced to the new cutoff: the 12:30 connect happened while
        // Paused → Active; the state (including its start) survives in the seed
        getFoldSeed ro5
        |> should equal { State = Some (Active, DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero))
                          Locked = false; Shadow = None; Reason = Some Initial }

[<Fact>]
let ``prune preserves the snapshot of an open session`` () =
    withTempDb <| fun prunedDb ->
        withTempDb <| fun fullDb ->
            let gate = obj()
            appendAll prunedDb gate retentionEvents
            appendAll fullDb gate retentionEvents
            pruneWith retentionHorizon prunedDb gate logger
            derive fullDb gate logger |> ignore

            use roPruned = openReadContext prunedDb
            use roFull   = openReadContext fullDb
            loadSnapshot roPruned |> should equal (loadSnapshot roFull)
