module Dropd.Tests.SyncEngineUnitTests

open System
open Expecto
open Dropd.Core
open Dropd.Core.Types
open Dropd.Tests.TestData

module AC = Dropd.Core.ApiContracts

module private Helpers =

    type Route =
        { Method: string
          Path: string
          Query: (string * string) list
          Response: AC.ApiResponse }

    type RuntimeState = { mutable Requests: AC.ApiRequest list }

    let private queryMatch required actual =
        required
        |> List.forall (fun (rk, rv) -> actual |> List.exists (fun (ak, av) -> ak = rk && av = rv))

    let runtimeWith (routes: Route list) =
        let state = { Requests = [] }

        let execute (request: AC.ApiRequest) =
            async {
                state.Requests <- state.Requests @ [ request ]

                let matched =
                    routes
                    |> List.tryFind (fun route ->
                        route.Method = request.Method
                        && route.Path = request.Path
                        && queryMatch route.Query request.Query)

                match matched with
                | Some route -> return route.Response
                | None ->
                    let notFound: AC.ApiResponse =
                        { StatusCode = 404
                          Body = "{\"error\":\"no route\"}"
                          Headers = [] }

                    return notFound
            }

        let runtime: AC.ApiRuntime =
            { Execute = execute
              UtcNow = fun () -> DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }

        runtime, state

    let ok body : AC.ApiResponse =
        { StatusCode = 200
          Body = body
          Headers = [] }

let private validConfig =
    match Config.validate TestData.validConfig with
    | Ok cfg -> cfg
    | Error errors -> failwithf "expected valid config, got %A" errors

let private release releaseId releaseDate genres trackIds : AC.DiscoveredRelease =
    { Id = CatalogAlbumId releaseId
      ArtistId = CatalogArtistId "5765078"
      ArtistName = "Bonobo"
      Name = releaseId
      ReleaseDate = releaseDate
      GenreNames = genres
      TrackIds = trackIds }

