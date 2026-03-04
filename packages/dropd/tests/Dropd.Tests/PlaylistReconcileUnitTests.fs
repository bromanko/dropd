module Dropd.Tests.PlaylistReconcileUnitTests

open System
open Expecto
open Dropd.Core
open Dropd.Core.Types
open Dropd.Tests.TestData

module AC = Dropd.Core.ApiContracts

module private Helpers =

    type RuntimeState = { mutable Requests: AC.ApiRequest list }

    let runtimeFrom (handler: AC.ApiRequest -> AC.ApiResponse) =
        let state = { Requests = [] }

        let execute (request: AC.ApiRequest) =
            async {
                state.Requests <- state.Requests @ [ request ]
                return handler request
            }

        let runtime: AC.ApiRuntime =
            { Execute = execute
              UtcNow = fun () -> DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) }

        runtime, state

    let response code body : AC.ApiResponse =
        { StatusCode = code
          Body = body
          Headers = [] }

let private validConfig =
    match Config.validate TestData.validConfig with
    | Ok cfg -> cfg
    | Error errors -> failwithf "expected valid config, got %A" errors

let private discovery : AC.DiscoveryResult =
    { SeedArtists = []
      SimilarArtists = []
      LabelArtists = []
      Releases =
        [ { Id = CatalogAlbumId "9001"
            ArtistId = CatalogArtistId "5765078"
            ArtistName = "Bonobo"
            Name = "Future Echoes"
            ReleaseDate = Some(DateOnly(2026, 2, 20))
            GenreNames = [ "Electronic" ]
            TrackIds = [ CatalogTrackId "track-9001-a"; CatalogTrackId "track-9001-b" ] }
          { Id = CatalogAlbumId "9002"
            ArtistId = CatalogArtistId "5765078"
            ArtistName = "Bonobo"
            Name = "Old Echoes"
            ReleaseDate = Some(DateOnly(2025, 12, 1))
            GenreNames = [ "Dance" ]
            TrackIds = [ CatalogTrackId "track-old" ] } ] }

