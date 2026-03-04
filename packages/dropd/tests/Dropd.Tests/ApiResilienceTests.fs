module Dropd.Tests.ApiResilienceTests

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
          // Setup: Route page 1 → {data: [...], next: "/page2"}, page 2 → {data: [...], next: null}.
          // Assert: Both pages are fetched. Combined data is complete.
          ptestCase "DD-083 follows pagination next links" <| fun _ -> ()

          // DD-084: If the configured page-limit is reached while a next link is still present,
          // then dropd shall log a warning.
          // Setup: MaxPages = 2. Route 3 pages with next links.
          // Assert: Only 2 pages fetched. Logs contain Code = PageLimitReached.
          ptestCase "DD-084 logs warning when page limit reached" <| fun _ -> ()

          // DD-085: If the configured page-limit is reached while a next link is still present,
          // then dropd shall continue the sync with the fetched subset.
          // Setup: MaxPages = 2. Route 3 pages with data.
          // Assert: Sync continues with data from first 2 pages only.
          //         ObservedOutput.Outcome is not Aborted.
          ptestCase "DD-085 continues sync with partial data after page limit" <| fun _ -> ()

          // DD-086: If sync runtime exceeds the configured maximum sync runtime, then dropd
          // shall abort the current sync.
          // Setup: MaxSyncRuntimeMinutes = 15. Simulate delays exceeding 15 minutes.
          // Assert: ObservedOutput.Outcome = Aborted.
          ptestCase "DD-086 aborts sync when runtime exceeds maximum" <| fun _ -> ()

          // DD-087: If sync runtime exceeds the configured maximum sync runtime, then dropd
          // shall log an aborted sync outcome.
          // Setup: Same as DD-086.
          // Assert: Logs contain entry with Code = SyncAbortedRuntimeExceeded.
          ptestCase "DD-087 logs aborted outcome on runtime exceeded" <| fun _ -> ()

          // DD-088: If the API error rate within a sync exceeds the configured threshold,
          // then dropd shall abort the current sync.
          // Setup: ErrorRateAbortPercent = 30. Route 40% of API calls to fail.
          // Assert: ObservedOutput.Outcome = Aborted.
          ptestCase "DD-088 aborts sync when error rate exceeds threshold" <| fun _ -> ()

          // DD-089: If the API error rate within a sync exceeds the configured threshold,
          // then dropd shall log an aborted sync outcome with error-rate details.
          // Setup: Same as DD-088.
          // Assert: Logs contain entry with Code = SyncAbortedErrorRate including rate details.
          ptestCase "DD-089 logs aborted outcome with error rate details" <| fun _ -> ()

          ]
