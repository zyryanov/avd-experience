module AvdStats.UnitTests.StatsTests

open System
open Xunit
open FsUnit.Xunit
open AvdStats.UnitTests.TestHelpers
open AvdStats.Stats

// ── helpers ───────────────────────────────────────────────────────────────────

let connectEvent t      = rdpEventAt 1024 t
let connectedEvent t    = rdpEventAt 1027 t
let disconnEvent t      = rdpEventAt 1026 t
let userDiscEvent t     = makeEventAt 1026 "" ["x"; "1"] t
let lockEvent t         = makeEventAt 4800 "Microsoft-Windows-Security-Auditing" [] t
let unlockEvent t       = makeEventAt 4801 "Microsoft-Windows-Security-Auditing" [] t
let sleepEvent t        = makeEventAt 42 "Microsoft-Windows-Kernel-Power" [] t
let watchdogEvent t     = makeEventAt 1033 "Microsoft-Windows-TerminalServices-ClientActiveXCore" ["ThreadWatchdog"; "RECEIVE thread did not finish callback within 1000 milliseconds."; "-2147024474"] t

let step state event = stepState state event

// ── stepState: None → Connecting ─────────────────────────────────────────────

[<Fact>]
let ``None + ConnectionInitiated → Connecting, no closed interval`` () =
    let state, closed = step None (connectEvent t1)
    state |> Option.map fst |> should equal (Some Connecting)
    closed |> should equal None

[<Fact>]
let ``None + Connected → Active, no closed interval`` () =
    let state, closed = step None (connectedEvent t1)
    state |> Option.map fst |> should equal (Some Active)
    closed |> should equal None

// ── stepState: Connecting → Active ───────────────────────────────────────────

[<Fact>]
let ``Connecting + Connected → Active, closes Connecting interval`` () =
    let init = Some (Connecting, t0)
    let state, closed = step init (connectedEvent t1)
    state |> Option.map fst |> should equal (Some Active)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Connecting)
    closed |> Option.map (fun i -> i.Start) |> should equal (Some t0)
    closed |> Option.map (fun i -> i.End)   |> should equal (Some t1)

// ── stepState: Active → Paused ────────────────────────────────────────────────

[<Fact>]
let ``Active + PowerDown → Paused, closes Active interval`` () =
    let init = Some (Active, t0)
    let state, closed = step init (sleepEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)

[<Fact>]
let ``Active + UserDisconnected → Paused, closes Active interval`` () =
    let init = Some (Active, t0)
    let state, closed = step init (userDiscEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)

// ── stepState: Active → Issue ─────────────────────────────────────────────────

[<Fact>]
let ``Active + Disconnect → Issue`` () =
    let init = Some (Active, t0)
    let state, closed = step init (disconnEvent t1)
    state |> Option.map fst |> should equal (Some Issue)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)

[<Fact>]
let ``Active + ThreadWatchdog → Issue, closes Active at watchdog time`` () =
    let init = Some (Active, t0)
    let state, closed = step init (watchdogEvent t1)
    state |> Option.map fst |> should equal (Some Issue)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Active)
    closed |> Option.map (fun i -> i.End) |> should equal (Some t1)

[<Fact>]
let ``Issue + Disconnect (1026 after watchdog) → stays Issue`` () =
    let init = Some (Issue, t1)
    let state, closed = step init (disconnEvent t2)
    state |> Option.map fst |> should equal (Some Issue)
    closed |> should equal None

// ── stepState: Connecting → Paused / Issue ───────────────────────────────────

[<Fact>]
let ``Connecting + PowerDown → Paused, closes Connecting`` () =
    let init = Some (Connecting, t0)
    let state, closed = step init (sleepEvent t1)
    state |> Option.map fst |> should equal (Some Paused)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Connecting)

[<Fact>]
let ``Connecting + Disconnect → Issue`` () =
    let init = Some (Connecting, t0)
    let state, closed = step init (disconnEvent t1)
    state |> Option.map fst |> should equal (Some Issue)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Connecting)

// ── stepState: Paused → Connecting ───────────────────────────────────────────

[<Fact>]
let ``Paused + ConnectionInitiated → Connecting, closes Paused`` () =
    let init = Some (Paused, t0)
    let state, closed = step init (connectEvent t1)
    state |> Option.map fst |> should equal (Some Connecting)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Paused)

// ── stepState: Issue → Connecting ────────────────────────────────────────────

