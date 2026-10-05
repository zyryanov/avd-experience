module AvdStats.IntegrationTests.DbFlowTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open AvdStats.EventLog
open AvdStats.Stats
open AvdStats.DbRepository
open AvdStats.IntegrationTests.Fixtures

let private t0 = DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours 2.0)

let private makeEvent (id: int) (t: DateTimeOffset) (props: string list) : LogEvent =
    { Id = id; TimeCreated = t; Provider = "TestProvider"; Message = Some "message"; Properties = props }

// ── Events ───────────────────────────────────────────────────────────────────

[<Fact>]
let ``saveEvent + getEventsInRange roundtrips events including properties`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveEvent ctx "Ch" (makeEvent 1024 (t0.AddMinutes 0.0) ["a"; "b"])
        saveEvent ctx "Ch" (makeEvent 1027 (t0.AddMinutes 1.0) [])
        saveEvent ctx "Ch" (makeEvent 1026 (t0.AddMinutes 2.0) ["x"; "1"])

        use ro = openReadContext dbPath
        let events = getEventsInRange ro (t0.AddMinutes -1.0) (t0.AddMinutes 2.5)
        events |> List.map (fun e -> e.Id) |> should equal [1024; 1027; 1026]
        events.Head.Properties |> should equal [ "a"; "b" ]
        events.Head.Message |> should equal (Some "message")
        events.Head.TimeCreated |> should equal t0

[<Fact>]
let ``eventExists matches only the exact channel, id and time`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveEvent ctx "Security" (makeEvent 4800 (t0.AddMinutes 1.0) [])

        eventExists ctx "Security" (makeEvent 4800 (t0.AddMinutes 1.0) []) |> should equal true
        eventExists ctx "Security" (makeEvent 4800 (t0.AddMinutes 2.0) []) |> should equal false   // different time
        eventExists ctx "System"   (makeEvent 4800 (t0.AddMinutes 1.0) []) |> should equal false   // different channel
        eventExists ctx "Security" (makeEvent 4801 (t0.AddMinutes 1.0) []) |> should equal false   // different id

[<Fact>]
let ``getEventsInRange is bounded by the window`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveEvent ctx "Ch" (makeEvent 1024 (t0.AddMinutes -10.0) [])
        saveEvent ctx "Ch" (makeEvent 1027 (t0.AddMinutes 0.0) [])
        saveEvent ctx "Ch" (makeEvent 1026 (t0.AddMinutes 10.0) [])

        use ro = openReadContext dbPath
        let events = getEventsInRange ro t0 t0
        events |> List.map (fun e -> e.Id) |> should equal [ 1027 ]

[<Fact>]
let ``getEventsBefore returns all events strictly before the instant`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveEvent ctx "Ch" (makeEvent 4800 (t0.AddMinutes -5.0) [])
        saveEvent ctx "Ch" (makeEvent 4801 (t0.AddMinutes 0.0) [])
        saveEvent ctx "Ch" (makeEvent 1024 (t0.AddMinutes 5.0) [])

        use ro = openReadContext dbPath
        let events = getEventsBefore ro t0
        events |> List.map (fun e -> e.Id) |> should equal [ 4800 ]

// ── Monitor polling ──────────────────────────────────────────────────────────

[<Fact>]
let ``canonical reads order equal-timestamp events by channel rank then id`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        let t = t0.AddMinutes 5.0
        // same instant, three channels; Security must sort before System before RDP
        saveEvent ctx rdpChannel      (makeEvent 1024 t [])
        saveEvent ctx securityChannel (makeEvent 4801 t [])
        saveEvent ctx systemChannel   (makeEvent 42   t [])
        saveEvent ctx rdpChannel      (makeEvent 1102 t [])   // same channel+time → by event id

        use ro = openReadContext dbPath
        getAllEvents ro |> List.map (fun e -> e.Id) |> should equal [ 4801; 42; 1024; 1102 ]

        getEventsInRange ro t0 (t.AddMinutes 1.0)
        |> List.map (fun e -> e.Id) |> should equal [ 4801; 42; 1024; 1102 ]

[<Fact>]
let ``getAllEventRows returns canonical order with record ids`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        let t = t0.AddMinutes 5.0
        saveEvent ctx rdpChannel      (makeEvent 1024 t [])
        saveEvent ctx securityChannel (makeEvent 4801 t [])
        saveEvent ctx rdpChannel      (makeEvent 1102 t [])   // same channel+time → by event id

        use ro = openReadContext dbPath
        // RecordIds follow insertion order; canonical order re-sorts by
        // (time, channel rank, event id) — independent of arrival order.
        getAllEventRows ro
        |> List.map (fun (rid, ch, e) -> rid, ch, e.Id)
        |> should equal [ (2L, securityChannel, 4801); (1L, rdpChannel, 1024); (3L, rdpChannel, 1102) ]

