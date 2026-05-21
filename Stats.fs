module AvdStats.Stats

open System
open AvdStats.EventLog
open AvdStats.Events

type IntervalKind = Active | Connecting | Paused | Issue

type ConnectReason = Initial | PostIssue | PostPause

type Interval = {
    Kind: IntervalKind
    Start: DateTimeOffset
    End: DateTimeOffset
}

type DayStats = {
    Date: DateOnly
    ActiveTime: TimeSpan
    ConnectingTime: TimeSpan
    PausedTime: TimeSpan
    IssueTime: TimeSpan
    IssueCount: int
    ReportTime: TimeSpan
}

type PeriodStats = {
    ByDay: DayStats list
    TotalActive: TimeSpan
    TotalConnecting: TimeSpan
    TotalPaused: TimeSpan
    TotalIssue: TimeSpan
    TotalIssueCount: int
    TotalReport: TimeSpan
}


type EventTrace = {
    Event: LogEvent
    IsRelevant: bool
    StateBefore: (IntervalKind * DateTimeOffset) option
    StateAfter:  (IntervalKind * DateTimeOffset) option
    ClosedInterval: Interval option
    ReportContribution: TimeSpan
}

type IntervalSlice = {
    Interval: Interval
    Date: DateOnly
    DurationOnDate: TimeSpan
}

type TraceResult = {
    Intervals: Interval list
    EventTraces: EventTrace list
    IntervalSlices: IntervalSlice list
}


let nextConnectReason
    (prevState: (IntervalKind * DateTimeOffset) option)
    (nextState: (IntervalKind * DateTimeOffset) option)
    (currentReason: ConnectReason option)
    (closedInterval: Interval option)
    : ConnectReason option =
    let prevKind = prevState |> Option.map fst
    let nextKind = nextState |> Option.map fst
    // PostIssue expires when leaving a Paused interval of >= 3h
    let reasonAfterLongPause =
        match currentReason, closedInterval with
        | Some PostIssue, Some iv when iv.Kind = Paused && (iv.End - iv.Start) >= TimeSpan.FromHours 3.0 -> None
        | _ -> currentReason
    match nextKind with
    | Some Connecting when prevKind <> Some Connecting ->
        match prevKind with
        | None        -> Some Initial
        | Some Issue  -> Some PostIssue
        | Some Paused ->
            // Preserve PostIssue through a short Paused detour so Active recovery overhead still applies.
            match reasonAfterLongPause with
            | Some PostIssue -> Some PostIssue
            | _              -> Some PostPause
        | _           -> Some PostPause
    | Some Connecting -> currentReason  // re-fire while already Connecting — preserve reason
    | Some Active ->
        match prevKind with
        | Some Issue  -> Some PostIssue  // direct Issue→Active (no Connecting step)
        | Some Paused -> reasonAfterLongPause  // expire PostIssue if Paused >= 3h
        | _           -> currentReason         // carry forward from Connecting→Active
    | Some Paused ->
        match prevKind with
        | Some Connecting -> currentReason   // mid-reconnect abort: preserve PostIssue for retry
        | Some Active ->
            match currentReason with
            | Some PostIssue -> None         // Active(PostIssue) closed: overhead charged, consume it
            | r              -> r
        | _               -> reasonAfterLongPause  // Paused→Paused via unlock: check long duration
    | _ -> None

let intervalReportContrib (closedKind: IntervalKind) (reason: ConnectReason option) (dur: TimeSpan) : TimeSpan =
    let fiveMin    = TimeSpan.FromMinutes 5.0
    let fifteenMin = TimeSpan.FromMinutes 15.0
    match closedKind with
    | Connecting -> dur + match reason with Some Initial -> fiveMin | _ -> TimeSpan.Zero
    | Issue      -> dur
    | Active     -> match reason with Some PostIssue -> min fifteenMin dur | _ -> TimeSpan.Zero
    | Paused     -> TimeSpan.Zero

// Split an interval into (date, duration) segments at local midnight boundaries.
let splitByDay (interval: Interval) : (DateOnly * TimeSpan) list =
    let rec go current acc =
        if current >= interval.End then List.rev acc
        else
            let date = DateOnly.FromDateTime current.LocalDateTime
            let nextMidnight = DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(current.LocalDateTime.Date.AddDays 1.0))
            let segEnd = min nextMidnight interval.End
            go segEnd ((date, segEnd - current) :: acc)
    go interval.Start []

