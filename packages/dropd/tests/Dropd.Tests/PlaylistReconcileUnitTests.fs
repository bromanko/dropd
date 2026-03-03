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

              let handler (request: AC.ApiRequest) : AC.ApiResponse =
                  match request.Method, request.Path with
                  | "GET", _ when request.Path.EndsWith("/tracks") -> Helpers.response 200 tracksBody
                  | "POST", _ when request.Path.EndsWith("/tracks") -> Helpers.response 200 "{}"
                  | "DELETE", _ -> Helpers.response 200 "{}"
                  | _ -> Helpers.response 200 "{}"

              let cfg =
                  { validConfig with
                      Playlists = [ { Name = "Electronic Drops"; GenreCriteria = [ "electronic" ] } ] }

              let runtime, _ = Helpers.runtimeFrom handler

              let result, logs =
                  PlaylistReconcile.reconcilePlaylists cfg discovery runtime |> Async.RunSynchronously

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
                  | "GET", "/v1/me/library/playlists/One/tracks" -> Helpers.response 404 "{}"
                  | "GET", "/v1/me/library/playlists/Two/tracks" -> Helpers.response 404 "{}"
                  | "POST", "/v1/me/library/playlists" when request.Body.IsSome && request.Body.Value.Contains("\"One\"") ->
                      Helpers.response 500 "{\"error\":\"create failed\"}"
                  | "POST", "/v1/me/library/playlists" when request.Body.IsSome && request.Body.Value.Contains("\"Two\"") ->
                      Helpers.response 201 "{\"id\":\"pl-two\"}"
                  | "POST", "/v1/me/library/playlists/One/tracks"
                  | "POST", "/v1/me/library/playlists/Two/tracks" -> Helpers.response 200 "{}"
                  | _ -> Helpers.response 200 "{}"

              let runtime, state = Helpers.runtimeFrom handler
              let result, logs = PlaylistReconcile.reconcilePlaylists cfg discovery runtime |> Async.RunSynchronously

              Expect.isTrue result.HadPlaylistFailures "partial failure flag should be set"

              Expect.isTrue
                  (logs |> List.exists (fun log -> log.Code = "PlaylistCreateFailure" && log.Data.["playlist"] = "One"))
                  "create failure should be logged"

              Expect.isTrue
                  (state.Requests
                   |> List.exists (fun request -> request.Method = "POST" && request.Path = "/v1/me/library/playlists" && request.Body.IsSome && request.Body.Value.Contains("\"Two\"")))
                  "second playlist should still be processed"
        ]
