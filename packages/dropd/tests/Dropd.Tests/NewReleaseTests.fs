module Dropd.Tests.NewReleaseTests

open Expecto

[<Tests>]
let tests =
    testList
        "New Release Detection"
        [

          // DD-023: When dropd has a combined list of seed artists, similar artists, and
          // label-sourced artists, dropd shall query the Apple Music catalog for new releases
          // from each artist.
          // Setup: Route GET /v1/catalog/us/artists/{id}/albums for seed, similar, and label artists.
          // Assert: Requests contain album queries for all three source types.
          ptestCase "DD-023 queries new releases from all artist sources" <| fun _ -> ()

          // DD-024: When dropd queries for new releases, dropd shall retrieve releases
          // sorted by release date in descending order.
          // Setup: Route artist albums endpoint → canned albums in mixed order.
          //        Use query param sort=-releaseDate.
          // Assert: Requests include sort=-releaseDate parameter.
          //         Returned releases are ordered newest-first.
          ptestCase "DD-024 retrieves releases sorted by release date descending" <| fun _ -> ()

          // DD-025: When dropd retrieves new releases, dropd shall include only releases
          // with a release date within a configurable lookback period.
          // Setup: LookbackDays = 30. Route albums with one 10-day-old and one 60-day-old release.
          // Assert: Only the 10-day-old release is included.
          ptestCase "DD-025 includes only releases within lookback period" <| fun _ -> ()

          // DD-026: When dropd queries for new releases, dropd shall include releases
          // where the artist appears as a featured or collaborating artist.
          // Setup: Route artist albums → include a collaboration release.
          // Assert: Collaboration release appears in the candidate set.
          ptestCase "DD-026 includes collaboration releases" <| fun _ -> ()

          // DD-027: When dropd aggregates release candidates from multiple discovery sources,
          // dropd shall deduplicate releases by Apple Music catalog release identifier.
          // Setup: Same album ID returned for seed artist and label discovery paths.
          // Assert: Release is processed exactly once (single genre classification pass).
          ptestCase "DD-027 deduplicates releases by catalog ID" <| fun _ -> ()

          // DD-028: When dropd builds the track-add set for a playlist update, dropd shall
          // deduplicate candidate tracks by Apple Music catalog track identifier.
          // Setup: Same track ID referenced from two different albums.
          // Assert: Track appears at most once in the add set.
          ptestCase "DD-028 deduplicates tracks by catalog track ID" <| fun _ -> ()

          // DD-029: If the Apple Music catalog is unavailable when querying for new releases,
          // then dropd shall log an error.
          // Setup: Route Apple Music artist albums → 503 for all calls.
          // Assert: Logs contain error entry for catalog unavailability.
          ptestCase "DD-029 logs error when Apple Music catalog unavailable" <| fun _ -> ()

          // DD-030: If the Apple Music catalog is unavailable when querying for new releases,
          // then dropd shall abort the current sync.
          // Setup: Route Apple Music → 503 for all calls.
          // Assert: ObservedOutput.Outcome = Aborted.
          ptestCase "DD-030 aborts sync when Apple Music catalog unavailable" <| fun _ -> ()

          ]