[<Fact>]
let ``Issue + ConnectionInitiated → Connecting, closes Issue`` () =
    let init = Some (Issue, t0)
    let state, closed = step init (connectEvent t1)
    state |> Option.map fst |> should equal (Some Connecting)
    closed |> Option.map (fun i -> i.Kind) |> should equal (Some Issue)

// ── stepState: no-op cases ────────────────────────────────────────────────────

[<Fact>]
let ``Active + Connected again → no change`` () =
    let init = Some (Active, t0)
    let state, closed = step init (connectedEvent t1)
    state |> should equal init
    closed |> should equal None

[<Fact>]
let ``Active + ConnectionInitiated → no change (already active)`` () =
    let init = Some (Active, t0)
    let state, closed = step init (connectEvent t1)
    state |> should equal init
    closed |> should equal None

[<Fact>]
let ``PowerDown while Paused → no change`` () =
    let init = Some (Paused, t0)
    let state, closed = step init (sleepEvent t1)
    state |> should equal init
    closed |> should equal None

// ── lock/unlock scenarios (walk-level via computeWithTrace) ───────────────────

let runTrace events =
    let periodEnd = DateTimeOffset.MaxValue
    let _, tr = computeWithTrace None false None periodEnd events
    tr.Intervals

[<Fact>]
let ``Active + Lock + no events + Unlock → Active resumes after Paused`` () =
    // No AVD disconnect during lock → session survived → shadow stays Active → resumes Active
    let events = [
        connectedEvent t0
        lockEvent t1
        unlockEvent t2
    ]
    let intervals = runTrace events
    let kinds = intervals |> List.map (fun i -> i.Kind)
    kinds |> should equal [ Active; Paused; Active ]
    intervals |> List.item 1 |> (fun i -> i.Start) |> should equal t1
    intervals |> List.item 1 |> (fun i -> i.End)   |> should equal t2
    intervals |> List.item 2 |> (fun i -> i.Start) |> should equal t2

[<Fact>]
let ``Active + Lock + Disconnect + Unlock → disconnect absorbed into Paused`` () =
    // Drop during lock is absorbed into the Paused period; shadow stays Paused
    let events = [
        connectedEvent t0
        lockEvent t1
        disconnEvent t2
        unlockEvent t3
    ]
    let intervals = runTrace events
    intervals |> List.item 0 |> (fun i -> i.Kind) |> should equal Active
    intervals |> List.item 1 |> (fun i -> i.Kind) |> should equal Paused
    intervals |> List.item 1 |> (fun i -> i.Start) |> should equal t1
    intervals |> List.item 1 |> (fun i -> i.End)   |> should equal t3
    intervals |> List.item 2 |> (fun i -> i.Kind) |> should equal Paused
    intervals |> List.item 2 |> (fun i -> i.Start) |> should equal t3

[<Fact>]
let ``Active + Lock + Disconnect + Reconnect + Unlock → Paused then Active`` () =
    let events = [
        connectedEvent t0
        lockEvent t1
        disconnEvent t2
        connectEvent t3
        connectedEvent t4
        unlockEvent t5
    ]
    let intervals = runTrace events
    let kinds = intervals |> List.map (fun i -> i.Kind)
    kinds |> should equal [ Active; Paused; Active ]
    intervals |> List.item 2 |> (fun i -> i.Start) |> should equal t5

[<Fact>]
let ``Active + Lock + PowerDown + Unlock → Active resumes (sleep ignored in shadow)`` () =
    // Sleep during lock is transparent — no AVD disconnect fired → shadow stays Active → resumes
    let events = [
        connectedEvent t0
        lockEvent t1
        sleepEvent t2
        unlockEvent t3
    ]
    let intervals = runTrace events
    let kinds = intervals |> List.map (fun i -> i.Kind)
    kinds |> should equal [ Active; Paused; Active ]
    intervals |> List.item 1 |> (fun i -> i.Start) |> should equal t1
    intervals |> List.item 1 |> (fun i -> i.End)   |> should equal t3
    intervals |> List.item 2 |> (fun i -> i.Start) |> should equal t3

[<Fact>]
let ``Active + Lock + UserDisconnect + Unlock → Paused at unlock, not None`` () =
    // Before fix: shadow→None on userDisconnect → outward None → Initial connect (+5m)
    // After fix:  shadow→None on userDisconnect → outward Paused → PostPause connect (no +5m)
    let events = [
        connectedEvent t0
        lockEvent t1
        userDiscEvent t2
        unlockEvent t3
    ]
    let intervals = runTrace events
    // Active closes at lock; Paused (lock→unlock) closes at unlock; new Paused opens at unlock
    intervals |> List.item 0 |> (fun i -> i.Kind)  |> should equal Active
    intervals |> List.item 1 |> (fun i -> i.Kind)  |> should equal Paused
    intervals |> List.item 1 |> (fun i -> i.Start) |> should equal t1
    intervals |> List.item 1 |> (fun i -> i.End)   |> should equal t3
    intervals |> List.item 2 |> (fun i -> i.Kind)  |> should equal Paused
    intervals |> List.item 2 |> (fun i -> i.Start) |> should equal t3

