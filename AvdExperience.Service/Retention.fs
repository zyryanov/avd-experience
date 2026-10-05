module AvdStats.Service.Retention

// Pruning of RawEvents older than the retention horizon (retentionDays, 90).
//
// Deleting old events naively would corrupt the state machine: the fold's memory
// (open Paused/Issue state, lock flag, shadow state, connect reason) persists
// across days, and stepState never returns the state to None once a session has
// started — a naive cut would dangle the first surviving event off a None state
// (e.g. a disconnect opening a spurious Paused interval) and misclassify the first
// post-cutoff reconnect as Initial. So every prune:
//   1. cuts at the newest event timestamp at/before the horizon — a whole
//      same-timestamp batch, never mid-batch (canonical order folds siblings
//      together, so the seed must be taken after a complete batch);
//   2. folds everything up to that boundary (seeded by the previous seed — the
//      chain is preserved across prune runs) and persists the resulting state as
//      the RetentionSeed;
//   3. deletes the events, stamps RetentionStart (the CLI's pruned-history
//      warning horizon), and re-derives so Intervals and StateSnapshots drop the
//      pruned rows (the periodic tick can't notice — deletes don't move
//      max(RecordId)).
// fold(retained, seed) is identical to fold(full history) from the cutoff onward
// (a left-fold identity), so reports over retained windows stay exact. Prune is
// idempotent; a second run finds nothing — except late-arriving old events
// (replay after a long service outage re-inserts them via the dedup guard), which
// it re-deletes.

open System
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging
open AvdStats.Stats
open AvdStats.DbRepository
open AvdStats.Service.Ingestion

let pruneWith (horizon: DateTimeOffset) (dbPath: string) (lockObj: obj) (logger: ILogger) : unit =
    lock lockObj (fun () ->
        try
            let cutoff =
                use ro = openReadContext dbPath
                getLatestTimeAtOrBefore ro horizon

            match cutoff with
            | None -> ()   // nothing older than the horizon — DB younger than retention
            | Some tDel ->
                // Capture the exact machine state after the cutoff batch, continuing
                // from the previous seed so repeated prunes stay fold-exact.
                let seed =
                    use ro = openReadContext dbPath
                    let prev = getFoldSeed ro
                    let state, locked, shadow, reason =
                        getEventsAtOrBefore ro tDel
                        |> foldState prev.State prev.Locked prev.Shadow prev.Reason
                    { State = state; Locked = locked; Shadow = shadow; Reason = reason }

                use conn = new SqliteConnection(getConnectionString dbPath)
                conn.Open()
                use ctx = createContext conn
                ctx.BeginTransaction()

                let deleted = deleteEventsAtOrBefore ctx tDel
                saveRetentionSeed ctx seed
                setMeta ctx retentionStartKey (formatIso tDel)
                ctx.CommitTransaction()

                logger.LogInformation(
                    "Retention: pruned {Count} events at/before {Cutoff} (horizon {Horizon}); seed recorded",
                    deleted, tDel, horizon)

                // Re-derive so Intervals and the snapshot drop the pruned rows.
                derive dbPath lockObj logger |> ignore
        with ex ->
            logger.LogError(ex, "Retention prune failed"))

/// Prune everything older than the retention horizon (retentionDays).
let prune (dbPath: string) (lockObj: obj) (logger: ILogger) : unit =
    pruneWith (DateTimeOffset.UtcNow.AddDays -retentionDays) dbPath lockObj logger
