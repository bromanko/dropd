module Dropd.Tests.ObservabilityTests

open Expecto
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

let private config =
    { validConfig with
        LabelNames = []
        Playlists = [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ] }

let private baseSetup extras =
    setupWith
        ([ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
           route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515,5765078" ] (Always(okFixture "favorited-artists.json"))
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
        "Observability"
        [
          testCase "DD-074 logs sync start and completion with track counts"
          <| fun _ ->
              let output = runSync config (baseSetup [])

              Expect.isTrue (output.Logs |> List.exists (fun log -> log.Code = "SyncStarted")) "start log expected"

              let completion = output.Logs |> List.tryFind (fun log -> log.Code = "SyncCompleted")
              Expect.isSome completion "completion log expected"
              Expect.isTrue (completion.Value.Data.ContainsKey "tracks_added") "completion log should include tracks_added"
              Expect.isTrue (completion.Value.Data.ContainsKey "tracks_removed") "completion log should include tracks_removed"

          testCase "DD-075 logs API failures with endpoint and status"
          <| fun _ ->
              let output =
                  runSync
                      config
                      (baseSetup [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 500 (fixture "error-500.json"))) ])

              Expect.isTrue
                  (output.Logs
                   |> List.exists (fun log ->
                       log.Code = "ApiFailure"
                       && log.Data.ContainsKey "endpoint"
                       && log.Data.ContainsKey "status"
                       && log.Data.["status"] = "500"
                       && log.Data.ContainsKey "message"))
                  "api failure log should include endpoint, status, and message"

          ptestCase "DD-076 retains logs for 7 days" <| fun _ -> ()

          ptestCase "DD-077 deletes logs older than retention window" <| fun _ -> ()

          ptestCase "DD-078 logs skipped sync with reason code" <| fun _ -> ()

          testCase "DD-079 logs sync outcome status"
          <| fun _ ->
              let success = runSync config (baseSetup [])

              let partial =
                  runSync
                      config
                      (baseSetup [ route "apple" "POST" "/v1/me/library/playlists/p.elecDrops/tracks" [] (Always(withStatus 500 "{\"error\":\"add failed\"}")) ])

              let aborted =
                  runSync
                      config
                      (baseSetup [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 503 (fixture "error-500.json"))) ])

              let outcomeText output =
                  output.Logs
                  |> List.tryFind (fun log -> log.Code = "SyncOutcome")
                  |> Option.bind (fun log -> log.Data.TryFind "outcome")

              Expect.equal (outcomeText success) (Some "success") "success run should log success outcome"
              Expect.equal (outcomeText partial) (Some "partial_failure") "partial run should log partial_failure"
              Expect.equal (outcomeText aborted) (Some "aborted") "aborted run should log aborted"
        ]