[<Fact>]
let ``Reconnect after lock-induced user-disconnect is PostPause, no +5m bonus`` () =
    // First connect (Initial): +5m grace applied.
    // Second connect after lock+userDisc+unlock: PostPause, no +5m grace.
    let t6 = t5.AddMinutes 1.0
    let pd = t6.AddHours 1.0
    let events = [
        connectEvent   t0   // None → Connecting (Initial)
        connectedEvent t1   // Connecting(5min) closes: +5min + 5min grace = 10min report
        lockEvent      t2   // Active → Paused
        userDiscEvent  t3   // shadow → None
        unlockEvent    t4   // Paused → Paused (fix: not None)
        connectEvent   t5   // Paused → Connecting (PostPause)
        connectedEvent t6   // Connecting(1min) closes: +1min, no grace = 1min report
    ]
    let stats, _ = computeWithTrace None false None pd events
    // 10min (first) + 1min (second, PostPause) = 11min total
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 11.0)

[<Fact>]
let ``Issue + Lock (short) + Unlock → resumes as Issue`` () =
    let unlockShort = t2.AddMinutes 30.0
    let events = [
        connectedEvent t0
        watchdogEvent  t1   // Active → Issue
        lockEvent      t2   // Issue → Paused, shadow=Issue
        unlockEvent    unlockShort   // short Paused (<3h) → Issue resumes
    ]
    let intervals = runTrace events
    intervals |> List.map (fun i -> i.Kind) |> should equal [ Active; Issue; Paused; Issue ]

[<Fact>]
let ``Issue + Lock (≥3h) + Unlock → Paused, not Issue`` () =
    let unlockLong = t2.AddHours 4.0
    let events = [
        connectedEvent t0
        watchdogEvent  t1   // Active → Issue
        lockEvent      t2   // Issue → Paused, shadow=Issue
        unlockEvent    unlockLong   // long Paused (≥3h) → stale Issue reset to Paused
    ]
    let intervals = runTrace events
    intervals |> List.map (fun i -> i.Kind) |> should equal [ Active; Issue; Paused; Paused ]

[<Fact>]
let ``No session + Lock + Connect + Connected + Unlock → Active at unlock`` () =
    let events = [
        lockEvent t1
        connectEvent t2
        connectedEvent t3
        unlockEvent t4
    ]
    let intervals = runTrace events
    let kinds = intervals |> List.map (fun i -> i.Kind)
    kinds |> should equal [ Active ]
    intervals |> List.head |> (fun i -> i.Start) |> should equal t4

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
    let result = nextConnectReason None (Some (Connecting, t1)) None None
    result |> should equal (Some Initial)

[<Fact>]
let ``Issue → Connecting gives PostIssue`` () =
    let result = nextConnectReason (Some (Issue, t0)) (Some (Connecting, t1)) None None
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Paused → Connecting gives PostPause when no prior reason`` () =
    let result = nextConnectReason (Some (Paused, t0)) (Some (Connecting, t1)) None None
    result |> should equal (Some PostPause)

[<Fact>]
let ``Active → Connecting gives PostPause`` () =
    let result = nextConnectReason (Some (Active, t0)) (Some (Connecting, t1)) None None
    result |> should equal (Some PostPause)

[<Fact>]
let ``Connecting → Connecting preserves current reason`` () =
    let result = nextConnectReason (Some (Connecting, t0)) (Some (Connecting, t1)) (Some PostIssue) None
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Issue → Active directly gives PostIssue`` () =
    let result = nextConnectReason (Some (Issue, t0)) (Some (Active, t1)) (Some PostIssue) None
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Connecting → Active carries forward reason`` () =
    let result = nextConnectReason (Some (Connecting, t0)) (Some (Active, t1)) (Some PostPause) None
    result |> should equal (Some PostPause)

[<Fact>]
let ``Active → Paused with PostIssue clears reason (overhead consumed)`` () =
    let result = nextConnectReason (Some (Active, t0)) (Some (Paused, t1)) (Some PostIssue) None
    result |> should equal None

