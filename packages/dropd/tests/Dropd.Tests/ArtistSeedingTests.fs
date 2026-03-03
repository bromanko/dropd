module Dropd.Tests.ArtistSeedingTests

open Expecto
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

let private baseConfig =
    { validConfig with
        LabelNames = []
        Playlists = [] }

[<Tests>]
let tests =
    testList
        "Artist Seeding"
        [
          testCase "DD-001 retrieves library artists during sync"
          <| fun _ ->
              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [] (Always(okFixture "favorited-artists.json")) ]

              let output = runSync baseConfig setup

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Method = "GET" && req.Path = "/v1/me/library/artists"))
                  "sync should request library artists"

          testCase "DD-002 retrieves favorited artists during sync"
          <| fun _ ->
              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [] (Always(okFixture "favorited-artists.json")) ]

              let output = runSync baseConfig setup

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Method = "GET" && req.Path = "/v1/me/ratings/artists"))
                  "sync should request favorited artists"

          testCase "DD-003 deduplicates library and favorited artists"
          <| fun _ ->
              let dedupFavorites =
                  """
{
  "data": [
    { "id": "657515", "attributes": { "name": "Radiohead" } }
  ]
}
"""

              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [] (Always(withStatus 200 dedupFavorites))
                        route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
                        route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-5765078.json")) ]

              let output = runSync baseConfig setup

              let radioheadFetches =
                  output.Requests
                  |> List.filter (fun req -> req.Method = "GET" && req.Path = "/v1/catalog/us/artists/657515/albums")

              Expect.equal radioheadFetches.Length 1 "deduplicated seed artist should be queried once"
        ]
