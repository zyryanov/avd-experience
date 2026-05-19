module AvdStats.Report

open System
open Spectre.Console
open AvdStats.Stats

let fmtTime (ts: TimeSpan) = sprintf "%dh %02dm" (int ts.TotalHours) ts.Minutes

let formatDuration (ts: TimeSpan) =
    if ts.TotalSeconds < 60.0 then sprintf "%ds" (int ts.TotalSeconds)
    elif ts.TotalSeconds < 3600.0 then sprintf "%dm %ds" ts.Minutes ts.Seconds
    else sprintf "%dh %dm %ds" (int ts.TotalHours) ts.Minutes ts.Seconds

let formatKind (s: (IntervalKind * DateTimeOffset) option) =
    match s with
    | None -> "None"
    | Some (k, _) -> sprintf "%A" k

let stateColor (s: (IntervalKind * DateTimeOffset) option) =
    match s with
    | None                 -> "grey"
    | Some (Active, _)     -> "green"
    | Some (Connecting, _) -> "yellow"
    | Some (Paused, _)     -> "steelblue1"
    | Some (Issue, _)      -> "red"

let stateMarkup (s: (IntervalKind * DateTimeOffset) option) =
    sprintf "[%s]%s[/]" (stateColor s) (formatKind s)

let printTrace (traces: EventTrace list) =
    traces
    |> List.filter (fun t -> t.StateBefore <> t.StateAfter)
    |> List.iter (fun t ->
        let ts = t.Event.TimeCreated.LocalDateTime.ToString "MM-dd HH:mm:ss"
        let durStr =
            match t.ClosedInterval with
            | Some iv -> sprintf "  [dim](%s: %s)[/]" (formatKind (Some (iv.Kind, iv.Start))) (formatDuration (iv.End - iv.Start))
            | None -> ""
        let reportStr =
            if t.ReportContribution > TimeSpan.Zero
            then sprintf "  [magenta]+%s[/]" (formatDuration t.ReportContribution)
            else ""
        AnsiConsole.MarkupLine(sprintf "[grey]%s[/] [dim]#%-4d[/]  %s [grey]→[/] %s%s%s"
            ts t.Event.Id (stateMarkup t.StateBefore) (stateMarkup t.StateAfter) durStr reportStr))

let printStats (stats: PeriodStats) =
    let table = Table()
    table.Border <- TableBorder.Rounded

    table.AddColumn(TableColumn("[bold white]Date[/]").LeftAligned())         |> ignore
    table.AddColumn(TableColumn("[green bold]Active[/]").RightAligned())      |> ignore
    table.AddColumn(TableColumn("[yellow bold]Connecting[/]").RightAligned()) |> ignore
    table.AddColumn(TableColumn("[steelblue1 bold]Paused[/]").RightAligned()) |> ignore
    table.AddColumn(TableColumn("[red bold]Issue[/]").RightAligned())         |> ignore
    table.AddColumn(TableColumn("[dim]Issues#[/]").RightAligned())            |> ignore
    table.AddColumn(TableColumn("[magenta bold]Report[/]").RightAligned())    |> ignore

    for d in stats.ByDay do
        let hasIssues = d.IssueCount > 0
        let issueTime  = if hasIssues then sprintf "[red]%s[/]"      (fmtTime d.IssueTime) else sprintf "[dim]%s[/]"  (fmtTime d.IssueTime)
        let issueCount = if hasIssues then sprintf "[red bold]%d[/]" d.IssueCount          else sprintf "[dim]%d[/]" d.IssueCount
        table.AddRow(
            d.Date.ToString("yyyy-MM-dd"),
            sprintf "[green]%s[/]"      (fmtTime d.ActiveTime),
            sprintf "[yellow]%s[/]"     (fmtTime d.ConnectingTime),
            sprintf "[steelblue1]%s[/]" (fmtTime d.PausedTime),
            issueTime,
            issueCount,
            sprintf "[magenta]%s[/]"    (fmtTime d.ReportTime)
        ) |> ignore

    if stats.ByDay.Length > 1 then
        table.AddEmptyRow() |> ignore

        let tIssue = if stats.TotalIssueCount > 0 then sprintf "[red bold]%s[/]" (fmtTime stats.TotalIssue) else sprintf "[dim]%s[/]" (fmtTime stats.TotalIssue)
        let tCount = if stats.TotalIssueCount > 0 then sprintf "[red bold]%d[/]" stats.TotalIssueCount      else sprintf "[dim]%d[/]" stats.TotalIssueCount

        table.AddRow(
            "[bold]TOTAL[/]",
            sprintf "[green bold]%s[/]"      (fmtTime stats.TotalActive),
            sprintf "[yellow bold]%s[/]"     (fmtTime stats.TotalConnecting),
            sprintf "[steelblue1 bold]%s[/]" (fmtTime stats.TotalPaused),
            tIssue,
            tCount,
            sprintf "[magenta bold]%s[/]"    (fmtTime stats.TotalReport)
        ) |> ignore

    AnsiConsole.WriteLine()
    AnsiConsole.Write(table)
