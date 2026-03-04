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

          testCase "DD-076 retains logs for 7 days" <| fun _ ->
              let tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dropd-test-{System.Guid.NewGuid()}")
              System.IO.Directory.CreateDirectory(tempDir) |> ignore
              try
                  let now = System.DateTimeOffset(2026, 3, 1, 0, 0, 0, System.TimeSpan.Zero)
                  let recentFile = System.IO.Path.Combine(tempDir, "recent.log")
                  let oldFile = System.IO.Path.Combine(tempDir, "old.log")
                  System.IO.File.WriteAllText(recentFile, "recent log")
                  System.IO.File.WriteAllText(oldFile, "old log")
                  System.IO.File.SetLastWriteTimeUtc(recentFile, now.AddDays(-3.0).UtcDateTime)
                  System.IO.File.SetLastWriteTimeUtc(oldFile, now.AddDays(-10.0).UtcDateTime)

                  let deleted = Dropd.Core.LogRetention.prune tempDir 7 now

                  Expect.equal deleted 1 "should delete 1 old file"
                  Expect.isTrue (System.IO.File.Exists recentFile) "recent file should remain"
              finally
                  if System.IO.Directory.Exists tempDir then
                      System.IO.Directory.Delete(tempDir, true)

          testCase "DD-077 deletes logs older than retention window" <| fun _ ->
              let tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dropd-test-{System.Guid.NewGuid()}")
              System.IO.Directory.CreateDirectory(tempDir) |> ignore
              try
                  let now = System.DateTimeOffset(2026, 3, 1, 0, 0, 0, System.TimeSpan.Zero)
                  let file2d = System.IO.Path.Combine(tempDir, "file-2d.log")
                  let file8d = System.IO.Path.Combine(tempDir, "file-8d.log")
                  let file14d = System.IO.Path.Combine(tempDir, "file-14d.log")
                  System.IO.File.WriteAllText(file2d, "2 day old")
                  System.IO.File.WriteAllText(file8d, "8 day old")
                  System.IO.File.WriteAllText(file14d, "14 day old")
                  System.IO.File.SetLastWriteTimeUtc(file2d, now.AddDays(-2.0).UtcDateTime)
                  System.IO.File.SetLastWriteTimeUtc(file8d, now.AddDays(-8.0).UtcDateTime)
                  System.IO.File.SetLastWriteTimeUtc(file14d, now.AddDays(-14.0).UtcDateTime)

                  let deleted = Dropd.Core.LogRetention.prune tempDir 7 now

                  Expect.equal deleted 2 "should delete 2 files"
                  Expect.isTrue (System.IO.File.Exists file2d) "2-day-old file should remain"
                  Expect.isFalse (System.IO.File.Exists file8d) "8-day-old file should be deleted"
                  Expect.isFalse (System.IO.File.Exists file14d) "14-day-old file should be deleted"
              finally
                  if System.IO.Directory.Exists tempDir then
                      System.IO.Directory.Delete(tempDir, true)

          testCase "DD-078 logs skipped sync with reason code" <| fun _ ->
              // Test SkipSync AlreadyRunning
              let syncTime = System.TimeOnly(4, 0)
              let now = System.DateTimeOffset(2026, 3, 1, 4, 0, 0, System.TimeSpan.Zero)
              let decision1 = Dropd.Core.Scheduling.decide now syncTime true None
              Expect.equal decision1 (Dropd.Core.Scheduling.SkipSync Dropd.Core.Types.AlreadyRunning) "should be SkipSync AlreadyRunning"
              let log1 = Dropd.Core.Scheduling.logDecision decision1
              Expect.isSome log1 "should produce a log entry"
              Expect.equal log1.Value.Code "SyncSkippedOverlap" "code should be SyncSkippedOverlap"

              // Test MissedWhileUnavailable via decideOnStartup
              let nowLate = System.DateTimeOffset(2026, 3, 1, 10, 0, 0, System.TimeSpan.Zero)
              let decision2 = Dropd.Core.Scheduling.decideOnStartup nowLate syncTime None
              Expect.equal decision2 (Dropd.Core.Scheduling.SkipSync Dropd.Core.Types.MissedWhileUnavailable) "should be SkipSync MissedWhileUnavailable"
              let log2 = Dropd.Core.Scheduling.logDecision decision2
              Expect.isSome log2 "should produce a log entry"
              Expect.equal log2.Value.Code "SyncSkippedMissed" "code should be SyncSkippedMissed"

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
