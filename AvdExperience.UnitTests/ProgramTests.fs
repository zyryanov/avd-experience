module AvdStats.UnitTests.ProgramTests

open Xunit
open FsUnit.Xunit
open Argu
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

// ── CLI parser: the service subcommand must be discoverable via --help ────────
// Regression: 'service' was dispatched before the Argu parser, so --help never
// mentioned the service control commands. It is now a nested ParseResults
// subcommand (like 'config'), so it is listed under SUBCOMMANDS in --help.

let parser = ArgumentParser.Create<Program.Args>(programName = "avd-experience")

[<Fact>]
let ``usage lists the service subcommand and its verbs`` () =
    let usage = parser.PrintUsage()
    usage.Contains "service" |> should equal true
    usage.Contains "uninstall" |> should equal true

[<Fact>]
let ``'service status' parses as the Service subcommand`` () =
    let args = parser.ParseCommandLine [| "service"; "status" |]
    (args.GetResult Program.Service).Contains Program.ServiceArgs.Status |> should equal true

[<Fact>]
let ``service parsing is position-independent`` () =
    let args = parser.ParseCommandLine [| "-s"; "2026-05-01"; "service"; "install" |]
    (args.GetResult Program.Service).Contains Program.ServiceArgs.Install |> should equal true
    // Program.Start is ambiguous now that ServiceArgs also has a Start case
    args.GetResult Program.Args.Start |> should equal "2026-05-01"

[<Fact>]
let ``bare 'service' parses without a verb`` () =
    let args = parser.ParseCommandLine [| "service" |]
    let service = args.GetResult Program.Service
    service.Contains Program.ServiceArgs.Status |> should equal false
    service.Contains Program.ServiceArgs.Start |> should equal false

// ── The config subcommand ────────────────────────────────────────────────────

[<Fact>]
let ``'config show' parses as Show`` () =
    let args = parser.ParseCommandLine [| "config"; "show" |]
    let cfg = args.GetResult Program.Config
    cfg.Contains Program.ConfigArgs.Show |> should equal true

[<Fact>]
let ``'config set retention-days 30' parses key and value`` () =
    let args = parser.ParseCommandLine [| "config"; "set"; "retention-days"; "30" |]
    let key, value = (args.GetResult Program.Config).GetResult Program.ConfigArgs.Set
    key |> should equal "retention-days"
    value |> should equal "30"

[<Fact>]
let ``'config reset retention-days' parses`` () =
    let args = parser.ParseCommandLine [| "config"; "reset"; "retention-days" |]
    (args.GetResult Program.Config).GetResult Program.ConfigArgs.Reset |> should equal "retention-days"

[<Fact>]
let ``config parsing is position-independent`` () =
    let args = parser.ParseCommandLine [| "-s"; "2026-05-01"; "config"; "show" |]
    (args.GetResult Program.Config).Contains Program.ConfigArgs.Show |> should equal true
    args.GetResult Program.Args.Start |> should equal "2026-05-01"

[<Fact>]
let ``usage lists the config subcommand and its verbs`` () =
    let usage = parser.PrintUsage()
    usage.Contains "config" |> should equal true
    usage.Contains "retention-days" |> should equal true

