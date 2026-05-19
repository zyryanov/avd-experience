module AvdStats.SysInfo

open System
open System.IO
open System.Runtime.InteropServices

type DiskSpec =
    { Name: string
      TotalBytes: int64
      FreeBytes: int64 }

type SystemSpec =
    { CpuModel: string
      LogicalCpus: int
      TotalRamBytes: uint64
      AvailRamBytes: uint64
      OsDescription: string
      MachineName: string
      Disks: DiskSpec list }

// ── pure helpers (unit-tested) ──────────────────────────────────────────────

/// Human-readable byte size in binary units. Negative input → "—".
let formatBytes (bytes: float) : string =
    let units = [| "B"; "KiB"; "MiB"; "GiB"; "TiB" |]
    let rec go v i =
        if v >= 1024.0 && i < units.Length - 1 then go (v / 1024.0) (i + 1)
        else sprintf "%.1f %s" v units.[i]
    if bytes < 0.0 then "—" else go bytes 0

/// Percent of RAM in use, from total/avail byte counts. 0.0 when total is 0.
let ramUsedPct (totalBytes: uint64) (availBytes: uint64) : float =
    if totalBytes = 0UL then 0.0
    else float (totalBytes - availBytes) / float totalBytes * 100.0

/// Percent of a disk in use, from total/free byte counts. 0.0 when total is 0.
let diskUsedPct (totalBytes: int64) (freeBytes: int64) : float =
    if totalBytes <= 0L then 0.0
    else float (totalBytes - freeBytes) / float totalBytes * 100.0

// ── OS interop (thin, Windows-only, not unit-tested) ────────────────────────

[<StructLayout(LayoutKind.Sequential)>]
type MEMORYSTATUSEX =
    struct
        val mutable dwLength: uint32
        val mutable dwMemoryLoad: uint32
        val mutable ullTotalPhys: uint64
        val mutable ullAvailPhys: uint64
        val mutable ullTotalPageFile: uint64
        val mutable ullAvailPageFile: uint64
        val mutable ullTotalVirtual: uint64
        val mutable ullAvailVirtual: uint64
        val mutable ullAvailExtendedVirtual: uint64
    end

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool GlobalMemoryStatusEx(MEMORYSTATUSEX& lpBuffer)

/// (total, avail) physical RAM bytes. (0,0) if the call fails or off-Windows.
let private readMemory () : uint64 * uint64 =
    try
        let mutable m = MEMORYSTATUSEX()
        m.dwLength <- uint32 (Marshal.SizeOf<MEMORYSTATUSEX>())
        if GlobalMemoryStatusEx(&m) then m.ullTotalPhys, m.ullAvailPhys
        else 0UL, 0UL
    with _ -> 0UL, 0UL

let private readDisks () : DiskSpec list =
    try
        DriveInfo.GetDrives()
        |> Array.filter (fun d -> d.IsReady && d.DriveType = DriveType.Fixed)
        |> Array.map (fun d -> { Name = d.Name; TotalBytes = d.TotalSize; FreeBytes = d.TotalFreeSpace })
        |> Array.toList
    with _ -> []

/// Snapshot the host's static hardware spec.
let collect () : SystemSpec =
    let total, avail = readMemory ()
    { CpuModel =
        Environment.GetEnvironmentVariable "PROCESSOR_IDENTIFIER"
        |> Option.ofObj
        |> Option.defaultValue (string RuntimeInformation.ProcessArchitecture)
      LogicalCpus = Environment.ProcessorCount
      TotalRamBytes = total
      AvailRamBytes = avail
      OsDescription = RuntimeInformation.OSDescription
      MachineName = Environment.MachineName
      Disks = readDisks () }
