module Dropd.Tests.HarnessTests

open Expecto
open Dropd.Core.Types
open Dropd.Core.Config
open Dropd.Tests.TestHarness

let private ok body =
    { StatusCode = 200
      Body = body
      Headers = []
      DelayMs = None }

let private withStatus code body =
    { StatusCode = code
      Body = body
      Headers = []
      DelayMs = None }

let private emptySetup = { Routes = Map.empty }

let private setupWith routes =
    { Routes = routes |> Map.ofList }

[<Tests>]
let tests =
    testList
        "Harness"
        [

          testCase "Sequence [401; 200] serves 401 then 200 then repeats last"
          <| fun _ ->
              let script =
                  Sequence [ withStatus 401 "unauthorized"; ok "success" ]

              let state = { Index = 0 }
              let r1 = serveResponse script state
              Expect.equal r1.StatusCode 401 "first call should be 401"
              let r2 = serveResponse script state
              Expect.equal r2.StatusCode 200 "second call should be 200"
              let r3 = serveResponse script state
              Expect.equal r3.StatusCode 200 "third call should repeat last (200)"

          testCase "Always script returns same response every time"
          <| fun _ ->
              let script = Always(ok "always")
              let state = { Index = 0 }
              let r1 = serveResponse script state
              let r2 = serveResponse script state
              Expect.equal r1.StatusCode 200 "first"
              Expect.equal r2.StatusCode 200 "second"
              Expect.equal r1.Body "always" "body unchanged"

          testCase "findRoute matches service/method/path"
          <| fun _ ->
              let key =
                  { Service = "apple"
                    Method = "GET"
                    Path = "/v1/me/library/artists"
                    QueryMatch = [] }

              let setup = setupWith [ key, Always(ok "artists") ]

              let result = findRoute setup "apple" "GET" "/v1/me/library/artists" []
              Expect.isSome result "should match route"

          testCase "findRoute returns None for missing route"
          <| fun _ ->
              let result = findRoute emptySetup "apple" "GET" "/v1/me/library/artists" []
              Expect.isNone result "should return None"

          testCase "findRoute with QueryMatch matches when required params present"
          <| fun _ ->
              let key =
                  { Service = "apple"
                    Method = "GET"
                    Path = "/v1/catalog/us/search"
                    QueryMatch = [ ("types", "record-labels") ] }

              let setup = setupWith [ key, Always(ok "labels") ]

              let result =
                  findRoute setup "apple" "GET" "/v1/catalog/us/search" [ ("types", "record-labels"); ("limit", "1") ]

              Expect.isSome result "should match with extra query params"

          testCase "findRoute with QueryMatch does not match different param value"
          <| fun _ ->
              let key =
                  { Service = "apple"
                    Method = "GET"
                    Path = "/v1/catalog/us/search"
                    QueryMatch = [ ("types", "record-labels") ] }

              let setup = setupWith [ key, Always(ok "labels") ]

              let result =
                  findRoute setup "apple" "GET" "/v1/catalog/us/search" [ ("types", "artists"); ("limit", "1") ]

              Expect.isNone result "should not match different query value"

          testCase "findRoute with empty QueryMatch matches any query"
          <| fun _ ->
              let key =
                  { Service = "apple"
                    Method = "GET"
                    Path = "/v1/me/library/artists"
                    QueryMatch = [] }

              let setup = setupWith [ key, Always(ok "any") ]

              let result =
                  findRoute setup "apple" "GET" "/v1/me/library/artists" [ ("limit", "100"); ("offset", "0") ]

              Expect.isSome result "empty QueryMatch matches any query"

          testCase "Last.fm error scenarios use HTTP 200 with error payloads"
          <| fun _ ->
              // Last.fm returns 200 for ALL responses, even errors.
              // Auth failure: {"error":10,"message":"Invalid API key ..."}
              let authErrorKey =
                  { Service = "lastfm"
                    Method = "GET"
                    Path = "/2.0"
                    QueryMatch = [ ("method", "artist.getSimilar"); ("artist", "test") ] }

              let authErrorBody =
                  """{"error":10,"message":"Invalid API key - You must be granted a valid key by last.fm"}"""

              // Missing artist: {"error":6,"message":"The artist you supplied could not be found"}
              let missingArtistKey =
                  { Service = "lastfm"
                    Method = "GET"
                    Path = "/2.0"
                    QueryMatch = [ ("method", "artist.getSimilar"); ("artist", "nonexistent") ] }

              let missingArtistBody =
                  """{"error":6,"message":"The artist you supplied could not be found"}"""

              let setup =
                  setupWith
                      [ authErrorKey, Always(ok authErrorBody)
                        missingArtistKey, Always(ok missingArtistBody) ]

              // Verify auth error is served as 200
              let authResult =
                  findRoute setup "lastfm" "GET" "/2.0" [ ("method", "artist.getSimilar"); ("artist", "test") ]

              Expect.isSome authResult "auth error route should match"

              let authResp = serveResponse authResult.Value { Index = 0 }
              Expect.equal authResp.StatusCode 200 "Last.fm auth error should be HTTP 200"
              Expect.stringContains authResp.Body "\"error\":10" "should contain error code 10"

              // Verify missing artist is served as 200
              let missingResult =
                  findRoute
                      setup
                      "lastfm"
                      "GET"
                      "/2.0"
                      [ ("method", "artist.getSimilar")
                        ("artist", "nonexistent") ]

              Expect.isSome missingResult "missing artist route should match"

              let missingResp = serveResponse missingResult.Value { Index = 0 }
              Expect.equal missingResp.StatusCode 200 "Last.fm missing artist should be HTTP 200"
              Expect.stringContains missingResp.Body "\"error\":6" "should contain error code 6"

          testCase "Request recorder captures method/path/query/body"
          <| fun _ ->
              // RecordedRequest is a plain record — verify construction and field access.
              let req =
                  { Service = "apple"
                    Method = "POST"
                    Path = "/v1/me/library/playlists"
                    Query = [ ("locale", "en-US") ]
                    Headers = [ ("Authorization", "Bearer token123") ]
                    Body = Some """{"attributes":{"name":"Electronic"}}""" }

              Expect.equal req.Method "POST" "method"
              Expect.equal req.Path "/v1/me/library/playlists" "path"
              Expect.equal req.Query [ ("locale", "en-US") ] "query"
              Expect.equal req.Body (Some """{"attributes":{"name":"Electronic"}}""") "body"
              Expect.equal req.Service "apple" "service"

          testCase "runSync returns a non-throwing baseline output"
          <| fun _ ->
              let output = runSync defaults emptySetup
              Expect.equal output.Requests [] "baseline run should not emit requests"
              Expect.equal output.Logs [] "baseline run should not emit logs"
              Expect.equal output.Outcome (Some Success) "baseline run should complete successfully"

          ]
