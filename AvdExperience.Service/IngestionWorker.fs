module AvdStats.Service.IngestionWorker

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open AvdStats.EventLog
open AvdStats.Service.Ingestion
open AvdStats.DbRepository

/// Hosted shell around the append-only ingestion core (Ingestion.fs):
/// subscribes first (buffered), replays the backlog, drains into live appends,
/// derives the interval/snapshot views, then keeps deriving on a periodic tick.
type IngestionWorker(logger: ILogger<IngestionWorker>, dbPath: string) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) = task {
        logger.LogInformation("AvdExperience Service starting. DB: {DbPath}", dbPath)
        initDatabase dbPath

        // Backfill marker: absent while the backlog replay is running, set once the
        // service is caught up and the derived tables are refreshed. CLI readers
        // wait on it so reports are not silently partial.
        use metaCtx = openContext dbPath
        deleteMeta metaCtx backfillCompleteKey

        // Record the initial-backlog horizon once, on the first warmup: the first
        // replay queries each channel from now−initialBacklogDays, so events older
        // than that were never ingested. The CLI warns when a report window starts
        // before this instant instead of silently printing a partial report.
        if tryGetMeta metaCtx backfillStartKey |> Option.isNone then
            setMeta metaCtx backfillStartKey (formatIso (DateTimeOffset.UtcNow.AddDays -initialBacklogDays))

        // Appends serialize on this lock (SQLite is single-writer); the live buffer
        // shares it so enqueue/drain never races a commit.
        let gate = obj()

        // Subscribe BEFORE replaying the backlog: EventLogWatcher (no bookmark) only
        // raises events logged after activation, so a replay-then-subscribe order would
        // permanently lose events arriving during the replay. Buffered events are
        // appended after the replay; the dedup guard skips the overlap. Append order
        // is irrelevant — derived state is recomputed from the stored events.
        let buffer = new LiveEventBuffer(gate, appendEvent dbPath gate logger)

        let onError (ex: exn) =
            logger.LogWarning(ex, "Event subscription error")

        use _rdp = subscribeChannel rdpChannel      "*"                     (buffer.Submit rdpChannel)      onError
        use _sys = subscribeChannel systemChannel   (makeIdXPath systemIds)   (buffer.Submit systemChannel)   onError
        use _sec = subscribeChannel securityChannel (makeIdXPath securityIds) (buffer.Submit securityChannel) onError

        logger.LogInformation("Subscriptions active — replaying backlog since watermarks…")
        replayPendingPasses defaultSource dbPath gate logger
        buffer.StopBuffering()

        // The derivation cursor must come from the same snapshot derive folded:
        // probing max(RecordId) afterwards could include an event appended mid-derive
        // and mark it derived before it actually is — the tick would then never
        // re-derive it until yet another event arrives.
        let _, _, _, _, derivedId = derive dbPath gate logger
        let mutable lastDerivedId = derivedId

        setMeta metaCtx backfillCompleteKey (formatIso DateTimeOffset.UtcNow)
        logger.LogInformation("AvdExperience Service monitoring events.")

        // Periodic derivation: keep Intervals + snapshot consistent with RawEvents.
        // Cheap when idle — one max(RecordId) probe per tick.
        use timer = new PeriodicTimer(TimeSpan.FromSeconds 2.0)
        try
            while true do
                let! _ = timer.WaitForNextTickAsync(ct)
                let maxId =
                    use ro = openReadContext dbPath
                    getMaxEventRecordId ro
                if maxId <> lastDerivedId then
                    let _, _, _, _, derivedId = derive dbPath gate logger
                    lastDerivedId <- derivedId
        with :? OperationCanceledException ->
            ()   // cancellation on shutdown
        logger.LogInformation("AvdExperience Service stopping.")
    }

    static member DefaultDbPath = AvdStats.DbRepository.defaultDbPath
