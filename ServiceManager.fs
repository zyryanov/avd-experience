module AvdStats.ServiceManager

open System
open System.Diagnostics
open System.IO
open System.ServiceProcess
open System.Threading
open Spectre.Console
open AvdStats.DbRepository
open AvdStats.Elevation

let serviceName        = "AvdExperienceService"
let serviceDisplayName = "AVD Experience Monitor Service"
let serviceDescription = "Monitors Windows Event Logs for AVD session activity and stores intervals to a local database."

type ServiceState =
    | NotInstalled
    | Stopped
    | StartPending
    | Running
    | StopPending
    | Other of string

let getStatus () : ServiceState =
    try
        use sc = new ServiceController(serviceName)
        match sc.Status with
        | ServiceControllerStatus.Stopped     -> Stopped
        | ServiceControllerStatus.Running     -> Running
        | ServiceControllerStatus.StartPending -> StartPending
        | ServiceControllerStatus.StopPending  -> StopPending
        | s -> Other (string s)
    with :? InvalidOperationException -> NotInstalled

/// Starts the service if it is not running and waits for it. Does not elevate.
let tryStart () : Result<unit, string> =
    try
        use sc = new ServiceController(serviceName)
        match sc.Status with
        | ServiceControllerStatus.Running -> Ok ()
        | ServiceControllerStatus.StartPending ->
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds 5.0)
            Ok ()
        | _ ->
            sc.Start()
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds 5.0)
            Ok ()
    with
    | :? InvalidOperationException as ex -> Error ex.Message
    | :? System.ServiceProcess.TimeoutException -> Error "timed out waiting for the service to start"
    | ex -> Error ex.Message

/// Stops the service. Requires elevation on standard Windows service ACLs.
let tryStop () : Result<unit, string> =
    try
        use sc = new ServiceController(serviceName)
        match sc.Status with
        | ServiceControllerStatus.Stopped -> Ok ()
        | _ ->
            sc.Stop()
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds 10.0)
            Ok ()
    with
    | :? InvalidOperationException -> Error (sprintf "service '%s' is not installed" serviceName)
    | :? System.ServiceProcess.TimeoutException -> Error "timed out waiting for the service to stop"
    | ex -> Error ex.Message

/// Make sure the service is installed and running so the DB is fresh enough to
/// query (report and --monitor paths). Auto-installs (elevating once) when the
/// service is missing; starts it (elevating only if needed) when stopped.
/// Returns false when the service could not be installed/started.
let ensureServiceReady () : bool =
    match getStatus () with
    | NotInstalled ->
        AnsiConsole.MarkupLine "[yellow]AVD Service is not installed. Elevating to install…[/]"
        match relaunchElevated [| "service"; "install" |] with
        | Some 0 -> true
        | Some code ->
            AnsiConsole.MarkupLine(sprintf "[red]✗ Service installation failed[/] [grey](exit %d)[/]" code)
            false
        | None ->
            AnsiConsole.MarkupLine "[yellow]⚠ Elevation declined[/] — cannot install the service."
            false
    | Running | StartPending -> true
    | _ ->
        AnsiConsole.MarkupLine "[yellow]AVD Service is stopped. Starting service…[/]"
        match tryStart () with
        | Ok () -> true
        | Error _ when not (isElevated ()) ->
            match relaunchElevated [| "service"; "start" |] with
            | Some 0 -> true
            | _ ->
                AnsiConsole.MarkupLine("[red]✗ Could not start the AVD Service.[/]")
                false
        | Error e ->
            AnsiConsole.MarkupLine(sprintf "[red]✗ Could not start the AVD Service:[/] %s" (Markup.Escape e))
            false

let private run (exe: string) (args: string) : Result<string, string> =
    let psi = ProcessStartInfo(exe, args)
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start(psi)
    let out = p.StandardOutput.ReadToEnd()
    let err = p.StandardError.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode = 0 then Ok out
    else Error (if String.IsNullOrWhiteSpace err then out.Trim() else err.Trim())

/// %ProgramData%\AvdExperience
let dbDir () =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.CommonApplicationData, "AvdExperience")

/// Locates avd-service.exe: beside this executable (published) or in a sibling
/// dev build output folder.
let findServiceExe () : string option =
    let baseDir = AppContext.BaseDirectory
    [ Path.Combine(baseDir, "avd-service.exe")
      Path.Combine(baseDir, "..", "..", "..", "AvdExperience.Service", "bin", "Debug", "net10.0", "avd-service.exe")
      Path.Combine(baseDir, "..", "..", "..", "AvdExperience.Service", "bin", "Release", "net10.0", "avd-service.exe") ]
    |> List.map Path.GetFullPath
    |> List.tryFind File.Exists