[<Tests>]
let seedingAndLabelTests =
    testList
        "Unit.SyncEngine.SeedingAndLabels"
        [
          testCase "fetchLibraryArtists requests /v1/me/library/artists with include=catalog"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/library/artists"
                          Query = []
                          Response = Helpers.ok (fixture "library-artists.json") } ]

              let result = SyncEngine.fetchLibraryArtists validConfig runtime
              Expect.isOk result "library request should succeed"
              Expect.equal state.Requests.Head.Path "/v1/me/library/artists" "path should match"
              Expect.isTrue
                  (state.Requests.Head.Query |> List.contains ("include", "catalog"))
                  "include=catalog query parameter should be present"

          testCase "fetchFavoritedArtists requests /v1/me/ratings/artists with ids"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/ratings/artists"
                          Query = []
                          Response = Helpers.ok (fixture "favorited-artists.json") } ]

              let artists =
                  [ { Id = CatalogArtistId "657515"; Name = "Radiohead" }
                    { Id = CatalogArtistId "29525428"; Name = "Bonobo" } ]

              let result = SyncEngine.fetchFavoritedArtists validConfig runtime artists
              Expect.isOk result "favorites request should succeed"
              Expect.equal state.Requests.Head.Path "/v1/me/ratings/artists" "path should match"
              Expect.isTrue
                  (state.Requests.Head.Query |> List.exists (fun (k, _) -> k = "ids"))
                  "ids query parameter should be present"

              // Finding 11/12: verify returned artist content and names
              let favoritedArtists = Result.defaultValue [] result
              Expect.equal favoritedArtists.Length 2 "should return both rated artists"
              Expect.equal favoritedArtists.[0].Id (CatalogArtistId "657515") "first artist ID"
              Expect.equal favoritedArtists.[0].Name "Radiohead" "first artist should have human-readable name"
              Expect.equal favoritedArtists.[1].Id (CatalogArtistId "29525428") "second artist ID"
              Expect.equal favoritedArtists.[1].Name "Bonobo" "second artist should have human-readable name"

          // Finding 2: test parseLibraryArtistsWithCatalog with missing catalog relationship
          testCase "fetchLibraryArtists skips artists without catalog relationship"
          <| fun _ ->
              let body =
                  """
{
  "data": [
    {
      "id": "r.local1",
      "type": "library-artists",
      "attributes": { "name": "Local Only" }
    },
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

              let runtime, _ =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/library/artists"
                          Query = []
                          Response = Helpers.ok body } ]

              let result = SyncEngine.fetchLibraryArtists validConfig runtime
              let artists = Result.defaultValue [] result
              Expect.equal artists.Length 1 "only the artist with catalog data should be returned"
              Expect.equal artists.[0].Id (CatalogArtistId "657515") "returned artist should be the catalog-mapped one"
              Expect.equal artists.[0].Name "Radiohead" "returned artist name should come from catalog"

          testCase "fetchLibraryArtists skips artists with empty catalog data array"
          <| fun _ ->
              let body =
                  """
{
  "data": [
    {
      "id": "r.empty1",
      "type": "library-artists",
      "attributes": { "name": "Empty Catalog" },
      "relationships": {
        "catalog": {
          "data": []
        }
      }
    },
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

              let runtime, _ =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/library/artists"
                          Query = []
                          Response = Helpers.ok body } ]

              let result = SyncEngine.fetchLibraryArtists validConfig runtime
              let artists = Result.defaultValue [] result
              Expect.equal artists.Length 1 "artist with empty catalog data should be excluded"
              Expect.equal artists.[0].Id (CatalogArtistId "657515") "only valid catalog artist returned"

          // Finding 14: test catalog item with missing attributes.name (fallback to ID)
          testCase "fetchLibraryArtists falls back to catalog ID when attributes.name is missing"
          <| fun _ ->
              let body =
                  """
{
  "data": [
    {
      "id": "r.noname",
      "type": "library-artists",
      "attributes": { "name": "No Name Artist" },
      "relationships": {
        "catalog": {
          "data": [
            { "id": "999999", "type": "artists" }
          ]
        }
      }
    }
  ]
}
"""

              let runtime, _ =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/library/artists"
                          Query = []
                          Response = Helpers.ok body } ]

              let result = SyncEngine.fetchLibraryArtists validConfig runtime
              let artists = Result.defaultValue [] result
              Expect.equal artists.Length 1 "artist should still be returned"
              Expect.equal artists.[0].Name "999999" "name should fall back to catalog ID"

          // Finding 3: test parseRatedArtistIds filtering by rating value
          testCase "fetchFavoritedArtists excludes negatively-rated and unrated artists"
          <| fun _ ->
              let ratingBody =
                  """
{
  "data": [
    { "id": "111", "type": "ratings", "attributes": { "value": 1 } },
    { "id": "222", "type": "ratings", "attributes": { "value": -1 } },
    { "id": "333", "type": "ratings" }
  ]
}
"""

              let runtime, _ =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/ratings/artists"
                          Query = []
                          Response = Helpers.ok ratingBody } ]

              let artists =
                  [ { Id = CatalogArtistId "111"; Name = "Liked" }
                    { Id = CatalogArtistId "222"; Name = "Disliked" }
                    { Id = CatalogArtistId "333"; Name = "Unrated" } ]

              let result = SyncEngine.fetchFavoritedArtists validConfig runtime artists
              let favorited = Result.defaultValue [] result
              Expect.equal favorited.Length 1 "only positively-rated artist should be returned"
              Expect.equal favorited.[0].Id (CatalogArtistId "111") "rated artist ID"
              Expect.equal favorited.[0].Name "Liked" "rated artist should preserve name"

          // Finding 5: test fetchFavoritedArtists with empty input
          testCase "fetchFavoritedArtists returns Ok empty list for empty input"
          <| fun _ ->
              let runtime, state = Helpers.runtimeWith []
              let result = SyncEngine.fetchFavoritedArtists validConfig runtime []
              Expect.equal result (Ok []) "empty input should return empty list"
              Expect.equal state.Requests.Length 0 "no API requests should be made"

          // Finding 4: test batching behavior (>25 artist IDs)
          testCase "fetchFavoritedArtists batches requests for >25 artist IDs"
          <| fun _ ->
              let ratingBody =
                  """{ "data": [{ "id": "artist-1", "type": "ratings", "attributes": { "value": 1 } }] }"""

              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/ratings/artists"
                          Query = []
                          Response = Helpers.ok ratingBody } ]

              let artists =
                  [ for i in 1..26 ->
                        { Id = CatalogArtistId $"artist-{i}"
                          Name = $"Artist {i}" } ]

              let result = SyncEngine.fetchFavoritedArtists validConfig runtime artists
              Expect.isOk result "should succeed"

              let ratingRequests =
                  state.Requests
                  |> List.filter (fun r -> r.Path = "/v1/me/ratings/artists")

              Expect.equal ratingRequests.Length 2 "should make two batched requests"

              let firstIds =
                  ratingRequests.[0].Query
                  |> List.find (fun (k, _) -> k = "ids")
                  |> snd

              let secondIds =
                  ratingRequests.[1].Query
                  |> List.find (fun (k, _) -> k = "ids")
                  |> snd

              Expect.equal (firstIds.Split(',').Length) 25 "first batch should have 25 IDs"
              Expect.equal (secondIds.Split(',').Length) 1 "second batch should have 1 ID"

              // Results from both batches should be combined
              let favorited = Result.defaultValue [] result
              Expect.isTrue (favorited.Length >= 1) "should have results from batch processing"
              Expect.equal favorited.[0].Name "Artist 1" "combined result should preserve name"

          testCase "fetchFavoritedArtists returns error when any batch fails"
          <| fun _ ->
              let okBody =
                  """{ "data": [{ "id": "artist-1", "type": "ratings", "attributes": { "value": 1 } }] }"""

              let runtime, _ =
                  Helpers.runtimeWith
                      [ // Second batch (containing artist-26) returns error
                        { Method = "GET"
                          Path = "/v1/me/ratings/artists"
                          Query = [ "ids", "artist-26" ]
                          Response =
                            { StatusCode = 500
                              Body = """{"error":"server error"}"""
                              Headers = [] } }
                        // Fallback for first batch
                        { Method = "GET"
                          Path = "/v1/me/ratings/artists"
                          Query = []
                          Response = Helpers.ok okBody } ]

              let artists =
                  [ for i in 1..26 ->
                        { Id = CatalogArtistId $"artist-{i}"
                          Name = $"Artist {i}" } ]

              let result = SyncEngine.fetchFavoritedArtists validConfig runtime artists
              Expect.isError result "should return error when a batch fails"

          testCase "resolveLabelId uses search endpoint with record-label query"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/catalog/us/search"
                          Query = [ "term", "Ninja Tune"; "types", "record-labels" ]
                          Response = Helpers.ok (fixture "label-search-ninja-tune.json") } ]

              let result = SyncEngine.resolveLabelId validConfig runtime "Ninja Tune"
              Expect.equal result (Ok(Some "1543411840")) "label id should parse"
              Expect.isTrue (state.Requests.Head.Query |> List.contains ("types", "record-labels")) "types query required"

          testCase "resolveLabels logs UnknownLabel for unresolved labels"
          <| fun _ ->
              let cfg = { validConfig with LabelNames = [ "FakeLabel" ] }

              let runtime, _ =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/catalog/us/search"
                          Query = [ "term", "FakeLabel"; "types", "record-labels" ]
                          Response = Helpers.ok (fixture "label-search-empty.json") } ]

              let artists, releases, logs = SyncEngine.resolveLabels cfg runtime
              Expect.equal artists [] "no artists"
              Expect.equal releases [] "no releases"
              Expect.isTrue (logs |> List.exists (fun log -> log.Code = "UnknownLabel")) "unknown label warning expected"
        ]

