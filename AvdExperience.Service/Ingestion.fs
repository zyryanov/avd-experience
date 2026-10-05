module AvdStats.Service.Ingestion

// Append-only ingestion core: the service's ONLY live responsibility is appending
// raw events (+ per-channel watermarks) transactionally and idempotently. Arrival
// order is irrelevant — Intervals and StateSnapshots are DERIVED views recomputed
// from the stored events in the canonical order by `derive`, so out-of-order live
// delivery can never corrupt persisted state, and restarts need no snapshot
// restore (the fold over RawEvents, continued from the retention seed, *is* the state).
// Kept free of hosting concerns (BackgroundService, DI) so it can be
// integration-tested directly; IngestionWorker.fs is the thin hosted shell.

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Data.Sqlite
open AvdStats.EventLog
open AvdStats.Events
open AvdStats.Stats
open AvdStats.DbRepository

let systemIds   = [42; 107; 1074; 6006; 6008]   // sleep/resume/shutdown
let securityIds = [4800; 4801]                 // workstation lock/unlock

let private channels = [ rdpChannel; systemChannel; securityChannel ]

// The backlog window itself (initialBacklogDays) lives in DbRepository so the CLI
// can phrase its partial-history warning without referencing the service project.

let private replayQuery (channel: string) (from: DateTimeOffset) (until: DateTimeOffset) =
    match channel with
    | c when c = systemChannel   -> queryByTimeRange systemChannel systemIds from until
    | c when c = securityChannel -> queryByTimeRange securityChannel securityIds from until
    | _                          -> queryByTimeRange channel [] from until

/// Abstraction over "read events from a channel in [from, until]". The real service
/// reads the Windows event log; tests substitute a fake so no live channels are touched.
type EventLogSource = string -> DateTimeOffset -> DateTimeOffset -> LogEvent list

let defaultSource : EventLogSource =
    fun channel from until ->
        replayQuery channel from until |> Result.defaultValue []

/// Buffers events delivered by live subscriptions while the backlog replay runs.
///
/// `EventLogWatcher` (no bookmark) only raises events logged *after* activation, so
/// subscriptions must be enabled *before* the backlog replay — a replay-then-subscribe
/// order loses every event that arrives during the replay. While replay runs, `Submit`
/// enqueues; `StopBuffering` atomically switches to pass-through and drains the queue,
/// so every event reaches `handle` exactly once (events the replay already committed
/// are skipped downstream by the append dedup guard). Order within the buffer is
/// irrelevant — appends are order-independent and derived state is recomputed.
/// All members serialize on `lockObj` (the append lock).
type LiveEventBuffer(lockObj: obj, handle: string -> LogEvent -> unit) =
    let queue = Queue<string * LogEvent>()
    let buffering = ref true

    member _.Submit(channel: string) (e: LogEvent) =
        lock lockObj (fun () ->
            if !buffering then queue.Enqueue (channel, e)
            else handle channel e)

    member _.StopBuffering() =
        lock lockObj (fun () ->
            buffering := false
            while queue.Count > 0 do
                let ch, e = queue.Dequeue()
                handle ch e)

/// Append one raw event and advance its channel watermark, atomically and
/// idempotently: (Channel, EventId, TimeCreated) is the dedup key, so replay
/// boundary events, the warmup→live handover overlap, and watcher re-delivery
/// are all skipped. This is the single write path for live and replayed events.
///
/// Events at/before the retention cutoff are dropped without being stored or
/// advancing the watermark: the retention seed already summarizes everything
/// before the cutoff, and re-arrived old events (backlog replay after a long
/// outage, clock skew) would otherwise fold *backward* on top of the seed state
/// and corrupt the derivation. Watermarks are left untouched — a dropped event
/// is simply re-read and re-dropped on each restart (bounded by OS log rotation).
///
/// Failure semantics: a failed transaction rolls back (watermark not advanced), so
/// a failed *replayed* append is re-read from the event log on the next restart.
/// A failed *live* append is not redelivered by the watcher — it is recovered by
/// the next replay only if no later event in the same channel has advanced the
/// watermark past it in the meantime.
let appendEvent (dbPath: string) (lockObj: obj) (logger: ILogger) (channel: string) (e: LogEvent) =
    lock lockObj (fun () ->
        try
            use conn = new SqliteConnection(getConnectionString dbPath)
            conn.Open()
            use ctx = createContext conn
            ctx.BeginTransaction()

            if eventExists ctx channel e then
                logger.LogDebug("Skipping already-stored event #{EventId} from {Channel}", e.Id, channel)
            elif tryGetMeta ctx retentionStartKey
                 |> Option.map parseIso
                 |> Option.exists (fun cutoff -> e.TimeCreated <= cutoff) then
                logger.LogInformation(
                    "Skipping event #{EventId} from {Channel}: logged at {Time} — before the retention cutoff",
                    e.Id, channel, e.TimeCreated)
            else
                saveEvent ctx channel e
                upsertWatermark ctx channel e.TimeCreated

            ctx.CommitTransaction()
        with ex ->
            logger.LogError(ex, "Failed to append event #{EventId} from {Channel}", e.Id, channel))

