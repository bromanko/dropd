module Dropd.Tests.SimilarArtistTests

open Expecto
open Dropd.Core.Types
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

module AC = Dropd.Core.ApiContracts

/// Config with a single seed artist (Bonobo via library+favorites), no labels.
let private similarConfig =
    { validConfig with
        LabelNames = []
        Playlists = [] }

/// Minimal Apple Music routes that produce seed artist "Bonobo" (id=5765078).
/// Library artists returns Bonobo only; favorites confirms it.
let private baseSeedRoutes =
    [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
      route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515,5765078" ] (Always(okFixture "favorited-artists.json"))
      // Seed artist album routes
      route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
      route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-5765078.json")) ]

/// Route for Last.fm similar artist lookup for Bonobo.
let private lastFmBonoboRoute =
    route "lastfm" "GET" "/2.0" [ "method", "artist.getSimilar"; "artist", "Bonobo" ] (Always(okLastFmFixture "similar-bonobo.json"))

/// Route for Last.fm similar artist lookup for Radiohead.
let private lastFmRadioheadRoute =
    route "lastfm" "GET" "/2.0" [ "method", "artist.getSimilar"; "artist", "Radiohead" ] (Always(okLastFmFixture "similar-radiohead.json"))

/// Apple Music search routes for resolving similar artists.
let private artistSearchRoutes =
    [ route "apple" "GET" "/v1/catalog/us/search" [ "term", "Burial"; "types", "artists" ] (Always(okFixture "artist-search-burial.json"))
      route "apple" "GET" "/v1/catalog/us/search" [ "term", "The  Black  Keys"; "types", "artists" ] (Always(okFixture "artist-search-black-keys.json"))
      route "apple" "GET" "/v1/catalog/us/search" [ "term", " Four   Tet "; "types", "artists" ] (Always(okFixture "artist-search-four-tet.json"))
      route "apple" "GET" "/v1/catalog/us/search" [ "term", "NoMatch"; "types", "artists" ] (Always(okFixture "artist-search-empty.json")) ]

/// Album routes for resolved similar artists (Burial, Black Keys, Four Tet).
let private similarArtistAlbumRoutes =
    [ route "apple" "GET" "/v1/catalog/us/artists/14294754/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
      route "apple" "GET" "/v1/catalog/us/artists/136975/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
      route "apple" "GET" "/v1/catalog/us/artists/390999/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json")) ]

