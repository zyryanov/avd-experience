module AvdStats.Tests.StatsTests

open System
open Xunit
open FsUnit.Xunit
open AvdStats.Tests.TestHelpers
open AvdStats.Stats

// ── helpers ───────────────────────────────────────────────────────────────────

let connectEvent t    = rdpEventAt 1024 t
let connectedEvent t  = rdpEventAt 1027 t
let disconnEvent t    = rdpEventAt 1026 t
let userDiscEvent t   = makeEventAt 1026 "" ["x"; "1"] t
let lockEvent t       = makeEventAt 4800 "Microsoft-Windows-Security-Auditing" [] t
let unlockEvent t     = makeEventAt 4801 "Microsoft-Windows-Security-Auditing" [] t
let sleepEvent t      = makeEventAt 42 "Microsoft-Windows-Kernel-Power" [] t

let step state locked event = stepState state locked event

// ── stepState: None → Connecting ─────────────────────────────────────────────

[<Fact>]
let ``None + ConnectionInitiated → Connecting, no closed interval`` () =
    let state, closed = step None false (connectEvent t1)
    state |> Option.map fst |> should equal (Some Connecting)
    closed |> should equal None

[<Fact>]
let ``None + Connected → Active, no closed interval`` () =
    let state, closed = step None false (connectedEvent t1)
    state |> Option.map fst |> should equal (Some Active)
    closed |> should equal None

// ── stepState: Connecting → Active ───────────────────────────────────────────

[<Fact>]
let ``Connecting + Connected → Active, closes Connecting interval`` () =
    let init = Some (Connecting, t0)
    let state, closed = step init false (connectedEvent t1)
    state |> Option.map fst |> should equal (Some Active)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Connecting)
    closed |> Option.map (fun i -> i.Start) |> should equal (Some t0)
    closed |> Option.map (fun i -> i.End)   |> should equal (Some t1)

// ── stepState: Active → Paused ────────────────────────────────────────────────

[<Fact>]
let ``Active + PowerDown → Paused, closes Active interval`` () =
    let init = Some (Active, t0)
    let state, closed = step init false (sleepEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)

[<Fact>]
let ``Active + UserDisconnected → Paused, closes Active interval`` () =
    let init = Some (Active, t0)
    let state, closed = step init false (userDiscEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)

[<Fact>]
let ``Active + WorkstationLocked → Paused, closes Active interval`` () =
    let init = Some (Active, t0)
    let state, closed = step init false (lockEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)

[<Fact>]
let ``Active + Disconnect while locked → Paused`` () =
    let init = Some (Active, t0)
    let state, closed = step init true (disconnEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)

// ── stepState: Active → Issue ─────────────────────────────────────────────────

[<Fact>]
let ``Active + Disconnect while not locked → Issue`` () =
    let init = Some (Active, t0)
    let state, closed = step init false (disconnEvent t1)
    state |> Option.map fst |> should equal (Some Issue)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)

// ── stepState: Connecting → Paused ───────────────────────────────────────────

[<Fact>]
let ``Connecting + PowerDown → Paused, closes Connecting`` () =
    let init = Some (Connecting, t0)
    let state, closed = step init false (sleepEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Connecting)

[<Fact>]
let ``Connecting + Disconnect while locked → Paused`` () =
    let init = Some (Connecting, t0)
    let state, closed = step init true (disconnEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Connecting)

[<Fact>]
let ``Connecting + Disconnect while not locked → Issue`` () =
    let init = Some (Connecting, t0)
    let state, closed = step init false (disconnEvent t1)
    state |> Option.map fst |> should equal (Some Issue)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Connecting)

// ── stepState: Paused → Connecting ───────────────────────────────────────────

[<Fact>]
let ``Paused + ConnectionInitiated → Connecting, closes Paused`` () =
    let init = Some (Paused, t0)
    let state, closed = step init false (connectEvent t1)
    state |> Option.map fst |> should equal (Some Connecting)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Paused)

// ── stepState: Issue → Connecting ────────────────────────────────────────────

[<Fact>]
let ``Issue + ConnectionInitiated → Connecting, closes Issue`` () =
    let init = Some (Issue, t0)
    let state, closed = step init false (connectEvent t1)
    state |> Option.map fst |> should equal (Some Connecting)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Issue)

// ── stepState: no-op cases ────────────────────────────────────────────────────

[<Fact>]
let ``Active + Connected again → no change`` () =
    let init = Some (Active, t0)
    let state, closed = step init false (connectedEvent t1)
    state |> should equal init
    closed |> should equal None

[<Fact>]
let ``Active + ConnectionInitiated → no change (already active)`` () =
    let init = Some (Active, t0)
    let state, closed = step init false (connectEvent t1)
    state |> should equal init
    closed |> should equal None

[<Fact>]
let ``PowerDown while Paused → no change`` () =
    let init = Some (Paused, t0)
    let state, closed = step init false (sleepEvent t1)
    state |> should equal init
    closed |> should equal None

// ── intervalReportContrib ─────────────────────────────────────────────────────

let dur10 = TimeSpan.FromMinutes 10.0
let dur3  = TimeSpan.FromMinutes 3.0
let dur20 = TimeSpan.FromMinutes 20.0

