module AvdStats.UnitTests.TestHelpers

open System
open AvdStats.EventLog

let makeEvent id provider props : LogEvent =
    { Id          = id
      TimeCreated = DateTimeOffset.UtcNow
      Provider    = provider
      Message     = None
      Properties  = props }

let makeEventAt id provider props (t: DateTimeOffset) : LogEvent =
    { Id          = id
      TimeCreated = t
      Provider    = provider
      Message     = None
      Properties  = props }

let rdpEvent id = makeEvent id "Microsoft-Windows-TerminalServices-ClientActiveX" []
let rdpEventAt id t = makeEventAt id "Microsoft-Windows-TerminalServices-ClientActiveX" [] t
let sysEvent id = makeEvent id "Microsoft-Windows-Kernel-Power" []
let secEvent id = makeEvent id "Microsoft-Windows-Security-Auditing" []

let t0 = DateTimeOffset(2025, 5, 1, 10, 0, 0, TimeSpan.Zero)
let t1 = t0.AddMinutes 5.0
let t2 = t0.AddMinutes 10.0
let t3 = t0.AddMinutes 15.0
let t4 = t0.AddMinutes 20.0
let t5 = t0.AddMinutes 30.0