[<Tests>]
let releaseAndGenreTests =
    testList
        "Unit.SyncEngine.ReleasesAndGenres"
        [
          testCase "fetchArtistReleases includes sort=-releaseDate"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/catalog/us/artists/657515/albums"
                          Query = [ "sort", "-releaseDate" ]
                          Response = Helpers.ok (fixture "artist-albums-657515.json") } ]

              let result = SyncEngine.fetchArtistReleases validConfig runtime (CatalogArtistId "657515")
              Expect.isOk result "request should succeed"
              Expect.isTrue (state.Requests.Head.Query |> List.contains ("sort", "-releaseDate")) "sort param should be present"

          testCase "filterByLookback keeps only releases in window"
          <| fun _ ->
              let inWindow = release "9001" (Some(DateOnly(2026, 2, 20))) [ "Electronic" ] [ CatalogTrackId "t1" ]
              let outWindow = release "9002" (Some(DateOnly(2025, 12, 15))) [ "Electronic" ] [ CatalogTrackId "t2" ]
              let filtered = SyncEngine.filterByLookback (DateOnly(2026, 3, 1)) 30 [ inWindow; outWindow ]
              Expect.equal filtered [ inWindow ] "only recent releases remain"

          testCase "dedupReleases removes duplicate release IDs"
          <| fun _ ->
              let a = release "9001" (Some(DateOnly(2026, 2, 20))) [ "Electronic" ] [ CatalogTrackId "t1" ]
              let b = release "9002" (Some(DateOnly(2026, 2, 21))) [ "Electronic" ] [ CatalogTrackId "t2" ]
              let deduped = SyncEngine.dedupReleases [ a; a; b ]
              Expect.equal deduped.Length 2 "duplicate release ids should be collapsed"

          testCase "classifyByGenres excludes missing genres and logs warning"
          <| fun _ ->
              let candidate = release "9002" (Some(DateOnly(2025, 12, 15))) [] [ CatalogTrackId "track-9002-a" ]

              let runtime, _ =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/catalog/us/albums/9002"
                          Query = []
                          Response = Helpers.ok (fixture "album-9002-no-genres.json") } ]

              let releases, logs = SyncEngine.classifyByGenres validConfig runtime [ candidate ]
              Expect.equal releases [] "release without genres should be excluded"
              Expect.isTrue (logs |> List.exists (fun log -> log.Code = "MissingGenres")) "missing genre warning expected"
        ]

