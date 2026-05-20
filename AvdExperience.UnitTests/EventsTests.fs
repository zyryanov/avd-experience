module AvdStats.UnitTests.EventsTests

open Xunit
open FsUnit.Xunit
open AvdStats.UnitTests.TestHelpers
open AvdStats.Events

// ── isConnectInitiated ────────────────────────────────────────────────────────

[<Fact>]
let ``isConnectInitiated matches 1024`` () =
    rdpEvent 1024 |> isConnectInitiated |> should equal true

[<Fact>]
let ``isConnectInitiated matches 1102`` () =
    rdpEvent 1102 |> isConnectInitiated |> should equal true

[<Fact>]
let ``isConnectInitiated rejects other ids`` () =
    rdpEvent 1027 |> isConnectInitiated |> should equal false

// ── isConnected ───────────────────────────────────────────────────────────────

[<Fact>]
let ``isConnected matches 1027`` () =
    rdpEvent 1027 |> isConnected |> should equal true

[<Fact>]
let ``isConnected rejects 1024`` () =
    rdpEvent 1024 |> isConnected |> should equal false

// ── isDisconnected ────────────────────────────────────────────────────────────

[<Fact>]
let ``isDisconnected matches 1026`` () =
    rdpEvent 1026 |> isDisconnected |> should equal true

[<Fact>]
let ``isDisconnected rejects 1027`` () =
    rdpEvent 1027 |> isDisconnected |> should equal false

// ── isUserDisconnected ────────────────────────────────────────────────────────

[<Fact>]
let ``isUserDisconnected matches 1026 with reason 1`` () =
    makeEvent 1026 "" ["x"; "1"] |> isUserDisconnected |> should equal true

[<Fact>]
let ``isUserDisconnected matches 1026 with reason 2`` () =
    makeEvent 1026 "" ["x"; "2"] |> isUserDisconnected |> should equal true

[<Fact>]
let ``isUserDisconnected matches 1026 with reason 3`` () =
    makeEvent 1026 "" ["x"; "3"] |> isUserDisconnected |> should equal true

[<Fact>]
let ``isUserDisconnected rejects 1026 with no properties`` () =
    rdpEvent 1026 |> isUserDisconnected |> should equal false

[<Fact>]
let ``isUserDisconnected rejects wrong id`` () =
    makeEvent 1027 "" ["x"; "1"] |> isUserDisconnected |> should equal false

// ── isPowerDown ───────────────────────────────────────────────────────────────

[<Fact>]
let ``isPowerDown matches Kernel-Power 42`` () =
    makeEvent 42 "Microsoft-Windows-Kernel-Power" [] |> isPowerDown |> should equal true

[<Fact>]
let ``isPowerDown matches User32 1074`` () =
    makeEvent 1074 "User32" [] |> isPowerDown |> should equal true

[<Fact>]
let ``isPowerDown matches user32 case-insensitive`` () =
    makeEvent 1074 "USER32" [] |> isPowerDown |> should equal true

[<Fact>]
let ``isPowerDown matches EventLog 6006`` () =
    makeEvent 6006 "EventLog" [] |> isPowerDown |> should equal true

[<Fact>]
let ``isPowerDown matches EventLog 6008`` () =
    makeEvent 6008 "EventLog" [] |> isPowerDown |> should equal true

[<Fact>]
let ``isPowerDown rejects Kernel-Power 107`` () =
    makeEvent 107 "Microsoft-Windows-Kernel-Power" [] |> isPowerDown |> should equal false

[<Fact>]
let ``isPowerDown rejects wrong provider for id 42`` () =
    makeEvent 42 "SomeOther" [] |> isPowerDown |> should equal false

// ── isPowerResume ─────────────────────────────────────────────────────────────

[<Fact>]
let ``isPowerResume matches Kernel-Power 107`` () =
    makeEvent 107 "Microsoft-Windows-Kernel-Power" [] |> isPowerResume |> should equal true

[<Fact>]
let ``isPowerResume rejects Kernel-Power 42`` () =
    makeEvent 42 "Microsoft-Windows-Kernel-Power" [] |> isPowerResume |> should equal false

[<Fact>]
let ``isPowerResume rejects wrong provider`` () =
    makeEvent 107 "SomeOther" [] |> isPowerResume |> should equal false

// ── isConnectionCanceled ──────────────────────────────────────────────────────

[<Fact>]
let ``isConnectionCanceled matches 1033`` () =
    rdpEvent 1033 |> isConnectionCanceled |> should equal true

[<Fact>]
let ``isConnectionCanceled rejects 1032`` () =
    rdpEvent 1032 |> isConnectionCanceled |> should equal false

// ── isThreadWatchdog ──────────────────────────────────────────────────────────

[<Fact>]
let ``isThreadWatchdog matches 1033 with ThreadWatchdog property`` () =
    makeEvent 1033 "" ["ThreadWatchdog"; "RECEIVE thread did not finish callback within 1000 milliseconds."; "-2147024474"]
    |> isThreadWatchdog |> should equal true

[<Fact>]
let ``isThreadWatchdog rejects 1033 with no properties`` () =
    rdpEvent 1033 |> isThreadWatchdog |> should equal false

[<Fact>]
let ``isThreadWatchdog rejects 1033 with non-watchdog component`` () =
    makeEvent 1033 "" ["slint"; "SL::OnDisconnected"; "16644"]
    |> isThreadWatchdog |> should equal false

[<Fact>]
let ``isThreadWatchdog rejects other id with ThreadWatchdog property`` () =
    makeEvent 1026 "" ["ThreadWatchdog"; "msg"; "0"]
    |> isThreadWatchdog |> should equal false

// ── isWorkstationLocked / Unlocked ────────────────────────────────────────────

[<Fact>]
let ``isWorkstationLocked matches 4800`` () =
    secEvent 4800 |> isWorkstationLocked |> should equal true

[<Fact>]
let ``isWorkstationLocked rejects 4801`` () =
    secEvent 4801 |> isWorkstationLocked |> should equal false

[<Fact>]
let ``isWorkstationUnlocked matches 4801`` () =
    secEvent 4801 |> isWorkstationUnlocked |> should equal true

[<Fact>]
let ``isWorkstationUnlocked rejects 4800`` () =
    secEvent 4800 |> isWorkstationUnlocked |> should equal false

// ── isRelevant ────────────────────────────────────────────────────────────────

[<Theory>]
[<InlineData(1024)>]
[<InlineData(1102)>]
[<InlineData(1027)>]
[<InlineData(1026)>]
[<InlineData(1033)>]
[<InlineData(4800)>]
[<InlineData(4801)>]
let ``isRelevant true for known event ids`` (id: int) =
    rdpEvent id |> isRelevant |> should equal true

[<Fact>]
let ``isRelevant true for Kernel-Power 42`` () =
    makeEvent 42 "Microsoft-Windows-Kernel-Power" [] |> isRelevant |> should equal true

[<Fact>]
let ``isRelevant true for Kernel-Power 107`` () =
    makeEvent 107 "Microsoft-Windows-Kernel-Power" [] |> isRelevant |> should equal true

[<Fact>]
let ``isRelevant false for noise event`` () =
    rdpEvent 226 |> isRelevant |> should equal false