[<Fact>]
let ``Active → Paused with non-PostIssue reason preserves it`` () =
    let result = nextConnectReason (Some (Active, t0)) (Some (Paused, t1)) (Some PostPause) None
    result |> should equal (Some PostPause)

[<Fact>]
let ``Connecting → Paused preserves PostIssue (mid-reconnect abort)`` () =
    let result = nextConnectReason (Some (Connecting, t0)) (Some (Paused, t1)) (Some PostIssue) None
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Paused → Active with PostIssue and short Paused (<3h) preserves reason`` () =
    let shortIv = Some { Kind = Paused; Start = t0; End = t0.AddHours 1.0 }
    let result = nextConnectReason (Some (Paused, t0)) (Some (Active, t1)) (Some PostIssue) shortIv
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Paused → Active with PostIssue and long Paused (≥3h) expires reason`` () =
    let longIv = Some { Kind = Paused; Start = t0; End = t0.AddHours 4.0 }
    let result = nextConnectReason (Some (Paused, t0)) (Some (Active, t1)) (Some PostIssue) longIv
    result |> should equal None

[<Fact>]
let ``Paused → Connecting with PostIssue and short Paused (<3h) gives PostIssue`` () =
    let shortIv = Some { Kind = Paused; Start = t0; End = t0.AddHours 1.0 }
    let result = nextConnectReason (Some (Paused, t0)) (Some (Connecting, t1)) (Some PostIssue) shortIv
    result |> should equal (Some PostIssue)

[<Fact>]
let ``Paused → Connecting with PostIssue and long Paused (≥3h) gives Initial`` () =
    let longIv = Some { Kind = Paused; Start = t0; End = t0.AddHours 4.0 }
    let result = nextConnectReason (Some (Paused, t0)) (Some (Connecting, t1)) (Some PostIssue) longIv
    result |> should equal (Some Initial)

[<Fact>]
let ``Paused → Connecting with no reason and long Paused (≥3h) gives Initial`` () =
    let longIv = Some { Kind = Paused; Start = t0; End = t0.AddHours 4.0 }
    let result = nextConnectReason (Some (Paused, t0)) (Some (Connecting, t1)) None longIv
    result |> should equal (Some Initial)

[<Fact>]
let ``Paused → Connecting at exactly 3h boundary gives Initial`` () =
    let edgeIv = Some { Kind = Paused; Start = t0; End = t0.AddHours 3.0 }
    let result = nextConnectReason (Some (Paused, t0)) (Some (Connecting, t1)) (Some PostIssue) edgeIv
    result |> should equal (Some Initial)

[<Fact>]
let ``Issue drop then short Paused detour: Active gets PostIssue recovery overhead`` () =
    // Sequence: Active→Issue(5m)→Connecting(5m, PostIssue)→Paused(5m, <3h)→Connecting(10m, PostIssue preserved)→Active(10m)→Paused
    // Expected report: Issue 5m + Connecting 5m + Connecting 10m + Active(PostIssue, 10m) = 30m
    let t6 = t5.AddMinutes 10.0
    let pd = t6.AddMinutes 1.0
    let events = [
        connectedEvent t0           // None → Active
        watchdogEvent  t1           // Active(5m) → Issue
        connectEvent   t2           // Issue(5m) → Connecting  [PostIssue]
        userDiscEvent  t3           // Connecting(5m) → Paused  [reason preserved=PostIssue]
        connectEvent   t4           // Paused(5m, <3h, PostIssue) → Connecting  [PostIssue preserved]
        connectedEvent t5           // Connecting(10m) → Active  [PostIssue preserved]
        userDiscEvent  t6           // Active(10m, PostIssue) → Paused: +10m report
    ]
    let stats, _ = computeWithTrace None false None pd events
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 30.0)

[<Fact>]
let ``Issue drop then long Paused detour (≥3h): reconnect is Initial, gets +5m grace`` () =
    // Paused ≥ 3h → reason resets to Initial → Connecting(10m) gets +5m grace
    // Expected report: Issue 5m + Connecting 5m (PostIssue) + Connecting 10m+5grace (Initial) = 25m
    let tLong   = t3.AddHours 4.0
    let tConn2  = tLong.AddMinutes 10.0
    let tActive = tLong.AddMinutes 20.0
    let pd      = tActive.AddMinutes 1.0
    let events = [
        connectedEvent t0
        watchdogEvent  t1           // Active(5m) → Issue
        connectEvent   t2           // Issue(5m) → Connecting  [PostIssue]
        userDiscEvent  t3           // Connecting(5m) → Paused
        connectEvent   tLong        // Paused(4h, ≥3h) → Connecting  [Initial — new connection]
        connectedEvent tConn2       // Connecting(10m) → Active  [Initial]
        userDiscEvent  tActive      // Active(10m, Initial) → Paused: +0
    ]
    let stats, _ = computeWithTrace None false None pd events
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 25.0)

