module Dropd.Tests.LabelDiscoveryTests

open Expecto

[<Tests>]
let tests =
    testList
        "Label Discovery"
        [

          // DD-004: dropd shall store a user-configured list of record label names.
          // Setup: Create SyncConfig with LabelNames = ["Ninja Tune"; "Warp Records"].
          // Assert: config.LabelNames.Length = 2.
          ptestCase "DD-004 stores configured label names" <| fun _ -> ()

          // DD-005: When dropd performs a sync, dropd shall resolve each configured
          // label name to an Apple Music catalog record label identifier.
          // Setup: Route GET /v1/catalog/us/search?types=record-labels&term=Ninja+Tune
          //        → canned search result with record label ID.
          // Assert: Requests contain the search call; resolved label ID is used.
          ptestCase "DD-005 resolves label names to catalog IDs" <| fun _ -> ()

          // DD-006: When dropd has resolved a record label identifier, dropd shall
          // retrieve the latest releases for that label from the Apple Music catalog.
          // Setup: Route GET /v1/catalog/us/record-labels/{id}/latest-releases → canned releases.
          // Assert: Requests contain the latest-releases call.
          ptestCase "DD-006 retrieves latest releases for resolved labels" <| fun _ -> ()

          // DD-007: If a configured label name cannot be resolved to an Apple Music
          // catalog identifier, then dropd shall log a warning identifying the unresolved label.
          // Setup: Route search for "FakeLabel" → empty results.
          // Assert: Logs contain entry with Code = UnknownLabel mentioning "FakeLabel".
          ptestCase "DD-007 logs warning for unresolved label" <| fun _ -> ()

          // DD-008: If a configured label name cannot be resolved to an Apple Music
          // catalog identifier, then dropd shall continue processing remaining configured labels.
          // Setup: Route "FakeLabel" → empty, "Ninja Tune" → valid result.
          // Assert: Requests show continued processing for "Ninja Tune".
          ptestCase "DD-008 continues processing after unresolved label" <| fun _ -> ()

          ]
