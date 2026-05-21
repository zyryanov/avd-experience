module AvdStats.UnitTests.PropertyTests

open System
open Xunit
open FsCheck
open AvdStats.UnitTests.TestHelpers
open AvdStats.Stats

// ── splitByDay ────────────────────────────────────────────────────────────────

[<Fact>]
let ``splitByDay: slice durations sum to interval duration`` () =
    let prop (dayIndex: int) (startMin: int) (durationMin: int) =
        let dayIndex    = abs dayIndex % 500
        let startMin    = abs startMin % 1440
        let durationMin = abs durationMin % 2880 + 1
        let offset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 1, 1))
        let start  = DateTimeOffset(2025, 1, 1, 0, 0, 0, offset).AddDays(float dayIndex).AddMinutes(float startMin)
        let end_   = start.AddMinutes(float durationMin)
        let interval = { Kind = Active; Start = start; End = end_ }
        let slices = splitByDay interval
        let total  = slices |> List.sumBy (fun (_, d) -> d.TotalSeconds)
        abs(total - (end_ - start).TotalSeconds) < 0.001
    Check.QuickThrowOnFailure prop

[<Fact>]
let ``splitByDay: all slices have positive duration`` () =
    let prop (dayIndex: int) (startMin: int) (durationMin: int) =
        let dayIndex    = abs dayIndex % 500
        let startMin    = abs startMin % 1440
        let durationMin = abs durationMin % 2880 + 1
        let offset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 1, 1))
        let start  = DateTimeOffset(2025, 1, 1, 0, 0, 0, offset).AddDays(float dayIndex).AddMinutes(float startMin)
        let end_   = start.AddMinutes(float durationMin)
        let interval = { Kind = Active; Start = start; End = end_ }
        splitByDay interval |> List.forall (fun (_, d) -> d > TimeSpan.Zero)
    Check.QuickThrowOnFailure prop

// ── computeWithTrace ─────────────────────────────────────────────────────────

[<Fact>]
let ``computeWithTrace: ByDay sums equal period totals for any session duration`` () =
    let prop (durationHoursRaw: int) (dayOfMonthRaw: int) =
        let durationHours = abs durationHoursRaw % 48 + 1
        let dayOfMonth    = abs dayOfMonthRaw % 25 + 1
        let offset = TimeZoneInfo.Local.GetUtcOffset(DateTime(2025, 3, dayOfMonth))
        let baseT  = DateTimeOffset(2025, 3, dayOfMonth, 0, 0, 0, offset)
        // pd in 2025 → effectiveEnd = pd (deterministic, in the past)
        let pd     = baseT.AddHours(float durationHours + 1.0)
        let events = [ rdpEventAt 1027 baseT; makeEventAt 1026 "" ["x";"1"] (baseT.AddHours(float durationHours)) ]
        let stats, _ = computeWithTrace None false None pd events
        let sumActive = stats.ByDay |> List.sumBy (fun d -> d.ActiveTime.TotalSeconds)
        let sumPaused = stats.ByDay |> List.sumBy (fun d -> d.PausedTime.TotalSeconds)
        abs(sumActive - stats.TotalActive.TotalSeconds) < 0.01 &&
        abs(sumPaused - stats.TotalPaused.TotalSeconds) < 0.01
    Check.QuickThrowOnFailure prop

[<Fact>]
let ``computeWithTrace: all intervals have non-negative duration`` () =
    let baseTime = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
    // pd in 2025, well after all events → effectiveEnd = pd
    let pd = DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero)
    let prop (kindIndices: int list) =
        let events =
            kindIndices
            |> List.truncate 15
            |> List.mapi (fun i kindIdx ->
                let t = baseTime.AddMinutes(float ((i + 1) * 5))
                match abs kindIdx % 7 with
                | 0 -> rdpEventAt 1024 t
                | 1 -> rdpEventAt 1027 t
                | 2 -> rdpEventAt 1026 t
                | 3 -> makeEventAt 1026 "" ["x";"1"] t
                | 4 -> makeEventAt 4800 "Microsoft-Windows-Security-Auditing" [] t
                | 5 -> makeEventAt 4801 "Microsoft-Windows-Security-Auditing" [] t
                | _ -> makeEventAt 42 "Microsoft-Windows-Kernel-Power" [] t)
        let _, trace = computeWithTrace None false None pd events
        trace.Intervals |> List.forall (fun i -> i.End >= i.Start)
    Check.QuickThrowOnFailure prop
