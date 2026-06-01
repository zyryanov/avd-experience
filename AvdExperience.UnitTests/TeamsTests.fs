module AvdStats.UnitTests.TeamsTests

open System
open Xunit
open FsUnit.Xunit
open AvdStats.PerfMonitor
open AvdStats.Teams

let private mk cpu ram rtt fps : PerfSample =
    { Time = DateTimeOffset.Now; CpuPct = cpu; RamPct = ram; RamUsedMB = 0.0; DiskPct = 0.0
      DiskReadBps = 0.0; DiskWriteBps = 0.0; NetSentBps = 0.0; NetRecvBps = 0.0
      RttMs = rtt; OutputFps = fps; EncodingTimeMs = 0.0; FrameQuality = 0.0
      FramesSkippedSec = 0.0; LossRate = 0.0 }

let private sampleSummary recipient hasIssues =
    { MachineName = "TEST-HOST"
      WindowMinutes = 10
      Samples = [ mk 10.0 50.0 100.0 30.0; mk 20.0 60.0 150.0 25.0; mk 30.0 70.0 200.0 20.0 ]
      Recipient = recipient
      HasIssues = hasIssues }

// ── buildSummary ────────────────────────────────────────────────────────────

[<Fact>]
let ``buildSummary marks payload as MessageCard`` () =
    let json = buildSummary (sampleSummary None false)
    json |> should haveSubstring "\"@type\":\"MessageCard\""
    json |> should haveSubstring "\"@context\":\"https://schema.org/extensions\""

[<Fact>]
let ``buildSummary includes machine name in title`` () =
    let json = buildSummary (sampleSummary None false)
    json |> should haveSubstring "AVD perf — TEST-HOST"

[<Fact>]
let ``buildSummary uses blue theme color when no issues`` () =
    let json = buildSummary (sampleSummary None false)
    json |> should haveSubstring "\"themeColor\":\"#0078D7\""

[<Fact>]
let ``buildSummary uses red theme color when issues present`` () =
    let json = buildSummary (sampleSummary None true)
    json |> should haveSubstring "\"themeColor\":\"#D13438\""

[<Fact>]
let ``buildSummary omits recipient field when None`` () =
    let json = buildSummary (sampleSummary None false)
    json.Contains "\"recipient\"" |> should equal false

[<Fact>]
let ``buildSummary includes recipient field when Some`` () =
    let json = buildSummary (sampleSummary (Some "user@example.com") false)
    json |> should haveSubstring "\"recipient\":\"user@example.com\""

[<Fact>]
let ``buildSummary omits recipient when value is whitespace`` () =
    let json = buildSummary (sampleSummary (Some "   ") false)
    json.Contains "\"recipient\"" |> should equal false

[<Fact>]
let ``buildSummary includes core metric facts`` () =
    let json = buildSummary (sampleSummary None false)
    for label in [ "CPU avg/peak"; "RAM avg/peak"; "RTT avg/peak"; "FPS avg/peak"; "Loss avg/peak" ] do
        json |> should haveSubstring label

[<Fact>]
let ``buildSummary reports correct average and peak for CPU`` () =
    let json = buildSummary (sampleSummary None false)
    // CPU: avg of 10/20/30 = 20.0%, peak = 30.0%
    json |> should haveSubstring "20.0% / 30.0%"

[<Fact>]
let ``buildSummary handles empty sample list`` () =
    let empty = { sampleSummary None false with Samples = [] }
    let json = buildSummary empty
    json |> should haveSubstring "0 samples"
    // No facts when no samples
    json |> should haveSubstring "\"facts\":[]"

[<Fact>]
let ``buildSummary escapes quotes in machine name`` () =
    let s = { sampleSummary None false with MachineName = "evil\"name" }
    let json = buildSummary s
    json |> should haveSubstring "evil\\\"name"

// ── parseDotenv ─────────────────────────────────────────────────────────────

[<Fact>]
let ``parseDotenv reads a simple KEY=VALUE`` () =
    let m = parseDotenv "TEAMS_WEBHOOK_URL=https://example.com/x"
    Map.find "TEAMS_WEBHOOK_URL" m |> should equal "https://example.com/x"

[<Fact>]
let ``parseDotenv ignores blank lines and comments`` () =
    let txt = "\n# comment line\nFOO=bar\n\n   # indented comment\nBAZ=qux\n"
    let m = parseDotenv txt
    m |> Map.count |> should equal 2
    Map.find "FOO" m |> should equal "bar"
    Map.find "BAZ" m |> should equal "qux"

[<Fact>]
let ``parseDotenv strips surrounding double quotes`` () =
    let m = parseDotenv "URL=\"https://q.example/path?x=1\""
    Map.find "URL" m |> should equal "https://q.example/path?x=1"

[<Fact>]
let ``parseDotenv strips surrounding single quotes`` () =
    let m = parseDotenv "URL='value'"
    Map.find "URL" m |> should equal "value"

[<Fact>]
let ``parseDotenv keeps embedded equals signs in value`` () =
    let m = parseDotenv "URL=https://q.example/path?api-version=1&sig=abc=="
    Map.find "URL" m |> should equal "https://q.example/path?api-version=1&sig=abc=="

[<Fact>]
let ``parseDotenv skips lines without equals sign`` () =
    let m = parseDotenv "no_equals_here\nKEY=ok"
    m |> Map.count |> should equal 1
    Map.find "KEY" m |> should equal "ok"
