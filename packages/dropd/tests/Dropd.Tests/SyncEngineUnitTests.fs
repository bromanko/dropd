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
          testCase "fetchLibraryArtists requests /v1/me/library/artists"
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

          testCase "fetchFavoritedArtists requests /v1/me/ratings/artists"
          <| fun _ ->
              let runtime, state =
                  Helpers.runtimeWith
                      [ { Method = "GET"
                          Path = "/v1/me/ratings/artists"
                          Query = []
                          Response = Helpers.ok (fixture "favorited-artists.json") } ]

              let result = SyncEngine.fetchFavoritedArtists validConfig runtime
              Expect.isOk result "favorites request should succeed"
              Expect.equal state.Requests.Head.Path "/v1/me/ratings/artists" "path should match"

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