[<Fact>]
let ``getEventsAfterRecordId returns only newer rows in RecordId order`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveEvent ctx "Ch" (makeEvent 1024 (t0.AddMinutes 0.0) [])
        saveEvent ctx "Ch" (makeEvent 1027 (t0.AddMinutes 1.0) [])

        use ro = openReadContext dbPath
        let maxId = getMaxEventRecordId ro
        maxId |> should equal 2L

        let after = getEventsAfterRecordId ro maxId
        after |> List.length |> should equal 0

        saveEvent ctx "Ch" (makeEvent 1026 (t0.AddMinutes 2.0) [])
        // reopen context to see the new row
        use ro2 = openReadContext dbPath
        let newer = getEventsAfterRecordId ro2 maxId
        newer |> List.map (fun (_, _, e) -> e.Id) |> should equal [ 1026 ]

[<Fact>]
let ``getMaxEventRecordId is zero for an empty database`` () =
    withTempDb <| fun dbPath ->
        use ro = openReadContext dbPath
        getMaxEventRecordId ro |> should equal 0L

[<Fact>]
let ``tryRead maps infrastructure failures to Error instead of throwing`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveEvent ctx "Ch" (makeEvent 1024 t0 [])

        tryRead dbPath (fun ro -> countEvents ro)
        |> should equal (Ok 1 : Result<int, string>)

        // a missing database cannot be opened in read-only mode — Error, not an exception
        let missing = dbPath + ".missing.db"
        match tryRead missing (fun ro -> countEvents ro) with
        | Error _ -> true
        | Ok _ -> false
        |> should equal true

// ── Watermarks ───────────────────────────────────────────────────────────────

[<Fact>]
let ``upsertWatermark inserts then updates without duplicating rows`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        upsertWatermark ctx "Ch" t0
        upsertWatermark ctx "Ch" (t0.AddHours 5.0)
        upsertWatermark ctx "Other" t0

        use ro = openReadContext dbPath
        getWatermark ro "Ch" |> should equal (Some (t0.AddHours 5.0))
        getWatermark ro "Other" |> should equal (Some t0)
        getWatermark ro "Missing" |> should equal None

// ── State snapshot ───────────────────────────────────────────────────────────

[<Fact>]
let ``meta upsert, read and delete roundtrip`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        setMeta ctx "k" "v1"
        setMeta ctx "k" "v2"          // upsert, not a second row
        tryGetMeta ctx "k" |> should equal (Some "v2")
        tryGetMeta ctx "missing" |> should equal None

        deleteMeta ctx "k"
        tryGetMeta ctx "k" |> should equal None
        deleteMeta ctx "k"            // deleting a missing key is a no-op
        tryGetMeta ctx "k" |> should equal None

[<Fact>]
let ``saveSnapshot + loadSnapshot roundtrips the full state machine`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveSnapshot ctx (Some (Active, t0)) true (Some (Connecting, t0.AddMinutes 1.0)) (Some PostIssue)

        use ro = openReadContext dbPath
        let state, locked, shadow, reason = loadSnapshot ro
        state |> should equal (Some (Active, t0))
        locked |> should equal true
        shadow |> should equal (Some (Connecting, t0.AddMinutes 1.0))
        reason |> should equal (Some PostIssue)

[<Fact>]
let ``saveSnapshot overwrites the single row and loadSnapshot defaults to empty state`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveSnapshot ctx (Some (Active, t0)) true None (Some Initial)
        saveSnapshot ctx None false None None   // overwrite, not a second row

        use ro = openReadContext dbPath
        let state, locked, shadow, reason = loadSnapshot ro
        state |> should equal None
        locked |> should equal false
        shadow |> should equal None
        reason |> should equal None

// ── Intervals & counts ───────────────────────────────────────────────────────

[<Fact>]
let ``saveInterval + getIntervalsInRange returns intervals overlapping the window`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        let iv = { Kind = Connecting; Start = t0; End = t0.AddMinutes 1.0 }
        saveInterval ctx iv (Some Initial)
        let iv2 = { Kind = Active; Start = t0.AddDays 1.0; End = t0.AddDays 1.0 + TimeSpan.FromHours 2.0 }
        saveInterval ctx iv2 None

        use ro = openReadContext dbPath
        let found = getIntervalsInRange ro t0 t0
        found |> List.length |> should equal 1
        found.Head.Kind |> should equal "Connecting"
        found.Head.DurationMs |> should equal 60000L
        found.Head.ConnectReason |> should equal "Initial"
        // 1 min connecting + 5 min initial grace
        found.Head.ReportContribMs |> should equal 360000L

        // an interval closed with no connect reason stores "" — distinct from a
        // genuine "Initial" connect reason
        let activeRow = getIntervalsInRange ro (t0.AddDays 1.0) (t0.AddDays 1.0) |> List.head
        activeRow.ConnectReason |> should equal ""

        countEvents ro |> should equal 0
        countIntervals ro |> should equal 2

// ── Timestamp normalization & ordering ───────────────────────────────────────

[<Fact>]
let ``formatIso normalizes timestamps to UTC with a uniform offset suffix`` () =
    formatIso (DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours 2.0))
    |> should equal "2026-10-01T07:00:00.0000000+00:00"

[<Fact>]
let ``event reads are chronological regardless of insertion order`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveEvent ctx "Ch" (makeEvent 1026 (t0.AddMinutes 10.0) [])
        saveEvent ctx "Ch" (makeEvent 1027 (t0.AddMinutes 0.0) [])
        saveEvent ctx "Ch" (makeEvent 4800 (t0.AddMinutes 5.0) [])

        use ro = openReadContext dbPath
        getEventsInRange ro (t0.AddMinutes -1.0) (t0.AddMinutes 20.0)
        |> List.map (fun e -> e.Id)
        |> should equal [ 1027; 4800; 1026 ]

        getEventsBefore ro (t0.AddMinutes 6.0)
        |> List.map (fun e -> e.Id)
        |> should equal [ 1027; 4800 ]

