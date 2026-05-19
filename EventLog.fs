module AvdStats.EventLog

open System
open System.Diagnostics.Eventing.Reader

type LogEvent = {
    Id: int
    TimeCreated: DateTimeOffset
    Provider: string
    Message: string option
    Properties: string list
}

type QueryError =
    | AccessDenied
    | ChannelNotFound of string
    | QueryFailed of string

let private tryGetMessage (record: EventLogRecord) =
    try record.FormatDescription() |> Option.ofObj
    with :? EventLogException -> None

let private toLogEvent (record: EventLogRecord) : LogEvent =
    { Id = record.Id
      TimeCreated =
          record.TimeCreated
          |> Option.ofNullable
          |> Option.map DateTimeOffset
          |> Option.defaultValue DateTimeOffset.MinValue
      Provider = record.ProviderName |> Option.ofObj |> Option.defaultValue ""
      Message = tryGetMessage record
      Properties = record.Properties |> Seq.map (fun p -> string p.Value) |> Seq.toList }

let queryEvents (channel: string) (xpathQuery: string) : Result<LogEvent list, QueryError> =
    try
        let query = EventLogQuery(channel, PathType.LogName, xpathQuery)
        use reader = new EventLogReader(query)
        let rec readAll acc =
            match reader.ReadEvent() with
            | null -> List.rev acc
            | record ->
                use r = record :?> EventLogRecord
                readAll (toLogEvent r :: acc)
        Ok(readAll [])
    with
    | :? UnauthorizedAccessException -> Error AccessDenied
    | :? EventLogNotFoundException -> Error(ChannelNotFound channel)
    | ex -> Error(QueryFailed ex.Message)

let queryByTimeRange
    (channel: string)
    (eventIds: int list)
    (from: DateTimeOffset)
    (until: DateTimeOffset)
    : Result<LogEvent list, QueryError> =
    let idFilter =
        if eventIds.IsEmpty then
            ""
        else
            let ids = eventIds |> List.map (sprintf "EventID=%d") |> String.concat " or "
            sprintf " and (%s)" ids

    let xpath =
        sprintf
            "*[System[TimeCreated[@SystemTime>='%s' and @SystemTime<='%s']%s]]"
            (from.UtcDateTime.ToString("o"))
            (until.UtcDateTime.ToString("o"))
            idFilter

    queryEvents channel xpath

let querySystemPowerEvents (from: DateTimeOffset) (until: DateTimeOffset) : Result<LogEvent list, QueryError> =
    // 42=sleep/hibernate, 107=resume, 1074=shutdown initiated, 6006=clean shutdown, 6008=unexpected shutdown
    queryByTimeRange "System" [42; 107; 1074; 6006; 6008] from until

let queryLockEvents (from: DateTimeOffset) (until: DateTimeOffset) : Result<LogEvent list, QueryError> =
    // 4800=workstation locked, 4801=workstation unlocked
    queryByTimeRange "Security" [4800; 4801] from until

let makeIdXPath (ids: int list) =
    if ids.IsEmpty then "*"
    else
        let clause = ids |> List.map (sprintf "EventID=%d") |> String.concat " or "
        sprintf "*[System[%s]]" clause

let subscribeChannel (channel: string) (xpathQuery: string) (callback: LogEvent -> unit) (onError: exn -> unit) : IDisposable =
    try
        let query = EventLogQuery(channel, PathType.LogName, xpathQuery)
        let watcher = new EventLogWatcher(query)
        watcher.EventRecordWritten.Add(fun args ->
            match args.EventException with
            | null ->
                match args.EventRecord with
                | null -> ()
                | record ->
                    use r = record :?> EventLogRecord
                    callback (toLogEvent r)
            | ex -> onError ex)
        watcher.Enabled <- true
        watcher :> IDisposable
    with ex ->
        onError ex
        { new IDisposable with member _.Dispose() = () }
