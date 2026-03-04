module Dropd.Tests.PlaylistManagementTests

open Expecto
open Dropd.Core.Types
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

let private seedLibraryBody =
    """
{
  "data": [
    {
      "id": "r.abc123",
      "type": "library-artists",
      "attributes": { "name": "Radiohead" },
      "relationships": {
        "catalog": {
          "data": [
            { "id": "657515", "type": "artists", "attributes": { "name": "Radiohead" } }
          ]
        }
      }
    }
  ]
}
"""

let private seedRatingBody =
    """
{
  "data": [
    { "id": "657515", "type": "ratings", "attributes": { "value": 1 } }
  ]
}
"""

let private emptyPlaylistList =
    """{ "data": [] }"""

let private playlistListWithElectronic =
    """
{
  "data": [
    { "id": "p.existing", "type": "library-playlists", "attributes": { "name": "Electronic Drops" } }
  ]
}
"""

let private playlistListWithBoth =
    """
{
  "data": [
    { "id": "p.elec", "type": "library-playlists", "attributes": { "name": "Electronic Drops" } },
    { "id": "p.dance", "type": "library-playlists", "attributes": { "name": "Dance Drops" } }
  ]
}
"""

let private onePlaylist =
    { validConfig with
        LabelNames = []
        Playlists = [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ] }

let private twoPlaylists =
    { validConfig with
        LabelNames = []
        Playlists =
            [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
              { Name = "Dance Drops"; GenreCriteria = [ "electronic" ] } ] }

// Base routes for "first run" — playlists don't exist yet.
let private baseRoutes =
    [ route "apple" "GET" "/v1/me/library/artists" [] (Always(withStatus 200 seedLibraryBody))
      route "apple" "GET" "/v1/me/ratings/artists" [] (Always(withStatus 200 seedRatingBody))
      route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
      route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 emptyPlaylistList))
      route "apple" "POST" "/v1/me/library/playlists" [] (Always(okFixture "playlist-create-success.json"))
      route "apple" "POST" "/v1/me/library/playlists/p.playlistCreated/tracks" [] (Always(withStatus 200 "{}"))
      route "apple" "DELETE" "/v1/me/library/playlists/p.playlistCreated/tracks" [] (Always(withStatus 200 "{}")) ]

let private setupWithExtras extras = setupWith (baseRoutes @ extras)

