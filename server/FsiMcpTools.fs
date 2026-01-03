module FsiMcpTools
            
open System.ComponentModel
open ModelContextProtocol.Server

//to test: npx @modelcontextprotocol/inspector
//this uses SSE transport over http://localhost:5000/sse
open Microsoft.Extensions.Logging

type FsixModeConfig = { UseFsix: bool }

type FsiTools(fsiService: FsiService.FsiService, fsixService: FsixDaemonService.FsixDaemonService, config: FsixModeConfig, logger: ILogger<FsiTools>) =
    
    let sendCode code (source: obj) =
        if config.UseFsix then
            fsixService.SendToFsi(code, source :?> FsixDaemonService.FsiInputSource)
        else
            fsiService.SendToFsi(code, source :?> FsiService.FsiInputSource)
    
    let syncFile filePath =
        if config.UseFsix then
            fsixService.SyncFileToFsi(filePath)
        else
            fsiService.SyncFileToFsi(filePath)
    
    let getRecentEvents count =
        if config.UseFsix then
            fsixService.GetRecentEvents(count) |> Array.map (fun e ->
                {| EventType = e.EventType; Source = e.Source; Content = e.Content; Timestamp = e.Timestamp; SessionId = e.SessionId |})
        else
            fsiService.GetRecentEvents(count) |> Array.map (fun e ->
                {| EventType = e.EventType; Source = e.Source; Content = e.Content; Timestamp = e.Timestamp; SessionId = e.SessionId |})
    
    let getAllEvents () =
        if config.UseFsix then
            fsixService.GetAllEvents() |> Array.map (fun e ->
                {| EventType = e.EventType; Source = e.Source; Content = e.Content; Timestamp = e.Timestamp; SessionId = e.SessionId |})
        else
            fsiService.GetAllEvents() |> Array.map (fun e ->
                {| EventType = e.EventType; Source = e.Source; Content = e.Content; Timestamp = e.Timestamp; SessionId = e.SessionId |})
    
    let getSessionId () =
        if config.UseFsix then
            fsixService.GetSessionId()
        else
            fsiService.GetSessionId()
    
    [<McpServerTool(Name=McpToolNames.SendFSharpCode)>]
    [<Description("Send F# code to the FSI (F# Interactive) session for execution. Make sure to the statements with ';;' just as you would when interacting with fsi.exe.")>]
    member _.SendFSharpCode(agentName: string, code: string) : string =
        logger.LogDebug("MCP-TOOL: SendFSharpCode called by {AgentName}: {Code}", agentName, code)
        let source:obj = 
            if config.UseFsix then
                FsixDaemonService.FsiInputSource.Api agentName :> obj
            else
                FsiService.FsiInputSource.Api agentName :> obj
        match sendCode code source with
        | Ok result ->
            logger.LogDebug("MCP-TOOL: SendFSharpCode succeeded: {Result}", result)
            result
        | Error msg ->
            logger.LogDebug("MCP-TOOL: SendFSharpCode failed: {Message}", msg)
            $"Error: {msg}"
    
    [<McpServerTool>]
    [<Description("Load and execute an F# script file (.fsx) in the FSI session. The file is parsed and statements are sent individually.")>]
    member _.LoadFSharpScript(filePath: string) : string = 
        match syncFile filePath with
        | Ok result -> result
        | Error msg -> $"Error: {msg}"
    
    [<McpServerTool(Name=McpToolNames.GetRecentFsiEvents)>]
    [<Description("Get recent FSI events.")>]
    member _.GetRecentFsiEvents(count: int option) : string = 
        let eventCount = defaultArg count 10
        let events = getRecentEvents eventCount
        
        if events.Length = 0 then
            "No FSI events available yet. Execute some F# code first."
        else
            let eventStrings = events |> Array.map (fun e -> 
                $"[{e.Timestamp}] {e.EventType.ToUpper()} ({e.Source}): {e.Content}")
            String.concat "\n" eventStrings
    
    [<McpServerTool(Name=McpToolNames.GetFsiStatus)>]
    [<Description("Get information about the FSI service status.")>]
    member _.GetFsiStatus() : string =
        
        let sessionId = getSessionId()
        let totalEvents = getAllEvents().Length
        
        let status = [
            $"🚀 FSI Server Status (Session: {sessionId}):"
            ""
            $"📈 Event Statistics: {totalEvents} total events captured"
            ""
            "💡 Available Tools:"
            "- SendFSharpCode: Execute F# code (triggers real-time notifications)"
            "- LoadFSharpScript: Load .fsx files (triggers real-time notifications)"  
            "- GetFsiStatus: Get session info and statistics"
            ""
            "🔄 Multi-source Support:"
            "- Console input (direct typing)"
            "- MCP API calls (from agents)"
            ""
        ]
        String.concat "\n" status