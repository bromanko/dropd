module Dropd.Tests.AuthenticationTests

open System.IO
open Expecto
open Dropd.Core.Types
open Dropd.Core.Config
open Dropd.Tests.TestHarness
open Dropd.Tests.TestData

let private authConfig =
    { validConfig with
        LabelNames = []
        Playlists = [] }

let private happyAuthSetup =
    setupWith
        [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
          route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515,5765078" ] (Always(okFixture "favorited-artists.json"))
          route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
          route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-5765078.json")) ]

[<Tests>]
let tests =
    testList
        "Authentication"
        [
          testCase "DD-060 authenticates with developer and user tokens"
          <| fun _ ->
              let output = runSync authConfig happyAuthSetup

              let appleRequests = output.Requests |> List.filter (fun req -> req.Service = "apple")

              // Finding 5: guard that the filter actually matched requests.
              Expect.isNonEmpty appleRequests "should have recorded Apple Music requests"

              // Finding 16: auth header values are redacted in the observable log;
              // verify the header keys are present (token correctness is covered by
              // SyncEngineUnitTests which inspect raw ApiRequest headers).
              Expect.isTrue
                  (appleRequests
                   |> List.forall (fun req -> req.Headers |> List.exists (fun (name, _) -> name = "Authorization")))
                  "all Apple requests should include Authorization header"

              Expect.isTrue
                  (appleRequests
                   |> List.filter (fun req -> req.Path.StartsWith "/v1/me")
                   |> List.forall (fun req -> req.Headers |> List.exists (fun (name, _) -> name = "Music-User-Token")))
                  "/v1/me requests should include Music-User-Token"

          testCase "DD-061 authenticates with Last.fm API key"
          <| fun _ ->
              // Setup: route a Last.fm endpoint so we can see the request.
              let lastFmRoute =
                  route "lastfm" "GET" "/2.0" [] (Always(okLastFmFixture "similar-bonobo.json"))

              let artistSearchRoutes =
                  [ route "apple" "GET" "/v1/catalog/us/search" [ "term", "Burial"; "types", "artists" ] (Always(okFixture "artist-search-burial.json"))
                    route "apple" "GET" "/v1/catalog/us/search" [ "term", "The  Black  Keys"; "types", "artists" ] (Always(okFixture "artist-search-black-keys.json"))
                    route "apple" "GET" "/v1/catalog/us/search" [ "term", "NoMatch"; "types", "artists" ] (Always(okFixture "artist-search-empty.json")) ]

              let setup =
                  setupWith
                      ([ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                         route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515,5765078" ] (Always(okFixture "favorited-artists.json"))
                         route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
                         route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-5765078.json"))
                         lastFmRoute ]
                       @ artistSearchRoutes)

              let output = runSync authConfig setup

              let lastFmRequests = output.Requests |> List.filter (fun req -> req.Service = "lastfm")
              Expect.isNonEmpty lastFmRequests "should have made Last.fm requests"

              // The api_key query parameter is redacted in the observable log to
              // prevent persisting secrets. Verify it is present but redacted.
              let hasRedactedApiKey =
                  lastFmRequests
                  |> List.forall (fun req -> req.Query |> List.exists (fun (k, v) -> k = "api_key" && v = "[redacted]"))

              Expect.isTrue hasRedactedApiKey "all Last.fm requests should include api_key (redacted in observable log)"

          testCase "DD-062 credentials stored outside source code"
          <| fun _ ->
              let defaultsConfig = defaults
              let hasPlaceholderTokens =
                  (match defaultsConfig.AppleMusicDeveloperToken with
                   | Dropd.Core.Types.AppleMusicDeveloperToken token -> token = "")
                  && (match defaultsConfig.AppleMusicUserToken with
                      | Dropd.Core.Types.AppleMusicUserToken token -> token = "")
                  && (match defaultsConfig.LastFmApiKey with
                      | Dropd.Core.Types.LastFmApiKey token -> token = "")

              Expect.isTrue hasPlaceholderTokens "defaults should only contain empty placeholders"

              let coreFiles = Directory.GetFiles("packages/dropd/src/Dropd.Core", "*.fs", SearchOption.AllDirectories)

              let hasHardcodedCredential =
                  coreFiles
                  |> Array.exists (fun path ->
                      let text = File.ReadAllText(path)
                      text.Contains("Bearer ey")
                      || text.Contains("music-user-token")
                      || text.Contains("lastfm_api_key"))

              Expect.isFalse hasHardcodedCredential "source should not contain hardcoded credential literals"

          ptestCase "DD-063 generates valid ES256 developer tokens" <| fun _ -> ()

          testCase "DD-064 includes Authorization header in Apple Music requests"
          <| fun _ ->
              let output = runSync authConfig happyAuthSetup
              let appleRequests = output.Requests |> List.filter (fun req -> req.Service = "apple")

              // Finding 5: ensure the filter matched at least one request.
              Expect.isNonEmpty appleRequests "should have recorded Apple Music requests"

              // Finding 16: auth values are redacted; verify header key presence.
              Expect.isTrue
                  (appleRequests
                   |> List.forall (fun req -> req.Headers |> List.exists (fun (name, _) -> name = "Authorization")))
                  "every Apple request should include Authorization header"

          testCase "DD-065 includes Music-User-Token in /v1/me requests"
          <| fun _ ->
              let output = runSync authConfig happyAuthSetup

              let meRequests =
                  output.Requests
                  |> List.filter (fun req -> req.Service = "apple" && req.Path.StartsWith "/v1/me")

              // Finding 5: ensure the filter matched at least one /v1/me request.
              Expect.isNonEmpty meRequests "should have recorded /v1/me requests"

              // Finding 16: auth values are redacted; verify header key presence.
              Expect.isTrue
                  (meRequests
                   |> List.forall (fun req -> req.Headers |> List.exists (fun (name, _) -> name = "Music-User-Token")))
                  "all personalized endpoints should include Music-User-Token"

          testCase "DD-066 logs error on Apple Music auth failure"
          <| fun _ ->
              let setup =
                  setupWith [ route "apple" "GET" "/v1/me/library/artists" [] (Always(withStatus 401 (fixture "error-401.json"))) ]

              let output = runSync authConfig setup

              Expect.isTrue
                  (output.Logs |> List.exists (fun log -> log.Code = AppleMusicAuthFailure))
                  "401 should emit AppleMusicAuthFailure log"

          testCase "DD-067 aborts sync on Apple Music auth failure"
          <| fun _ ->
              let setup =
                  setupWith [ route "apple" "GET" "/v1/me/library/artists" [] (Always(withStatus 401 (fixture "error-401.json"))) ]

              let output = runSync authConfig setup
              Expect.equal output.Outcome (Some(Aborted "AuthFailure")) "401 should abort sync"

          testCase "DD-068 logs error on Last.fm auth failure"
          <| fun _ ->
              let lastFmInvalidKeyRoute =
                  route "lastfm" "GET" "/2.0" [] (Always(okLastFmFixture "similar-invalid-key.json"))

              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515,5765078" ] (Always(okFixture "favorited-artists.json"))
                        route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
                        route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-5765078.json"))
                        lastFmInvalidKeyRoute ]

              let output = runSync authConfig setup

              Expect.isTrue
                  (output.Logs |> List.exists (fun log -> log.Code = LastFmAuthFailure))
                  "invalid API key should emit LastFmAuthFailure log"

          testCase "DD-069 continues sync without similar artists on Last.fm auth failure"
          <| fun _ ->
              let lastFmInvalidKeyRoute =
                  route "lastfm" "GET" "/2.0" [] (Always(okLastFmFixture "similar-invalid-key.json"))

              let cfg =
                  { authConfig with
                      Playlists = [] }

              let setup =
                  setupWith
                      [ route "apple" "GET" "/v1/me/library/artists" [] (Always(okFixture "library-artists.json"))
                        route "apple" "GET" "/v1/me/ratings/artists" [ "ids", "657515,5765078" ] (Always(okFixture "favorited-artists.json"))
                        route "apple" "GET" "/v1/catalog/us/artists/657515/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-657515.json"))
                        route "apple" "GET" "/v1/catalog/us/artists/5765078/albums" [ "sort", "-releaseDate" ] (Always(okFixture "artist-albums-5765078.json"))
                        lastFmInvalidKeyRoute ]

              let output = runSync cfg setup

              // Sync should not abort — it should continue with seed data only.
              match output.Outcome with
              | Some (Dropd.Core.Types.Success) -> ()
              | Some (Dropd.Core.Types.PartialFailure) -> ()
              | other -> failwithf "expected Success or PartialFailure, got %A (sync should not abort on Last.fm auth failure)" other

          ptestCase "DD-070 regenerates token on Apple Music 401" <| fun _ -> ()

          ptestCase "DD-071 retries once on Apple Music 401" <| fun _ -> ()

          ptestCase "DD-072 logs re-authorization needed on 403" <| fun _ -> ()

          ptestCase "DD-073 aborts sync on 403 for personalized endpoints" <| fun _ -> ()
        ]
