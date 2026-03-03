module Dropd.Tests.ArtistSeedingTests

open Expecto

[<Tests>]
let tests =
    testList
        "Artist Seeding"
        [

          // DD-001: When dropd performs a sync, dropd shall retrieve the list of
          // artists from the user's Apple Music library.
          // Setup: Route GET /v1/me/library/artists → canned artist list.
          // Assert: ObservedOutput.Requests contains GET /v1/me/library/artists.
          ptestCase "DD-001 retrieves library artists during sync" <| fun _ -> ()

          // DD-002: When dropd performs a sync, dropd shall retrieve the list of
          // favorited artists from the user's Apple Music library.
          // Setup: Route GET /v1/me/ratings/artists → canned favorites list.
          // Assert: ObservedOutput.Requests contains GET /v1/me/ratings/artists.
          ptestCase "DD-002 retrieves favorited artists during sync" <| fun _ -> ()

          // DD-003: When dropd identifies library artists and favorited artists,
          // dropd shall merge them into a deduplicated seed artist list.
          // Setup: Route library artists with artist A, favorites with artist A.
          // Assert: Seed list contains artist A exactly once.
          ptestCase "DD-003 deduplicates library and favorited artists" <| fun _ -> ()

          ]