let stepState
    (state: (IntervalKind * DateTimeOffset) option)
    (e: LogEvent)
    : (IntervalKind * DateTimeOffset) option * Interval option =
    let close kind t = Some { Kind = kind; Start = t; End = e.TimeCreated }
    match state with
    | Some (kind, t) when isPowerDown e && (kind = Active || kind = Connecting) ->
        Some (Paused, e.TimeCreated), close kind t
    | _ when isPowerDown e ->
        state, None
    | Some (kind, t) when (kind = Paused || kind = Issue) && isConnectInitiated e ->
        Some (Connecting, e.TimeCreated), close kind t
    | None when isConnectInitiated e ->
        Some (Connecting, e.TimeCreated), None
    | _ when isConnectInitiated e ->
        state, None
    | Some (Connecting, t) when isConnected e ->
        Some (Active, e.TimeCreated), close Connecting t
    | Some (kind, t) when (kind = Paused || kind = Issue) && isConnected e ->
        Some (Active, e.TimeCreated), close kind t
    | None when isConnected e ->
        Some (Active, e.TimeCreated), None
    | Some (Active, _) when isConnected e ->
        state, None
    | Some (Active, t) when isUserDisconnected e ->
        Some (Paused, e.TimeCreated), close Active t
    | Some (Connecting, t) when isUserDisconnected e ->
        Some (Paused, e.TimeCreated), close Connecting t
    | None when isUserDisconnected e ->
        Some (Paused, e.TimeCreated), None
    | Some (Active, t) when isThreadWatchdog e ->
        Some (Issue, e.TimeCreated), close Active t
    | Some (Active, t) when isDisconnected e ->
        Some (Issue, e.TimeCreated), close Active t
    | Some (Connecting, t) when isDisconnected e ->
        Some (Issue, e.TimeCreated), close Connecting t
    | None when isDisconnected e ->
        Some (Issue, e.TimeCreated), None
    | _ ->
        state, None

// Tracks AVD state during workstation lock without generating outward intervals.
// Shadow starts as Paused; unexpected drops stay Paused (absorbed into lock period).
// Only explicit reconnect events advance shadow to Active; powerDown resets to None.
let private shadowStep (shadow: (IntervalKind * DateTimeOffset) option) (e: LogEvent) =
    match shadow with
        | _ when isUserDisconnected e -> None
        | _ when isConnectInitiated e -> Some (Connecting, e.TimeCreated)
        | Some (Connecting, _) when isConnected e -> Some (Active, e.TimeCreated)
        | Some (_, _) when isConnected e -> Some (Active, e.TimeCreated)
        | None when isConnected e -> Some (Active, e.TimeCreated)
        | Some (Active, _) when isThreadWatchdog e -> Some (Paused, e.TimeCreated)
        | Some (Active, _) when isDisconnected e -> Some (Paused, e.TimeCreated)
        | Some (Connecting, _) when isDisconnected e -> Some (Paused, e.TimeCreated)
        | _ -> shadow

// Advance outward state, locked flag, and shadow state by one event.
// Encapsulates lock/unlock routing so callers (monitor, bootstrap) share the same logic.
// Returns: (newState, newLocked, newShadow, closedInterval)
let advanceState
    (state: (IntervalKind * DateTimeOffset) option)
    (locked: bool)
    (shadowState: (IntervalKind * DateTimeOffset) option)
    (e: LogEvent)
    : (IntervalKind * DateTimeOffset) option * bool * (IntervalKind * DateTimeOffset) option * Interval option =
    if isWorkstationLocked e && not locked then
        let outward, closed =
            match state with
            | Some ((Active | Connecting | Issue) as kind, t) ->
                Some (Paused, e.TimeCreated), Some { Kind = kind; Start = t; End = e.TimeCreated }
            | _ -> state, None
        outward, true, state, closed
    elif isWorkstationUnlocked e && locked then
        let closed = match state with Some (Paused, t) -> Some { Kind = Paused; Start = t; End = e.TimeCreated } | _ -> None
        let newState = match shadowState with Some (kind, _) -> Some (kind, e.TimeCreated) | None -> Some (Paused, e.TimeCreated)
        newState, false, None, closed
    elif locked then
        state, true, shadowStep shadowState e, None
    else
        let newState, closed = stepState state e
        newState, false, None, closed

