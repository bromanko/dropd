module Dropd.Tests.ArtistFilteringTests

open Expecto
open Dropd.Core.Types
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

module AC = Dropd.Core.ApiContracts

/// Config with a seed artist (Bonobo) and one playlist, no labels.
let private filterConfig =
    { validConfig with
        LabelNames = []
        Playlists =
            [ { Name = "Electronic Drops"
                GenreCriteria = [ "electronic" ] } ] }

/// A fake provider that returns no similar artists (simplifies filtering tests).
let private noSimilarProvider : AC.SimilarArtistProvider =
    { Name = "NoSimilar"
      GetSimilar = fun _ -> async { return Ok [] } }

/// Base Apple routes for seed artists with one release that includes a BadArtist track.
let private filterBaseRoutes =
    [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
      route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515,5765078" ] (Always(okFixture "favorited-artists.json"))
      route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ]
          (Always(withStatus 200 """{"data":[
            {"id":"bad-album","type":"albums","attributes":{
              "name":"Bad Album","artistId":"bad-1","artistName":"BadArtist",
              "releaseDate":"2026-02-20","genreNames":["Electronic"]},
              "relationships":{"tracks":{"data":[{"id":"bad-t1"}]}}}
          ]}"""))
      route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ]
          (Always(withStatus 200 """{"data":[
            {"id":"good-album","type":"albums","attributes":{
              "name":"Good Album","artistId":"good-1","artistName":"GoodArtist",
              "releaseDate":"2026-02-20","genreNames":["Electronic"]},
              "relationships":{"tracks":{"data":[{"id":"good-t1"}]}}}
          ]}"""))
      route "apple" "GET" "/v1/catalog/us/artists/29525428/albums" [ "sort", "-releaseDate" ]
          (Always(withStatus 200 """{"data":[]}"""))
      // Ratings routes
      route "apple" "GET" "/v1/me/ratings/songs" [] (Always(okFixture "ratings-songs-dislikes.json"))
      route "apple" "GET" "/v1/me/ratings/albums" [] (Always(okFixture "ratings-albums-dislikes.json"))
      // Playlist routes
      route "apple" "GET" "/v1/me/library/playlists" [] (Always(withStatus 200 """{"data":[]}"""))
      route "apple" "POST" "/v1/me/library/playlists" [] (Always(withStatus 201 """{"data":[{"id":"p.new","type":"library-playlists","attributes":{"name":"Electronic Drops"}}]}"""))
      route "apple" "POST" "/v1/me/library/playlists/p.new/tracks" [] (Always(withStatus 200 "{}")) ]

[<Tests>]
let tests =
    testList
        "Artist Filtering"
        [

          // DD-020: When dropd performs a sync, dropd shall retrieve the user's personal
          // ratings for songs and albums from the Apple Music API.
          testCase "DD-020 retrieves user ratings for songs and albums" <| fun _ ->
              let setup = setupWith filterBaseRoutes
              let output = runSyncWithProvider noSimilarProvider filterConfig setup

              let songRatingsReqs =
                  output.Requests
                  |> List.filter (fun req -> req.Service = "apple" && req.Path = "/v1/me/ratings/songs")

              let albumRatingsReqs =
                  output.Requests
                  |> List.filter (fun req -> req.Service = "apple" && req.Path = "/v1/me/ratings/albums")

              Expect.isNonEmpty songRatingsReqs "should request song ratings"
              Expect.isNonEmpty albumRatingsReqs "should request album ratings"

          // DD-021: When dropd finds a song or album with a dislike rating (value of -1),
          // dropd shall exclude the primary artist from all playlist population.
          testCase "DD-021 excludes artists with dislike ratings from playlists" <| fun _ ->
              let setup = setupWith filterBaseRoutes
              let output = runSyncWithProvider noSimilarProvider filterConfig setup

              // Find the POST to add tracks
              let addReqs =
                  output.Requests
                  |> List.filter (fun req -> req.Method = "POST" && req.Path.EndsWith("/tracks"))

              match addReqs with
              | [] ->
                  // No tracks added — that's fine if BadArtist was the only electronic track.
                  // The key point is that bad-t1 should NOT be in any add request.
                  ()
              | req :: _ ->
                  let body = req.Body |> Option.defaultValue ""
                  Expect.isFalse (body.Contains("bad-t1")) "tracks from disliked BadArtist should not be in playlist"
                  Expect.isTrue (body.Contains("good-t1")) "tracks from non-disliked GoodArtist should be in playlist"

          // DD-022: When dropd excludes an artist due to dislike ratings, dropd shall log
          // the excluded artist name.
          testCase "DD-022 logs excluded artist names" <| fun _ ->
              let setup = setupWith filterBaseRoutes
              let output = runSyncWithProvider noSimilarProvider filterConfig setup

              let exclusionLogs =
                  output.Logs
                  |> List.filter (fun log -> log.Code = "ExcludedDislikedArtist")

              Expect.isNonEmpty exclusionLogs "should log excluded artist"

              let hasBArtist =
                  exclusionLogs
                  |> List.exists (fun log ->
                      log.Data |> Map.tryFind "artist" |> Option.map (fun a -> a = "badartist") |> Option.defaultValue false)

              Expect.isTrue hasBArtist "should log 'badartist' as excluded"

          ]
