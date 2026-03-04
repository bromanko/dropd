namespace Dropd.Tests

open System.IO
open Dropd.Core.Types
open Dropd.Core.Config
open Dropd.Tests.TestHarness

module TestData =

    let validConfig : SyncConfig =
        { defaults with
            AppleMusicDeveloperToken = AppleMusicDeveloperToken "dev-token"
            AppleMusicUserToken = AppleMusicUserToken "user-token"
            LastFmApiKey = LastFmApiKey "lastfm-key"
            LabelNames = [ "Ninja Tune" ]
            Playlists =
                [ { Name = "Electronic Drops"
                    GenreCriteria = [ "electronic" ] }
                  { Name = "Dance Floor"
                    GenreCriteria = [ "dance" ] } ] }

    let private fixturePath name =
        Path.Combine("packages", "dropd", "tests", "Dropd.Tests", "Fixtures", "apple", name)

    let fixture name = File.ReadAllText(fixturePath name)

    let okFixture name : CannedResponse =
        { StatusCode = 200
          Body = fixture name
          Headers = []
          DelayMs = None }

    let private lastFmFixturePath name =
        Path.Combine("packages", "dropd", "tests", "Dropd.Tests", "Fixtures", "lastfm", name)

    let lastFmFixture name = File.ReadAllText(lastFmFixturePath name)

    let okLastFmFixture name : CannedResponse =
        { StatusCode = 200
          Body = lastFmFixture name
          Headers = []
          DelayMs = None }

    let withStatus code body : CannedResponse =
        { StatusCode = code
          Body = body
          Headers = []
          DelayMs = None }

    let setupWith routes : FakeApiSetup =
        { Routes = routes |> Map.ofList }

    let route service method_ path query script =
        { Service = service
          Method = method_
          Path = path
          QueryMatch = query },
        script
