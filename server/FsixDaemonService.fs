module FsixDaemonService

open System
open System.IO
open System.Diagnostics
open System.Net.Sockets
open System.Text
open System.Text.Json
open Microsoft.Extensions.Logging
open System.Collections.Concurrent
open StreamJsonRpc
open Nerdbank.Streams

type FsiInputSource = 
    | Console 
    | Api of string
    | FileSync of string

type FsiEvent = {
    EventType: string
    Source: string 
    Content: string
    Timestamp: string
    SessionId: string
}

[<CLIMutable>]
type EvalResponse = {
    EvaluationResult: obj
    Diagnostics: obj[]
    EvaluatedCode: string
    Metadata: System.Collections.Generic.Dictionary<string, obj>
}

type FsixDaemonService(logger: ILogger<FsixDaemonService>, sessionId: string, projectPath: string option) =
    let mutable fsixProcess: Process option = None
    let mutable tcpClient: TcpClient option = None
    let mutable networkStream: NetworkStream option = None
    let eventQueue = ConcurrentQueue<FsiEvent>()
    let mutable rpcId = 0
    let mutable daemonPort = 0

    let getSourceName = function
        | Console -> "console"
        | Api source -> $"api:{source}"
        | FileSync path -> $"file:{path}"
    
    let createFsiEvent eventType source content =
        {
            EventType = eventType
            Source = source
            Content = content 
            Timestamp = DateTime.UtcNow.ToString("O")
            SessionId = sessionId
        }
    
    let addEvent (event: FsiEvent) =
        eventQueue.Enqueue(event)

    let readJsonRpcMessage (stream: NetworkStream) =
        // Read headers until we find Content-Length
        let rec readHeaders acc =
            let headerBytes = ResizeArray<byte>()
            let mutable b = stream.ReadByte()
            while b <> -1 && not (headerBytes.Count >= 2 && headerBytes.[headerBytes.Count - 2] = byte '\r' && headerBytes.[headerBytes.Count - 1] = byte '\n') do
                headerBytes.Add(byte b)
                b <- stream.ReadByte()
            
            let headerLine = Encoding.UTF8.GetString(headerBytes.ToArray()).Trim()
            if String.IsNullOrEmpty(headerLine) then
                acc
            else if headerLine.StartsWith("Content-Length:") then
                let length = Int32.Parse(headerLine.Substring(15).Trim())
                readHeaders (Some length)
            else
                readHeaders acc
        
        match readHeaders None with
        | Some contentLength ->
            // Read content
            let buffer = Array.zeroCreate contentLength
            let mutable totalRead = 0
            while totalRead < contentLength do
                let read = stream.Read(buffer, totalRead, contentLength - totalRead)
                if read = 0 then failwith "Connection closed"
                totalRead <- totalRead + read
            Some (Encoding.UTF8.GetString(buffer))
        | None -> None

    let sendJsonRpcRequest (stream: NetworkStream) (method: string) (parameters: obj) =
        rpcId <- rpcId + 1
        let request = {|
            jsonrpc = "2.0"
            id = rpcId
            method = method
            ``params`` = parameters
        |}
        
        let json = JsonSerializer.Serialize(request)
        let jsonBytes = Encoding.UTF8.GetBytes(json)
        let headers = $"Content-Length: {jsonBytes.Length}\r\n\r\n"
        let headerBytes = Encoding.UTF8.GetBytes(headers)
        
        stream.Write(headerBytes, 0, headerBytes.Length)
        stream.Write(jsonBytes, 0, jsonBytes.Length)
        stream.Flush()

    member this.StartFsi(args: string[]) =
        this.StartFsixDaemon(args)
    
    member this.ConnectToExistingDaemon(port: int) =
        logger.LogInformation($"Connecting to existing fsix daemon on port {port}")
        daemonPort <- port
        
        try
            let client = new TcpClient()
            client.Connect("localhost", port)
            let stream = client.GetStream()
            tcpClient <- Some client
            networkStream <- Some stream
            
            logger.LogInformation("🔗 Connected to existing fsix daemon")
            Console.WriteLine($"✨ Connected to fsix daemon on port {port}")
            Ok ()
        with
        | ex -> 
            logger.LogError($"Failed to connect to fsix daemon on port {port}: {ex.Message}")
            Error ex.Message
    
    member this.StartFsixDaemon(args: string[]) =
        logger.LogDebug("FSIX-DAEMON-START: StartFsixDaemon called")
        
        // Check for --connect-port argument to connect to existing daemon
        let connectPort = 
            args 
            |> Array.tryFindIndex (fun arg -> arg = "--connect-port")
            |> Option.bind (fun idx -> 
                if idx + 1 < args.Length then 
                    match Int32.TryParse(args.[idx + 1]) with
                    | (true, port) -> Some port
                    | _ -> None
                else None)
        
        match connectPort with
        | Some port ->
            // Connect to existing daemon instead of starting new one
            match this.ConnectToExistingDaemon(port) with
            | Ok () -> 
                // Return a dummy process that never exits
                let psi = ProcessStartInfo()
                psi.FileName <- "cmd.exe"
                psi.Arguments <- "/c pause"
                psi.RedirectStandardOutput <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                Process.Start(psi)
            | Error msg ->
                failwith $"Failed to connect: {msg}"
        | None ->
            // Original behavior: start new daemon
            // Find free port
            use tempListener = new TcpListener(System.Net.IPAddress.Loopback, 0)
            tempListener.Start()
            daemonPort <- (tempListener.LocalEndpoint :?> System.Net.IPEndPoint).Port
            tempListener.Stop()
            
            logger.LogInformation($"Starting fsix daemon on port {daemonPort}")
            
            let fsiArgs =
                match projectPath with
                | Some path when File.Exists(path) -> $"--daemon 127.0.0.1 {daemonPort} --proj \"{path}\""
                | Some path when path.EndsWith(".sln") && File.Exists(path) -> $"--daemon 127.0.0.1 {daemonPort} --sln \"{path}\""
                | _ -> $"--daemon 127.0.0.1 {daemonPort}"

            let psi = ProcessStartInfo()
            psi.FileName <- "fsix"
            psi.Arguments <- fsiArgs
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true

            let proc = Process.Start(psi)
            fsixProcess <- Some proc
            logger.LogInformation($"✅ fsix daemon process started with PID {proc.Id}")
            
            // Monitor stdout
            async {
                try
                    while not proc.HasExited do
                        let! line = proc.StandardOutput.ReadLineAsync() |> Async.AwaitTask
                        if not (isNull line) then
                            logger.LogDebug($"FSIX-OUT: {line}")
                            Console.WriteLine(line)
                            createFsiEvent "output" "fsix" line |> addEvent
                with
                | ex -> logger.LogError($"Output monitoring error: {ex.Message}")
            } |> Async.Start
            
            // Monitor stderr
            async {
                try
                    while not proc.HasExited do
                        let! line = proc.StandardError.ReadLineAsync() |> Async.AwaitTask
                        if not (isNull line) then
                            logger.LogDebug($"FSIX-ERR: {line}")
                            Console.WriteLine($"ERROR: {line}")
                            createFsiEvent "error" "fsix" line |> addEvent
                with
                | ex -> logger.LogError($"Error monitoring error: {ex.Message}")
            } |> Async.Start
            
            // Wait for daemon to be ready and connect
            // fsix takes time to load configuration and start listening (about 5 seconds for a project with dependencies)
            System.Threading.Thread.Sleep(6000) // Give daemon time to start
            
            try
                let client = new TcpClient()
                client.Connect("localhost", daemonPort)
                let stream = client.GetStream()
                tcpClient <- Some client
                networkStream <- Some stream
                
                logger.LogInformation("🔗 Connected to fsix daemon")
                Console.WriteLine($"✨ fsix daemon ready on port {daemonPort}")
                
            with
            | ex -> 
                logger.LogError($"Failed to connect to fsix daemon: {ex.Message}")
                proc.Kill()
                fsixProcess <- None
            
            proc

    member this.SendToFsi(code: string, source: FsiInputSource) =
        let sourceName = getSourceName source
        logger.LogDebug($"FSIX-INPUT: SendToFsi from {sourceName}: {code.Trim()}")
        
        if daemonPort = 0 then
            logger.LogDebug("FSIX-ERROR: SendToFsi called but fsix not connected")
            Error "Fsix daemon not running"
        else
            if source <> Console then
                Console.WriteLine($"({sourceName})> {code.Trim()}")
            
            let event = createFsiEvent "input" sourceName (code.Trim())
            addEvent event
            
            try
                // Call eval async - connection stays open until response
                System.Threading.Tasks.Task.Run(fun () ->
                    try
                        use client = new TcpClient()
                        client.Connect("localhost", daemonPort)
                        use stream = client.GetStream()
                        
                        // Use StreamJsonRpc to communicate with fsix daemon
                        let formatter = new JsonMessageFormatter()
                        let handler = new HeaderDelimitedMessageHandler(stream, formatter)
                        use rpc = new JsonRpc(handler)
                        rpc.StartListening()
                        
                        Console.WriteLine("[DEBUG] Sending eval request via StreamJsonRpc...")
                        let args = [| box code; box (Map.empty<string, obj>) |]
                        
                        let task = rpc.InvokeAsync<EvalResponse>("eval", args)
                        let result = task.GetAwaiter().GetResult()
                        
                        match box result with
                        | null ->
                            Console.WriteLine("[DEBUG] Result is null!")
                        | _ ->
                            Console.WriteLine($"[DEBUG] Got EvalResponse!")
                            
                            // Extract stdout from Metadata
                            if result.Metadata.ContainsKey("stdout") then
                                match result.Metadata.["stdout"] with
                                | :? string as stdout when not (String.IsNullOrWhiteSpace(stdout)) ->
                                    let timestamp = System.DateTime.Now.ToString("HH:mm:ss.fff")
                                    Console.WriteLine($"[{timestamp}] {stdout}")
                                    createFsiEvent "output" "fsix" stdout |> addEvent
                                | _ -> ()
                    with ex ->
                        Console.WriteLine($"[DEBUG] Exception: {ex.Message}")
                ) |> ignore
                
                Ok "Code sent to fsix"
            with
            | ex -> 
                Console.WriteLine($"[DEBUG] Exception: {ex.Message}")
                logger.LogError($"Error sending to fsix: {ex.Message}")
                Error ex.Message
    
    member this.SyncFileToFsi(filePath: string) =
        if File.Exists(filePath) then
            let content = File.ReadAllText(filePath)
            
            let statements = 
                content.Split([|";;"|], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim() + ";;")
                |> Array.filter (fun s -> not (String.IsNullOrWhiteSpace(s)) && not (s.TrimStart().StartsWith("//")))
            
            let fileName = Path.GetFileName filePath
            for stmt in statements do
                match this.SendToFsi(stmt, FileSync fileName) with
                | Ok _ -> ()
                | Error msg -> logger.LogError($"Error syncing statement: {msg}")
            
            Ok $"Synced {statements.Length} statements from {fileName}"
        else
            Error $"File not found: {filePath}"

    member _.GetRecentEvents(count: int) =
        eventQueue.ToArray()
        |> Array.rev
        |> Array.take (min count eventQueue.Count)
        |> Array.rev
    
    member _.GetAllEvents() =
        eventQueue.ToArray()
    
    member _.GetSessionId() = sessionId

    member _.Cleanup() =
        networkStream |> Option.iter (fun s -> s.Close())
        tcpClient |> Option.iter (fun c -> c.Close())
        match fsixProcess with
        | Some proc when not proc.HasExited ->
            proc.Kill()
            proc.WaitForExit(5000) |> ignore
        | _ -> ()

    interface IDisposable with
        member this.Dispose() = this.Cleanup()
