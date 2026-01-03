module Program

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Serilog

type Program() =
    static member ConfigureServices(builder: WebApplicationBuilder, sessionId: string, projectPath: string option, useFsix: bool) =
        // Use Serilog for logging - global logger already configured in main()
        builder.Host.UseSerilog() |> ignore
        
        builder.Services.AddSingleton<FsiService.FsiService>(fun serviceProvider ->
            let logger = serviceProvider.GetRequiredService<ILogger<FsiService.FsiService>>()
            new FsiService.FsiService(logger, sessionId)
        )
        |> ignore
        
        builder.Services.AddSingleton<FsixDaemonService.FsixDaemonService>(fun serviceProvider ->
            let logger = serviceProvider.GetRequiredService<ILogger<FsixDaemonService.FsixDaemonService>>()
            new FsixDaemonService.FsixDaemonService(logger, sessionId, projectPath)
        )
        |> ignore

        // Register configuration record
        builder.Services.AddSingleton<FsiMcpTools.FsixModeConfig>(
            { FsiMcpTools.FsixModeConfig.UseFsix = useFsix }) |> ignore
        
        builder.Services.AddSingleton<FsiMcpTools.FsiTools>(fun serviceProvider ->
            let fsiService = serviceProvider.GetRequiredService<FsiService.FsiService>()
            let fsixService = serviceProvider.GetRequiredService<FsixDaemonService.FsixDaemonService>()
            let config = serviceProvider.GetRequiredService<FsiMcpTools.FsixModeConfig>()
            let logger = serviceProvider.GetRequiredService<ILogger<FsiMcpTools.FsiTools>>()
            new FsiMcpTools.FsiTools(fsiService, fsixService, config, logger)
        ) |> ignore
        
        builder
            .Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FsiMcpTools.FsiTools>()
        |> ignore

        builder.WebHost.UseUrls("http://0.0.0.0:5020")
        |> ignore

    static member ConfigureApp(app: WebApplication) =
        // Configure middleware pipeline
        app.UseDeveloperExceptionPage() |> ignore

        // Map MCP endpoints first (they use /mcp path prefix)
        app.MapMcp() |> ignore
            
        app.MapGet("/health", Func<string>(fun () -> "Ready to work!"))
        |> ignore
        
        // Add simple HTTP POST endpoint for sending F# code
        app.MapPost("/api/send", Func<Microsoft.AspNetCore.Http.HttpContext, Task<Microsoft.AspNetCore.Http.IResult>>(fun ctx ->
            task {
                use reader = new System.IO.StreamReader(ctx.Request.Body)
                let! body = reader.ReadToEndAsync()
                
                try
                    // Parse JSON body - expecting {"code": "...", "agentName": "...", "useFsix": true/false}
                    let json = System.Text.Json.JsonDocument.Parse(body)
                    let root = json.RootElement
                    
                    let mutable codeProp = Unchecked.defaultof<System.Text.Json.JsonElement>
                    if not (root.TryGetProperty("code", &codeProp)) then
                        return Microsoft.AspNetCore.Http.Results.BadRequest({| status = "error"; message = "Missing 'code' property" |})
                    else
                    
                    let code = codeProp.GetString()
                    
                    let mutable agentProp = Unchecked.defaultof<System.Text.Json.JsonElement>
                    let agentName = 
                        if root.TryGetProperty("agentName", &agentProp) then
                            agentProp.GetString()
                        else
                            "neovim"
                    
                    let mutable useFsixProp = Unchecked.defaultof<System.Text.Json.JsonElement>
                    let useFsix = 
                        if root.TryGetProperty("useFsix", &useFsixProp) && useFsixProp.ValueKind = System.Text.Json.JsonValueKind.True then
                            true
                        else
                            false
                    
                    // Get appropriate service and send code async (fire-and-forget for speed)
                    let fsixService = ctx.RequestServices.GetRequiredService<FsixDaemonService.FsixDaemonService>()
                    
                    // Start send in background
                    Task.Run(fun () ->
                        let result =
                            if useFsix then
                                fsixService.SendToFsi(code, FsixDaemonService.FsiInputSource.Api agentName)
                            else
                                let fsiService = ctx.RequestServices.GetRequiredService<FsiService.FsiService>()
                                fsiService.SendToFsi(code, FsiService.FsiInputSource.Api agentName)
                        
                        match result with
                        | Ok _ -> ()
                        | Error msg -> eprintfn "FSI send error: %s" msg
                    ) |> ignore
                    
                    // Return immediately
                    return Microsoft.AspNetCore.Http.Results.Ok({| status = "success"; message = "Code sent" |})
                with ex ->
                    return Microsoft.AspNetCore.Http.Results.BadRequest({| status = "error"; message = ex.Message |})
            }
        ))
        |> ignore

    static member CreateWebApplication(args: string[], sessionId: string, projectPath: string option, useFsix: bool) =
        let builder = WebApplication.CreateBuilder(args)
        Program.ConfigureServices(builder, sessionId, projectPath, useFsix)
        let app = builder.Build()
        Program.ConfigureApp(app)
        app

