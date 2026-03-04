module Dropd.Tests.ApiResilienceTests

open System
open Expecto
open Dropd.Core.Types
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

let private resilienceConfig =
    { validConfig with
        LabelNames = []
        Playlists = [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ] }

let private baseResilienceSetup extras =
    setupWith
        ([ route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515,5765078" ] (Always(okFixture "favorited-artists.json"))
           route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
           route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-5765078.json"))
           route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 """{ "data": [{ "id": "p.elecDrops", "type": "library-playlists", "attributes": { "name": "Electronic Drops" } }] }"""))
           route "apple" "GET" "/v1/me/library/playlists/p.elecDrops/tracks" [] (Always(withStatus 200 (fixture "playlist-tracks-existing.json")))
           route "apple" "POST" "/v1/me/library/playlists/p.elecDrops/tracks" [] (Always(withStatus 200 "{}"))
           route "apple" "DELETE" "/v1/me/library/playlists/p.elecDrops/tracks" [] (Always(withStatus 200 "{}")) ]
         @ extras)

[<Tests>]
let tests =
    testList
        "API Resilience"
        [

          // DD-080: When an Apple Music API request returns HTTP 429 with a Retry-After header,
          // dropd shall wait for the indicated duration before retrying the request.
          testCase "DD-080 honors Retry-After header on 429" <| fun _ ->
              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ]
                          (Sequence [
                              { StatusCode = 429; Body = """{"error":"rate limited"}"""; Headers = [ "Retry-After", "5" ]; DelayMs = None }
                              okFixture "library-artists.json"
                          ])
                  ]

              let output = runSync resilienceConfig setup

              let libraryArtistRequests =
                  output.Requests |> List.filter (fun r -> r.Path = "/v1/me/library/artists")
              Expect.equal libraryArtistRequests.Length 2 "should make 2 requests to library-artists"

              Expect.notEqual output.Outcome (Some(Aborted "LibraryArtistsFailed")) "outcome should not be aborted"

              let retryLog = output.Logs |> List.tryFind (fun log -> log.Code = "RetryAfterWait")
              Expect.isSome retryLog "should have RetryAfterWait log"
              Expect.equal retryLog.Value.Data.["delay_ms"] "5000" "delay should be 5000ms"

          // DD-081: When an API request returns HTTP 429 without a Retry-After header,
          // dropd shall wait 2 seconds before retrying the request.
          testCase "DD-081 waits 2s on 429 without Retry-After" <| fun _ ->
              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ]
                          (Sequence [
                              { StatusCode = 429; Body = """{"error":"rate limited"}"""; Headers = []; DelayMs = None }
                              okFixture "library-artists.json"
                          ])
                  ]

              let output = runSync resilienceConfig setup

              let retryLog = output.Logs |> List.tryFind (fun log -> log.Code = "RetryAfterWait")
              Expect.isSome retryLog "should have RetryAfterWait log"
              Expect.equal retryLog.Value.Data.["delay_ms"] "2000" "delay should be 2000ms (default)"

          // DD-082: When an API request fails due to timeout, transient network error,
          // or HTTP 5xx response, dropd shall retry up to the configured retry limit
          // using exponential backoff with jitter.
          testCase "DD-082 retries with exponential backoff on transient failures" <| fun _ ->
              let config = { resilienceConfig with MaxRetries = 3; RequestTimeoutSeconds = 1 }

              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ]
                          (Sequence [
                              // First attempt: simulate timeout via delay > timeout
                              { StatusCode = 200; Body = fixture "library-artists.json"; Headers = []; DelayMs = Some 1500 }
                              // Second attempt: 503
                              { StatusCode = 503; Body = fixture "error-500.json"; Headers = []; DelayMs = None }
                              // Third attempt: success
                              { StatusCode = 200; Body = fixture "library-artists.json"; Headers = []; DelayMs = None }
                          ])
                  ]

              let output = runSync config setup

              // Assert: exactly 3 requests to library-artists (timeout + 503 + success)
              let libraryArtistRequests =
                  output.Requests |> List.filter (fun r -> r.Path = "/v1/me/library/artists")
              Expect.equal libraryArtistRequests.Length 3 "should make 3 requests to library-artists"

              // Assert: outcome is not Aborted
              Expect.notEqual output.Outcome (Some(Aborted "LibraryArtistsFailed")) "outcome should not be aborted"

              // Assert: logs contain retry log codes
              let retryLogs = output.Logs |> List.filter (fun log -> log.Code = "TransientRetryScheduled")
              Expect.isTrue (retryLogs.Length >= 1) "should have at least one TransientRetryScheduled log"

          // DD-083: When an Apple Music API paginated response contains a next link, dropd
          // shall request subsequent pages until next is absent or the page-limit is reached.
          testCase "DD-083 follows pagination next links" <| fun _ ->
              let page1Body = """{
                "data": [{
                    "id": "r.abc123", "type": "library-artists",
                    "attributes": { "name": "Radiohead" },
                    "relationships": { "catalog": { "data": [{ "id": "657515", "type": "artists", "attributes": { "name": "Radiohead" } }] } }
                }],
                "next": "/v1/me/library/artists?offset=25"
              }"""
              let page2Body = """{
                "data": [{
                    "id": "r.def456", "type": "library-artists",
                    "attributes": { "name": "Bonobo" },
                    "relationships": { "catalog": { "data": [{ "id": "5765078", "type": "artists", "attributes": { "name": "Bonobo" } }] } }
                }]
              }"""

              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ] (Always { StatusCode = 200; Body = page1Body; Headers = []; DelayMs = None })
                      route "apple" "GET" "/v1/me/library/artists?offset=25" [] (Always { StatusCode = 200; Body = page2Body; Headers = []; DelayMs = None })
                  ]

              let output = runSync resilienceConfig setup

              let libraryArtistRequests =
                  output.Requests |> List.filter (fun r -> r.Path.StartsWith "/v1/me/library/artists")
              Expect.equal libraryArtistRequests.Length 2 "should fetch 2 pages of library artists"
              Expect.notEqual output.Outcome (Some(Aborted "LibraryArtistsFailed")) "outcome should not be aborted"

          // DD-084: If the configured page-limit is reached while a next link is still present,
          // then dropd shall log a warning.
          testCase "DD-084 logs warning when page limit reached" <| fun _ ->
              let config = { resilienceConfig with MaxPages = 1 }
              let pageBody = """{
                "data": [{
                    "id": "r.abc123", "type": "library-artists",
                    "attributes": { "name": "Radiohead" },
                    "relationships": { "catalog": { "data": [{ "id": "657515", "type": "artists", "attributes": { "name": "Radiohead" } }] } }
                }, {
                    "id": "r.def456", "type": "library-artists",
                    "attributes": { "name": "Bonobo" },
                    "relationships": { "catalog": { "data": [{ "id": "5765078", "type": "artists", "attributes": { "name": "Bonobo" } }] } }
                }],
                "next": "/v1/me/library/artists?offset=25"
              }"""

              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ] (Always { StatusCode = 200; Body = pageBody; Headers = []; DelayMs = None })
                  ]

              let output = runSync config setup

              let libraryArtistRequests =
                  output.Requests |> List.filter (fun r -> r.Path.StartsWith "/v1/me/library/artists")
              Expect.equal libraryArtistRequests.Length 1 "should only fetch 1 page"

              let pageLimitLog = output.Logs |> List.tryFind (fun log -> log.Code = "PageLimitReached")
              Expect.isSome pageLimitLog "should log PageLimitReached"
              Expect.isTrue (pageLimitLog.Value.Data.ContainsKey "endpoint") "should include endpoint in log data"

          // DD-085: If the configured page-limit is reached while a next link is still present,
          // then dropd shall continue the sync with the fetched subset.
          testCase "DD-085 continues sync with partial data after page limit" <| fun _ ->
              let config = { resilienceConfig with MaxPages = 1 }
              let pageBody = """{
                "data": [{
                    "id": "r.abc123", "type": "library-artists",
                    "attributes": { "name": "Radiohead" },
                    "relationships": { "catalog": { "data": [{ "id": "657515", "type": "artists", "attributes": { "name": "Radiohead" } }] } }
                }, {
                    "id": "r.def456", "type": "library-artists",
                    "attributes": { "name": "Bonobo" },
                    "relationships": { "catalog": { "data": [{ "id": "5765078", "type": "artists", "attributes": { "name": "Bonobo" } }] } }
                }],
                "next": "/v1/me/library/artists?offset=25"
              }"""

              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ] (Always { StatusCode = 200; Body = pageBody; Headers = []; DelayMs = None })
                  ]

              let output = runSync config setup

              // Sync should continue despite page limit — verify that album requests were made
              let albumRequests =
                  output.Requests |> List.filter (fun r -> r.Path.Contains "/albums")
              Expect.isTrue (albumRequests.Length >= 1) "sync should continue to fetch albums after page limit"
              Expect.notEqual output.Outcome (Some(Aborted "PageLimitReached")) "outcome should not be Aborted"

          // DD-086: If sync runtime exceeds the configured maximum sync runtime, then dropd
          // shall abort the current sync.
          testCase "DD-086 aborts sync when runtime exceeds maximum" <| fun _ ->
              let config = { resilienceConfig with MaxSyncRuntimeMinutes = 1 }
              let callCount = ref 0
              let startTime = DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
              let clockFn () =
                  callCount.Value <- callCount.Value + 1
                  // After a few UtcNow calls (wrap + initial requests), advance past limit
                  if callCount.Value > 3 then
                      startTime.AddMinutes(2.0)
                  else
                      startTime

              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ] (Always(okFixture "library-artists.json"))
                  ]

              let output = runSyncWithClock clockFn config setup

              Expect.equal output.Outcome (Some(Aborted "RuntimeExceeded")) "outcome should be Aborted RuntimeExceeded"

          // DD-087: If sync runtime exceeds the configured maximum sync runtime, then dropd
          // shall log an aborted sync outcome.
          testCase "DD-087 logs aborted outcome on runtime exceeded" <| fun _ ->
              let config = { resilienceConfig with MaxSyncRuntimeMinutes = 1 }
              let callCount = ref 0
              let startTime = DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)
              let clockFn () =
                  callCount.Value <- callCount.Value + 1
                  if callCount.Value > 3 then
                      startTime.AddMinutes(2.0)
                  else
                      startTime

              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ] (Always(okFixture "library-artists.json"))
                  ]

              let output = runSyncWithClock clockFn config setup

              let abortLog = output.Logs |> List.tryFind (fun log -> log.Code = "SyncAbortedRuntimeExceeded")
              Expect.isSome abortLog "should log SyncAbortedRuntimeExceeded"

          // DD-088: If the API error rate within a sync exceeds the configured threshold,
          // then dropd shall abort the current sync.
          testCase "DD-088 aborts sync when error rate exceeds threshold" <| fun _ ->
              let config = { resilienceConfig with ErrorRateAbortPercent = 30; MaxRetries = 0 }

              // Route many endpoints to return 500 so error rate exceeds 30%.
              // Library artists and ratings succeed so the sync gets far enough.
              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ] (Always(okFixture "library-artists.json"))
                      // Both artist album endpoints return 500
                      route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 500 (fixture "error-500.json")))
                      route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(withStatus 500 (fixture "error-500.json")))
                  ]

              let output = runSync config setup

              match output.Outcome with
              | Some(Aborted reason) ->
                  Expect.isTrue (reason = "ErrorRate" || reason = "CatalogUnavailable") $"should abort with ErrorRate or CatalogUnavailable, got {reason}"
              | other ->
                  failtest $"expected Aborted outcome, got {other}"

          // DD-089: If the API error rate within a sync exceeds the configured threshold,
          // then dropd shall log an aborted sync outcome with error-rate details.
          testCase "DD-089 logs aborted outcome with error rate details" <| fun _ ->
              let config = { resilienceConfig with ErrorRateAbortPercent = 30; MaxRetries = 0 }

              let setup =
                  baseResilienceSetup [
                      route "apple" "GET" "/v1/me/library/artists" [ "include", "catalog" ] (Always(okFixture "library-artists.json"))
                      route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 500 (fixture "error-500.json")))
                      route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(withStatus 500 (fixture "error-500.json")))
                  ]

              let output = runSync config setup

              let errorRateLog = output.Logs |> List.tryFind (fun log -> log.Code = "SyncAbortedErrorRate")
              Expect.isSome errorRateLog "should log SyncAbortedErrorRate"
              let data = errorRateLog.Value.Data
              Expect.isTrue (data.ContainsKey "error_rate") "should include error_rate"
              Expect.isTrue (data.ContainsKey "failed") "should include failed"
              Expect.isTrue (data.ContainsKey "total") "should include total"

          ]
