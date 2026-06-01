module AvdStats.Teams

open System
open System.IO
open System.Net.Http
open System.Text
open System.Threading.Tasks
open AvdStats.PerfMonitor

// ── pure: payload builder + dotenv ──────────────────────────────────────────

type Summary =
    { MachineName: string
      WindowMinutes: int
      Samples: PerfSample list
      Recipient: string option
      HasIssues: bool }

let private jsonEscape (s: string) =
    let sb = StringBuilder(s.Length + 8)
    for c in s do
        match c with
        | '"'  -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\n' -> sb.Append "\\n"  |> ignore
        | '\r' -> sb.Append "\\r"  |> ignore
        | '\t' -> sb.Append "\\t"  |> ignore
        | c when int c < 0x20 -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
        | c -> sb.Append c |> ignore
    sb.ToString()

let private q (s: string) = "\"" + jsonEscape s + "\""

let private fact (name: string) (value: string) =
    sprintf "{%s:%s,%s:%s}" (q "name") (q name) (q "value") (q value)

let private formatBpsShort (bps: float) =
    if bps >= 1048576.0 then sprintf "%.1f MB/s" (bps / 1048576.0)
    elif bps >= 1024.0  then sprintf "%.1f KB/s" (bps / 1024.0)
    else sprintf "%.0f B/s" bps

let private avgPeakPct (samples: PerfSample list) (field: PerfSample -> float) =
    sprintf "%.1f%% / %.1f%%" (average field samples) (peak field samples)

let private avgPeakBps (samples: PerfSample list) (field: PerfSample -> float) =
    sprintf "%s / %s" (formatBpsShort (average field samples)) (formatBpsShort (peak field samples))

let private avgPeakUnit (unit: string) (samples: PerfSample list) (field: PerfSample -> float) =
    sprintf "%.1f%s / %.1f%s" (average field samples) unit (peak field samples) unit

/// Build the MessageCard JSON body for a perf summary.
let buildSummary (s: Summary) : string =
    let n = List.length s.Samples
    let title = sprintf "AVD perf — %s" s.MachineName
    let text  = sprintf "Window: %d min · %d samples" s.WindowMinutes n
    let themeColor = if s.HasIssues then "#D13438" else "#0078D7"
    let facts =
        if n = 0 then []
        else
            [ fact "CPU avg/peak"        (avgPeakPct s.Samples (fun x -> x.CpuPct))
              fact "RAM avg/peak"        (avgPeakPct s.Samples (fun x -> x.RamPct))
              fact "Disk avg/peak"       (avgPeakPct s.Samples (fun x -> x.DiskPct))
              fact "DiskRead avg/peak"   (avgPeakBps s.Samples (fun x -> x.DiskReadBps))
              fact "DiskWrite avg/peak"  (avgPeakBps s.Samples (fun x -> x.DiskWriteBps))
              fact "NetSent avg/peak"    (avgPeakBps s.Samples (fun x -> x.NetSentBps))
              fact "NetRecv avg/peak"    (avgPeakBps s.Samples (fun x -> x.NetRecvBps))
              fact "RTT avg/peak"        (avgPeakUnit " ms"  s.Samples (fun x -> x.RttMs))
              fact "FPS avg/peak"        (avgPeakUnit " fps" s.Samples (fun x -> x.OutputFps))
              fact "Frames skipped avg"  (sprintf "%.2f/s" (average (fun x -> x.FramesSkippedSec) s.Samples))
              fact "Loss avg/peak"       (avgPeakUnit "%"   s.Samples (fun x -> x.LossRate)) ]
    let factsJson = String.concat "," facts
    let recipientField =
        match s.Recipient with
        | Some r when not (String.IsNullOrWhiteSpace r) -> sprintf ",%s:%s" (q "recipient") (q r)
        | _ -> ""
    sprintf "{%s:%s,%s:%s,%s:%s,%s:%s%s,%s:[{%s:%s,%s:%s,%s:[%s]}]}"
        (q "@type")      (q "MessageCard")
        (q "@context")   (q "https://schema.org/extensions")
        (q "summary")    (q title)
        (q "themeColor") (q themeColor)
        recipientField
        (q "sections")
        (q "activityTitle") (q title)
        (q "text")          (q text)
        (q "facts")         factsJson

/// Parse a minimal `.env` file (KEY=VALUE). Ignores blanks, `#` comments,
/// trims surrounding quotes on the value.
let parseDotenv (content: string) : Map<string, string> =
    let stripQuotes (s: string) =
        if s.Length >= 2 && ((s.StartsWith "\"" && s.EndsWith "\"") || (s.StartsWith "'" && s.EndsWith "'"))
        then s.Substring(1, s.Length - 2)
        else s
    content.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun raw ->
        let line = raw.Trim()
        if line = "" || line.StartsWith "#" then None
        else
            let idx = line.IndexOf '='
            if idx <= 0 then None
            else
                let k = line.Substring(0, idx).Trim()
                let v = line.Substring(idx + 1).Trim() |> stripQuotes
                Some (k, v))
    |> Map.ofArray

let loadDotenv (path: string) : Map<string, string> =
    if File.Exists path then parseDotenv (File.ReadAllText path)
    else Map.empty

// ── effectful: HTTP ─────────────────────────────────────────────────────────

let createClient () : HttpClient =
    let handler = new HttpClientHandler()
    handler.ServerCertificateCustomValidationCallback <-
        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    let c = new HttpClient(handler)
    c.Timeout <- TimeSpan.FromSeconds 15.0
    c

let postAsync (client: HttpClient) (url: string) (body: string) : Task<Result<unit, string>> =
    task {
        try
            use content = new StringContent(body, Encoding.UTF8, "application/json")
            let! resp = client.PostAsync(url, content)
            if resp.IsSuccessStatusCode then return Ok ()
            else
                let! txt = resp.Content.ReadAsStringAsync()
                return Error (sprintf "HTTP %d: %s" (int resp.StatusCode) txt)
        with ex ->
            return Error ex.Message
    }
