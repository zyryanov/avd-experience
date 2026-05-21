module AvdStats.IntegrationTests.FullFlowTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open AvdStats.Stats
open AvdStats.CsvExport
open AvdStats.IntegrationTests.Fixtures

// ── Scenario 1: Clean workday ─────────────────────────────────────────────────

[<Fact>]
let ``clean workday: connecting and active durations are exact`` () =
    let stats, _ = computeWithTrace None false None cleanWorkdayEnd cleanWorkdayEvents
    stats.TotalConnecting |> should equal (TimeSpan.FromMinutes 1.0)
    stats.TotalActive     |> should equal (TimeSpan.FromHours 7.0 + TimeSpan.FromMinutes 59.0)

[<Fact>]
let ``clean workday: zero issues`` () =
    let stats, _ = computeWithTrace None false None cleanWorkdayEnd cleanWorkdayEvents
    stats.TotalIssueCount |> should equal 0
    stats.TotalIssue      |> should equal TimeSpan.Zero

[<Fact>]
let ``clean workday: report time is 6 minutes (initial connecting grace)`` () =
    let stats, _ = computeWithTrace None false None cleanWorkdayEnd cleanWorkdayEvents
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 6.0)

[<Fact>]
let ``clean workday: single day in ByDay`` () =
    let stats, _ = computeWithTrace None false None cleanWorkdayEnd cleanWorkdayEvents
    stats.ByDay |> List.length |> should equal 1

// ── Scenario 2: Drop and reconnect ───────────────────────────────────────────

[<Fact>]
let ``drop and reconnect: exactly one issue`` () =
    let stats, _ = computeWithTrace None false None issueReconnectEnd issueReconnectEvents
    stats.TotalIssueCount |> should equal 1
    stats.TotalIssue      |> should equal (TimeSpan.FromMinutes 5.0)

[<Fact>]
let ``drop and reconnect: connecting time is sum of both intervals`` () =
    let stats, _ = computeWithTrace None false None issueReconnectEnd issueReconnectEvents
    stats.TotalConnecting |> should equal (TimeSpan.FromMinutes 3.0)

[<Fact>]
let ``drop and reconnect: report time is 28 minutes`` () =
    // C1(Initial,1min)=6 + Issue(5min)=5 + C2(PostIssue,2min)=2 + Active2(PostIssue,capped15)=15
    let stats, _ = computeWithTrace None false None issueReconnectEnd issueReconnectEvents
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 28.0)

// ── Scenario 3: Lock/unlock cycle ────────────────────────────────────────────

[<Fact>]
let ``lock unlock: zero issues`` () =
    let stats, _ = computeWithTrace None false None lockUnlockEnd lockUnlockEvents
    stats.TotalIssueCount |> should equal 0
    stats.TotalIssue      |> should equal TimeSpan.Zero

[<Fact>]
let ``lock unlock: paused time equals lock duration plus tail`` () =
    // Paused1: 12:00–12:30 (30min lock), Paused2: 17:00–18:00 (1h tail) = 90min total
    let stats, _ = computeWithTrace None false None lockUnlockEnd lockUnlockEvents
    stats.TotalPaused |> should equal (TimeSpan.FromMinutes 90.0)

[<Fact>]
let ``lock unlock: report time is 7 minutes`` () =
    // C1(Initial,1min)=6 + C2(PostPause,1min)=1
    let stats, _ = computeWithTrace None false None lockUnlockEnd lockUnlockEvents
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 7.0)

// ── Scenario 4: Multi-day ────────────────────────────────────────────────────

[<Fact>]
let ``multi-day: ByDay has entry for each day`` () =
    let stats, _ = computeWithTrace None false None (multiDayEnd()) (multiDayEvents())
    stats.ByDay |> List.length |> should equal 2

[<Fact>]
let ``multi-day: ByDay active sums equal period total`` () =
    let stats, _ = computeWithTrace None false None (multiDayEnd()) (multiDayEvents())
    let sumActive =
        stats.ByDay
        |> List.sumBy (fun d -> d.ActiveTime.TotalSeconds)
        |> TimeSpan.FromSeconds
    sumActive |> should equal stats.TotalActive

[<Fact>]
let ``multi-day: total active is 15h58min across two days`` () =
    let stats, _ = computeWithTrace None false None (multiDayEnd()) (multiDayEvents())
    stats.TotalActive |> should equal (TimeSpan.FromHours 15.0 + TimeSpan.FromMinutes 58.0)

// ── CSV export ────────────────────────────────────────────────────────────────

[<Fact>]
let ``writeEventsCsv: produces header and one row per event`` () =
    let _, trace = computeWithTrace None false None cleanWorkdayEnd cleanWorkdayEvents
    let path = Path.GetTempFileName()
    try
        writeEventsCsv path trace.EventTraces |> ignore
        let lines = File.ReadAllLines path
        lines.[0] |> should startWith "EventId"
        // 3 events in cleanWorkdayEvents
        lines.Length - 1 |> should equal 3
    finally
        File.Delete path

[<Fact>]
let ``writeIntervalsCsv: produces header and one row per interval slice`` () =
    let _, trace = computeWithTrace None false None cleanWorkdayEnd cleanWorkdayEvents
    let path = Path.GetTempFileName()
    try
        writeIntervalsCsv path trace.IntervalSlices trace.EventTraces
        let lines = File.ReadAllLines path
        lines.[0] |> should startWith "Kind"
        lines.Length - 1 |> should equal trace.IntervalSlices.Length
    finally
        File.Delete path

[<Fact>]
let ``writeEventsCsv: event rows contain correct event IDs`` () =
    let _, trace = computeWithTrace None false None cleanWorkdayEnd cleanWorkdayEvents
    let path = Path.GetTempFileName()
    try
        writeEventsCsv path trace.EventTraces |> ignore
        let lines = File.ReadAllLines path
        let ids = lines |> Array.skip 1 |> Array.map (fun l -> l.Split(',').[0])
        ids |> should equal [| "1024"; "1027"; "1026" |]
    finally
        File.Delete path
