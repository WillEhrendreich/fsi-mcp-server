module IFsiService

open System

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

type IFsiService =
    abstract member StartFsi: string[] -> System.Diagnostics.Process
    abstract member SendToFsi: string * FsiInputSource -> Result<string, string>
    abstract member SyncFileToFsi: string -> Result<string, string>
    abstract member GetRecentEvents: int -> FsiEvent[]
    abstract member GetAllEvents: unit -> FsiEvent[]
    abstract member GetSessionId: unit -> string
    abstract member Cleanup: unit -> unit
    inherit IDisposable
