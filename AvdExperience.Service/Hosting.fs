module AvdStats.Service.Hosting

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open AvdStats.Service.IngestionWorker

/// DI registration for the service. The worker is created through a factory because its
/// dbPath parameter is not resolvable by constructor injection — registering the worker
/// with AddHostedService<T>() instead previously crashed the service at startup
/// ("Unable to resolve service for type 'System.String'").
let configureServices (dbPath: string) (services: IServiceCollection) : IServiceCollection =
    services.AddSingleton<IngestionWorker>(fun sp ->
        new IngestionWorker(sp.GetRequiredService<ILogger<IngestionWorker>>(), dbPath))
    |> ignore
    services.AddSingleton<IHostedService>(fun sp ->
        sp.GetRequiredService<IngestionWorker>() :> IHostedService)
    |> ignore
    services

let buildHost (argv: string[]) (dbPath: string) =
    Host.CreateDefaultBuilder(argv)
        .UseWindowsService(fun opts ->
            opts.ServiceName <- "AvdExperienceService")
        .ConfigureServices(fun _ services -> configureServices dbPath services |> ignore)
        .Build()
