module AvdStats.IntegrationTests.Fixtures

open System
open AvdStats.EventLog

let private make id provider props (t: DateTimeOffset) : LogEvent =
    { Id = id; TimeCreated = t; Provider = provider; Message = None; Properties = props }

let rdp id t     = make id "Microsoft-Windows-TerminalServices-ClientActiveX" [] t
let disc t       = make 1026 "Microsoft-Windows-TerminalServices-ClientActiveX" [] t
// isUserDisconnected checks Properties.[1] = "1"|"2"
let userDisc t   = make 1026 "" ["x"; "1"] t
let lock t       = make 4800 "Microsoft-Windows-Security-Auditing" [] t
let unlock t     = make 4801 "Microsoft-Windows-Security-Auditing" [] t

// ── Scenario 1: Clean workday ─────────────────────────────────────────────────
// 09:00 initiate → 09:01 connected → 17:00 user disconnect, periodEnd 20:00
// Expected: 1min connecting, 7h59min active, 0 issues
// Report: Connecting(Initial,1min)=6min (1+5grace), Active(Initial)=0 → total 6min
let cleanWorkdayEnd = DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero)
let cleanWorkdayEvents =
    let b = DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)
    [ rdp 1024  (b.AddHours 9.0)
      rdp 1027  (b.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
      userDisc  (b.AddHours 17.0) ]

// ── Scenario 2: Drop and reconnect ───────────────────────────────────────────
// 09:00 initiate → 09:01 connected → 11:00 unexpected drop →
// 11:05 initiate → 11:07 connected → 17:00 user disconnect, periodEnd 18:00
// Report: C1(Initial,1min)=6, Active1(Initial)=0, Issue(5min)=5,
//         C2(PostIssue,2min)=2, Active2(PostIssue,5h53min)=min(15,353)=15 → total 28min
let issueReconnectEnd = DateTimeOffset(2026, 1, 16, 18, 0, 0, TimeSpan.Zero)
let issueReconnectEvents =
    let b = DateTimeOffset(2026, 1, 16, 0, 0, 0, TimeSpan.Zero)
    [ rdp 1024  (b.AddHours 9.0)
      rdp 1027  (b.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
      disc      (b.AddHours 11.0)
      rdp 1024  (b.AddHours 11.0 + TimeSpan.FromMinutes 5.0)
      rdp 1027  (b.AddHours 11.0 + TimeSpan.FromMinutes 7.0)
      userDisc  (b.AddHours 17.0) ]

// ── Scenario 3: Lock/unlock cycle ────────────────────────────────────────────
// 09:00 initiate → 09:01 connected → 12:00 lock →
// 12:30 unlock → 12:30 initiate → 12:31 connected → 17:00 user disconnect, periodEnd 18:00
// Report: C1(Initial,1min)=6, Active1(Initial)=0, Paused=0,
//         C2(PostPause,1min)=1, Active2(PostPause)=0 → total 7min
let lockUnlockEnd = DateTimeOffset(2026, 1, 17, 18, 0, 0, TimeSpan.Zero)
let lockUnlockEvents =
    let b = DateTimeOffset(2026, 1, 17, 0, 0, 0, TimeSpan.Zero)
    [ rdp 1024  (b.AddHours 9.0)
      rdp 1027  (b.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
      lock      (b.AddHours 12.0)
      unlock    (b.AddHours 12.5)
      rdp 1024  (b.AddHours 12.5)
      rdp 1027  (b.AddHours 12.5 + TimeSpan.FromMinutes 1.0)
      userDisc  (b.AddHours 17.0) ]

// ── Scenario 4: Multi-day ────────────────────────────────────────────────────
// Day1 09:00 initiate → 09:01 connected → 17:00 user disconnect
// Day2 09:00 initiate → 09:01 connected → 17:00 user disconnect, periodEnd 18:00
// Uses local timezone offsets so splitByDay produces exactly 2 dates
let multiDayEnd () =
    let off = TimeZoneInfo.Local.GetUtcOffset DateTime.Now
    DateTimeOffset(2026, 1, 22, 18, 0, 0, off)

let multiDayEvents () =
    let off = TimeZoneInfo.Local.GetUtcOffset DateTime.Now
    let day1 = DateTimeOffset(2026, 1, 21, 0, 0, 0, off)
    let day2 = DateTimeOffset(2026, 1, 22, 0, 0, 0, off)
    [ rdp 1024  (day1.AddHours 9.0)
      rdp 1027  (day1.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
      userDisc  (day1.AddHours 17.0)
      rdp 1024  (day2.AddHours 9.0)
      rdp 1027  (day2.AddHours 9.0 + TimeSpan.FromMinutes 1.0)
      userDisc  (day2.AddHours 17.0) ]