[<Tests>]
let tests =
    testList
        "Unit.PlaylistReconcile"
        [
          testCase "computePlan calculates add and remove sets"
          <| fun _ ->
              let existing: PlaylistReconcile.ExistingTrack list =
                  [ { Id = CatalogTrackId "track-9001-a"
                      ReleaseDate = Some(DateOnly(2026, 2, 20)) }
                    { Id = CatalogTrackId "track-existing-old"
                      ReleaseDate = Some(DateOnly(2025, 12, 1)) } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      100
                      Set.empty
                      { Name = "Electronic Drops"
                        GenreCriteria = [ "electronic" ] }
                      discovery.Releases
                      existing

              Expect.equal plan.AddTracks [ CatalogTrackId "track-9001-b" ] "add set should include only missing track"
              Expect.equal plan.RemoveTracks [ CatalogTrackId "track-existing-old" ] "remove set should include only stale track"

          testCase "computePlan deduplicates add tracks"
          <| fun _ ->
              let duplicateRelease: AC.DiscoveredRelease =
                  { Id = CatalogAlbumId "9100"
                    ArtistId = CatalogArtistId "1"
                    ArtistName = "X"
                    Name = "Dupe"
                    ReleaseDate = Some(DateOnly(2026, 2, 20))
                    GenreNames = [ "Electronic" ]
                    TrackIds = [ CatalogTrackId "dup"; CatalogTrackId "dup" ] }

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      100
                      Set.empty
                      { Name = "Electronic Drops"
                        GenreCriteria = [ "electronic" ] }
                      [ duplicateRelease ]
                      []

              Expect.equal plan.AddTracks [ CatalogTrackId "dup" ] "add set should be unique"

          testCase "computePlan removes tracks older than rolling window"
          <| fun _ ->
              let existing: PlaylistReconcile.ExistingTrack list =
                  [ { Id = CatalogTrackId "old"
                      ReleaseDate = Some(DateOnly(2025, 12, 1)) }
                    { Id = CatalogTrackId "recent"
                      ReleaseDate = Some(DateOnly(2026, 2, 20)) } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      100
                      Set.empty
                      { Name = "Dance Floor"
                        GenreCriteria = [ "dance" ] }
                      discovery.Releases
                      existing

              Expect.equal plan.RemoveTracks [ CatalogTrackId "old" ] "only old tracks should be removed"

          // Finding 6: computePlan with an empty releases list.
          testCase "computePlan produces empty addTracks when no releases match criteria"
          <| fun _ ->
              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      100
                      Set.empty
                      { Name = "Electronic Drops"
                        GenreCriteria = [ "electronic" ] }
                      [] // no releases
                      [ { Id = CatalogTrackId "stale"
                          ReleaseDate = Some(DateOnly(2025, 1, 1)) } ]

              Expect.equal plan.AddTracks [] "no releases should yield no adds"
              Expect.equal plan.RemoveTracks [ CatalogTrackId "stale" ] "stale tracks still removed even with no new releases"

          // Finding 7: existing track with ReleaseDate = None must not be removed.
          testCase "computePlan retains existing tracks with no release date"
          <| fun _ ->
              let existing: PlaylistReconcile.ExistingTrack list =
                  [ { Id = CatalogTrackId "no-date"; ReleaseDate = None }
                    { Id = CatalogTrackId "stale"; ReleaseDate = Some(DateOnly(2025, 1, 1)) } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      100
                      Set.empty
                      { Name = "Electronic Drops"
                        GenreCriteria = [ "electronic" ] }
                      discovery.Releases
                      existing

              Expect.equal plan.RemoveTracks [ CatalogTrackId "stale" ] "only dated stale tracks should be removed"

              Expect.isFalse
                  (plan.RemoveTracks |> List.contains (CatalogTrackId "no-date"))
                  "no-date tracks should not be removed"

          // Finding 17: parseExistingTracks (private) exercised via reconcilePlaylists
          // with a fixture containing one valid item and one malformed item.
          testCase "reconcilePlaylists ignores malformed track items silently"
          <| fun _ ->
              let tracksBody =
                  """{"data":[
                    {"id":"good-track","attributes":{"releaseDate":"2026-02-01"}},
                    {"attributes":{"releaseDate":"2026-02-01"}},
                    {"id":"","attributes":{}},
                    {"id":"another-good","attributes":{}}
                  ]}"""

              let playlistListBody =
                  """{"data":[{"id":"p.existing","type":"library-playlists","attributes":{"name":"Electronic Drops"}}]}"""

              let handler (request: AC.ApiRequest) : AC.ApiResponse =
                  match request.Method, request.Path with
                  | "GET", "/v1/me/library/playlists" -> Helpers.response 200 playlistListBody
                  | "GET", _ when request.Path.EndsWith("/tracks") -> Helpers.response 200 tracksBody
                  | "POST", _ when request.Path.EndsWith("/tracks") -> Helpers.response 200 "{}"
                  | "DELETE", _ -> Helpers.response 200 "{}"
                  | _ -> Helpers.response 200 "{}"

              let cfg =
                  { validConfig with
                      Playlists = [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ] }

              let runtime, _ = Helpers.runtimeFrom handler

              let result, logs =
                  PlaylistReconcile.reconcilePlaylists cfg discovery runtime Map.empty |> Async.RunSynchronously

              Expect.isEmpty logs "no error logs expected for valid parse"
              Expect.isFalse result.HadPlaylistFailures "should not report failure for malformed items"

          testCase "reconcilePlaylists continues after one playlist failure"
          <| fun _ ->
              let cfg =
                  { validConfig with
                      Playlists =
                        [ { Name = "One"
                            GenreCriteria = [ "electronic" ] }
                          { Name = "Two"
                            GenreCriteria = [ "electronic" ] } ] }

              let handler (request: AC.ApiRequest) : AC.ApiResponse =
                  match request.Method, request.Path with
                  | "GET", "/v1/me/library/playlists" -> Helpers.response 200 """{"data":[]}"""
                  | "POST", "/v1/me/library/playlists" when request.Body.IsSome && request.Body.Value.Contains("\"One\"") ->
                      Helpers.response 500 "{\"error\":\"create failed\"}"
                  | "POST", "/v1/me/library/playlists" when request.Body.IsSome && request.Body.Value.Contains("\"Two\"") ->
                      Helpers.response 201 """{"data":[{"id":"p.two","type":"library-playlists","attributes":{"name":"Two"}}]}"""
                  | "POST", _ when request.Path.EndsWith("/tracks") -> Helpers.response 200 "{}"
                  | _ -> Helpers.response 200 "{}"

              let runtime, state = Helpers.runtimeFrom handler
              let result, logs = PlaylistReconcile.reconcilePlaylists cfg discovery runtime Map.empty |> Async.RunSynchronously

              Expect.isTrue result.HadPlaylistFailures "partial failure flag should be set"

              Expect.isTrue
                  (logs |> List.exists (fun log -> log.Code = "PlaylistCreateFailure" && log.Data.["playlist"] = "One"))
                  "create failure should be logged"

              Expect.isTrue
                  (state.Requests
                   |> List.exists (fun request -> request.Method = "POST" && request.Path = "/v1/me/library/playlists" && request.Body.IsSome && request.Body.Value.Contains("\"Two\"")))
                  "second playlist should still be processed"
        ]

[<Tests>]
let similarCapTests =
    testList
        "Unit.PlaylistReconcile.SimilarCap"
        [
          testCase "cap limits similar tracks to configured percentage"
          <| fun _ ->
              // 10 total tracks: 5 from similar artist, 5 from seed artist. Cap = 20%.
              // allowedSimilar = floor(20 * 10 / 100) = 2
              let similarArtistId = CatalogArtistId "similar-1"
              let seedArtistId = CatalogArtistId "seed-1"

              let releases : AC.DiscoveredRelease list =
                  [ // 5 seed tracks
                    { Id = CatalogAlbumId "seed-album"
                      ArtistId = seedArtistId
                      ArtistName = "SeedArtist"
                      Name = "SeedAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"seed-t{i}" ] }
                    // 5 similar tracks
                    { Id = CatalogAlbumId "sim-album"
                      ArtistId = similarArtistId
                      ArtistName = "SimilarArtist"
                      Name = "SimAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"sim-t{i}" ] } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      20  // 20% cap
                      (Set.ofList [ similarArtistId ])
                      { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                      releases
                      []

              let similarTracks =
                  plan.AddTracks |> List.filter (fun (CatalogTrackId id) -> id.StartsWith "sim-")
              let seedTracks =
                  plan.AddTracks |> List.filter (fun (CatalogTrackId id) -> id.StartsWith "seed-")

              Expect.isTrue (similarTracks.Length <= 2) $"at most 2 similar tracks allowed, got {similarTracks.Length}"
              Expect.equal seedTracks.Length 5 "all seed tracks should be present"

          testCase "repeated runs are deterministic"
          <| fun _ ->
              let similarArtistId = CatalogArtistId "similar-1"
              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "seed-album"
                      ArtistId = CatalogArtistId "seed-1"
                      ArtistName = "SeedArtist"
                      Name = "SeedAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"seed-t{i}" ] }
                    { Id = CatalogAlbumId "sim-album"
                      ArtistId = similarArtistId
                      ArtistName = "SimilarArtist"
                      Name = "SimAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"sim-t{i}" ] } ]

              let makePlan () =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      20
                      (Set.ofList [ similarArtistId ])
                      { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                      releases
                      []

              let plan1 = makePlan()
              let plan2 = makePlan()

              Expect.equal plan1.AddTracks plan2.AddTracks "same inputs should produce same output"

          testCase "non-similar tracks are never removed to satisfy cap"
          <| fun _ ->
              let similarArtistId = CatalogArtistId "similar-1"
              let seedArtistId = CatalogArtistId "seed-1"

              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "seed-album"
                      ArtistId = seedArtistId
                      ArtistName = "SeedArtist"
                      Name = "SeedAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..8 -> CatalogTrackId $"seed-t{i}" ] }
                    { Id = CatalogAlbumId "sim-album"
                      ArtistId = similarArtistId
                      ArtistName = "SimilarArtist"
                      Name = "SimAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..2 -> CatalogTrackId $"sim-t{i}" ] } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      10  // 10% cap, so 1 similar out of 10 total
                      (Set.ofList [ similarArtistId ])
                      { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                      releases
                      []

              let seedTracks =
                  plan.AddTracks |> List.filter (fun (CatalogTrackId id) -> id.StartsWith "seed-")

              // All 8 seed tracks must be present regardless of cap
              Expect.equal seedTracks.Length 8 "all non-similar tracks must be included"

          testCase "cap of 0 percent excludes all similar tracks"
          <| fun _ ->
              let similarArtistId = CatalogArtistId "similar-1"
              let seedArtistId = CatalogArtistId "seed-1"

              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "seed-album"
                      ArtistId = seedArtistId
                      ArtistName = "SeedArtist"
                      Name = "SeedAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"seed-t{i}" ] }
                    { Id = CatalogAlbumId "sim-album"
                      ArtistId = similarArtistId
                      ArtistName = "SimilarArtist"
                      Name = "SimAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"sim-t{i}" ] } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      0  // 0% cap
                      (Set.ofList [ similarArtistId ])
                      { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                      releases
                      []

              let simTracks = plan.AddTracks |> List.filter (fun (CatalogTrackId id) -> id.StartsWith "sim-")
              Expect.equal simTracks.Length 0 "0% cap should exclude all similar tracks"
              let seedTracks = plan.AddTracks |> List.filter (fun (CatalogTrackId id) -> id.StartsWith "seed-")
              Expect.equal seedTracks.Length 5 "all seed tracks should remain"

          testCase "cap of 100 percent includes all similar tracks"
          <| fun _ ->
              let similarArtistId = CatalogArtistId "similar-1"
              let seedArtistId = CatalogArtistId "seed-1"

              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "seed-album"
                      ArtistId = seedArtistId
                      ArtistName = "SeedArtist"
                      Name = "SeedAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"seed-t{i}" ] }
                    { Id = CatalogAlbumId "sim-album"
                      ArtistId = similarArtistId
                      ArtistName = "SimilarArtist"
                      Name = "SimAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"sim-t{i}" ] } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      100  // 100% cap
                      (Set.ofList [ similarArtistId ])
                      { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                      releases
                      []

              let simTracks = plan.AddTracks |> List.filter (fun (CatalogTrackId id) -> id.StartsWith "sim-")
              Expect.equal simTracks.Length 5 "100% cap should include all similar tracks"

          testCase "cap of 1 percent with small total truncates to 0 similar tracks"
          <| fun _ ->
              let similarArtistId = CatalogArtistId "similar-1"
              let seedArtistId = CatalogArtistId "seed-1"

              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "seed-album"
                      ArtistId = seedArtistId
                      ArtistName = "SeedArtist"
                      Name = "SeedAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..3 -> CatalogTrackId $"seed-t{i}" ] }
                    { Id = CatalogAlbumId "sim-album"
                      ArtistId = similarArtistId
                      ArtistName = "SimilarArtist"
                      Name = "SimAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..2 -> CatalogTrackId $"sim-t{i}" ] } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      1  // 1% cap, total = 5, allowedSimilar = floor(1*5/100) = 0
                      (Set.ofList [ similarArtistId ])
                      { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                      releases
                      []

              let simTracks = plan.AddTracks |> List.filter (fun (CatalogTrackId id) -> id.StartsWith "sim-")
              Expect.equal simTracks.Length 0 "1% cap with 5 tracks should floor-truncate to 0 similar tracks"

          testCase "cap of 50 percent allows exactly half"
          <| fun _ ->
              let similarArtistId = CatalogArtistId "similar-1"
              let seedArtistId = CatalogArtistId "seed-1"

              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "seed-album"
                      ArtistId = seedArtistId
                      ArtistName = "SeedArtist"
                      Name = "SeedAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"seed-t{i}" ] }
                    { Id = CatalogAlbumId "sim-album"
                      ArtistId = similarArtistId
                      ArtistName = "SimilarArtist"
                      Name = "SimAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..5 -> CatalogTrackId $"sim-t{i}" ] } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      50  // 50% cap, total = 10, allowedSimilar = 5
                      (Set.ofList [ similarArtistId ])
                      { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                      releases
                      []

              let simTracks = plan.AddTracks |> List.filter (fun (CatalogTrackId id) -> id.StartsWith "sim-")
              Expect.equal simTracks.Length 5 "50% of 10 = 5 similar tracks allowed"

          testCase "cap applies even when all tracks are from similar artists"
          <| fun _ ->
              // No seed artist tracks — all 10 tracks from similar artists.
              // 20% cap: allowedSimilar = floor(20 * 10 / 100) = 2
              let similarArtistId = CatalogArtistId "similar-1"

              let releases : AC.DiscoveredRelease list =
                  [ { Id = CatalogAlbumId "sim-album"
                      ArtistId = similarArtistId
                      ArtistName = "SimilarArtist"
                      Name = "SimAlbum"
                      ReleaseDate = Some(DateOnly(2026, 2, 20))
                      GenreNames = [ "Electronic" ]
                      TrackIds = [ for i in 1..10 -> CatalogTrackId $"sim-t{i}" ] } ]

              let plan =
                  PlaylistReconcile.computePlan
                      (DateOnly(2026, 3, 1))
                      30
                      20  // 20% cap
                      (Set.ofList [ similarArtistId ])
                      { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] }
                      releases
                      []

              Expect.equal plan.AddTracks.Length 2 "only 20% of 10 = 2 similar tracks allowed with no seed tracks"
        ]
