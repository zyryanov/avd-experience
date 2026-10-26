module AvdStats.UnitTests.ReportTests

open System
open Xunit
open FsUnit.Xunit
open AvdStats.Stats
open AvdStats.Report

let private day (date: DateOnly) =
    { Date          = date
      ActiveTime    = TimeSpan.FromHours 4.0
      ConnectingTime = TimeSpan.FromMinutes 10.0
      PausedTime    = TimeSpan.FromHours 1.0
      IssueTime     = TimeSpan.Zero
      IssueCount    = 0
      ReportTime    = TimeSpan.Zero }

// 2026-10-24 is a Saturday, 26th a Monday.
let private saturday = DateOnly(2026, 10, 24)
let private sunday   = DateOnly(2026, 10, 25)
let private monday   = DateOnly(2026, 10, 26)

[<Fact>]
let ``isWeekend is true only for Saturday and Sunday`` () =
    isWeekend saturday |> should equal true
    isWeekend sunday   |> should equal true
    isWeekend monday   |> should equal false

[<Theory>]
[<InlineData("2026-10-30")>] // Friday
[<InlineData("2026-10-26")>] // Monday
[<InlineData("2026-10-28")>] // Wednesday
let ``isWeekend is false for work days`` (iso: string) =
    isWeekend (DateOnly.Parse iso) |> should equal false

[<Fact>]
let ``dayRow dims every non-signal cell on a weekend`` () =
    let cells = dayRow (day saturday)
    cells.Length |> should equal 7
    for cell in cells do
        cell |> should startWith "[dim]"

[<Fact>]
let ``dayRow does not dim work day cells`` () =
    // zero-valued issue/report cells are dim on every row (pre-existing styling),
    // so only the always-coloured cells are checked here
    let cells = dayRow (day monday)
    cells[0..3] |> List.iter (fun cell -> cell |> should not' (startWith "[dim]"))

[<Fact>]
let ``dayRow keeps a weekend issue visible`` () =
    let cells = dayRow { day saturday with IssueTime = TimeSpan.FromMinutes 20.0; IssueCount = 2 }
    cells[4] |> should equal "[red]0h 20m[/]"
    cells[5] |> should equal "[red bold]2[/]"

[<Fact>]
let ``dayRow dims a weekend report contribution`` () =
    let cells = dayRow { day sunday with ReportTime = TimeSpan.FromMinutes 20.0 }
    cells[6] |> should equal "[dim][magenta]0h 20m[/][/]"

[<Fact>]
let ``dayRow dims a weekend report contribution even on a day with issues`` () =
    let cells = dayRow { day sunday with ReportTime = TimeSpan.FromMinutes 20.0; IssueTime = TimeSpan.FromMinutes 5.0; IssueCount = 1 }
    cells[4] |> should equal "[red]0h 05m[/]"
    cells[5] |> should equal "[red bold]1[/]"
    cells[6] |> should equal "[dim][magenta]0h 20m[/][/]"

[<Fact>]
let ``dayRow includes the three-letter weekday in the date cell`` () =
    let cells = dayRow (day monday)
    cells[0] |> should equal "2026-10-26 Mon"
    (dayRow (day saturday))[0] |> should equal "[dim]2026-10-24 Sat[/]"
