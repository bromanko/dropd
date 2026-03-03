module Dropd.Tests.NewReleaseTests

open Expecto
open Dropd.Core.Types
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

let private singleLibraryArtistBody =
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

let private singleRatingBody =
    """
{
  "data": [
    { "id": "657515", "type": "ratings", "attributes": { "value": 1 } }
  ]
}
"""

let private playlistConfig =
    { validConfig with
        LabelNames = [ "Ninja Tune" ]
        Playlists = [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ] }

let private happySetup extraRoutes =
    setupWith
        ([ route "apple" "GET" "/v1/me/library/artists" [] (Always(withStatus 200 singleLibraryArtistBody))
           route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515" ] (Always(withStatus 200 singleRatingBody))
           route "apple" "GET" "/v1/catalog/us/search" [ "term", "Ninja Tune"; "types", "record-labels" ] (Always(okFixture "label-search-ninja-tune.json"))
           route "apple" "GET" "/v1/catalog/us/record-labels/1543411840" [] (Always(okFixture "label-latest-releases-1543411840.json"))
           route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
           route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-5765078.json"))
           route "apple" "GET" "/v1/catalog/us/albums/9002" [] (Always(okFixture "album-9002-no-genres.json"))
           route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 """{ "data": [] }"""))
           route "apple" "POST" "/v1/me/library/playlists" [] (Always(okFixture "playlist-create-success.json"))
           route "apple" "POST" "/v1/me/library/playlists/p.playlistCreated/tracks" [] (Always(withStatus 200 "{}")) ]
         @ extraRoutes)

[<Tests>]
let tests =
    testList
        "New Release Detection"
        [
          testCase "DD-023 queries new releases from all artist sources"
          <| fun _ ->
              let output = runSync playlistConfig (happySetup [])

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Path = "/v1/catalog/us/artists/657515/albums"))
                  "seed artist release query should run"

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Path = "/v1/catalog/us/record-labels/1543411840"))
                  "label release query should run"

          testCase "DD-024 retrieves releases sorted by release date descending"
          <| fun _ ->
              let output = runSync playlistConfig (happySetup [])

              Expect.isTrue
                  (output.Requests
                   |> List.exists (fun req -> req.Path = "/v1/catalog/us/artists/657515/albums" && req.Query |> List.contains ("sort", "-releaseDate")))
                  "artist release query should include sort=-releaseDate"

          testCase "DD-025 includes only releases within lookback period"
          <| fun _ ->
              let output = runSync playlistConfig (happySetup [])

              let addRequest =
                  output.Requests
                  |> List.tryFind (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks")

              Expect.isSome addRequest "playlist add request should be present"
              Expect.stringContains addRequest.Value.Body.Value "track-9001-a" "in-window release tracks should be included"
              Expect.isFalse (addRequest.Value.Body.Value.Contains("track-9002-a")) "out-of-window release tracks should be excluded"

          testCase "DD-026 includes collaboration releases"
          <| fun _ ->
              let collabBody =
                  """
{
  "data": [
    {
      "id": "9101",
      "attributes": {
        "name": "Collab",
        "artistName": "Guest Artist",
        "artistId": "657515",
        "releaseDate": "2026-02-25",
        "genreNames": ["Electronic"]
      },
      "relationships": { "tracks": { "data": [ { "id": "collab-track" } ] } }
    }
  ]
}
"""

              let setup =
                  happySetup
                      [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 200 collabBody)) ]

              let output = runSync playlistConfig setup

              let addRequest =
                  output.Requests
                  |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks")

              Expect.stringContains addRequest.Body.Value "collab-track" "collaboration track should be included"

          testCase "DD-027 deduplicates releases by catalog ID"
          <| fun _ ->
              let output = runSync playlistConfig (happySetup [])

              let addRequest =
                  output.Requests
                  |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks")

              Expect.equal (addRequest.Body.Value.Split("track-9001-a").Length - 1) 1 "duplicate release should contribute tracks once"

          testCase "DD-028 deduplicates tracks by catalog track ID"
          <| fun _ ->
              let noLabelConfig =
                  { playlistConfig with
                      LabelNames = [] }

              let duplicateTrackBody =
                  """
{
  "data": [
    {
      "id": "9001",
      "attributes": {
        "name": "Future Echoes",
        "artistName": "Radiohead",
        "artistId": "657515",
        "releaseDate": "2026-02-20",
        "genreNames": ["Electronic"]
      },
      "relationships": {
        "tracks": {
          "data": [
            { "id": "dup-track" },
            { "id": "dup-track" }
          ]
        }
      }
    }
  ]
}
"""

              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(withStatus 200 singleLibraryArtistBody))
                        route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515" ] (Always(withStatus 200 singleRatingBody))
                        route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 200 duplicateTrackBody))
                        route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 """{ "data": [] }"""))
                        route "apple" "POST" "/v1/me/library/playlists" [] (Always(okFixture "playlist-create-success.json"))
                        route "apple" "POST" "/v1/me/library/playlists/p.playlistCreated/tracks" [] (Always(withStatus 200 "{}")) ]

              let output = runSync noLabelConfig setup

              let addRequest =
                  output.Requests
                  |> List.find (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks")

              Expect.equal (addRequest.Body.Value.Split("dup-track").Length - 1) 1 "duplicate track id should appear once"

          testCase "DD-029 logs error when Apple Music catalog unavailable"
          <| fun _ ->
              let setup =
                  happySetup
                      [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "error-500.json" |> fun r -> { r with StatusCode = 503 })) ]

              let output = runSync playlistConfig setup

              Expect.isTrue
                  (output.Logs
                   |> List.exists (fun log -> log.Code = "ApiFailure" && log.Data.ContainsKey "status" && log.Data.["status"] = "503"))
                  "catalog failure should be logged"

          testCase "DD-030 aborts sync when Apple Music catalog unavailable"
          <| fun _ ->
              let setup =
                  happySetup
                      [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "error-500.json" |> fun r -> { r with StatusCode = 503 })) ]

              let output = runSync playlistConfig setup
              Expect.equal output.Outcome (Some(Aborted "CatalogUnavailable")) "sync should abort on catalog outage"
        ]
