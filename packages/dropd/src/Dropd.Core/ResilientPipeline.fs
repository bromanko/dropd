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

        let rng =
            match jitterSeed with
            | Some seed -> Random(seed)
            | None -> Random()

        let timeoutMs = pipelineConfig.RequestTimeoutSeconds * 1000
        let syncStartTime = inner.UtcNow()

        let executeWithTimeout (request: AC.ApiRequest) : Async<Result<AC.ApiResponse, string>> =
            async {
                try
                    let! child = Async.StartChild(inner.Execute request, timeoutMs)
                    let! response = child
                    return Ok response
                with :? TimeoutException ->
                    return Error "timeout"
            }

        let isTransient (statusCode: int) = statusCode >= 500
        let basedelayMs = 1000

        let checkGuards () =
            // Runtime limit check
            let elapsed = inner.UtcNow() - syncStartTime
            if elapsed > TimeSpan.FromMinutes(float pipelineConfig.MaxSyncRuntimeMinutes) then
                let logEntry : AC.LogEntry =
                    { Level = AC.Error
                      Code = "SyncAbortedRuntimeExceeded"
                      Message = $"Sync runtime exceeded maximum of {pipelineConfig.MaxSyncRuntimeMinutes} minutes."
                      Data = Map [ "elapsed_minutes", string (int elapsed.TotalMinutes); "limit_minutes", string pipelineConfig.MaxSyncRuntimeMinutes ] }
                raise (SyncAbortedException("RuntimeExceeded", logEntry))

            // Error rate check (only after 5+ requests to avoid early abort)
            if stats.TotalRequests >= 5 then
                let errorRate = stats.FailedRequests * 100 / stats.TotalRequests
                if errorRate > pipelineConfig.ErrorRateAbortPercent then
                    let logEntry : AC.LogEntry =
                        { Level = AC.Error
                          Code = "SyncAbortedErrorRate"
                          Message = $"API error rate ({errorRate}%%) exceeded threshold ({pipelineConfig.ErrorRateAbortPercent}%%)."
                          Data = Map [
                              "error_rate", string errorRate
                              "failed", string stats.FailedRequests
                              "total", string stats.TotalRequests ] }
                    raise (SyncAbortedException("ErrorRate", logEntry))

        let execute (request: AC.ApiRequest) : Async<AC.ApiResponse> =
            async {
                // Only retry and track stats for Apple Music requests.
                // Last.fm errors are handled gracefully by the similar-artist provider.
                let shouldRetry = request.Service = AC.AppleMusic
                let mutable attempt = 0
                let mutable finalResponse : AC.ApiResponse option = None

                if not shouldRetry then
                    let! response = inner.Execute request
                    return response
                else

                while finalResponse.IsNone do
                    let! result = executeWithTimeout request

                    match result with
                    | Ok response when response.StatusCode = 429 ->
                        // Rate-limit handling: 429 is retryable.

                        if attempt < pipelineConfig.MaxRetries then
                            let retryAfter =
                                response.Headers
                                |> List.tryFind (fun (k, _) -> String.Equals(k, "Retry-After", StringComparison.OrdinalIgnoreCase))
                                |> Option.bind (fun (_, v) -> match Int32.TryParse(v) with | true, n -> Some n | _ -> None)
                                |> Option.defaultValue 2

                            let delayMs = retryAfter * 1000
                            stats.ComputedDelays.Add(delayMs)

                            appendLog
                                { Level = AC.Info
                                  Code = "RetryAfterWait"
                                  Message = $"Rate limited (429). Waiting {delayMs}ms before retry."
                                  Data = Map [ "delay_ms", string delayMs; "attempt", string attempt ] }

                            do! delay delayMs
                            attempt <- attempt + 1
                        else
                            finalResponse <- Some response

                    | Ok response when isTransient response.StatusCode ->
                        if attempt < pipelineConfig.MaxRetries then
                            let jitter = rng.Next(basedelayMs)
                            let delayMs = basedelayMs * (pown 2 attempt) + jitter
                            stats.ComputedDelays.Add(delayMs)

                            appendLog
                                { Level = AC.Info
                                  Code = "TransientRetryScheduled"
                                  Message = $"Transient failure (HTTP {response.StatusCode}). Retrying in {delayMs}ms."
                                  Data = Map [ "delay_ms", string delayMs; "attempt", string attempt; "status", string response.StatusCode ] }

                            do! delay delayMs
                            attempt <- attempt + 1
                        else
                            finalResponse <- Some response

                    | Ok response ->
                        // Success or non-retryable status.
                        finalResponse <- Some response

                    | Error _timeoutMsg ->
                        if attempt < pipelineConfig.MaxRetries then
                            let jitter = rng.Next(basedelayMs)
                            let delayMs = basedelayMs * (pown 2 attempt) + jitter
                            stats.ComputedDelays.Add(delayMs)

                            appendLog
                                { Level = AC.Info
                                  Code = "TransientRetryScheduled"
                                  Message = $"Request timeout. Retrying in {delayMs}ms."
                                  Data = Map [ "delay_ms", string delayMs; "attempt", string attempt; "status", "504" ] }

                            do! delay delayMs
                            attempt <- attempt + 1
                        else
                            // Synthesize a 504 response for the timeout.
                            finalResponse <-
                                Some
                                    { StatusCode = 504
                                      Body = """{"error":"request timeout"}"""
                                      Headers = [] }

                // Track stats and check guards after retry loop completes
                let response = finalResponse.Value
                stats.TotalRequests <- stats.TotalRequests + 1

                if response.StatusCode >= 500 then
                    stats.FailedRequests <- stats.FailedRequests + 1

                checkGuards ()

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