[<Tests>]
let tests =
    testList
        "Playlist Management"
        [
          testCase "DD-045 creates new playlist on first run"
          <| fun _ ->
              let output = runSync onePlaylist (setupWithExtras [])

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists"))
                  "playlist should be created when missing"

          testCase "DD-046 adds matched tracks to playlist"
          <| fun _ ->
              let output = runSync onePlaylist (setupWithExtras [])

              Expect.isTrue
                  (output.Requests
                   |> List.exists (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks"))
                  "track-add request should be emitted"

          testCase "DD-047 skips tracks already in playlist"
          <| fun _ ->
              let existingBody =
                  """
{
  "data": [
    { "id": "track-9001-a", "attributes": { "releaseDate": "2026-02-20" } }
  ]
}
"""

              let output =
                  runSync
                      onePlaylist
                      (setupWithExtras
                          [ route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 playlistListWithElectronic))
                            route "apple" "GET" "/v1/me/library/playlists/p.existing/tracks" [] (Always(withStatus 200 existingBody))
                            route "apple" "POST" "/v1/me/library/playlists/p.existing/tracks" [] (Always(withStatus 200 "{}")) ])

              let addRequest =
                  output.Requests
                  |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.existing/tracks")

              Expect.isFalse (addRequest.Body.Value.Contains("track-9001-a")) "existing track should not be re-added"
              Expect.stringContains addRequest.Body.Value "track-9001-b" "new track should still be added"

          testCase "DD-047b skips tracks when existing playlist uses library IDs with catalogId in playParams"
          <| fun _ ->
              // Real Apple Music playlists return library-scoped IDs (i.xxx) at
              // the top level, with the catalog ID nested in
              // attributes.playParams.catalogId.  The engine must use catalogId
              // for dedup, not the library ID.
              let existingBody =
                  """
{
  "data": [
    {
      "id": "i.libraryId001",
      "type": "library-songs",
      "attributes": {
        "releaseDate": "2026-02-20",
        "playParams": { "catalogId": "track-9001-a", "id": "i.libraryId001", "isLibrary": true, "kind": "song" }
      }
    }
  ]
}
"""

              let output =
                  runSync
                      onePlaylist
                      (setupWithExtras
                          [ route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 playlistListWithElectronic))
                            route "apple" "GET" "/v1/me/library/playlists/p.existing/tracks" [] (Always(withStatus 200 existingBody))
                            route "apple" "POST" "/v1/me/library/playlists/p.existing/tracks" [] (Always(withStatus 200 "{}")) ])

              let addRequest =
                  output.Requests
                  |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.existing/tracks")

              Expect.isFalse (addRequest.Body.Value.Contains("track-9001-a")) "catalogId track should not be re-added"
              Expect.stringContains addRequest.Body.Value "track-9001-b" "new track should still be added"

          testCase "DD-048 identifies tracks outside rolling window for removal"
          <| fun _ ->
              // Track removal is not yet supported by the Apple Music REST API,
              // so the engine logs stale tracks at Info level instead of issuing
              // a DELETE request.  Verify the plan identifies the stale track and
              // that the skip log is emitted.
              let existingBody = fixture "playlist-tracks-existing.json"

              let output =
                  runSync
                      onePlaylist
                      (setupWithExtras
                          [ route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 playlistListWithElectronic))
                            route "apple" "GET" "/v1/me/library/playlists/p.existing/tracks" [] (Always(withStatus 200 existingBody))
                            route "apple" "POST" "/v1/me/library/playlists/p.existing/tracks" [] (Always(withStatus 200 "{}")) ])

              // No DELETE request should be issued.
              Expect.isFalse
                  (output.Requests |> List.exists (fun req -> req.Method = "DELETE"))
                  "no DELETE request should be issued"

              // The skip log should mention the stale track.
              Expect.isTrue
                  (output.Logs
                   |> List.exists (fun log ->
                       log.Code = "PlaylistTrackRemoveSkipped"
                       && log.Data.ContainsKey "trackIds"
                       && log.Data.["trackIds"].Contains("track-existing-old")))
                  "stale track should be logged as skipped"

          testCase "DD-049 computes desired state before mutations"
          <| fun _ ->
              let firstAlbums =
                  """
{
  "data": [
    {
      "id": "9400",
      "attributes": {
        "name": "First",
        "artistName": "Radiohead",
        "artistId": "657515",
        "releaseDate": "2026-02-20",
        "genreNames": ["Electronic"]
      },
      "relationships": { "tracks": { "data": [ { "id": "first-track" } ] } }
    }
  ]
}
"""

              let secondAlbums =
                  """
{
  "data": [
    {
      "id": "9500",
      "attributes": {
        "name": "Second",
        "artistName": "Radiohead",
        "artistId": "657515",
        "releaseDate": "2026-02-21",
        "genreNames": ["Electronic"]
      },
      "relationships": { "tracks": { "data": [ { "id": "second-track" } ] } }
    }
  ]
}
"""

              let first = runSync onePlaylist (setupWithExtras [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 200 firstAlbums)) ])
              let second = runSync onePlaylist (setupWithExtras [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 200 secondAlbums)) ])

              let firstAdd = first.Requests |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks")
              let secondAdd = second.Requests |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks")

              Expect.stringContains firstAdd.Body.Value "first-track" "first run should add first-track"
              Expect.stringContains secondAdd.Body.Value "second-track" "second run should recalculate and add second-track"

          testCase "DD-050 reconciles after partial-failure sync"
          <| fun _ ->
              let failingSetup =
                  setupWithExtras
                      [ route "apple" "POST" "/v1/me/library/playlists/p.playlistCreated/tracks" [] (Always(withStatus 500 "{\"error\":\"add failed\"}")) ]

              let succeedingSetup = setupWithExtras []

              let first = runSync onePlaylist failingSetup
              let second = runSync onePlaylist succeedingSetup

              Expect.equal first.Outcome (Some PartialFailure) "first sync should be partial failure"

              Expect.isTrue
                  (second.Requests
                   |> List.exists (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks"))
                  "second sync should still reconcile playlist mutations"

          testCase "DD-051 logs error on playlist creation failure"
          <| fun _ ->
              let output =
                  runSync
                      onePlaylist
                      (setupWithExtras [ route "apple" "POST" "/v1/me/library/playlists" [] (Always(withStatus 500 "{\"error\":\"create failed\"}")) ])

              Expect.isTrue
                  (output.Logs |> List.exists (fun log -> log.Code = "PlaylistCreateFailure" && log.Data.["playlist"] = "Electronic Drops"))
                  "create failure should be logged with playlist name"

          testCase "DD-052 continues after playlist creation failure"
          <| fun _ ->
              // MaxRetries = 3 means 4 total attempts per request (1 original + 3 retries).
              // Provide 4 × 500 for the first playlist creation to exhaust retries, then
              // a 200 for the second playlist creation.
              let output =
                  runSync
                      twoPlaylists
                      (setupWithExtras [ route "apple" "POST" "/v1/me/library/playlists" [] (Sequence [
                          withStatus 500 "{\"error\":\"create failed\"}"
                          withStatus 500 "{\"error\":\"create failed\"}"
                          withStatus 500 "{\"error\":\"create failed\"}"
                          withStatus 500 "{\"error\":\"create failed\"}"
                          okFixture "playlist-create-success.json" ]) ])

              let createRequests =
                  output.Requests |> List.filter (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists")

              // 4 attempts for first playlist (all fail) + 1 for second playlist (succeeds)
              Expect.equal createRequests.Length 5 "both playlists should attempt creation (with retries)"

          testCase "DD-053 logs error on track addition failure"
          <| fun _ ->
              let output =
                  runSync
                      onePlaylist
                      (setupWithExtras [ route "apple" "POST" "/v1/me/library/playlists/p.playlistCreated/tracks" [] (Always(withStatus 500 "{\"error\":\"add failed\"}")) ])

              Expect.isTrue
                  (output.Logs
                   |> List.exists (fun log ->
                       log.Code = "PlaylistTrackAddFailure"
                       && log.Data.ContainsKey "playlist"
                       && log.Data.["playlist"] = "Electronic Drops"
                       && log.Data.ContainsKey "trackIds"))
                  "track-add failure should include playlist and track ids"

          testCase "DD-054 continues after track addition failure"
          <| fun _ ->
              let output =
                  runSync
                      twoPlaylists
                      (setupWithExtras
                          [ route "apple" "POST" "/v1/me/library/playlists/p.playlistCreated/tracks" [] (Always(withStatus 500 "{\"error\":\"add failed\"}")) ])

              // Second playlist also gets created with the same fixture ID, so both
              // use p.playlistCreated. The test verifies that the second playlist's
              // creation request is still attempted after the first playlist's track-add fails.
              let createRequests =
                  output.Requests |> List.filter (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists")

              Expect.equal createRequests.Length 2 "both playlists should be created despite first track-add failure"
        ]
