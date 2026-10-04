module AvdStats.DbRepository

open System
open System.IO
open Microsoft.Data.Sqlite
open SqlHydra.Query
open SqlHydra.Query.SqliteExtensions
open AvdStats.EventLog
open AvdStats.Stats
open AvdStats.DbSchema
open AvdStats.DbSchema.main

let defaultDbPath =
    let programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    Path.Combine(programData, "AvdExperience", "avd.db")

// Note: no `Cache=Shared` — SQLite shared cache is a per-process pager shared across
// connections; if a ReadOnly connection is the first to attach a file (possible after
// pooling resets), it poisons every subsequent write connection in the same process
// with 'attempt to write a readonly database'. WAL alone provides the cross-process
// reader/writer concurrency between the Service and the CLI.
let getConnectionString (dbPath: string) =
    sprintf "Data Source=%s;Mode=ReadWriteCreate" dbPath

let getReadOnlyConnectionString (dbPath: string) =
    sprintf "Data Source=%s;Mode=ReadOnly" dbPath

let initDatabase (dbPath: string) =
    let dir = Path.GetDirectoryName(dbPath)
    if not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore

    use conn = new SqliteConnection(getConnectionString dbPath)
    conn.Open()

    // Enable WAL mode for high concurrency between Service (writer) and CLI (readers)
    use walCmd = conn.CreateCommand()
    walCmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;"
    walCmd.ExecuteNonQuery() |> ignore

    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        CREATE TABLE IF NOT EXISTS RawEvents (
            RecordId INTEGER PRIMARY KEY AUTOINCREMENT,
            Channel TEXT NOT NULL,
            EventId INTEGER NOT NULL,
            TimeCreated TEXT NOT NULL,
            Provider TEXT NOT NULL,
            Message TEXT,
            PropertiesJson TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_events_time ON RawEvents(TimeCreated);
        -- DB-level backstop for the ingestion dedup guard (eventExists):
        -- the same (Channel, EventId, TimeCreated) must never be stored twice.
        CREATE UNIQUE INDEX IF NOT EXISTS idx_events_dedup
            ON RawEvents(Channel, EventId, TimeCreated);

        CREATE TABLE IF NOT EXISTS Intervals (
            IntervalId INTEGER PRIMARY KEY AUTOINCREMENT,
            Kind TEXT NOT NULL,
            StartTime TEXT NOT NULL,
            EndTime TEXT NOT NULL,
            DurationMs INTEGER NOT NULL,
            ConnectReason TEXT NOT NULL,
            ReportContribMs INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_intervals_range ON Intervals(StartTime, EndTime);

        CREATE TABLE IF NOT EXISTS SyncWatermarks (
            Channel TEXT PRIMARY KEY,
            LastSyncedTime TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS StateSnapshots (
            SnapshotId INTEGER PRIMARY KEY,
            CurrentKind TEXT,
            CurrentStart TEXT,
            Locked INTEGER NOT NULL,
            ShadowKind TEXT,
            ShadowStart TEXT,
            Reason TEXT
        );

        CREATE TABLE IF NOT EXISTS ServiceMeta (
            MetaKey TEXT NOT NULL PRIMARY KEY,
            MetaValue TEXT NOT NULL
        );
    """
    cmd.ExecuteNonQuery() |> ignore

// ── Timestamp helpers ────────────────────────────────────────────────────────
// All timestamps are stored normalized to UTC ISO 8601 ("+00:00" suffix), so
// lexicographic string comparison in SQL WHERE clauses matches chronological order.

let formatIso (dt: DateTimeOffset) = dt.ToUniversalTime().ToString("o")
let parseIso (s: string) = DateTimeOffset.Parse(s)

// ── Event channels & canonical order ──────────────────────────────────────────

let rdpChannel      = "Microsoft-Windows-TerminalServices-RDPClient/Operational"
let systemChannel   = "System"
let securityChannel = "Security"

/// Rank for the canonical event order when timestamps tie. Security (workstation
/// lock/unlock) first — `advanceState` routes lock/unlock before AVD transitions,
/// so ties should resolve the way the state machine itself would — then System,
/// then RDP, then anything else.
let channelRank (channel: string) =
    if channel = securityChannel then 0
    elif channel = systemChannel then 1
    elif channel = rdpChannel then 2
    else 3

// The canonical event order — time, then channel rank, then event ID — is one
// strict total order over stored events ((Channel, EventId, TimeCreated) is the
// dedup index, so no two rows share the key). Every reader (CLI report fold,
// monitor tail, service derivation) uses it, so all folds agree on equal-
// timestamp events.

// ── Record <-> domain mapping ────────────────────────────────────────────────

let private jsonOptions = System.Text.Json.JsonSerializerOptions()

let private toLogEvent (r: RawEvents) : LogEvent =
    { Id = int r.EventId
      TimeCreated = parseIso r.TimeCreated
      Provider = r.Provider
      Message = r.Message
      Properties =
          r.PropertiesJson
          |> Option.map (fun json -> System.Text.Json.JsonSerializer.Deserialize<string list>(json, jsonOptions))
          |> Option.defaultValue [] }

/// Storage format for IntervalKind / ConnectReason columns (derive compares rows
/// against these exact strings).
let intervalKindToString = sprintf "%A"
let connectReasonToString = sprintf "%A"

let private canonicalizeRows (rows: RawEvents list) : (int64 * string * LogEvent) list =
    rows
    |> List.sortBy (fun r -> r.TimeCreated, channelRank r.Channel, r.EventId)
    |> List.map (fun r -> r.RecordId, r.Channel, toLogEvent r)

let private canonicalize (rows: RawEvents list) : LogEvent list =
    canonicalizeRows rows |> List.map (fun (_, _, e) -> e)

let parseIntervalKind (s: string) : IntervalKind =
    match s with
    | "Active" -> Active
    | "Connecting" -> Connecting
    | "Paused" -> Paused
    | "Issue" -> Issue
    | _ -> failwithf "Unknown interval kind: %s" s

let parseConnectReason (s: string) : ConnectReason =
    match s with
    | "Initial" -> Initial
    | "PostIssue" -> PostIssue
    | "PostPause" -> PostPause
    | _ -> failwithf "Unknown connect reason: %s" s

// ── Context helpers ──────────────────────────────────────────────────────────

let createContext (conn: SqliteConnection) =
    new QueryContext(conn, SqliteEmitter())

let openContext (dbPath: string) =
    let conn = new SqliteConnection(getConnectionString dbPath)
    conn.Open()
    createContext conn

let openReadContext (dbPath: string) =
    let conn = new SqliteConnection(getReadOnlyConnectionString dbPath)
    conn.Open()
    createContext conn

/// Run `f` against a read-only context, mapping infrastructure failures (unreadable
/// or locked database — e.g. the `BUILTIN\Users` grant missing after a manual
/// `sc create` install, or the file deleted mid-run) to a message instead of an
/// exception, so the CLI can print a friendly hint rather than crash.
let tryRead (dbPath: string) (f: QueryContext -> 't) : Result<'t, string> =
    try
        use ctx = openReadContext dbPath
        Ok (f ctx)
    with ex ->
        Error ex.Message

// ── Write helpers (used by the Service) ──────────────────────────────────────

let saveEvent (ctx: QueryContext) (channel: string) (e: LogEvent) =
    insert {
        for r in RawEvents do
        entity {
            RecordId    = 0L
            Channel     = channel
            EventId     = int64 e.Id
            TimeCreated = formatIso e.TimeCreated
            Provider    = e.Provider
            Message     = e.Message
            PropertiesJson =
                if e.Properties.IsEmpty then None
                else Some (System.Text.Json.JsonSerializer.Serialize(e.Properties, jsonOptions))
        }
        getId r.RecordId
    }
    |> ctx.Insert
    |> ignore

/// True when an event with this exact (Channel, EventId, TimeCreated) is already
/// stored — the ingestion dedup guard. It makes every replay path idempotent:
/// watermark boundary replays, backward watermarks, and the warmup→live handover
/// all skip events that were already committed.
let eventExists (ctx: QueryContext) (channel: string) (e: LogEvent) : bool =
    // Hoist value computations out of the where clause — SqlHydra can only
    // translate pure column-vs-constant comparisons.
    let eventId = int64 e.Id
    let timeCreated = formatIso e.TimeCreated
    select {
        for r in RawEvents do
        where (r.Channel = channel && r.EventId = eventId && r.TimeCreated = timeCreated)
        select r.RecordId
    }
    |> ctx.Select
    |> Seq.isEmpty
    |> not

/// Column value for a closed interval's ConnectReason: the reason in effect at
/// close time, or an empty string when none was (e.g. an Issue interval closed
/// before any connect reason is established). The column is NOT NULL, so "" —
/// not "Initial", which is a genuine connect reason — means "no reason".
let connectReasonColumn (reason: ConnectReason option) =
    reason |> Option.map connectReasonToString |> Option.defaultValue ""

let saveInterval (ctx: QueryContext) (iv: Interval) (reason: ConnectReason option) =
    let contrib = intervalReportContrib iv.Kind reason (iv.End - iv.Start)
    insert {
        for i in Intervals do
        entity {
            IntervalId      = 0L
            Kind            = intervalKindToString iv.Kind
            StartTime       = formatIso iv.Start
            EndTime         = formatIso iv.End
            DurationMs      = int64 (iv.End - iv.Start).TotalMilliseconds
            ConnectReason   = connectReasonColumn reason
            ReportContribMs = int64 contrib.TotalMilliseconds
        }
        getId i.IntervalId
    }
    |> ctx.Insert
    |> ignore

let upsertWatermark (ctx: QueryContext) (channel: string) (time: DateTimeOffset) =
    insert {
        for w in SyncWatermarks do
        entity { Channel = Some channel; LastSyncedTime = formatIso time }
        onConflictDoUpdate w.Channel (w.LastSyncedTime)
    }
    |> ctx.Insert
    |> ignore

let getWatermark (ctx: QueryContext) (channel: string) : DateTimeOffset option =
    select {
        for w in SyncWatermarks do
        where (w.Channel = Some channel)
        select w
    }
    |> ctx.SelectOne
    |> Option.map (fun w -> parseIso w.LastSyncedTime)

let saveSnapshot
    (ctx: QueryContext)
    (state: (IntervalKind * DateTimeOffset) option)
    (locked: bool)
    (shadow: (IntervalKind * DateTimeOffset) option)
    (reason: ConnectReason option) =
    let kindAndStart = Option.map (fun (k, t) -> intervalKindToString k, formatIso t)
    let cur = kindAndStart state
    let sha = kindAndStart shadow
    insert {
        for s in StateSnapshots do
        entity {
            SnapshotId   = 1L
            CurrentKind  = cur |> Option.map fst
            CurrentStart = cur |> Option.map snd
            Locked       = if locked then 1L else 0L
            ShadowKind   = sha |> Option.map fst
            ShadowStart  = sha |> Option.map snd
            Reason       = reason |> Option.map connectReasonToString
        }
        onConflictDoUpdate
            s.SnapshotId
            (s.CurrentKind, s.CurrentStart, s.Locked, s.ShadowKind, s.ShadowStart, s.Reason)
    }
    |> ctx.Insert
    |> ignore

// ── Service metadata (single-row markers, e.g. backfill readiness) ────────────

/// Meta key set once the service has finished replaying its backlog into the DB.
/// Absent while warmup is running (or when the DB was just created).
let backfillCompleteKey = "BackfillComplete"

let tryGetMeta (ctx: QueryContext) (key: string) : string option =
    select {
        for m in ServiceMeta do
        where (m.MetaKey = key)
        select m.MetaValue
    }
    |> ctx.SelectOne

let setMeta (ctx: QueryContext) (key: string) (value: string) =
    insert {
        for m in ServiceMeta do
        entity { MetaKey = key; MetaValue = value }
        onConflictDoUpdate m.MetaKey (m.MetaValue)
    }
    |> ctx.Insert
    |> ignore

let deleteMeta (ctx: QueryContext) (key: string) =
    delete {
        for m in ServiceMeta do
        where (m.MetaKey = key)
    }
    |> ctx.Delete
    |> ignore

/// Meta key recording the earliest instant the initial backlog replay reached
/// (first warmup time minus the backlog window). Events older than this were
/// never ingested; the CLI warns when a report window starts before it.
let backfillStartKey = "BackfillStart"

let getBackfillStart (ctx: QueryContext) : DateTimeOffset option =
    tryGetMeta ctx backfillStartKey |> Option.map parseIso

/// First-run backlog: how far back the initial event replay goes (the service's
/// replay default and the recorded BackfillStart horizon). Lives here so the CLI
/// can phrase its partial-history warning without referencing the service project.
let initialBacklogDays = 30.0

let loadSnapshot
    (ctx: QueryContext)
    : (IntervalKind * DateTimeOffset) option * bool * (IntervalKind * DateTimeOffset) option * ConnectReason option =
    let kindAndStart kind start =
        match kind, start with
        | Some k, Some t -> Some (parseIntervalKind k, parseIso t)
        | _ -> None
    match
        select {
            for s in StateSnapshots do
            where (s.SnapshotId = 1L)
            select s
        }
        |> ctx.SelectOne with
    | Some s ->
        kindAndStart s.CurrentKind s.CurrentStart,
        s.Locked <> 0L,
        kindAndStart s.ShadowKind s.ShadowStart,
        s.Reason |> Option.map parseConnectReason
    | None -> (None, false, None, None)

// ── Read helpers (used by the CLI and the service derivation) ────────────────

/// All events strictly before `until`, in canonical order — used to derive the
/// state machine state at the start of a report window.
let getEventsBefore (ctx: QueryContext) (until: DateTimeOffset) : LogEvent list =
    select {
        for e in RawEvents do
        where (e.TimeCreated < formatIso until)
        orderBy e.TimeCreated
        select e
    }
    |> ctx.Select
    |> Seq.toList
    |> canonicalize

/// Events within [from, until], in canonical order.
let getEventsInRange (ctx: QueryContext) (from: DateTimeOffset) (until: DateTimeOffset) : LogEvent list =
    select {
        for e in RawEvents do
        where (e.TimeCreated >= formatIso from && e.TimeCreated <= formatIso until)
        orderBy e.TimeCreated
        select e
    }
    |> ctx.Select
    |> Seq.toList
    |> canonicalize

/// Every stored event in canonical order, with row identities (RecordId, channel).
/// The derivation and the monitor's state fold over this; the RecordIds let
/// incremental readers track exactly which rows they have already folded.
let getAllEventRows (ctx: QueryContext) : (int64 * string * LogEvent) list =
    select {
        for e in RawEvents do
        orderBy e.TimeCreated
        select e
    }
    |> ctx.Select
    |> Seq.toList
    |> canonicalizeRows

/// Every stored event in canonical order — the derivation folds over this.
let getAllEvents (ctx: QueryContext) : LogEvent list =
    getAllEventRows ctx |> List.map (fun (_, _, e) -> e)

/// Newest RawEvents rows with RecordId > lastSeenId — the --monitor polling
/// query. Ordered by RecordId (arrival); the monitor sorts each poll batch into
/// the canonical order. The row's channel is included for that sort key.
let getEventsAfterRecordId (ctx: QueryContext) (lastSeenId: int64) : (int64 * string * LogEvent) list =
    select {
        for e in RawEvents do
        where (e.RecordId > lastSeenId)
        orderBy e.RecordId
        select e
    }
    |> ctx.Select
    |> Seq.map (fun r -> r.RecordId, r.Channel, toLogEvent r)
    |> Seq.toList

/// Every derived interval row, ordered by start time — derive compares stored
/// rows against a fresh fold and rewrites them when they differ.
let getAllIntervals (ctx: QueryContext) : Intervals list =
    select {
        for i in Intervals do
        orderBy i.StartTime
        select i
    }
    |> ctx.Select
    |> Seq.toList

/// Delete every derived interval row — derive rewrites them in one transaction.
let clearIntervals (ctx: QueryContext) =
    delete {
        for i in Intervals do
        where (i.IntervalId > 0L)
    }
    |> ctx.Delete
    |> ignore

/// Typed interval query — closed intervals overlapping [from, until].
let getIntervalsInRange (ctx: QueryContext) (from: DateTimeOffset) (until: DateTimeOffset) : Intervals list =
    select {
        for i in Intervals do
        where (i.StartTime <= formatIso until && i.EndTime >= formatIso from)
        orderBy i.StartTime
        select i
    }
    |> ctx.Select
    |> Seq.toList

let getMaxEventRecordId (ctx: QueryContext) : int64 =
    select {
        for e in RawEvents do
        orderByDescending e.RecordId
        take 1
        select e
    }
    |> ctx.Select
    |> Seq.tryHead
    |> Option.map (fun e -> e.RecordId)
    |> Option.defaultValue 0L

let countEvents (ctx: QueryContext) : int =
    select {
        for e in RawEvents do
        count
    }
    |> ctx.Select
    |> Seq.head

let countIntervals (ctx: QueryContext) : int =
    select {
        for i in Intervals do
        count
    }
    |> ctx.Select
    |> Seq.head
