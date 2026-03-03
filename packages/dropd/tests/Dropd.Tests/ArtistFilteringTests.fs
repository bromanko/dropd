module Dropd.Tests.ArtistFilteringTests

open Expecto

[<Tests>]
let tests =
    testList
        "Artist Filtering"
        [

          // DD-020: When dropd performs a sync, dropd shall retrieve the user's personal
          // ratings for songs and albums from the Apple Music API.
          // Setup: Route GET /v1/me/ratings/songs → canned ratings.
          //        Route GET /v1/me/ratings/albums → canned ratings.
          // Assert: Requests contain both ratings endpoints.
          ptestCase "DD-020 retrieves user ratings for songs and albums" <| fun _ -> ()

          // DD-021: When dropd finds a song or album with a dislike rating (value of -1),
          // dropd shall exclude the primary artist of that song or album from all playlist
          // population for the current sync.
          // Setup: Rating with value -1 for a song by "BadArtist". Seed list includes "BadArtist".
          // Assert: No tracks from "BadArtist" appear in any playlist output.
          ptestCase "DD-021 excludes artists with dislike ratings from playlists" <| fun _ -> ()

          // DD-022: When dropd excludes an artist due to dislike ratings, dropd shall log
          // the excluded artist name.
          // Setup: Same as DD-021.
          // Assert: Logs contain entry identifying "BadArtist" as excluded.
          ptestCase "DD-022 logs excluded artist names" <| fun _ -> ()

          ]
