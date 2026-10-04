module AvdStats.Elevation

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Security.Principal

let isElevated () =
    use identity = WindowsIdentity.GetCurrent()
    WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)

let private outputArg = "--elevation-output"

/// The process/command line used to relaunch this app:
/// - published exe: the exe itself
/// - `dotnet run` / framework-dependent: dotnet.exe with the app dll
let private launchTarget (args: string[]) =
    let quoted = args |> Array.map (sprintf "\"%s\"") |> String.concat " "
    match Environment.ProcessPath with
    | null -> None
    | pp when Path.GetFileName(pp).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) ->
        let dll = Path.Combine(AppContext.BaseDirectory, Reflection.Assembly.GetEntryAssembly().GetName().Name + ".dll")
        Some (pp, sprintf "\"%s\" %s" dll quoted)
    | pp -> Some (pp, quoted)

/// Parent: relaunch self elevated, pass temp file path, print child output after exit.
let relaunchElevated (argv: string[]) =
    let tempFile = Path.GetTempFileName()
    try
        match launchTarget (Array.append argv [| outputArg; tempFile |]) with
        | None -> None
        | Some (exe, arguments) ->
            let psi = ProcessStartInfo(exe)
            psi.Arguments <- arguments
            psi.Verb <- "runas"
            psi.UseShellExecute <- true
            try
                use p = Process.Start(psi)
                p.WaitForExit()
                File.ReadAllText(tempFile) |> printf "%s"
                Some p.ExitCode
            with :? Win32Exception ->
                None
    finally
        if File.Exists(tempFile) then File.Delete(tempFile)

/// Child: if launched by relaunchElevated, redirect Console.SetOut to the temp file.
/// Returns argv with the internal args stripped.
let tryRedirectOutput (argv: string[]) : string[] =
    match argv |> Array.tryFindIndex ((=) outputArg) with
    | None -> argv
    | Some i ->
        let writer = new StreamWriter(argv.[i + 1], false)
        writer.AutoFlush <- true
        Console.SetOut(writer)
        argv |> Array.indexed
              |> Array.filter (fun (j, _) -> j <> i && j <> i + 1)
              |> Array.map snd
