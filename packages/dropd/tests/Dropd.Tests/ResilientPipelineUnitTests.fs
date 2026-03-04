module Dropd.Tests.ResilientPipelineUnitTests

open System
open Expecto
open Dropd.Core
open Dropd.Core.ResilientPipeline

module AC = ApiContracts

let private defaultPipelineConfig : PipelineConfig =
    { MaxRetries = 3
      RequestTimeoutSeconds = 10
      MaxPages = 20
      PageSize = 100
      MaxSyncRuntimeMinutes = 15
      ErrorRateAbortPercent = 30 }

let private dummyRequest : AC.ApiRequest =
    { Service = AC.AppleMusic
      Method = "GET"
      Path = "/v1/me/library/artists"
      Query = []
      Headers = []
      Body = None }

let private okResponse body : AC.ApiResponse =
    { StatusCode = 200; Body = body; Headers = [] }

let private errorResponse code : AC.ApiResponse =
    { StatusCode = code; Body = """{"error":"fail"}"""; Headers = [] }

let private fakeRuntime (responses: AC.ApiResponse list) =
    let index = ref 0
    let callCount = ref 0
    let runtime : AC.ApiRuntime =
        { Execute = fun _ -> async {
              callCount.Value <- callCount.Value + 1
              let idx = min index.Value (responses.Length - 1)
              index.Value <- index.Value + 1
              return responses.[idx]
          }
          UtcNow = fun () -> DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }
    runtime, callCount

let private noDelay (_ms: int) = async { return () }

let private recordingDelay () =
    let recorded = ResizeArray<int>()
    let delay (ms: int) = async { recorded.Add(ms) }
    delay, recorded

[<Tests>]
let retryTests =
    testList "Unit.ResilientPipeline.Retry" [
        testCase "retries up to MaxRetries on 5xx then returns last failure" <| fun _ ->
            let inner, callCount = fakeRuntime [ errorResponse 503; errorResponse 503; errorResponse 503; errorResponse 503 ]
            let stats = createStats ()
            let delay, _delays = recordingDelay ()
            let wrapped = wrap { defaultPipelineConfig with MaxRetries = 3 } stats delay ignore (Some 42) inner

            let response = wrapped.Execute dummyRequest |> Async.RunSynchronously

            Expect.equal callCount.Value 4 "should make 1 + 3 retry attempts"
            Expect.equal response.StatusCode 503 "should return last 503"
            Expect.equal stats.FailedRequests 4 "all 4 attempts should be failures"

        testCase "succeeds on retry after transient 5xx" <| fun _ ->
            let inner, callCount = fakeRuntime [ errorResponse 503; errorResponse 503; okResponse """{"data":[]}""" ]
            let stats = createStats ()
            let delay, _delays = recordingDelay ()
            let wrapped = wrap { defaultPipelineConfig with MaxRetries = 3 } stats delay ignore (Some 42) inner

            let response = wrapped.Execute dummyRequest |> Async.RunSynchronously

            Expect.equal callCount.Value 3 "should make 3 total attempts"
            Expect.equal response.StatusCode 200 "should return 200"
            Expect.equal stats.FailedRequests 2 "2 failures before success"

        testCase "retries on timeout up to MaxRetries" <| fun _ ->
            // The fake runtime delays 1500ms on first two calls, but timeout is 1s.
            // Third call returns 200 quickly.
            let index = ref 0
            let callCount = ref 0
            let inner : AC.ApiRuntime =
                { Execute = fun _ -> async {
                      callCount.Value <- callCount.Value + 1
                      let i = index.Value
                      index.Value <- i + 1
                      if i < 2 then
                          do! Async.Sleep 1500
                          return okResponse """{"data":[]}"""
                      else
                          return okResponse """{"data":[]}"""
                  }
                  UtcNow = fun () -> DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }
            let stats = createStats ()
            let delay, _delays = recordingDelay ()
            let wrapped = wrap { defaultPipelineConfig with MaxRetries = 3; RequestTimeoutSeconds = 1 } stats delay ignore (Some 42) inner

            let response = wrapped.Execute dummyRequest |> Async.RunSynchronously

            Expect.equal callCount.Value 3 "should make 3 total attempts"
            Expect.equal response.StatusCode 200 "should return 200 on success"
            Expect.equal stats.FailedRequests 2 "2 timeout failures"

        testCase "computes exponential delays with deterministic jitter" <| fun _ ->
            let inner, _callCount = fakeRuntime [ errorResponse 503; errorResponse 503; errorResponse 503; errorResponse 503; okResponse """{"data":[]}""" ]
            let stats = createStats ()
            let delay, _delays = recordingDelay ()
            let wrapped = wrap { defaultPipelineConfig with MaxRetries = 4 } stats delay ignore (Some 42) inner

            let _response = wrapped.Execute dummyRequest |> Async.RunSynchronously

            Expect.equal stats.ComputedDelays.Count 4 "should have 4 computed delays"
            // Each delay should be in [base * 2^attempt, base * 2^attempt + 1000)
            // base = 1000, attempts 0..3 → [1000,2000), [2000,3000), [4000,5000), [8000,9000)
            let expected = [ (1000, 2000); (2000, 3000); (4000, 5000); (8000, 9000) ]
            Seq.zip stats.ComputedDelays expected
            |> Seq.iteri (fun i (actual, (lo, hi)) ->
                Expect.isTrue (actual >= lo && actual < hi) $"delay {i} = {actual} should be in [{lo}, {hi})")
    ]
