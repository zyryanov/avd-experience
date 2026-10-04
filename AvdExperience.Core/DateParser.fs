module AvdStats.DateParser

open System
open System.Text.RegularExpressions

let (|RelativeOffset|_|) (refDate: DateTime) (s: string) =
    let m = Regex.Match(s, @"^([+-]?\d+)\s*([a-zA-Z]+)(?:\s+ago)?$")
    if m.Success then
        let qtyStr = m.Groups.[1].Value
        let unitStr = m.Groups.[2].Value.ToLowerInvariant()
        match Int32.TryParse qtyStr with
        | true, n ->
            let isAgo = s.EndsWith "ago"
            let factor =
                if isAgo then -abs n
                elif qtyStr.StartsWith "+" then abs n
                elif qtyStr.StartsWith "-" then -abs n
                else -abs n
            try
                match unitStr with
                | "d" | "day" | "days"     -> Some (refDate.AddDays(float factor).Date)
                | "w" | "week" | "weeks"   -> Some (refDate.AddDays(float (factor * 7)).Date)
                | "m" | "month" | "months" -> Some (refDate.AddMonths(factor).Date)
                | "y" | "year" | "years"   -> Some (refDate.AddYears(factor).Date)
                | _ -> None
            with :? ArgumentOutOfRangeException -> None
        | _ -> None
    else None

let parseDateAt (refDate: DateTime) (s: string) =
    let trimmed = if isNull s then "" else s.Trim()
    let lower = trimmed.ToLowerInvariant()
    match lower with
    | "today" | "now" -> Ok refDate.Date
    | "yesterday"     -> Ok (refDate.Date.AddDays -1.0)
    | "tomorrow"      -> Ok (refDate.Date.AddDays 1.0)
    | RelativeOffset refDate d -> Ok d
    | _ ->
        match DateTime.TryParseExact(trimmed, "yyyy-MM-dd", null, Globalization.DateTimeStyles.None) with
        | true, d  -> Ok d.Date
        | false, _ -> Error (sprintf "Invalid date '%s' — expected yyyy-MM-dd or relative (today, yesterday, -1d, -2w, 3 days ago)" s)

let parseDate (s: string) =
    parseDateAt DateTime.Today s

let localMidnight (d: DateTime) =
    let offset = TimeZoneInfo.Local.GetUtcOffset(d)
    DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, offset)

let localEndOfDay (d: DateTime) =
    localMidnight(d.AddDays 1.0).AddTicks -1L
