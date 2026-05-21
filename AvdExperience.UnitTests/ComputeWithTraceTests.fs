module AvdStats.UnitTests.ComputeWithTraceTests

open System
open Xunit
open FsUnit.Xunit
open AvdStats.UnitTests.TestHelpers
open AvdStats.Stats

let connectEvent t   = rdpEventAt 1024 t
let connectedEvent t = rdpEventAt 1027 t
let disconnEvent t   = rdpEventAt 1026 t
let userDiscEvent t  = makeEventAt 1026 "" ["x"; "1"] t

// t0 = 2025-05-01 10:00 UTC; pd = 14:00 UTC — both in the past
// → effectiveEnd = min pd DateTimeOffset.Now = pd (deterministic)
let pd = t0.AddHours 4.0

[<Fact>]
let ``empty events returns zero stats and empty trace`` () =
    let stats, trace = computeWithTrace None false None pd []
    stats.TotalActive     |> should equal TimeSpan.Zero
    stats.TotalConnecting |> should equal TimeSpan.Zero
    stats.TotalPaused     |> should equal TimeSpan.Zero
    stats.TotalIssue      |> should equal TimeSpan.Zero
    stats.TotalIssueCount |> should equal 0
    stats.TotalReport     |> should equal TimeSpan.Zero
    trace.Intervals   |> List.length |> should equal 0
    trace.EventTraces |> List.length |> should equal 0

[<Fact>]
let ``clean session: connecting and active durations are exact`` () =
    // connect@t0 → connected@t1 → userDisc@t2; open Paused until pd
    let events = [ connectEvent t0; connectedEvent t1; userDiscEvent t2 ]
    let stats, _ = computeWithTrace None false None pd events
    stats.TotalConnecting |> should equal (t1 - t0)
    stats.TotalActive     |> should equal (t2 - t1)
    stats.TotalIssue      |> should equal TimeSpan.Zero
    stats.TotalIssueCount |> should equal 0

[<Fact>]
let ``unexpected disconnect while not locked opens issue interval`` () =
    // connected@t0 → disc@t1; active closes, issue opens until pd
    let events = [ connectedEvent t0; disconnEvent t1 ]
    let stats, _ = computeWithTrace None false None pd events
    stats.TotalActive     |> should equal (t1 - t0)
    stats.TotalIssue      |> should equal (pd - t1)
    stats.TotalIssueCount |> should equal 1

[<Fact>]
let ``issue then reconnect: report time is 20 min`` () =
    // connect@t0(Initial+5grace=10) → connected@t1 → disc@t2(Active Initial→0)
    // connect@t3(Issue=5) → connected@t4(Connecting PostIssue=5) → open Active (no trace, not counted)
    let events = [ connectEvent t0; connectedEvent t1; disconnEvent t2; connectEvent t3; connectedEvent t4 ]
    let stats, _ = computeWithTrace None false None pd events
    stats.TotalIssueCount |> should equal 1
    stats.TotalReport     |> should equal (TimeSpan.FromMinutes 20.0)

[<Fact>]
let ``events out of order are sorted before processing`` () =
    let events = [ connectedEvent t1; connectEvent t0; userDiscEvent t2 ]
    let stats, _ = computeWithTrace None false None pd events
    stats.TotalConnecting |> should equal (t1 - t0)
    stats.TotalActive     |> should equal (t2 - t1)

[<Fact>]
let ``cross-midnight session: ByDay active and paused sums equal period totals`` () =
    let localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 5, 1))
    let start = DateTimeOffset(2025, 5, 1, 22, 0, 0, localOffset)
    let disc  = start.AddHours 4.0  // 02:00 next day local
    let pd'   = start.AddHours 5.0  // 03:00 next day local (in the past)
    let events = [ rdpEventAt 1027 start; makeEventAt 1026 "" ["x";"1"] disc ]
    let stats, _ = computeWithTrace None false None pd' events
    stats.ByDay |> List.length |> should equal 2
    let sumActive = stats.ByDay |> List.sumBy (fun d -> d.ActiveTime.TotalSeconds) |> TimeSpan.FromSeconds
    let sumPaused = stats.ByDay |> List.sumBy (fun d -> d.PausedTime.TotalSeconds) |> TimeSpan.FromSeconds
    sumActive |> should equal stats.TotalActive
    sumPaused |> should equal stats.TotalPaused