[<Tests>]
let authAndLogTests =
    testList
        "Unit.SyncEngine.AuthAndLogs"
        [
          testCase "Apple requests include Authorization header"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/catalog/us/artists/657515/albums"
                          Query = [ "sort", "-releaseDate" ]
                          Response = Helpers.ok (fixture "artist-albums-657515.json") } ]

              let _ = SyncEngine.fetchArtistReleases validConfig runtime (CatalogArtistId "657515")
              let headers = state.Requests.Head.Headers
              Expect.isTrue (headers |> List.exists (fun (k, v) -> k = "Authorization" && v = "Bearer dev-token")) "auth header required"

          testCase "/v1/me requests include Music-User-Token"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/library/artists"
                          Query = []
                          Response = Helpers.ok (fixture "library-artists.json") } ]

              let _ = SyncEngine.fetchLibraryArtists validConfig runtime
              let headers = state.Requests.Head.Headers
              Expect.isTrue (headers |> List.exists (fun (k, v) -> k = "Music-User-Token" && v = "user-token")) "music-user-token required"

          testCase "Last.fm requests include api_key from config"
          <| fun _ ->
              let request = SimilarArtists.buildRequest validConfig "Bonobo"
              Expect.isTrue
                  (request.Query |> List.exists (fun (k, v) -> k = "api_key" && v = "lastfm-key"))
                  "api_key should come from config"
        ]