[<Fact>]
let ``range queries compare by instant across different UTC offsets`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        // same wall-clock reading, different offsets: 10:00+02:00 = 08:00Z, 10:00+01:00 = 09:00Z
        let atPlus2 = DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours 2.0)
        let atPlus1 = DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours 1.0)
        saveEvent ctx "Ch" (makeEvent 1024 atPlus1 [])
        saveEvent ctx "Ch" (makeEvent 1027 atPlus2 [])

        use ro = openReadContext dbPath
        getEventsInRange ro atPlus2 atPlus1
        |> List.map (fun e -> e.Id)
        |> should equal [ 1027; 1024 ]

        getEventsInRange ro atPlus2 atPlus2
        |> List.map (fun e -> e.Id)
        |> should equal [ 1027 ]

// ── Retention seed ───────────────────────────────────────────────────────────

[<Fact>]
let ``retention seed roundtrips through ServiceMeta and defaults to empty`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        getFoldSeed ctx |> should equal emptySeed   // nothing pruned → fold from nothing

        let t = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
        let seed = { State = Some (Paused, t); Locked = true
                     Shadow = Some (Issue, t.AddMinutes 5.0); Reason = Some PostIssue }
        saveRetentionSeed ctx seed
        getFoldSeed ctx |> should equal seed

        // An all-empty seed roundtrips too (locked=false, no state/shadow/reason)
        saveRetentionSeed ctx emptySeed
        getFoldSeed ctx |> should equal emptySeed

[<Fact>]
let ``deleteEventsAtOrBefore and getLatestTimeAtOrBefore cut at whole timestamps`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        saveEvent ctx rdpChannel (makeEvent 1024 (t0.AddMinutes 0.0) [])
        saveEvent ctx rdpChannel (makeEvent 1027 (t0.AddMinutes 1.0) [])
        saveEvent ctx securityChannel (makeEvent 4800 (t0.AddMinutes 1.0) [])   // same timestamp as 1027
        saveEvent ctx rdpChannel (makeEvent 1024 (t0.AddMinutes 2.0) [])

        use ro = openReadContext dbPath
        getLatestTimeAtOrBefore ro (t0.AddMinutes 1.5) |> should equal (Some (t0.AddMinutes 1.0))
        getLatestTimeAtOrBefore ro (t0.AddMinutes -1.0) |> should equal None

        // boundary is inclusive: everything at/before the cutoff goes, including
        // the same-timestamp sibling
        use wc = openContext dbPath
        deleteEventsAtOrBefore wc (t0.AddMinutes 1.0) |> ignore

        use ro2 = openReadContext dbPath
        getEventsInRange ro2 (t0.AddMinutes -1.0) (t0.AddMinutes 3.0)
        |> List.map (fun e -> e.Id)
        |> should equal [ 1024 ]   // only the 2-min event survives

// ── Settings (retention-days) ────────────────────────────────────────────────

[<Fact>]
let ``retention-days setting roundtrips and falls back to the default`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        getRetentionDays ctx |> should equal retentionDays   // fresh DB → built-in default

        setRetentionDays ctx 30.0
        getRetentionDays ctx |> should equal 30.0

        // reset removes the override row entirely, back to the default
        resetRetentionDays ctx
        tryGetMeta ctx retentionDaysKey |> should equal None
        getRetentionDays ctx |> should equal retentionDays

[<Theory>]
[<InlineData("30", true)>]
[<InlineData("30.5", true)>]
[<InlineData("1", true)>]
[<InlineData("0.5", false)>]
[<InlineData("-3", false)>]
[<InlineData("abc", false)>]
[<InlineData("", false)>]
[<InlineData("NaN", false)>]
[<InlineData("Infinity", false)>]
let ``tryParseRetentionDays accepts finite days of at least 1`` (input: string) (valid: bool) =
    tryParseRetentionDays input |> Option.isSome |> should equal valid

[<Fact>]
let ``an invalid stored retention value falls back to the default`` () =
    withTempDb <| fun dbPath ->
        use ctx = openContext dbPath
        setMeta ctx retentionDaysKey "abc"
        getRetentionDays ctx |> should equal retentionDays
        setMeta ctx retentionDaysKey "0.5"
        getRetentionDays ctx |> should equal retentionDays
        setMeta ctx retentionDaysKey "Infinity"
        getRetentionDays ctx |> should equal retentionDays
