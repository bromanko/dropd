namespace Dropd.Core

open System
open Dropd.Core.Types

module ResilientPipeline =

    module AC = ApiContracts

    /// Execution statistics tracked by the pipeline across all requests
    /// within a single sync run.
    type PipelineStats =
        { mutable TotalRequests: int
          mutable FailedRequests: int
          ComputedDelays: ResizeArray<int> }

    /// Configuration extracted from ValidSyncConfig for the pipeline.
    type PipelineConfig =
        { MaxRetries: int
          RequestTimeoutSeconds: int
          MaxPages: int
          PageSize: int
          MaxSyncRuntimeMinutes: int
          ErrorRateAbortPercent: int }

    /// Exception raised when execution guards trigger a sync abort.
    exception SyncAbortedException of reason: string * logEntry: AC.LogEntry

    let configFrom (config: Config.ValidSyncConfig) : PipelineConfig =
        { MaxRetries = Config.NonNegativeInt.value config.MaxRetries
          RequestTimeoutSeconds = Config.PositiveInt.value config.RequestTimeoutSeconds
          MaxPages = Config.PositiveInt.value config.MaxPages
          PageSize = Config.PositiveInt.value config.PageSize
          MaxSyncRuntimeMinutes = Config.PositiveInt.value config.MaxSyncRuntimeMinutes
          ErrorRateAbortPercent = Config.Percent.value config.ErrorRateAbortPercent }

    let createStats () : PipelineStats =
        { TotalRequests = 0
          FailedRequests = 0
          ComputedDelays = ResizeArray() }

    /// Wrap an ApiRuntime with resilience behaviour.
    ///
    /// Parameters:
    /// - `pipelineConfig`: retry/timeout/guard settings.
    /// - `stats`: mutable stats accumulator shared for the sync run.
    /// - `delay`: function that pauses for the given milliseconds. Inject
    ///   `Async.Sleep` in production or a no-op in tests.
    /// - `appendLog`: callback used by the pipeline to emit structured logs.
    /// - `jitterSeed`: optional seed for deterministic jitter in tests.
    /// - `inner`: the original ApiRuntime to wrap.
    let wrap
        (pipelineConfig: PipelineConfig)
        (stats: PipelineStats)
        (delay: int -> Async<unit>)
        (appendLog: AC.LogEntry -> unit)
        (jitterSeed: int option)
        (inner: AC.ApiRuntime)
        : AC.ApiRuntime =

        let execute (request: AC.ApiRequest) : Async<AC.ApiResponse> =
            async {
                // Phase 3 milestone 1: transparent pass-through.
                let! response = inner.Execute request
                stats.TotalRequests <- stats.TotalRequests + 1

                if response.StatusCode >= 500 then
                    stats.FailedRequests <- stats.FailedRequests + 1

                return response
            }

        { Execute = execute
          UtcNow = inner.UtcNow }

    /// Fetch all pages from a paginated Apple Music endpoint.
    ///
    /// The `firstRequest` is executed via the runtime. Subsequent pages
    /// are fetched by following the `next` field in each response body.
    /// Stops when `next` is absent, the response is an error, or
    /// `maxPages` is reached.
    ///
    /// `parsePage` should parse the body once and return `(items, nextPath)`.
    ///
    /// Returns `Ok (allItems, pagesFetched, truncated)` on success or
    /// `Error response` on the first non-2xx response. `truncated` is
    /// `true` when `maxPages` was reached while `next` was still present.
    let fetchAllPages
        (runtime: AC.ApiRuntime)
        (maxPages: int)
        (firstRequest: AC.ApiRequest)
        (parsePage: string -> 'a list * string option)
        : Async<Result<'a list * int * bool, AC.ApiResponse>> =
        async {
            let acc = ResizeArray<'a>()
            let mutable nextRequest = Some firstRequest
            let mutable page = 0
            let mutable error = None
            let mutable truncated = false

            while nextRequest.IsSome && page < maxPages && error.IsNone do
                let req = nextRequest.Value
                let! response = runtime.Execute req

                if response.StatusCode >= 200 && response.StatusCode < 300 then
                    let items, next = parsePage response.Body
                    acc.AddRange(items)
                    page <- page + 1

                    match next with
                    | Some nextPath when page < maxPages ->
                        nextRequest <-
                            Some
                                { req with
                                    Path = nextPath
                                    Query = [] }
                    | Some _ ->
                        truncated <- true
                        nextRequest <- None
                    | None -> nextRequest <- None
                elif response.StatusCode = 404 then
                    // Apple Music returns 404 for empty collections.
                    nextRequest <- None
                else
                    error <- Some response

            match error with
            | Some resp -> return Error resp
            | None -> return Ok(acc |> Seq.toList, page, truncated)
        }
