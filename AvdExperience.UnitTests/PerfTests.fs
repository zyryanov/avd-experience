module AvdStats.UnitTests.PerfTests

open System
open Xunit
open FsUnit.Xunit
open AvdStats.SysInfo
open AvdStats.PerfMonitor

// ── SysInfo: byte / percent helpers ─────────────────────────────────────────

[<Fact>]
let ``formatBytes scales to GiB`` () =
    formatBytes (8.0 * 1024.0 * 1024.0 * 1024.0) |> should equal "8.0 GiB"

[<Fact>]
let ``formatBytes keeps small values in bytes`` () =
    formatBytes 512.0 |> should equal "512.0 B"

[<Fact>]
let ``formatBytes negative is dash`` () =
    formatBytes -1.0 |> should equal "—"

[<Fact>]
let ``ramUsedPct computes half`` () =
    ramUsedPct 1000UL 500UL |> should (equalWithin 0.001) 50.0

[<Fact>]
let ``ramUsedPct zero total is zero`` () =
    ramUsedPct 0UL 0UL |> should equal 0.0

[<Fact>]
let ``diskUsedPct computes quarter`` () =
    diskUsedPct 400L 300L |> should (equalWithin 0.001) 25.0

[<Fact>]
let ``diskUsedPct zero total is zero`` () =
    diskUsedPct 0L 0L |> should equal 0.0

// ── PerfMonitor: sparkline ──────────────────────────────────────────────────

[<Fact>]
let ``sparkBar 0 is lowest block`` () =
    sparkBar 0.0 |> should equal '▁'

[<Fact>]
let ``sparkBar 100 is full block`` () =
    sparkBar 100.0 |> should equal '█'

[<Fact>]
let ``sparkBar clamps above 100`` () =
    sparkBar 150.0 |> should equal '█'

[<Fact>]
let ``sparkBar clamps below 0`` () =
    sparkBar -20.0 |> should equal '▁'

[<Fact>]
let ``sparkline maps each value`` () =
    sparkline [ 0.0; 100.0 ] |> should equal "▁█"

[<Fact>]
let ``sparkline empty is empty`` () =
    sparkline [] |> should equal ""

// ── PerfMonitor: rolling buffer + aggregation ───────────────────────────────

let private mk cpu : PerfSample =
    { Time = DateTimeOffset.Now; CpuPct = cpu; RamPct = 0.0; RamUsedMB = 0.0; DiskPct = 0.0 }

[<Fact>]
let ``pushSample keeps list under the cap`` () =
    pushSample 3 [ mk 1.0; mk 2.0 ] (mk 3.0) |> List.length |> should equal 3

[<Fact>]
let ``pushSample drops the oldest over the cap`` () =
    pushSample 2 [ mk 1.0; mk 2.0 ] (mk 3.0)
    |> List.map (fun s -> s.CpuPct)
    |> should equal [ 2.0; 3.0 ]

[<Fact>]
let ``average of cpu field`` () =
    [ mk 10.0; mk 20.0; mk 30.0 ] |> average (fun s -> s.CpuPct) |> should (equalWithin 0.001) 20.0

[<Fact>]
let ``average of empty is zero`` () =
    [] |> average (fun s -> s.CpuPct) |> should equal 0.0

[<Fact>]
let ``peak of cpu field`` () =
    [ mk 10.0; mk 55.0; mk 30.0 ] |> peak (fun s -> s.CpuPct) |> should (equalWithin 0.001) 55.0

[<Fact>]
let ``peak of empty is zero`` () =
    [] |> peak (fun s -> s.CpuPct) |> should equal 0.0
