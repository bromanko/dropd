module Dropd.Tests.LabelDiscoveryTests

open Expecto
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

let private noPlaylistConfig labels =
    { validConfig with
        LabelNames = labels
        Playlists = [] }

[<Tests>]
let tests =
    testList
        "Label Discovery"
        [
          testCase "DD-004 stores configured label names"
          <| fun _ ->
              let config = noPlaylistConfig [ "Ninja Tune"; "Warp Records" ]
              Expect.equal config.LabelNames.Length 2 "two labels should be stored"

          testCase "DD-005 resolves label names to catalog IDs"
          <| fun _ ->
              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [] (Always(okFixture "favorited-artists.json"))
                        route "apple" "GET" "/v1/catalog/us/search" [ "term", "Ninja Tune"; "types", "record-labels" ] (Always(okFixture "label-search-ninja-tune.json"))
                        route "apple" "GET" "/v1/catalog/us/record-labels/1543411840/latest-releases" [] (Always(okFixture "label-latest-releases-1543411840.json")) ]

              let output = runSync (noPlaylistConfig [ "Ninja Tune" ]) setup

              Expect.isTrue
                  (output.Requests
                   |> List.exists (fun req -> req.Path = "/v1/catalog/us/search" && req.Query |> List.contains ("term", "Ninja Tune")))
                  "search request should include configured label term"

          testCase "DD-006 retrieves latest releases for resolved labels"
          <| fun _ ->
              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [] (Always(okFixture "favorited-artists.json"))
                        route "apple" "GET" "/v1/catalog/us/search" [ "term", "Ninja Tune"; "types", "record-labels" ] (Always(okFixture "label-search-ninja-tune.json"))
                        route "apple" "GET" "/v1/catalog/us/record-labels/1543411840/latest-releases" [] (Always(okFixture "label-latest-releases-1543411840.json")) ]

              let output = runSync (noPlaylistConfig [ "Ninja Tune" ]) setup

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Path = "/v1/catalog/us/record-labels/1543411840/latest-releases"))
                  "latest releases endpoint should be called"

          testCase "DD-007 logs warning for unresolved label"
          <| fun _ ->
              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [] (Always(okFixture "favorited-artists.json"))
                        route "apple" "GET" "/v1/catalog/us/search" [ "term", "FakeLabel"; "types", "record-labels" ] (Always(okFixture "label-search-empty.json")) ]

              let output = runSync (noPlaylistConfig [ "FakeLabel" ]) setup

              Expect.isTrue
                  (output.Logs
                   |> List.exists (fun log -> log.Code = UnknownLabel && log.Data.ContainsKey "label" && log.Data.["label"] = "FakeLabel"))
                  "unresolved label warning should include label name"

          testCase "DD-008 continues processing after unresolved label"
          <| fun _ ->
              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [] (Always(okFixture "favorited-artists.json"))
                        route "apple" "GET" "/v1/catalog/us/search" [ "term", "FakeLabel"; "types", "record-labels" ] (Always(okFixture "label-search-empty.json"))
                        route "apple" "GET" "/v1/catalog/us/search" [ "term", "Ninja Tune"; "types", "record-labels" ] (Always(okFixture "label-search-ninja-tune.json"))
                        route "apple" "GET" "/v1/catalog/us/record-labels/1543411840/latest-releases" [] (Always(okFixture "label-latest-releases-1543411840.json")) ]

              let output = runSync (noPlaylistConfig [ "FakeLabel"; "Ninja Tune" ]) setup

              Expect.isTrue
                  (output.Requests |> List.exists (fun req -> req.Path = "/v1/catalog/us/record-labels/1543411840/latest-releases"))
                  "sync should continue to resolved labels after unresolved one"
        ]
