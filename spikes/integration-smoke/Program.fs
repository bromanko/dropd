open System
open System.IO
open System.Text.Json
open Dropd.Core
open Dropd.Core.Types

// ── Formatting helpers ────────────────────────────────────────────────────

let private serviceLabel = function
    | ApiContracts.AppleMusic -> "apple"
    | ApiContracts.LastFm     -> "lastfm"

let private levelLabel = function
    | ApiContracts.Debug   -> "DEBUG  "
    | ApiContracts.Info    -> "INFO   "
    | ApiContracts.Warning -> "WARNING"
    | ApiContracts.Error   -> "ERROR  "

let private printRequests (requests: ApiContracts.ApiRequest list) =
    printfn ""
    printfn "── Requests (%d) ──────────────────────────────────" requests.Length
    for r in requests do
        let qs =
            if List.isEmpty r.Query then ""
            else "?" + String.concat "&" (r.Query |> List.map (fun (k,v) -> $"{k}={v}"))
        printfn "  [%s] %s %s%s" (serviceLabel r.Service) r.Method r.Path qs

let private printLogs (logs: ApiContracts.LogEntry list) =
    printfn ""
    printfn "── Logs (%d) ───────────────────────────────────────" logs.Length
    for entry in logs do
        printfn "  %s [%s] %s" (levelLabel entry.Level) entry.Code entry.Message
        for KeyValue(k, v) in entry.Data do
            printfn "         %s: %s" k v

let private printOutcome = function
    | Success           -> printfn "\n✓ Outcome: success"
    | PartialFailure    -> printfn "\n⚠ Outcome: partial_failure (some playlist operations failed)"
    | Aborted reason    -> printfn "\n✗ Outcome: aborted (%s)" reason

let private exitCodeFor = function
    | Aborted _ -> 1
    | _         -> 0

// ── Playlist ID cache ─────────────────────────────────────────────────────
// Apple Music's playlist listing endpoint has severe eventual consistency
// issues — newly created playlists may not appear for minutes. We persist
// a local name→ID map so subsequent runs find playlists reliably.

let private cachePath =
    let assemblyDir = Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location)
    Path.Combine(assemblyDir, "playlist-cache.json")

let private loadPlaylistCache () : Map<string, string> =
    try
        if File.Exists(cachePath) then
            let json = File.ReadAllText(cachePath)
            let dict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(json)
            dict |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
        else
            Map.empty
    with _ ->
        Map.empty

let private savePlaylistCache (ids: Map<string, string>) =
    try
        let opts = JsonSerializerOptions(WriteIndented = true)
        let json = JsonSerializer.Serialize(ids |> Map.toSeq |> dict, opts)
        File.WriteAllText(cachePath, json)
    with _ ->
        ()

// ── Entry point ───────────────────────────────────────────────────────────

[<EntryPoint>]
let main _argv =
    printfn ""
    printfn "════════════════════════════════════════"
    printfn "  dropd — Live Sync"
    printfn "════════════════════════════════════════"

    match SmokeConfig.load () with
    | Error msg ->
        eprintfn "\nError: %s" msg
        1
    | Ok rawConfig ->

    match Config.validate rawConfig with
    | Error errors ->
        eprintfn "\nConfig validation failed:"
        for e in errors do eprintfn "  %A" e
        1
    | Ok config ->

    printfn ""
    printfn "Config:"
    printfn "  Playlists   : %d defined" config.Playlists.Length
    for p in config.Playlists do
        let genres = String.concat ", " p.GenreCriteria
        printfn "    • %s [%s]" p.Name genres
    let labelStr =
        if List.isEmpty config.LabelNames then "(none)"
        else String.concat ", " config.LabelNames
    printfn "  Labels      : %s" labelStr
    printfn "  Storefront  : %s" config.Storefront
    printfn "  Lookback    : %d days" (Config.PositiveInt.value config.LookbackDays)
    printfn "  Rolling win : %d days" (Config.PositiveInt.value config.RollingWindowDays)
    printfn "  Similar cap : %d%%" (Config.Percent.value config.SimilarArtistMaxPercent)
    printfn "  Dev token   : [redacted]"
    printfn "  User token  : [redacted]"
    printfn "  Last.fm key : [redacted]"
    printfn ""
    printfn "Running sync…"

    let baseRuntime = HttpRuntime.create ()
    let mutable requestCount = 0

    let runtime : ApiContracts.ApiRuntime =
        { baseRuntime with
            Execute =
                fun request ->
                    async {
                        requestCount <- requestCount + 1
                        let svc = serviceLabel request.Service
                        eprintf "  [%d] %s %s %s\r" requestCount svc request.Method request.Path
                        let! response = baseRuntime.Execute request
                        return response
                    } }

    let cachedIds = loadPlaylistCache ()
    let outcome, observed = SyncEngine.runSync config runtime cachedIds
    eprintfn "  %d requests completed.                                          " requestCount

    // Merge cached IDs with newly resolved ones and persist.
    let mergedIds =
        observed.ResolvedPlaylistIds
        |> Map.fold (fun acc k v -> Map.add k v acc) cachedIds
    savePlaylistCache mergedIds

    printRequests observed.Requests
    printLogs observed.Logs
    printOutcome outcome

    printfn ""
    exitCodeFor outcome
