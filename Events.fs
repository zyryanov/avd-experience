module AvdStats.Events

open System
open AvdStats.EventLog

let isConnectInitiated (e: LogEvent) = e.Id = 1024 || e.Id = 1102
let isConnected (e: LogEvent) = e.Id = 1027
let isDisconnected (e: LogEvent) = e.Id = 1026

let isUserDisconnected (e: LogEvent) =
    e.Id = 1026 &&
    e.Properties.Length > 1 &&
    (e.Properties.[1] = "1" || e.Properties.[1] = "2")

let isPowerDown (e: LogEvent) =
    (e.Provider = "Microsoft-Windows-Kernel-Power" && e.Id = 42) ||
    (String.Equals(e.Provider, "User32", StringComparison.OrdinalIgnoreCase) && e.Id = 1074) ||
    (e.Provider = "EventLog" && (e.Id = 6006 || e.Id = 6008))

let isPowerResume (e: LogEvent) =
    e.Provider = "Microsoft-Windows-Kernel-Power" && e.Id = 107

let isConnectionCanceled (e: LogEvent) = e.Id = 1033

let isWorkstationLocked (e: LogEvent) = e.Id = 4800
let isWorkstationUnlocked (e: LogEvent) = e.Id = 4801

let isRelevant (e: LogEvent) =
    isDisconnected e || isConnected e || isConnectInitiated e || isPowerDown e
    || isPowerResume e || isConnectionCanceled e || isWorkstationLocked e || isWorkstationUnlocked e

// 226=SSL teardown noise (every disconnect), 1104=multi-transport setup fail (non-fatal, TCP continues), 1105=multi-transport teardown (every disconnect)
let knownNoMarker = Set.ofList [ 226; 1025; 1028; 1029; 1103; 1104; 1105; 1401; 1402; 1403 ]

let marker (id: int) =
    match id with
    | 1024 | 1102 -> Some "ConnectionInitiated"
    | 1027 -> Some "Connected"
    | 1026 -> Some "Disconnected"
    | 1033 -> Some "ConnectionCanceled"
    | 42 -> Some "SystemSleep"
    | 107 -> Some "SystemResume"
    | 1074 | 6006 -> Some "SystemShutdown"
    | 6008 -> Some "SystemUnexpectedShutdown"
    | 4800 -> Some "WorkstationLocked"
    | 4801 -> Some "WorkstationUnlocked"
    | _ -> None
