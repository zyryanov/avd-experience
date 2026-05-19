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

/// Parent: relaunch self elevated, pass temp file path, print child output after exit.
let relaunchElevated (argv: string[]) =
    let tempFile = Path.GetTempFileName()
    try
        let psi = ProcessStartInfo(Environment.ProcessPath)
        psi.Arguments <-
            Array.append argv [| outputArg; tempFile |]
            |> Array.map (fun a -> sprintf "\"%s\"" a)
            |> String.concat " "
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