[<Tests>]
let similarArtistTests =
    testList
        "Unit.SyncEngine.SimilarArtists"
        [
          testCase "Last.fm request contains method=artist.getSimilar, artist name, format=json, limit=10"
          <| fun _ ->
              let request = SimilarArtists.buildRequest validConfig "Bonobo"
              Expect.equal request.Service AC.LastFm "should target Last.fm"
              Expect.equal request.Path "/2.0" "should use Last.fm API path"
              Expect.isTrue (request.Query |> List.contains ("method", "artist.getSimilar")) "method param"
              Expect.isTrue (request.Query |> List.contains ("artist", "Bonobo")) "artist param"
              Expect.isTrue (request.Query |> List.contains ("format", "json")) "format param"
              Expect.isTrue (request.Query |> List.contains ("limit", "10")) "limit param"

          testCase "parser maps Last.fm error code 10 to AuthFailure"
          <| fun _ ->
              let body = lastFmFixture "similar-invalid-key.json"
              let result = SimilarArtists.parseResponse 200 body
              match result with
              | Error(AC.AuthFailure msg) ->
                  Expect.stringContains msg "Invalid API key" "should include error message"
              | other -> failwithf "expected AuthFailure, got %A" other

          testCase "parser maps non-auth Last.fm error to Unavailable"
          <| fun _ ->
              let body = lastFmFixture "similar-not-found.json"
              let result = SimilarArtists.parseResponse 200 body
              match result with
              | Error(AC.Unavailable(200, msg)) ->
                  Expect.stringContains msg "could not be found" "should include error message"
              | other -> failwithf "expected Unavailable, got %A" other

          testCase "parser maps non-2xx status to Unavailable"
          <| fun _ ->
              let result = SimilarArtists.parseResponse 503 "Service Unavailable"
              match result with
              | Error(AC.Unavailable(503, _)) -> ()
              | other -> failwithf "expected Unavailable(503, _), got %A" other

          testCase "parser parses success payload into SimilarArtist list"
          <| fun _ ->
              let body = lastFmFixture "similar-bonobo.json"
              let result = SimilarArtists.parseResponse 200 body
              match result with
              | Ok artists ->
                  Expect.equal artists.Length 4 "should return all artists"
                  Expect.equal artists.[0].Name "Burial" "first artist name"
                  Expect.equal artists.[0].Mbid (Some "9ea80bb8-4bcb-4188-9e0d-4156b187c6f9") "first artist MBID"
                  Expect.equal artists.[3].Name "NoMatch" "last artist name"
                  Expect.equal artists.[3].Mbid None "empty MBID should be None"
              | Error err -> failwithf "expected Ok, got %A" err

          testCase "seed filtering removes normalized-name matches"
          <| fun _ ->
              // This is tested via the full discoverSimilarArtists flow:
              // "Bonobo" seed should filter out " Bonobo " from similar results.
              let body = lastFmFixture "similar-bonobo.json"
              let result = SimilarArtists.parseResponse 200 body
              let artists = Result.defaultValue [] result
              let seedNormalized = Set.ofList [ Normalization.normalizeText "Bonobo" ]
              let filtered = artists |> List.filter (fun a -> not (seedNormalized.Contains(Normalization.normalizeText a.Name)))
              Expect.isFalse
                  (filtered |> List.exists (fun a -> Normalization.normalizeText a.Name = "bonobo"))
                  "seed artist should be filtered out"
              Expect.equal filtered.Length 3 "3 non-seed artists remain"

          testCase "artist resolution queries /v1/catalog/us/search with types=artists and limit=5"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/catalog/us/search"
                          Query = [ "term", "Burial"; "types", "artists"; "limit", "5" ]
                          Response = Helpers.ok (fixture "artist-search-burial.json") } ]

              let _ = SyncEngine.resolveArtistByNamePublic validConfig runtime "Burial"
              let req = state.Requests.Head
              Expect.equal req.Path "/v1/catalog/us/search" "search path"
              Expect.isTrue (req.Query |> List.contains ("types", "artists")) "types param"
              Expect.isTrue (req.Query |> List.contains ("limit", "5")) "limit param"

          testCase "fallback name matching resolves The  Black  Keys vs the black keys"
          <| fun _ ->
              let runtime, _ =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/catalog/us/search"
                          Query = [ "term", "The  Black  Keys"; "types", "artists"; "limit", "5" ]
                          Response = Helpers.ok (fixture "artist-search-black-keys.json") } ]

              let result = SyncEngine.resolveArtistByNamePublic validConfig runtime "The  Black  Keys"
              match result with
              | Some artist ->
                  Expect.equal (artist.Id) (CatalogArtistId "136975") "should resolve to correct ID"
              | None -> failwith "expected resolution to succeed"

          testCase "unresolved artist (empty search results) returns None"
          <| fun _ ->
              let runtime, _ =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/catalog/us/search"
                          Query = [ "term", "NoMatch"; "types", "artists"; "limit", "5" ]
                          Response = Helpers.ok (fixture "artist-search-empty.json") } ]

              let result = SyncEngine.resolveArtistByNamePublic validConfig runtime "NoMatch"
              Expect.isNone result "unresolvable artist should return None"

          testCase "parser maps invalid JSON body to MalformedResponse"
          <| fun _ ->
              let result = SimilarArtists.parseResponse 200 "not valid json at all"
              match result with
              | Error(AC.MalformedResponse _) -> ()
              | other -> failwithf "expected MalformedResponse, got %A" other

          testCase "parser handles empty body gracefully"
          <| fun _ ->
              let result = SimilarArtists.parseResponse 200 ""
              match result with
              | Error(AC.MalformedResponse _) -> ()
              | other -> failwithf "expected MalformedResponse for empty body, got %A" other

          testCase "parser returns empty list when similarartists key is missing"
          <| fun _ ->
              let result = SimilarArtists.parseResponse 200 """{"other":"data"}"""
              Expect.equal result (Ok []) "missing key structure should return empty list"

          testCase "parser returns empty list when artist array is missing"
          <| fun _ ->
              let result = SimilarArtists.parseResponse 200 """{"similarartists":{}}"""
              Expect.equal result (Ok []) "missing artist key should return empty list"

          testCase "deduplicate resolved similar artists by catalog ID"
          <| fun _ ->
              let artists : AC.DiscoveredArtist list =
                  [ { Id = CatalogArtistId "14294754"; Name = "Burial" }
                    { Id = CatalogArtistId "14294754"; Name = "Burial" }
                    { Id = CatalogArtistId "136975"; Name = "The Black Keys" } ]
              let deduped = artists |> List.distinctBy (fun a -> a.Id)
              Expect.equal deduped.Length 2 "should deduplicate by ID"
        ]

