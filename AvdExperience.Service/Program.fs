module AvdStats.Service.Program

open Microsoft.Extensions.Hosting
open AvdStats.Service.Hosting
open AvdStats.Service.IngestionWorker

[<EntryPoint>]
let main argv =
    use host = buildHost argv IngestionWorker.DefaultDbPath
    host.Run()
    0