[<Fact>]
let ``PostIssue does not persist through lock/unlock cycles after first Active session`` () =
    // Issue → connect → Active(PostIssue, 5m, +5m) → lock → unlock → Active(no PostIssue, 5m, +0) → lock
    // Expected: Issue 5m + Connecting 5m + Active(PostIssue, 5m) = 15m
    let t6 = t5.AddMinutes 5.0
    let t7 = t6.AddMinutes 5.0
    let t8 = t7.AddMinutes 10.0
    let pd = t8.AddMinutes 1.0
    let events = [
        connectedEvent t0           // Active
        watchdogEvent  t1           // Active(5m) → Issue
        connectEvent   t2           // Issue(5m) → Connecting [PostIssue]
        connectedEvent t3           // Connecting(5m) → Active [PostIssue]
        lockEvent      t4           // Active(5m, PostIssue) → Paused: +5m; PostIssue consumed
        unlockEvent    t5           // Paused(10m) → Active [reason=None]
        lockEvent      t6           // Active(5m, no PostIssue) → Paused: +0
        unlockEvent    t7           // Paused(5m) → Active [reason=None]
        userDiscEvent  t8           // Active(10m, no PostIssue) → Paused: +0
    ]
    let stats, _ = computeWithTrace None false None pd events
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 15.0)

[<Fact>]
let ``Clean session + 3h+ pause + reconnect: both Connecting intervals get Initial grace`` () =
    // connect(t0)→connected(t1, 5m Connecting Initial+5grace=10m)→userDisc(t2)
    // →connect(t2+4h, 4h Paused closes, Initial)→connected(t2+4h+10m, 10m Connecting Initial+5grace=15m)
    // TotalReport = 10m + 15m = 25m
    let tPause   = t2
    let tConn2   = tPause.AddHours 4.0
    let tActive2 = tConn2.AddMinutes 10.0
    let pd       = tActive2.AddMinutes 1.0
    let events = [
        connectEvent   t0
        connectedEvent t1
        userDiscEvent  tPause
        connectEvent   tConn2
        connectedEvent tActive2
    ]
    let stats, _ = computeWithTrace None false None pd events
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 25.0)

// ── initReason propagation (cross-window PostIssue) ──────────────────────────

[<Fact>]
let ``initReason PostIssue: Active close contributes up to 15 min`` () =
    // Simulates query starting mid-session: state=Active(t0), reason=PostIssue already set.
    // Lock event at t1 closes Active(5min) with PostIssue → should get +5min report.
    let events = [ lockEvent t1 ]
    let initState = Some (Active, t0)
    let pd = t2
    let stats, _ = computeWithTrace initState false (Some PostIssue) pd events
    stats.TotalReport |> should equal (t1 - t0)

[<Fact>]
let ``initReason None: Active close contributes zero`` () =
    // Same scenario but no initReason → no recovery overhead.
    let events = [ lockEvent t1 ]
    let initState = Some (Active, t0)
    let pd = t2
    let stats, _ = computeWithTrace initState false None pd events
    stats.TotalReport |> should equal TimeSpan.Zero

[<Fact>]
let ``initReason PostIssue: Active crossing midnight, query starts day 2, gets +15m`` () =
    // Exact reproduction of the reported bug:
    //   - Session reconnected after issue at 23:25 on day 1 (PostIssue)
    //   - Query starts at midnight (day 2); initState=Active(midnight), initReason=PostIssue
    //   - Lock event fires 00:28 on day 2 → closes 28min Active with PostIssue → +15m capped
    let localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 5, 20))
    let midnight    = DateTimeOffset(2025, 5, 20, 0, 0, 0, localOffset)
    let lockAt      = midnight.AddMinutes 28.0
    let pd          = midnight.AddHours 2.0
    let events      = [ makeEventAt 4800 "Microsoft-Windows-Security-Auditing" [] lockAt ]
    let initState   = Some (Active, midnight)
    let stats, _    = computeWithTrace initState false (Some PostIssue) pd events
    stats.TotalReport |> should equal (TimeSpan.FromMinutes 15.0)

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