/// Registers the Windows service (auto-start), grants BUILTIN\Users access to the
/// DB folder so the un-elevated CLI can read it, and starts the service.
/// Requires elevation.
let install () : Result<unit, string> =
    let must (label: string) (r: Result<string, string>) =
        match r with
        | Ok _ -> ()
        | Error e -> failwithf "%s failed: %s" label e

    try
        let serviceExe =
            match findServiceExe () with
            | Some exe -> exe
            | None -> failwith "avd-service.exe not found — publish the service beside avd-experience.exe (or build the AvdExperience.Service project)"

        let dir = dbDir ()
        if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore

        // Grant BUILTIN\Users access so the un-elevated CLI can open the WAL database.
        // WAL readers also need write access to the shared -shm file, hence Modify.
        must "icacls grant" (run "icacls.exe" (sprintf "\"%s\" /grant *S-1-5-32-545:(OI)(CI)M" dir))

        let binPath = sprintf "\"%s\"" serviceExe
        // Idempotent: re-running install (e.g. to repair the ACL grant above after a
        // manual install) must succeed when the service is already registered.
        match run "sc.exe" (sprintf "create %s binPath= %s start= auto DisplayName= \"%s\"" serviceName binPath serviceDisplayName) with
        | Ok _ -> ()
        | Error e when e.Contains "1073" || e.Contains "already exists" -> ()   // already installed — fine
        | Error e -> failwithf "sc create failed: %s" e
        run "sc.exe" (sprintf "description %s \"%s\"" serviceName serviceDescription) |> ignore

        match run "sc.exe" (sprintf "start %s" serviceName) with
        | Ok _ -> ()
        | Error e when e.Contains "already" -> ()   // already running — fine
        | Error e -> failwithf "sc start failed: %s" e

        Ok ()
    with ex ->
        Error ex.Message

/// Deregisters the service. Data in %ProgramData%\AvdExperience is kept.
/// Requires elevation. Idempotent.
let uninstall () : Result<unit, string> =
    run "sc.exe" (sprintf "stop %s" serviceName) |> ignore
    match run "sc.exe" (sprintf "delete %s" serviceName) with
    | Ok _ -> Ok ()
    | Error e when e.Contains "1060" || e.Contains "does not exist" -> Ok ()
    | Error e -> Error e

// ── Backfill readiness & DB read UX ────────────────────────────────────────────

/// Bounded wait for the service to finish replaying its event-history backlog into
/// the DB. Used by the CLI so a report is not silently partial on a fresh install or
/// a just-started service.
let backfillWaitTimeout = TimeSpan.FromSeconds 30.0

/// True once the service finished backfilling event history into the DB
/// (the BackfillComplete meta row is present). False while warmup is running,
/// when the DB does not exist, or when it cannot be read yet.
let isBackfillComplete (dbPath: string) : bool =
    if not (File.Exists dbPath) then false
    else
        match tryRead dbPath (fun ctx -> tryGetMeta ctx backfillCompleteKey) with
        | Ok (Some _) -> true
        | _ -> false

/// Wait (with a status spinner) until the service finishes backfilling history.
/// Returns true when the DB is ready; false on timeout — callers should warn
/// that the data may be partial and proceed anyway.
let waitForBackfill (dbPath: string) (timeout: TimeSpan) : bool =
    if isBackfillComplete dbPath then true
    else
        let sw = Stopwatch.StartNew()
        let mutable complete = false
        AnsiConsole.Status().Start("Waiting for the AVD service to backfill event history…", fun _ ->
            while not complete && sw.Elapsed < timeout do
                Thread.Sleep 250
                complete <- isBackfillComplete dbPath)
        complete

/// Friendly output for a failed DB read (missing folder permissions after a manual
/// `sc create` install, DB deleted mid-run, corrupt file) instead of a stack trace.
let reportDbReadError (dbPath: string) (msg: string) =
    AnsiConsole.MarkupLine(sprintf "[red]✗ Cannot read %s:[/] %s" (Markup.Escape dbPath) (Markup.Escape msg))
    AnsiConsole.MarkupLine("[grey]If access is denied, re-run 'avd-experience service install' — it grants BUILTIN\\Users access to the database folder.[/]")
