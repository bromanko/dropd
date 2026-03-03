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
        ]