let private buildIntervalsWithTrace (initState: (IntervalKind * DateTimeOffset) option) (initLocked: bool) (periodEnd: DateTimeOffset) (events: LogEvent list) : Interval list * EventTrace list =
    let effectiveEnd = min periodEnd DateTimeOffset.Now

    let trace stateBefore stateAfter closed contribution (e: LogEvent) =
        { Event = e
          IsRelevant = isRelevant e
          StateBefore = stateBefore
          StateAfter = stateAfter
          ClosedInterval = closed
          ReportContribution = contribution }

    let rec walk state connectReason locked shadowState acc traces remaining =
        match remaining with
        | [] ->
            match state with
            | Some (kind, t) ->
                let closed = { Kind = kind; Start = t; End = effectiveEnd }
                List.rev (closed :: acc), List.rev traces
            | None -> List.rev acc, List.rev traces
        | e :: rest ->
            let newState, lk', shadow', closed = advanceState state locked shadowState e
            let contrib =
                match closed with
                | None    -> TimeSpan.Zero
                | Some iv -> intervalReportContrib iv.Kind connectReason (iv.End - iv.Start)
            let reason' = nextConnectReason state newState connectReason closed
            let acc' = match closed with Some iv -> iv :: acc | None -> acc
            walk newState reason' lk' shadow' acc' (trace state newState closed contrib e :: traces) rest

    walk initState None initLocked initState [] [] events

let computeWithTrace (initState: (IntervalKind * DateTimeOffset) option) (initLocked: bool) (periodEnd: DateTimeOffset) (events: LogEvent list) : PeriodStats * TraceResult =
    if events.IsEmpty then
        let empty = { ByDay = []; TotalActive = TimeSpan.Zero; TotalConnecting = TimeSpan.Zero; TotalPaused = TimeSpan.Zero; TotalIssue = TimeSpan.Zero; TotalIssueCount = 0; TotalReport = TimeSpan.Zero }
        let emptyTrace = { Intervals = []; EventTraces = []; IntervalSlices = [] }
        empty, emptyTrace
    else
        let events = events |> List.sortBy (fun e -> e.TimeCreated)
        let intervals, eventTraces = buildIntervalsWithTrace initState initLocked periodEnd events

        let slices =
            intervals
            |> List.collect (fun i ->
                splitByDay i |> List.map (fun (date, dur) -> { Interval = i; Date = date; DurationOnDate = dur }))

        let sumKind kind (segs: IntervalSlice list) =
            segs
            |> List.filter (fun s -> s.Interval.Kind = kind)
            |> List.sumBy (fun s -> s.DurationOnDate.TotalSeconds)
            |> TimeSpan.FromSeconds

        let countKind kind (segs: IntervalSlice list) =
            segs
            |> List.filter (fun s -> s.Interval.Kind = kind)
            |> List.distinctBy (fun s -> s.Interval.Start)
            |> List.length

        let dayReportTime date =
            eventTraces
            |> List.sumBy (fun t ->
                if DateOnly.FromDateTime t.Event.TimeCreated.LocalDateTime = date
                then t.ReportContribution.TotalSeconds
                else 0.0)
            |> TimeSpan.FromSeconds

        let byDay =
            slices
            |> List.groupBy (fun s -> s.Date)
            |> List.map (fun (date, segs) ->
                { Date = date
                  ActiveTime     = sumKind Active     segs
                  ConnectingTime = sumKind Connecting  segs
                  PausedTime     = sumKind Paused      segs
                  IssueTime      = sumKind Issue       segs
                  IssueCount     = countKind Issue     segs
                  ReportTime     = dayReportTime date })
            |> List.sortBy (fun d -> d.Date)

        let stats =
            { ByDay = byDay
              TotalActive      = sumKind Active     slices
              TotalConnecting  = sumKind Connecting  slices
              TotalPaused      = sumKind Paused      slices
              TotalIssue       = sumKind Issue       slices
              TotalIssueCount  = intervals |> List.filter (fun i -> i.Kind = Issue) |> List.length
              TotalReport      = eventTraces |> List.sumBy (fun t -> t.ReportContribution.TotalSeconds) |> TimeSpan.FromSeconds }

        let traceResult = { Intervals = intervals; EventTraces = eventTraces; IntervalSlices = slices }
        stats, traceResult