let createApp (args: string[]) (sessionId: string) =
    // Check for --use-fsix-daemon flag and --proj/--sln flags
    let useFsixDaemon = args |> Array.exists (fun arg -> arg = "--use-fsix-daemon" || arg = "fsi-mcp:--use-fsix-daemon")
    let projectPath =
        args 
        |> Array.tryFindIndex (fun arg -> arg.StartsWith("--proj") || arg.StartsWith("--sln"))
        |> Option.bind (fun idx -> 
            if idx + 1 < args.Length then Some args.[idx + 1]
            else None)
    
    let (regArgs, fsiArgs) = args |> Array.partition (fun arg -> 
        arg.StartsWith("fsi-mcp:") || 
        arg.StartsWith("--contentRoot") || 
        arg.StartsWith("--environment") || 
        arg.StartsWith("--applicationName") ||
        arg = "--use-fsix-daemon" ||
        arg.StartsWith("--proj") ||
        arg.StartsWith("--sln"))
    
    let app = Program.CreateWebApplication(regArgs |> Array.map _.Replace("fsi-mcp:",""), sessionId, projectPath, useFsixDaemon)
    
    let lifetime = app.Lifetime
    
    let fsixServiceWithProject =
        if useFsixDaemon then
            // Use fsix daemon mode - get service from DI (now has correct projectPath)
            let fsixService = app.Services.GetRequiredService<FsixDaemonService.FsixDaemonService>()
            
            printfn "⚠️  Warning: fsix daemon mode is experimental"
            printfn "✨ Starting fsix daemon with project: %s" (projectPath |> Option.defaultValue "none")
            
            let fsiProcess = fsixService.StartFsi(fsiArgs)
            
            // Setup cleanup on shutdown
            lifetime.ApplicationStopping.Register(fun () -> 
                fsixService.Cleanup()
            ) |> ignore
            
            Some fsixService
        else
            // Start FSI service normally
            let fsiService = app.Services.GetRequiredService<FsiService.FsiService>()
            let fsiProcess = fsiService.StartFsi(fsiArgs)
            
            // Setup cleanup on shutdown
            lifetime.ApplicationStopping.Register(fun () -> 
                fsiService.Cleanup()
            ) |> ignore
            
            None
    
    Console.CancelKeyPress.Add (fun _ ->
        Environment.Exit(0))
    
    let status =
        [ $"🚀 FSI.exe with MCP Server (Session: {sessionId})"
          ""
          "🛠️  MCP Tools Available:"
          "   - SendFSharpCode: Execute F# code"
          "   - LoadFSharpScript: Load .fsx files"
          "   - GetFsiEventStream: Access FSI resource"
          "   - GetFsiStatus: Get session info"
          ""
          "💡 Usage Modes:"
          "   💬 Console: Type F# commands (streams via both MCP + SSE)"
          "   🤖 MCP: Use tools (streams via both MCP + SSE)"
    ]
    status |> Seq.iter (printfn "%s")
    printfn "Press Ctrl+C to stop"
    printfn ""
    
    // Start console input forwarding in background
    let inputChannel = Channel.CreateUnbounded<string>()

    let startConsoleProducer (logger: Microsoft.Extensions.Logging.ILogger) (cts: CancellationToken) =
        Task.Run(fun () ->
            logger.LogInformation("Console producer started")
            while not cts.IsCancellationRequested do
                let line = Console.ReadLine()
                if not (isNull line) then
                    inputChannel.Writer.TryWrite line |> ignore
        , cts)
    
    let startFsiConsumer (fsiSvc: FsiService.FsiService) (logger: Microsoft.Extensions.Logging.ILogger) (cts: CancellationToken) =
        Task.Run(fun () ->
            logger.LogInformation("FSI consumer started")
            logger.LogDebug("CONSOLE-CONSUMER: FSI consumer task started")
            task {
                while! inputChannel.Reader.WaitToReadAsync(cts) do
                    logger.LogDebug("CONSOLE-CONSUMER: Reading from input channel...")
                    let! line = inputChannel.Reader.ReadAsync(cts)
                    logger.LogDebug("CONSOLE-CONSUMER: Got line from channel: {Line}", line)
                    match fsiSvc.SendToFsi(line, FsiService.FsiInputSource.Console) with
                    | Ok _       -> ()
                    | Error msg  -> logger.LogError("Console input error: {Msg}", msg)
            } :> Task
        , cts)
    
    let startFsixConsumer (fsixSvc: FsixDaemonService.FsixDaemonService) (logger: Microsoft.Extensions.Logging.ILogger) (cts: CancellationToken) =
        Task.Run(fun () ->
            logger.LogInformation("FSIX consumer started")
            logger.LogDebug("CONSOLE-CONSUMER: FSIX consumer task started")
            task {
                while! inputChannel.Reader.WaitToReadAsync(cts) do
                    logger.LogDebug("CONSOLE-CONSUMER: Reading from input channel...")
                    let! line = inputChannel.Reader.ReadAsync(cts)
                    logger.LogDebug("CONSOLE-CONSUMER: Got line from channel: {Line}", line)
                    match fsixSvc.SendToFsi(line, FsixDaemonService.FsiInputSource.Console) with
                    | Ok _       -> ()
                    | Error msg  -> logger.LogError("Console input error: {Msg}", msg)
            } :> Task
        , cts)
    
    let logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ConsoleBridge")

    // Diagnostic: Log console state at startup
    logger.LogDebug("=== CONSOLE STATE DIAGNOSTICS ===")
    logger.LogDebug("Console.IsInputRedirected: {IsInputRedirected}", Console.IsInputRedirected)
    logger.LogDebug("Console.IsOutputRedirected: {IsOutputRedirected}", Console.IsOutputRedirected)
    logger.LogDebug("Console.IsErrorRedirected: {IsErrorRedirected}", Console.IsErrorRedirected)
    logger.LogDebug("=================================")

    use cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping)

    // Start console tasks based on which service is running
    let (prodTask, consTask) =
        match fsixServiceWithProject with
        | Some fsixSvc ->
            let prod = startConsoleProducer logger cts.Token
            let cons = startFsixConsumer fsixSvc logger cts.Token
            (prod, cons)
        | None ->
            let fsiService = app.Services.GetRequiredService<FsiService.FsiService>()
            let prod = startConsoleProducer logger cts.Token
            let cons = startFsiConsumer fsiService logger cts.Token
            (prod, cons)
    
    app, Task.WhenAll [| prodTask; consTask |]

[<EntryPoint>]
let main args =
    // Generate session ID early for Serilog configuration
    let sessionId = Guid.NewGuid().ToString("N")[..7]
    // OS specific temp path    
    let tempPath = IO.Path.GetTempPath()
    let logFilePath = IO.Path.Combine(tempPath, $"fsi-mcp-debugging-{sessionId}.log")
    // Configure Serilog early - before any other logging
    Log.Logger <- LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.File(logFilePath)
        .CreateLogger()
    
    Log.Information("FSI MCP Server starting with session ID: {SessionId}", sessionId)
    
    try
        let (app, consoleTask) = createApp args sessionId
        let appTask = app.RunAsync()
        Task.WaitAll([| appTask; consoleTask |])
        0
    finally
        Log.CloseAndFlush()