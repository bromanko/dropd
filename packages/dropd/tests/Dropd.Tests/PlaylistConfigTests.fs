module Dropd.Tests.PlaylistConfigTests

open Expecto
open Dropd.Core.Types
open Dropd.Core.Config

[<Tests>]
let tests =
    testList
        "Playlist Configuration"
        [

          // DD-036: dropd shall store a user-configured list of playlist definitions,
          // each containing a playlist name and a set of genre criteria.
          testCase "DD-036 playlist definitions have name and genre criteria"
          <| fun _ ->
              let playlist =
                  { Name = "Electronic Drops"
                    GenreCriteria = [ "electronic"; "dance" ] }

              Expect.equal playlist.Name "Electronic Drops" "name stored"
              Expect.equal playlist.GenreCriteria [ "electronic"; "dance" ] "genre criteria stored"

              let config =
                  { defaults with
                      Playlists = [ playlist ] }

              Expect.equal config.Playlists.Length 1 "one playlist configured"

          // DD-037: dropd shall store a configurable rolling window duration that determines
          // how long tracks remain in a playlist, defaulting to 30 days.
          testCase "DD-037 rolling window defaults to 30 days"
          <| fun _ -> Expect.equal defaults.RollingWindowDays 30 "default rolling window"

          // DD-038: dropd shall store a configurable maximum percentage of similar-artist
          // tracks allowed per playlist.
          testCase "DD-038 similar artist max percent is configurable"
          <| fun _ ->
              Expect.equal defaults.SimilarArtistMaxPercent 20 "default similar artist percent"

              let custom =
                  { defaults with
                      SimilarArtistMaxPercent = 35 }

              Expect.equal custom.SimilarArtistMaxPercent 35 "custom value"

          // DD-039: dropd shall store a configurable new-release lookback duration,
          // defaulting to 30 days.
          testCase "DD-039 lookback defaults to 30 days"
          <| fun _ -> Expect.equal defaults.LookbackDays 30 "default lookback"

          // DD-040: dropd shall store a configurable API request timeout,
          // defaulting to 10 seconds.
          testCase "DD-040 request timeout defaults to 10 seconds"
          <| fun _ -> Expect.equal defaults.RequestTimeoutSeconds 10 "default timeout"

          // DD-041: dropd shall store a configurable maximum API retry count per request,
          // defaulting to 3 retries.
          testCase "DD-041 max retries defaults to 3"
          <| fun _ -> Expect.equal defaults.MaxRetries 3 "default retries"

          // DD-042: dropd shall store a configurable pagination policy with defaults of
          // 100 items per page and a maximum of 20 pages per endpoint call-chain per sync.
          testCase "DD-042 pagination defaults to 100 items/page and 20 max pages"
          <| fun _ ->
              Expect.equal defaults.PageSize 100 "default page size"
              Expect.equal defaults.MaxPages 20 "default max pages"

          // DD-043: dropd shall store a configurable maximum sync runtime,
          // defaulting to 15 minutes.
          testCase "DD-043 max sync runtime defaults to 15 minutes"
          <| fun _ -> Expect.equal defaults.MaxSyncRuntimeMinutes 15 "default max runtime"

          // DD-044: dropd shall store a configurable API error-rate abort threshold,
          // defaulting to 30 percent failed API requests within a sync.
          testCase "DD-044 error rate abort threshold defaults to 30 percent"
          <| fun _ -> Expect.equal defaults.ErrorRateAbortPercent 30 "default error rate"

          testCase "config validation reports missing credentials in defaults"
          <| fun _ ->
              match validate defaults with
              | Ok _ -> failtest "defaults should require credentials before use"
              | Error errors ->
                  Expect.isTrue
                      (errors |> List.contains (MissingCredential "AppleMusicDeveloperToken"))
                      "developer token should be required"

                  Expect.isTrue
                      (errors |> List.contains (MissingCredential "AppleMusicUserToken"))
                      "user token should be required"

                  Expect.isTrue
                      (errors |> List.contains (MissingCredential "LastFmApiKey"))
                      "Last.fm api key should be required"

          testCase "config validation accepts a fully valid config"
          <| fun _ ->
              let validConfig =
                  { defaults with
                      AppleMusicDeveloperToken = AppleMusicDeveloperToken "dev-token"
                      AppleMusicUserToken = AppleMusicUserToken "user-token"
                      LastFmApiKey = LastFmApiKey "api-key" }

              match validate validConfig with
              | Ok _ -> ()
              | Error errors -> failtestf "expected valid config, got errors: %A" errors

          testCase "config validation rejects invalid numeric ranges"
          <| fun _ ->
              let invalidConfig =
                  { defaults with
                      SimilarArtistMaxPercent = 101
                      PageSize = 0
                      ErrorRateAbortPercent = -1
                      AppleMusicDeveloperToken = AppleMusicDeveloperToken "dev-token"
                      AppleMusicUserToken = AppleMusicUserToken "user-token"
                      LastFmApiKey = LastFmApiKey "api-key" }

              match validate invalidConfig with
              | Ok _ -> failtest "expected range validation errors"
              | Error errors ->
                  Expect.isTrue
                      (errors |> List.contains (PercentOutOfRange("SimilarArtistMaxPercent", 101)))
                      "similar artist percent should be bounded"

                  Expect.isTrue
                      (errors |> List.contains (NonPositiveValue("PageSize", 0)))
                      "page size should be positive"

                  Expect.isTrue
                      (errors |> List.contains (PercentOutOfRange("ErrorRateAbortPercent", -1)))
                      "error rate abort percent should be bounded"

          ]
