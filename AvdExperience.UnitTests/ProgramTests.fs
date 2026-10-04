module AvdStats.UnitTests.ProgramTests

open Xunit
open FsUnit.Xunit
open AvdStats.DateParser

// Program.fs has no explicit module declaration; F# creates implicit module
// named "Program", accessible as Program.parseDate.

[<Fact>]
let ``parseDate valid date returns Ok`` () =
    match parseDate "2025-05-01" with
    | Ok d ->
        d.Year  |> should equal 2025
        d.Month |> should equal 5
        d.Day   |> should equal 1
    | Error msg -> failwith msg

[<Fact>]
let ``parseDate wrong format returns Error`` () =
    match parseDate "01-05-2025" with
    | Error _ -> ()
    | Ok _    -> failwith "Expected Error"

[<Fact>]
let ``parseDate garbage string returns Error`` () =
    match parseDate "not-a-date" with
    | Error _ -> ()
    | Ok _    -> failwith "Expected Error"

[<Fact>]
let ``parseDate empty string returns Error`` () =
    match parseDate "" with
    | Error _ -> ()
    | Ok _    -> failwith "Expected Error"

let refDate = System.DateTime(2026, 10, 15)

[<Theory>]
[<InlineData("today", 2026, 10, 15)>]
[<InlineData("now", 2026, 10, 15)>]
[<InlineData("TODAY", 2026, 10, 15)>]
[<InlineData("yesterday", 2026, 10, 14)>]
[<InlineData("tomorrow", 2026, 10, 16)>]
[<InlineData("-1d", 2026, 10, 14)>]
[<InlineData("-7d", 2026, 10, 8)>]
[<InlineData("1d", 2026, 10, 14)>]
[<InlineData("7d", 2026, 10, 8)>]
[<InlineData("1d ago", 2026, 10, 14)>]
[<InlineData("3 days ago", 2026, 10, 12)>]
[<InlineData("1 day ago", 2026, 10, 14)>]
[<InlineData("+1d", 2026, 10, 16)>]
[<InlineData("-1w", 2026, 10, 8)>]
[<InlineData("2 weeks ago", 2026, 10, 1)>]
[<InlineData("-1m", 2026, 9, 15)>]
[<InlineData("2 months ago", 2026, 8, 15)>]
[<InlineData("-1y", 2025, 10, 15)>]
[<InlineData("1 year ago", 2025, 10, 15)>]
let ``parseDateAt relative patterns return expected date`` (input: string, expY: int, expM: int, expD: int) =
    match parseDateAt refDate input with
    | Ok d ->
        d.Year  |> should equal expY
        d.Month |> should equal expM
        d.Day   |> should equal expD
    | Error msg -> failwith (sprintf "Failed to parse '%s': %s" input msg)

[<Theory>]
[<InlineData("3 bananas")>]
[<InlineData("yesterday at 5pm")>]
[<InlineData("2026/10/15")>]
[<InlineData("invalid-text")>]
let ``parseDateAt invalid inputs return Error`` (input: string) =
    match parseDateAt refDate input with
    | Error _ -> ()
    | Ok d    -> failwith (sprintf "Expected Error for '%s', but got %A" input d)

