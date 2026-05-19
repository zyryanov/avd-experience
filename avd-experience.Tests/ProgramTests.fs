module AvdStats.Tests.ProgramTests

open Xunit
open FsUnit.Xunit

// Program.fs has no explicit module declaration; F# creates implicit module
// named "Program", accessible as Program.parseDate.

[<Fact>]
let ``parseDate valid date returns Ok`` () =
    match Program.parseDate "2025-05-01" with
    | Ok d ->
        d.Year  |> should equal 2025
        d.Month |> should equal 5
        d.Day   |> should equal 1
    | Error msg -> failwith msg

[<Fact>]
let ``parseDate wrong format returns Error`` () =
    match Program.parseDate "01-05-2025" with
    | Error _ -> ()
    | Ok _    -> failwith "Expected Error"

[<Fact>]
let ``parseDate garbage string returns Error`` () =
    match Program.parseDate "not-a-date" with
    | Error _ -> ()
    | Ok _    -> failwith "Expected Error"

[<Fact>]
let ``parseDate empty string returns Error`` () =
    match Program.parseDate "" with
    | Error _ -> ()
    | Ok _    -> failwith "Expected Error"