/// Query the event log source for all events at/after each channel's watermark and
/// append them. The lower bound is inclusive: a restart between two same-timestamp
/// events must not lose the unprocessed sibling — the dedup guard skips the one that
/// is already stored. Sorted by the canonical key so backlog RecordIds are assigned
/// chronologically. Returns the number of events fed through.
let replayPending (source: EventLogSource) (dbPath: string) (lockObj: obj) (logger: ILogger) : int =
    let froms =
        use ctx = openContext dbPath
        channels
        |> List.map (fun ch -> ch, getWatermark ctx ch |> Option.defaultValue (DateTimeOffset.UtcNow.AddDays -initialBacklogDays))
        |> Map.ofList

    let now = DateTimeOffset.UtcNow
    let tagged =
        channels
        |> List.collect (fun ch ->
            source ch froms[ch] now
            |> List.filter (fun e -> e.TimeCreated >= froms[ch])   // at/after the watermark
            |> List.map (fun e -> ch, e))
        |> List.sortBy (fun (ch, e) -> e.TimeCreated, channelRank ch, e.Id)

    for (ch, e) in tagged do
        appendEvent dbPath lockObj logger ch e

    if not tagged.IsEmpty then
        logger.LogInformation("Replayed {Count} backlog events", tagged.Length)
    tagged.Length

/// Two replay passes: the second catches events that arrived while the first pass was
/// querying. Already-stored events are skipped by the dedup guard, so both passes are
/// idempotent.
let replayPendingPasses (source: EventLogSource) (dbPath: string) (lockObj: obj) (logger: ILogger) : unit =
    replayPending source dbPath lockObj logger |> ignore
    replayPending source dbPath lockObj logger |> ignore

/// Recompute the derived tables (Intervals + StateSnapshots) from the stored events,
/// folded in the canonical order. The derived tables are a pure function of
/// RawEvents: any arrival-order skew in the live append path is corrected here.
/// O(history) per call (milliseconds at ~10⁴ events) — it runs at warmup and on a
/// periodic tick when new events have arrived. Events appended after the fold's
/// read are picked up by the next derive, so the derived views are eventually
/// consistent within one tick. Returns the final derived state plus the max
/// RecordId of the snapshot that was folded — callers tracking derivation progress
/// must use this id, not a later max(RecordId) probe: a probe read after the fold
/// can include an event appended mid-derive and mark it derived before it is.
let derive (dbPath: string) (lockObj: obj) (logger: ILogger)
    : (IntervalKind * DateTimeOffset) option * bool * (IntervalKind * DateTimeOffset) option * ConnectReason option * int64 =
    let rows, seed =
        use ro = openReadContext dbPath
        getAllEventRows ro, getFoldSeed ro
    // Continued from the retention seed, not from nothing: pruning removes old
    // events, and the seed carries the exact machine state at the cutoff, so the
    // fold over the retained history is identical to the fold over all of it.
    let closed, st, lk, sh, rs =
        foldHistory seed.State seed.Locked seed.Shadow seed.Reason (rows |> List.map (fun (_, _, e) -> e))
    let maxRecordId = rows |> List.fold (fun acc (rid, _, _) -> max acc rid) 0L

    lock lockObj (fun () ->
        try
            use conn = new SqliteConnection(getConnectionString dbPath)
            conn.Open()
            use ctx = createContext conn
            ctx.BeginTransaction()

            let stored =
                getAllIntervals ctx
                |> List.map (fun i -> i.Kind, i.StartTime, i.EndTime, i.ConnectReason, i.DurationMs, i.ReportContribMs)
            let expected =
                closed
                |> List.map (fun (iv, reason) ->
                    let dur = iv.End - iv.Start
                    intervalKindToString iv.Kind,
                    formatIso iv.Start,
                    formatIso iv.End,
                    connectReasonColumn reason,
                    int64 dur.TotalMilliseconds,
                    int64 (intervalReportContrib iv.Kind reason dur).TotalMilliseconds)
            if stored <> expected then
                clearIntervals ctx
                closed |> List.iter (fun (iv, reason) -> saveInterval ctx iv reason)
                logger.LogInformation("Derived intervals rewritten: {Count} rows", expected.Length)

            saveSnapshot ctx st lk sh rs
            ctx.CommitTransaction()
        with ex ->
            logger.LogError(ex, "Failed to derive intervals/snapshot"))

    st, lk, sh, rs, maxRecordId

/// Replay everything since the watermarks, then derive. One-call warmup for callers
/// that do not interleave live subscriptions (the worker subscribes first and uses
/// the pieces directly). Returns the final derived state and the folded max RecordId.
let warmup (source: EventLogSource) (dbPath: string) (lockObj: obj) (logger: ILogger)
    : (IntervalKind * DateTimeOffset) option * bool * (IntervalKind * DateTimeOffset) option * ConnectReason option * int64 =
    replayPendingPasses source dbPath lockObj logger
    derive dbPath lockObj logger
