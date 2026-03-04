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

let private responseWithHeaders code body headers : AC.ApiResponse =
    { StatusCode = code; Body = body; Headers = headers }

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
            Expect.equal stats.FailedRequests 1 "one final failed outcome"

        testCase "succeeds on retry after transient 5xx" <| fun _ ->
            let inner, callCount = fakeRuntime [ errorResponse 503; errorResponse 503; okResponse """{"data":[]}""" ]
            let stats = createStats ()
            let delay, _delays = recordingDelay ()
            let wrapped = wrap { defaultPipelineConfig with MaxRetries = 3 } stats delay ignore (Some 42) inner

            let response = wrapped.Execute dummyRequest |> Async.RunSynchronously

            Expect.equal callCount.Value 3 "should make 3 total attempts"
            Expect.equal response.StatusCode 200 "should return 200"
            Expect.equal stats.FailedRequests 0 "final outcome succeeded"

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
            Expect.equal stats.FailedRequests 0 "final outcome succeeded"

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

[<Tests>]
let rateLimitTests =
    testList "Unit.ResilientPipeline.RateLimit" [
        testCase "waits Retry-After seconds on 429" <| fun _ ->
            let inner, _callCount = fakeRuntime [
                responseWithHeaders 429 """{"error":"rate limited"}""" [ "Retry-After", "3" ]
                okResponse """{"data":[]}"""
            ]
            let stats = createStats ()
            let delay, delays = recordingDelay ()
            let wrapped = wrap defaultPipelineConfig stats delay ignore (Some 42) inner

            let response = wrapped.Execute dummyRequest |> Async.RunSynchronously

            Expect.equal response.StatusCode 200 "should return 200 after retry"
            Expect.equal delays.Count 1 "should record one delay"
            Expect.equal delays.[0] 3000 "delay should be 3000ms (3 seconds)"

        testCase "waits 2 seconds on 429 without Retry-After" <| fun _ ->
            let inner, _callCount = fakeRuntime [
                responseWithHeaders 429 """{"error":"rate limited"}""" []
                okResponse """{"data":[]}"""
            ]
            let stats = createStats ()
            let delay, delays = recordingDelay ()
            let wrapped = wrap defaultPipelineConfig stats delay ignore (Some 42) inner

            let response = wrapped.Execute dummyRequest |> Async.RunSynchronously

            Expect.equal response.StatusCode 200 "should return 200 after retry"
            Expect.equal delays.Count 1 "should record one delay"
            Expect.equal delays.[0] 2000 "delay should be 2000ms (default 2 seconds)"
    ]

[<Tests>]
let paginationTests =
    testList "Unit.ResilientPipeline.Pagination" [
        testCase "follows next links across pages" <| fun _ ->
            let page1 = """{"data":["a","b"],"next":"/page2"}"""
            let page2 = """{"data":["c"]}"""
            let inner, _callCount = fakeRuntime [ okResponse page1; okResponse page2 ]

            let parsePage (body: string) =
                let doc = System.Text.Json.JsonDocument.Parse(body)
                let data = doc.RootElement.GetProperty("data")
                let items = data.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
                let next = JsonHelpers.tryGetString "next" doc.RootElement
                items, next

            let result =
                fetchAllPages inner 5 dummyRequest parsePage
                |> Async.RunSynchronously

            match result with
            | Ok (items, pagesFetched, truncated) ->
                Expect.equal items [ "a"; "b"; "c" ] "should combine items from both pages"
                Expect.equal pagesFetched 2 "should fetch 2 pages"
                Expect.isFalse truncated "should not be truncated"
            | Error _ -> failtest "should not return error"

        testCase "stops at maxPages and sets truncated flag" <| fun _ ->
            let page1 = """{"data":["a"],"next":"/page2"}"""
            let page2 = """{"data":["b"],"next":"/page3"}"""
            let page3 = """{"data":["c"],"next":"/page4"}"""
            let inner, _callCount = fakeRuntime [ okResponse page1; okResponse page2; okResponse page3 ]

            let parsePage (body: string) =
                let doc = System.Text.Json.JsonDocument.Parse(body)
                let data = doc.RootElement.GetProperty("data")
                let items = data.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
                let next = JsonHelpers.tryGetString "next" doc.RootElement
                items, next

            let result =
                fetchAllPages inner 2 dummyRequest parsePage
                |> Async.RunSynchronously

            match result with
            | Ok (items, pagesFetched, truncated) ->
                Expect.equal items [ "a"; "b" ] "should only have items from 2 pages"
                Expect.equal pagesFetched 2 "should fetch only 2 pages"
                Expect.isTrue truncated "should be truncated"
            | Error _ -> failtest "should not return error"

        testCase "returns Error on non-2xx response" <| fun _ ->
            let page1 = """{"data":["a"],"next":"/page2"}"""
            let inner, _callCount = fakeRuntime [ okResponse page1; errorResponse 500 ]

            let parsePage (body: string) =
                let doc = System.Text.Json.JsonDocument.Parse(body)
                let data = doc.RootElement.GetProperty("data")
                let items = data.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.toList
                let next = JsonHelpers.tryGetString "next" doc.RootElement
                items, next

            let result =
                fetchAllPages inner 5 dummyRequest parsePage
                |> Async.RunSynchronously

            match result with
            | Error resp ->
                Expect.equal resp.StatusCode 500 "should return 500 error"
            | Ok _ -> failtest "should return error"
    ]