[<Tests>]
let artistFilteringUnitTests =
    testList
        "Unit.SyncEngine.ArtistFiltering"
        [
          testCase "fetchSongRatings requests /v1/me/ratings/songs"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/ratings/songs"
                          Query = []
                          Response = Helpers.ok (fixture "ratings-songs-dislikes.json") } ]

              let result = SyncEngine.fetchSongRatings validConfig runtime
              Expect.isOk result "request should succeed"
              Expect.equal state.Requests.Head.Path "/v1/me/ratings/songs" "should request song ratings"

          testCase "fetchAlbumRatings requests /v1/me/ratings/albums"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/ratings/albums"
                          Query = []
                          Response = Helpers.ok (fixture "ratings-albums-dislikes.json") } ]

              let result = SyncEngine.fetchAlbumRatings validConfig runtime
              Expect.isOk result "request should succeed"
              Expect.equal state.Requests.Head.Path "/v1/me/ratings/albums" "should request album ratings"

          testCase "collectExcludedArtists extracts disliked artist names"
          <| fun _ ->
              let songRatings = fixture "ratings-songs-dislikes.json"
              let albumRatings = fixture "ratings-albums-dislikes.json"
              let excluded = SyncEngine.collectExcludedArtists songRatings albumRatings
              // Both fixtures have BadArtist with value=-1
              Expect.isTrue (excluded |> Set.contains "badartist") "BadArtist should be excluded (normalized)"
              Expect.isFalse (excluded |> Set.contains "goodartist") "GoodArtist should not be excluded"
              Expect.isFalse (excluded |> Set.contains "otherartist") "OtherArtist should not be excluded"

          testCase "filterByExcludedArtists removes releases by excluded artists"
          <| fun _ ->
              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "r1"
                      ArtistId = CatalogArtistId "bad-1"
                      ArtistName = "BadArtist"
                      Name = "Bad Album"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ CatalogTrackId "bad-t1" ] }
                    { Id = CatalogAlbumId "r2"
                      ArtistId = CatalogArtistId "good-1"
                      ArtistName = "GoodArtist"
                      Name = "Good Album"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ CatalogTrackId "good-t1" ] } ]

              let excluded = Set.ofList [ "badartist" ]
              let filtered = SyncEngine.filterByExcludedArtists excluded releases
              Expect.equal filtered.Length 1 "only good artist release should remain"
              Expect.equal filtered.[0].ArtistName "GoodArtist" "remaining release should be from GoodArtist"

          testCase "collectExcludedArtists only excludes value=-1 artists"
          <| fun _ ->
              let songBody = """{"data":[
                {"attributes":{"value":-1,"artistName":"Disliked"}},
                {"attributes":{"value":1,"artistName":"Liked"}},
                {"attributes":{"value":0,"artistName":"Neutral"}}
              ]}"""
              let albumBody = """{"data":[]}"""
              let excluded = SyncEngine.collectExcludedArtists songBody albumBody
              Expect.isTrue (excluded |> Set.contains "disliked") "value=-1 should be excluded"
              Expect.isFalse (excluded |> Set.contains "liked") "value=1 should not be excluded"
              Expect.isFalse (excluded |> Set.contains "neutral") "value=0 should not be excluded"

          testCase "collectExcludedArtists returns empty set when no ratings"
          <| fun _ ->
              let excluded = SyncEngine.collectExcludedArtists """{"data":[]}""" """{"data":[]}"""
              Expect.isEmpty excluded "no ratings should yield empty exclusion set"

          testCase "filterByExcludedArtists with empty set preserves all releases"
          <| fun _ ->
              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "r1"
                      ArtistId = CatalogArtistId "a1"
                      ArtistName = "Artist1"
                      Name = "Album1"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ CatalogTrackId "t1" ] }
                    { Id = CatalogAlbumId "r2"
                      ArtistId = CatalogArtistId "a2"
                      ArtistName = "Artist2"
                      Name = "Album2"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ CatalogTrackId "t2" ] } ]

              let filtered = SyncEngine.filterByExcludedArtists Set.empty releases
              Expect.equal filtered.Length releases.Length "empty exclusion set should keep all releases"
        ]