[<Fact>]
let ``Connecting Initial adds 5 min bonus`` () =
    let result = intervalReportContrib Connecting (Some Initial) dur10
    result |> should equal (TimeSpan.FromMinutes 15.0)

[<Fact>]
let ``Connecting PostIssue no bonus`` () =
    let result = intervalReportContrib Connecting (Some PostIssue) dur10
    result |> should equal dur10

[<Fact>]
let ``Connecting PostPause no bonus`` () =
    let result = intervalReportContrib Connecting (Some PostPause) dur10
    result |> should equal dur10

[<Fact>]
let ``Connecting None no bonus`` () =
    let result = intervalReportContrib Connecting None dur10
    result |> should equal dur10

[<Fact>]
let ``Issue returns full duration`` () =
    let result = intervalReportContrib Issue (Some Initial) dur10
    result |> should equal dur10

[<Fact>]
let ``Active PostIssue returns duration when under 15 min`` () =
    let result = intervalReportContrib Active (Some PostIssue) dur10
    result |> should equal dur10

[<Fact>]
let ``Active PostIssue caps at 15 min when over`` () =
    let result = intervalReportContrib Active (Some PostIssue) dur20
    result |> should equal (TimeSpan.FromMinutes 15.0)

[<Fact>]
let ``Active Initial returns zero`` () =
    let result = intervalReportContrib Active (Some Initial) dur10
    result |> should equal TimeSpan.Zero

[<Fact>]
let ``Active PostPause returns zero`` () =
    let result = intervalReportContrib Active (Some PostPause) dur10
    result |> should equal TimeSpan.Zero

[<Fact>]
let ``Paused returns zero`` () =
    let result = intervalReportContrib Paused (Some PostIssue) dur10
    result |> should equal TimeSpan.Zero

// ── nextConnectReason ─────────────────────────────────────────────────────────

[<Fact>]
let ``None → Connecting gives Initial`` () =
    let result = nextConnectReason None (Some (Connecting, t1)) None
    result |> should equal (Some Initial)

[<Fact>]
let ``Issue → Connecting gives PostIssue`` () =
    let result = nextConnectReason (Some (Issue, t0)) (Some (Connecting, t1)) None
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Paused → Connecting gives PostPause`` () =
    let result = nextConnectReason (Some (Paused, t0)) (Some (Connecting, t1)) None
    result |> should equal (Some PostPause)

[<Fact>]
let ``Active → Connecting gives PostPause`` () =
    let result = nextConnectReason (Some (Active, t0)) (Some (Connecting, t1)) None
    result |> should equal (Some PostPause)

[<Fact>]
let ``Connecting → Connecting preserves current reason`` () =
    let result = nextConnectReason (Some (Connecting, t0)) (Some (Connecting, t1)) (Some PostIssue)
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Issue → Active directly gives PostIssue`` () =
    let result = nextConnectReason (Some (Issue, t0)) (Some (Active, t1)) (Some PostIssue)
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Connecting → Active carries forward reason`` () =
    let result = nextConnectReason (Some (Connecting, t0)) (Some (Active, t1)) (Some PostPause)
    result |> should equal (Some PostPause)

[<Fact>]
let ``transition to Paused clears reason`` () =
    let result = nextConnectReason (Some (Active, t0)) (Some (Paused, t1)) (Some PostIssue)
    result |> should equal None

// ── splitByDay ────────────────────────────────────────────────────────────────

[<Fact>]
let ``Single-day interval gives one slice`` () =
    let localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 5, 1, 10, 0, 0))
    let start = DateTimeOffset(2025, 5, 1, 10, 0, 0, localOffset)
    let end_  = start.AddHours 2.0
    let interval = { Kind = Active; Start = start; End = end_ }
    let slices = splitByDay interval
    slices |> List.length |> should equal 1
    slices |> List.head |> snd |> should equal (TimeSpan.FromHours 2.0)

[<Fact>]
let ``Interval crossing midnight gives two slices`` () =
    let localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 5, 1))
    let start = DateTimeOffset(2025, 5, 1, 23, 0, 0, localOffset)
    let end_  = start.AddHours 2.0
    let interval = { Kind = Active; Start = start; End = end_ }
    let slices = splitByDay interval
    slices |> List.length |> should equal 2
    let totalDuration = slices |> List.sumBy (fun (_, d) -> d.TotalSeconds) |> TimeSpan.FromSeconds
    totalDuration |> should equal (TimeSpan.FromHours 2.0)

[<Fact>]
let ``Multi-day interval gives correct slice count`` () =
    let localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 5, 1))
    let start = DateTimeOffset(2025, 5, 1, 23, 0, 0, localOffset)
    let end_  = start.AddHours 26.0  // spans 3 days
    let interval = { Kind = Active; Start = start; End = end_ }
    let slices = splitByDay interval
    slices |> List.length |> should equal 3
    let totalDuration = slices |> List.sumBy (fun (_, d) -> d.TotalSeconds) |> TimeSpan.FromSeconds
    totalDuration |> should equal (TimeSpan.FromHours 26.0)

[<Fact>]
let ``Zero-duration interval gives no slices`` () =
    let localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 5, 1, 10, 0, 0))
    let start = DateTimeOffset(2025, 5, 1, 10, 0, 0, localOffset)
    let interval = { Kind = Paused; Start = start; End = start }
    let slices = splitByDay interval
    slices |> List.length |> should equal 0