[<Tests>]
let tests =
    testList
        "Similar Artist Discovery"
        [

          // DD-009: When dropd has a seed artist list, dropd shall query a similar-artist
          // data source for artists similar to each seed artist.
          testCase "DD-009 queries similar artists for each seed artist" <| fun _ ->
              let setup =
                  setupWith (baseSeedRoutes @ [ lastFmBonoboRoute; lastFmRadioheadRoute ] @ artistSearchRoutes @ similarArtistAlbumRoutes)

              let output = runSync similarConfig setup

              let lastFmRequests =
                  output.Requests
                  |> List.filter (fun req -> req.Service = "lastfm" && req.Path = "/2.0")

              Expect.isNonEmpty lastFmRequests "should have made Last.fm requests"

              // Verify request for Bonobo (one of the seed artists)
              let bonoboReq =
                  lastFmRequests
                  |> List.tryFind (fun req -> req.Query |> List.contains ("artist", "Bonobo"))

              Expect.isSome bonoboReq "should query similar artists for Bonobo"

              let req = bonoboReq.Value
              Expect.isTrue (req.Query |> List.contains ("method", "artist.getSimilar")) "method param"

          // DD-010: When the similar-artist data source returns similar artists, dropd shall
          // filter out artists that are already in the seed artist list.
          testCase "DD-010 filters out seed artists from similar results" <| fun _ ->
              // Bonobo is a seed artist and also appears in similar-bonobo.json results.
              // The similar artist flow should not generate an extra Apple search for Bonobo
              // beyond what the seed flow already requests.
              let setup =
                  setupWith (baseSeedRoutes @ [ lastFmBonoboRoute; lastFmRadioheadRoute ] @ artistSearchRoutes @ similarArtistAlbumRoutes)

              let output = runSync similarConfig setup

              // Bonobo (5765078) should not be re-searched in the artist search endpoint
              let bonoboSearchRequests =
                  output.Requests
                  |> List.filter (fun req ->
                      req.Service = "apple"
                      && req.Path = "/v1/catalog/us/search"
                      && req.Query |> List.exists (fun (k, v) -> k = "term" && Dropd.Core.Normalization.normalizeText v = "bonobo"))

              Expect.isEmpty bonoboSearchRequests "seed artist Bonobo should not be searched as a similar artist"

          // DD-011: When dropd identifies similar artists, dropd shall resolve each to
          // an Apple Music catalog artist identifier.
          testCase "DD-011 resolves similar artists to Apple Music catalog IDs" <| fun _ ->
              let setup =
                  setupWith (baseSeedRoutes @ [ lastFmBonoboRoute; lastFmRadioheadRoute ] @ artistSearchRoutes @ similarArtistAlbumRoutes)

              let output = runSync similarConfig setup

              // Burial (14294754) is a similar artist; verify its releases are fetched.
              let burialAlbumRequests =
                  output.Requests
                  |> List.filter (fun req -> req.Path = "/v1/catalog/us/artists/14294754/albums")

              Expect.isNonEmpty burialAlbumRequests "should fetch releases for resolved similar artist Burial"

          // DD-012: When dropd performs fallback name matching for artist resolution,
          // dropd shall normalize names.
          testCase "DD-012 normalizes artist names for fallback matching" <| fun _ ->
              // "The  Black  Keys" from Last.fm should match "the black keys" in Apple fixture.
              let setup =
                  setupWith (baseSeedRoutes @ [ lastFmBonoboRoute; lastFmRadioheadRoute ] @ artistSearchRoutes @ similarArtistAlbumRoutes)

              let output = runSync similarConfig setup

              let blackKeysAlbumRequests =
                  output.Requests
                  |> List.filter (fun req -> req.Path = "/v1/catalog/us/artists/136975/albums")

              Expect.isNonEmpty blackKeysAlbumRequests "should resolve and fetch releases for 'The Black Keys' via normalized matching"

          // DD-013: Deduplicate the resolved artist set by catalog artist identifier.
          testCase "DD-013 deduplicates resolved similar artists by catalog ID" <| fun _ ->
              // Both Bonobo and Radiohead return Burial as similar.
              // Verify Burial's album endpoint is queried only once.
              let setup =
                  setupWith (baseSeedRoutes @ [ lastFmBonoboRoute; lastFmRadioheadRoute ] @ artistSearchRoutes @ similarArtistAlbumRoutes)

              let output = runSync similarConfig setup

              let burialAlbumRequests =
                  output.Requests
                  |> List.filter (fun req -> req.Path = "/v1/catalog/us/artists/14294754/albums")

              Expect.equal burialAlbumRequests.Length 1 "should query Burial's releases exactly once despite appearing as similar for two seeds"

          // DD-014: If a similar artist cannot be resolved, skip that artist.
          testCase "DD-014 skips unresolvable similar artists" <| fun _ ->
              // "NoMatch" in similar-bonobo.json has empty search results.
              let setup =
                  setupWith (baseSeedRoutes @ [ lastFmBonoboRoute; lastFmRadioheadRoute ] @ artistSearchRoutes @ similarArtistAlbumRoutes)

              let output = runSync similarConfig setup

              // "NoMatch" should not trigger any Apple catalog artist search that returns results,
              // and should not appear in any album fetch. Verify by checking that the search for
              // "NoMatch" was attempted but no new album path was created from it.
              let noMatchSearchReqs =
                  output.Requests
                  |> List.filter (fun req ->
                      req.Service = "apple"
                      && req.Path = "/v1/catalog/us/search"
                      && req.Query |> List.exists (fun (k, v) -> k = "term" && v = "NoMatch"))

              Expect.isNonEmpty noMatchSearchReqs "should have attempted to search for NoMatch"

              // Known valid album paths: seed artists and resolved similar artists.
              // 29525428 is an artifact of the favorited-artists fixture returning an extra rated ID.
              let knownAlbumPaths =
                  Set.ofList
                      [ "/v1/catalog/us/artists/657515/albums"    // Radiohead (seed)
                        "/v1/catalog/us/artists/5765078/albums"   // Bonobo (seed)
                        "/v1/catalog/us/artists/29525428/albums"  // extra favorited artist from fixture
                        "/v1/catalog/us/artists/14294754/albums"  // Burial (similar)
                        "/v1/catalog/us/artists/136975/albums"    // Black Keys (similar)
                        "/v1/catalog/us/artists/390999/albums" ]  // Four Tet (similar)

              let unknownAlbumRequests =
                  output.Requests
                  |> List.filter (fun req ->
                      req.Path.StartsWith("/v1/catalog/us/artists/")
                      && req.Path.EndsWith("/albums")
                      && not (knownAlbumPaths.Contains req.Path))

              Expect.isEmpty unknownAlbumRequests "no release query should be made for unresolvable artists"

          // DD-015: If a similar artist cannot be resolved, continue processing remaining.
          testCase "DD-015 continues after unresolvable similar artist" <| fun _ ->
              // "NoMatch" is unresolvable, but Burial, Black Keys, Four Tet should still be resolved.
              let setup =
                  setupWith (baseSeedRoutes @ [ lastFmBonoboRoute; lastFmRadioheadRoute ] @ artistSearchRoutes @ similarArtistAlbumRoutes)

              let output = runSync similarConfig setup

              let burialAlbumRequests =
                  output.Requests
                  |> List.filter (fun req -> req.Path = "/v1/catalog/us/artists/14294754/albums")

              Expect.isNonEmpty burialAlbumRequests "Burial should still be resolved and queried despite NoMatch failing"

          // DD-016: If the similar-artist data source is unavailable, log an error.
          ptestCase "DD-016 logs error when similar-artist source unavailable" <| fun _ -> ()

          // DD-017: If the similar-artist data source is unavailable, continue sync.
          ptestCase "DD-017 continues sync without similar artists when source unavailable"
          <| fun _ -> ()

          // DD-018: Similar-artist access is provider-independent.
          testCase "DD-018 similar-artist access is provider-independent" <| fun _ ->
              // Create a fake provider that returns a known artist without making any Last.fm calls.
              let fakeProvider : AC.SimilarArtistProvider =
                  { Name = "FakeProvider"
                    GetSimilar =
                      fun (_artistName, _mbid) ->
                          async {
                              return Ok [ { Name = "Burial"; Mbid = Some "9ea80bb8-4bcb-4188-9e0d-4156b187c6f9" } ]
                          } }

              let setup =
                  setupWith (baseSeedRoutes @ artistSearchRoutes @ similarArtistAlbumRoutes)

              let output = runSyncWithProvider fakeProvider similarConfig setup

              // Verify no Last.fm requests were made (the fake provider was used instead).
              let lastFmRequests =
                  output.Requests |> List.filter (fun req -> req.Service = "lastfm")

              Expect.isEmpty lastFmRequests "no Last.fm requests when using a fake provider"

              // Verify the fake provider's artist (Burial) was resolved and queried.
              let burialAlbumRequests =
                  output.Requests
                  |> List.filter (fun req -> req.Path = "/v1/catalog/us/artists/14294754/albums")

              Expect.isNonEmpty burialAlbumRequests "fake provider's artist should be resolved and fetched"

          // DD-019: Limit tracks from similar artists to configurable max percentage.
          ptestCase "DD-019 limits similar-artist tracks to configured percentage" <| fun _ -> ()

          ]
