namespace Dropd.Tests

open System
open Dropd.Core
open Dropd.Core.Types
open Dropd.Core.Config

module TestHarness =

    // -- Log codes used by tests --

    [<Literal>]
    let UnknownLabel = "UnknownLabel"

    [<Literal>]
    let SimilarArtistServiceUnavailable = "SimilarArtistServiceUnavailable"

    [<Literal>]
    let AppleMusicAuthFailure = "AppleMusicAuthFailure"

    [<Literal>]
    let LastFmAuthFailure = "LastFmAuthFailure"

    [<Literal>]
    let PageLimitReached = "PageLimitReached"

    [<Literal>]
    let SyncAbortedRuntimeExceeded = "SyncAbortedRuntimeExceeded"

    [<Literal>]
    let SyncAbortedErrorRate = "SyncAbortedErrorRate"

    // -- Harness types --

    type CannedResponse =
        { StatusCode: int
          Body: string
          Headers: (string * string) list
          DelayMs: int option }

    type RecordedRequest =
        { Service: string
          Method: string
          Path: string
          Query: (string * string) list
          Headers: (string * string) list
          Body: string option }

    type EndpointKey =
        { Service: string
          Method: string
          Path: string
          QueryMatch: (string * string) list }

    type ResponseScript =
        | Always of CannedResponse
        | Sequence of CannedResponse list

    type FakeApiSetup =
        { Routes: Map<EndpointKey, ResponseScript> }

    type ObservedOutput =
        { Requests: RecordedRequest list
          Logs: ApiContracts.LogEntry list
          Outcome: SyncOutcome option }

    // -- Route matching --

    let private queryMatchSatisfied (required: (string * string) list) (actual: (string * string) list) =
        required
        |> List.forall (fun (k, v) ->
            actual |> List.exists (fun (ak, av) -> ak = k && av = v))

    let private routeMatches (key: EndpointKey) (service: string) (method_: string) (path: string) (query: (string * string) list) =
        key.Service = service
        && key.Method = method_
        && key.Path = path
        && queryMatchSatisfied key.QueryMatch query

    /// Find the matching route for a given request, returning the ResponseScript if found.
    let findRoute (setup: FakeApiSetup) (service: string) (method_: string) (path: string) (query: (string * string) list) =
        setup.Routes
        |> Map.tryPick (fun key script ->
            if routeMatches key service method_ path query then
                Some script
            else
                None)

    let private findRouteWithKey (setup: FakeApiSetup) (service: string) (method_: string) (path: string) (query: (string * string) list) =
        setup.Routes
        |> Map.tryPick (fun key script ->
            if routeMatches key service method_ path query then
                Some(key, script)
            else
                None)

    // -- Response script state --

    type ScriptState =
        { mutable Index: int }

    /// Serve the next response from a script, returning the updated state and
    /// the response.  The caller is responsible for writing the new state back
    /// to the script-state dictionary so that Sequence scripts advance correctly
    /// across invocations (Finding 14).
    let serveResponse (script: ResponseScript) (state: ScriptState) : ScriptState * CannedResponse =
        match script with
        | Always response -> state, response
        | Sequence responses ->
            let idx = min state.Index (responses.Length - 1)
            let newState = { Index = state.Index + 1 }
            newState, responses.[idx]

    /// Default 404 response for unmatched routes.
    let notFoundResponse =
        { StatusCode = 404
          Body = """{"error":"no matching route"}"""
          Headers = []
          DelayMs = None }

    let private toServiceString = function
        | ApiContracts.AppleMusic -> "apple"
        | ApiContracts.LastFm -> "lastfm"

    let private toRecordedRequest (request: ApiContracts.ApiRequest) =
        { Service = toServiceString request.Service
          Method = request.Method
          Path = request.Path
          Query = request.Query
          Headers = request.Headers
          Body = request.Body }

    let private toApiResponse (response: CannedResponse) : ApiContracts.ApiResponse =
        { StatusCode = response.StatusCode
          Body = response.Body
          Headers = response.Headers }

    let private fixedUtcNow () = DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)

    let private createRuntime (setup: FakeApiSetup) : ApiContracts.ApiRuntime =
        let scriptStates = Collections.Generic.Dictionary<EndpointKey, ScriptState>()

        let execute (request: ApiContracts.ApiRequest) =
            async {
                let service = toServiceString request.Service

                let responseScript =
                    findRouteWithKey setup service request.Method request.Path request.Query

                let response =
                    match responseScript with
                    | None -> notFoundResponse
                    | Some(key, script) ->
                        let state =
                            match scriptStates.TryGetValue key with
                            | true, existing -> existing
                            | _ ->
                                let created = { Index = 0 }
                                scriptStates.[key] <- created
                                created

                        // Finding 14: write the new state back so Sequence scripts
                        // advance correctly on each subsequent call.
                        let newState, canned = serveResponse script state
                        scriptStates.[key] <- newState
                        canned

                return response |> toApiResponse
            }

        { Execute = execute
          UtcNow = fixedUtcNow }

    // -- Sync runner --

    let runSync (config: SyncConfig) (setup: FakeApiSetup) : ObservedOutput =
        match Config.validate config with
        | Error _ ->
            let invalidLog: ApiContracts.LogEntry =
                { Level = ApiContracts.Error
                  Code = "InvalidConfig"
                  Message = "Sync configuration is invalid."
                  Data = Map.empty }

            { Requests = []
              Logs = [ invalidLog ]
              Outcome = Some(Aborted "InvalidConfig") }
        | Ok validConfig ->
            let runtime = createRuntime setup
            let outcome, observed = SyncEngine.runSync validConfig runtime

            { Requests = observed.Requests |> List.map toRecordedRequest
              Logs = observed.Logs
              Outcome = Some outcome }
