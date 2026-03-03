module Dropd.Tests.PlaylistManagementTests

open Expecto
open Dropd.Core.Types
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

let private seedBody =
    """
{
  "data": [
    { "id": "657515", "attributes": { "name": "Radiohead" } }
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

let private baseRoutes =
    [ route "apple" "GET" "/v1/me/library/artists" [] (Always(withStatus 200 seedBody))
      route "apple" "GET" "/v1/me/ratings/artists" [] (Always(withStatus 200 seedBody))
      route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
      route "apple" "GET" "/v1/me/library/playlists/Electronic%20Drops/tracks" [] (Always(withStatus 404 "{}"))
      route "apple" "GET" "/v1/me/library/playlists/Dance%20Drops/tracks" [] (Always(withStatus 404 "{}"))
      route "apple" "POST" "/v1/me/library/playlists" [] (Always(okFixture "playlist-create-success.json"))
      route "apple" "POST" "/v1/me/library/playlists/Electronic%20Drops/tracks" [] (Always(withStatus 200 "{}"))
      route "apple" "POST" "/v1/me/library/playlists/Dance%20Drops/tracks" [] (Always(withStatus 200 "{}"))
      route "apple" "DELETE" "/v1/me/library/playlists/Electronic%20Drops/tracks" [] (Always(withStatus 200 "{}")) ]

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
                   |> List.exists (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/Electronic%20Drops/tracks"))
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
                      (setupWithExtras [ route "apple" "GET" "/v1/me/library/playlists/Electronic%20Drops/tracks" [] (Always(withStatus 200 existingBody)) ])

              let addRequest =
                  output.Requests
                  |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/Electronic%20Drops/tracks")

              Expect.isFalse (addRequest.Body.Value.Contains("track-9001-a")) "existing track should not be re-added"
              Expect.stringContains addRequest.Body.Value "track-9001-b" "new track should still be added"

          testCase "DD-048 removes tracks outside rolling window"
          <| fun _ ->
              let existingBody = fixture "playlist-tracks-existing.json"

              let output =
                  runSync
                      onePlaylist
                      (setupWithExtras [ route "apple" "GET" "/v1/me/library/playlists/Electronic%20Drops/tracks" [] (Always(withStatus 200 existingBody)) ])

              let removeRequest =
                  output.Requests
                  |> List.find (fun req -> req.Method = "DELETE" && req.Path = "/v1/me/library/playlists/Electronic%20Drops/tracks")

              Expect.stringContains (removeRequest.Query |> List.map snd |> String.concat ",") "track-existing-old" "stale track should be removed"

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

              let firstAdd = first.Requests |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/Electronic%20Drops/tracks")
              let secondAdd = second.Requests |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/Electronic%20Drops/tracks")

              Expect.stringContains firstAdd.Body.Value "first-track" "first run should add first-track"
              Expect.stringContains secondAdd.Body.Value "second-track" "second run should recalculate and add second-track"

          testCase "DD-050 reconciles after partial-failure sync"
          <| fun _ ->
              let failingSetup =
                  setupWithExtras
                      [ route "apple" "POST" "/v1/me/library/playlists/Electronic%20Drops/tracks" [] (Always(withStatus 500 "{\"error\":\"add failed\"}")) ]

              let succeedingSetup = setupWithExtras []

              let first = runSync onePlaylist failingSetup
              let second = runSync onePlaylist succeedingSetup

              Expect.equal first.Outcome (Some PartialFailure) "first sync should be partial failure"

              Expect.isTrue
                  (second.Requests
                   |> List.exists (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/Electronic%20Drops/tracks"))
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
              let output =
                  runSync
                      twoPlaylists
                      (setupWithExtras [ route "apple" "POST" "/v1/me/library/playlists" [] (Sequence [ withStatus 500 "{\"error\":\"create failed\"}"; okFixture "playlist-create-success.json" ]) ])

              let createRequests =
                  output.Requests |> List.filter (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists")

              Expect.equal createRequests.Length 2 "both playlists should attempt creation"

          testCase "DD-053 logs error on track addition failure"
          <| fun _ ->
              let output =
                  runSync
                      onePlaylist
                      (setupWithExtras [ route "apple" "POST" "/v1/me/library/playlists/Electronic%20Drops/tracks" [] (Always(withStatus 500 "{\"error\":\"add failed\"}")) ])

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
                          [ route "apple" "POST" "/v1/me/library/playlists/Electronic%20Drops/tracks" [] (Always(withStatus 500 "{\"error\":\"add failed\"}"))
                            route "apple" "POST" "/v1/me/library/playlists/Dance%20Drops/tracks" [] (Always(withStatus 200 "{}")) ])

              Expect.isTrue
                  (output.Requests
                   |> List.exists (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/Dance%20Drops/tracks"))
                  "other playlists should continue after one add failure"
        ]
