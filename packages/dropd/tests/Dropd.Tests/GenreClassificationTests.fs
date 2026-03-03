module Dropd.Tests.GenreClassificationTests

open Expecto

[<Tests>]
let tests =
    testList
        "Genre Classification"
        [

          // DD-031: When dropd retrieves a new release, dropd shall read the genre metadata
          // from the Apple Music catalog entry for that release.
          // Setup: Route album detail → canned response with genreNames = ["Electronic", "Dance"].
          // Assert: Extracted genre list matches ["Electronic"; "Dance"].
          ptestCase "DD-031 reads genre metadata from catalog entries" <| fun _ -> ()

          // DD-032: When dropd has genre metadata for a release, dropd shall match the release
          // to configured genre playlists using normalized exact matching.
          // Setup: Playlist with GenreCriteria = ["electronic"]. Release has genreNames = [" Electronic "].
          // Assert: Release matches the playlist after normalization.
          ptestCase "DD-032 matches genres with normalized exact matching" <| fun _ -> ()

          // DD-033: When a release matches genre criteria for multiple configured playlists,
          // dropd shall include tracks from that release in each matching playlist.
          // Setup: Two playlists both matching "Electronic". Release tagged "Electronic".
          // Assert: Tracks appear in both playlists.
          ptestCase "DD-033 adds tracks to multiple matching playlists" <| fun _ -> ()

          // DD-034: If a release has no genre metadata in the Apple Music catalog, then
          // dropd shall log a warning identifying the release.
          // Setup: Route album detail → genreNames = [].
          // Assert: Logs contain warning identifying the release.
          ptestCase "DD-034 logs warning for releases without genre metadata" <| fun _ -> ()

          // DD-035: If a release has no genre metadata in the Apple Music catalog, then
          // dropd shall exclude that release from genre-based playlist assignment.
          // Setup: Route album detail → genreNames = [].
          // Assert: Release does not appear in any playlist.
          ptestCase "DD-035 excludes releases without genre metadata from playlists" <| fun _ -> ()

          ]