[<Tests>]
let guardTests =
    testList "Unit.ResilientPipeline.Guards" [
        testCase "aborts when runtime exceeds maximum" <| fun _ ->
            let utcNowCallCount = ref 0
            let startTime = DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
            let inner : AC.ApiRuntime =
                { Execute = fun _ -> async {
                      return okResponse """{"data":[]}"""
                  }
                  UtcNow = fun () ->
                      utcNowCallCount.Value <- utcNowCallCount.Value + 1
                      // First UtcNow call is from wrap() recording syncStartTime.
                      // Second is from checkGuards() after first Execute.
                      // Third is from checkGuards() after second Execute — return late time.
                      if utcNowCallCount.Value >= 3 then
                          startTime.AddMinutes(16.0)
                      else
                          startTime }
            let stats = createStats ()
            let config = { defaultPipelineConfig with MaxSyncRuntimeMinutes = 15 }
            let wrapped = wrap config stats noDelay ignore (Some 42) inner

            // First call succeeds
            let _r1 = wrapped.Execute dummyRequest |> Async.RunSynchronously

            // Second call should abort
            let mutable aborted = false
            let mutable reason = ""
            try
                let _r2 = wrapped.Execute dummyRequest |> Async.RunSynchronously
                ()
            with
            | SyncAbortedException(r, _) ->
                aborted <- true
                reason <- r

            Expect.isTrue aborted "should raise SyncAbortedException"
            Expect.equal reason "RuntimeExceeded" "reason should be RuntimeExceeded"

        testCase "does not abort when runtime is within limit" <| fun _ ->
            let startTime = DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
            let inner : AC.ApiRuntime =
                { Execute = fun _ -> async { return okResponse """{"data":[]}""" }
                  UtcNow = fun () -> startTime.AddMinutes(14.0) }
            let stats = createStats ()
            let config = { defaultPipelineConfig with MaxSyncRuntimeMinutes = 15 }
            let wrapped = wrap config stats noDelay ignore (Some 42) inner

            let response = wrapped.Execute dummyRequest |> Async.RunSynchronously
            Expect.equal response.StatusCode 200 "should return 200 without abort"

        testCase "aborts when error rate exceeds threshold" <| fun _ ->
            let callIndex = ref 0
            let inner : AC.ApiRuntime =
                { Execute = fun _ -> async {
                      callIndex.Value <- callIndex.Value + 1
                      // 4 out of 6 fail (67%)
                      if callIndex.Value <= 4 then
                          return errorResponse 500
                      else
                          return okResponse """{"data":[]}"""
                  }
                  UtcNow = fun () -> DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }
            let stats = createStats ()
            // MaxRetries = 0 so each call is a single attempt (no retries)
            let config = { defaultPipelineConfig with ErrorRateAbortPercent = 30; MaxRetries = 0 }
            let wrapped = wrap config stats noDelay ignore (Some 42) inner

            let mutable aborted = false
            let mutable reason = ""
            try
                // Make enough calls to trigger error rate check (need >= 5 total)
                for _ in 1..6 do
                    let _r = wrapped.Execute dummyRequest |> Async.RunSynchronously
                    ()
            with
            | SyncAbortedException(r, _) ->
                aborted <- true
                reason <- r

            Expect.isTrue aborted "should raise SyncAbortedException"
            Expect.equal reason "ErrorRate" "reason should be ErrorRate"

        testCase "does not abort on error rate below threshold" <| fun _ ->
            let callIndex = ref 0
            let inner : AC.ApiRuntime =
                { Execute = fun _ -> async {
                      callIndex.Value <- callIndex.Value + 1
                      // 1 out of 5 fails (20%)
                      if callIndex.Value = 1 then
                          return errorResponse 500
                      else
                          return okResponse """{"data":[]}"""
                  }
                  UtcNow = fun () -> DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }
            let stats = createStats ()
            let config = { defaultPipelineConfig with ErrorRateAbortPercent = 30; MaxRetries = 0 }
            let wrapped = wrap config stats noDelay ignore (Some 42) inner

            // All 5 calls should complete without abort
            for _ in 1..5 do
                let _r = wrapped.Execute dummyRequest |> Async.RunSynchronously
                ()

            Expect.equal stats.TotalRequests 5 "should complete all 5 requests"
    ]
