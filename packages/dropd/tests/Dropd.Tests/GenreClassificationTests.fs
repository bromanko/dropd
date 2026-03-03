module Dropd.Tests.GenreClassificationTests

open Expecto
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

let private baseConfig playlists =
    { validConfig with
        LabelNames = []
        Playlists = playlists }

let private baseSetup extraRoutes =
    setupWith
        ([ route "apple" "GET" "/v1/me/library/artists" [] (Always(withStatus 200 seedLibraryBody))
           route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515" ] (Always(withStatus 200 seedRatingBody))
           route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
           route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 """{ "data": [] }"""))
           route "apple" "POST" "/v1/me/library/playlists" [] (Always(okFixture "playlist-create-success.json"))
           route "apple" "POST" "/v1/me/library/playlists/p.playlistCreated/tracks" [] (Always(withStatus 200 "{}")) ]
         @ extraRoutes)

[<Tests>]
let tests =
    testList
        "Genre Classification"
        [
          testCase "DD-031 reads genre metadata from catalog entries"
          <| fun _ ->
              let releaseWithoutGenres =
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
        "genreNames": []
      },
      "relationships": { "tracks": { "data": [ { "id": "track-9001-a" } ] } }
    }
  ]
}
"""

              let output =
                  runSync
                      (baseConfig [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ])
                      (baseSetup
                          [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 200 releaseWithoutGenres))
                            route "apple" "GET" "/v1/catalog/us/albums/9001" [] (Always(okFixture "album-9001-with-genres.json")) ])

              let addRequest =
                  output.Requests
                  |> List.find (fun req -> req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks" && req.Method = "POST")

              Expect.stringContains addRequest.Body.Value "track-9001-a" "track should be assigned after reading genre metadata"

          testCase "DD-032 matches genres with normalized exact matching"
          <| fun _ ->
              let spacedGenreBody =
                  """
{
  "data": [
    {
      "id": "9100",
      "attributes": {
        "name": "Spacey",
        "artistName": "Radiohead",
        "artistId": "657515",
        "releaseDate": "2026-02-25",
        "genreNames": [" Electronic "]
      },
      "relationships": { "tracks": { "data": [ { "id": "space-track" } ] } }
    }
  ]
}
"""

              let output =
                  runSync
                      (baseConfig [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ])
                      (baseSetup [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 200 spacedGenreBody)) ])

              let addRequest =
                  output.Requests
                  |> List.find (fun req -> req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks" && req.Method = "POST")

              Expect.stringContains addRequest.Body.Value "space-track" "normalized genre should match playlist criteria"

          testCase "DD-033 adds tracks to multiple matching playlists"
          <| fun _ ->
              let output =
                  runSync
                      (baseConfig [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                                    { Name = "Dance Drops"; GenreCriteria = [ "dance"; "electronic" ] } ])
                      (baseSetup [])

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks" && req.Method = "POST"))
                  "electronic playlist should receive tracks"

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks" && req.Method = "POST"))
                  "second matching playlist should also receive tracks"

          testCase "DD-034 logs warning for releases without genre metadata"
          <| fun _ ->
              let noGenreRecent =
                  """
{
  "data": [
    {
      "id": "9200",
      "attributes": {
        "name": "No Genre",
        "artistName": "Radiohead",
        "artistId": "657515",
        "releaseDate": "2026-02-25",
        "genreNames": []
      },
      "relationships": { "tracks": { "data": [ { "id": "nog-track" } ] } }
    }
  ]
}
"""

              let output =
                  runSync
                      (baseConfig [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ])
                      (baseSetup
                          [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 200 noGenreRecent))
                            route "apple" "GET" "/v1/catalog/us/albums/9200" [] (Always(okFixture "album-9002-no-genres.json")) ])

              Expect.isTrue (output.Logs |> List.exists (fun log -> log.Code = "MissingGenres")) "missing-genre warning should be logged"

          testCase "DD-035 excludes releases without genre metadata from playlists"
          <| fun _ ->
              let noGenreRecent =
                  """
{
  "data": [
    {
      "id": "9200",
      "attributes": {
        "name": "No Genre",
        "artistName": "Radiohead",
        "artistId": "657515",
        "releaseDate": "2026-02-25",
        "genreNames": []
      },
      "relationships": { "tracks": { "data": [ { "id": "nog-track" } ] } }
    }
  ]
}
"""

              let output =
                  runSync
                      (baseConfig [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ])
                      (baseSetup
                          [ route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(withStatus 200 noGenreRecent))
                            route "apple" "GET" "/v1/catalog/us/albums/9200" [] (Always(okFixture "album-9002-no-genres.json")) ])

              let maybeAdd =
                  output.Requests
                  |> List.tryFind (fun req -> req.Method = "POST" && req.Path = "/v1/me/library/playlists/p.playlistCreated/tracks")

              match maybeAdd with
              | None -> ()
              | Some addReq -> Expect.isFalse (addReq.Body.Value.Contains("nog-track")) "no-genre release should not be assigned"
        ]
