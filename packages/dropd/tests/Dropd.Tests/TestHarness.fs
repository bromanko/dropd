namespace Dropd.Tests

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

    type LogEntry =
        { Level: string
          Code: string
          Message: string }

    type ObservedOutput =
        { Requests: RecordedRequest list
          Logs: LogEntry list
          Outcome: SyncOutcome option }

    // -- Route matching --

    let private queryMatchSatisfied (required: (string * string) list) (actual: (string * string) list) =
        required
        |> List.forall (fun (k, v) ->
            actual |> List.exists (fun (ak, av) -> ak = k && av = v))

    /// Find the matching route for a given request, returning the ResponseScript if found.
    let findRoute (setup: FakeApiSetup) (service: string) (method_: string) (path: string) (query: (string * string) list) =
        setup.Routes
        |> Map.tryPick (fun key script ->
            if key.Service = service
               && key.Method = method_
               && key.Path = path
               && queryMatchSatisfied key.QueryMatch query then
                Some script
            else
                None)

    // -- Response script state --

    type ScriptState =
        { mutable Index: int }

    /// Serve the next response from a script, advancing sequence position.
    let serveResponse (script: ResponseScript) (state: ScriptState) : CannedResponse =
        match script with
        | Always response -> response
        | Sequence responses ->
            let idx = min state.Index (responses.Length - 1)
            state.Index <- state.Index + 1
            responses.[idx]

    /// Default 404 response for unmatched routes.
    let notFoundResponse =
        { StatusCode = 404
          Body = """{"error":"no matching route"}"""
          Headers = []
          DelayMs = None }

    // -- Sync runner (minimal safe behavior) --

    let runSync (_config: SyncConfig) (_setup: FakeApiSetup) : ObservedOutput =
        { Requests = []
          Logs = []
          Outcome = Some Success }
