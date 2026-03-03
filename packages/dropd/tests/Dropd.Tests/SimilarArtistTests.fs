module Dropd.Tests.SimilarArtistTests

open Expecto

[<Tests>]
let tests =
    testList
        "Similar Artist Discovery"
        [

          // DD-009: When dropd has a seed artist list, dropd shall query a similar-artist
          // data source for artists similar to each seed artist.
          // Setup: Seed list with artist "Bonobo". Route Last.fm artist.getSimilar for Bonobo.
          // Assert: Requests contain Last.fm call for Bonobo.
          ptestCase "DD-009 queries similar artists for each seed artist" <| fun _ -> ()

          // DD-010: When the similar-artist data source returns similar artists, dropd shall
          // filter out artists that are already in the seed artist list.
          // Setup: Seed list has "Bonobo". Last.fm returns "Bonobo" as similar to another seed.
          // Assert: "Bonobo" does not appear in the discovery candidate list.
          ptestCase "DD-010 filters out seed artists from similar results" <| fun _ -> ()

          // DD-011: When dropd identifies similar artists, dropd shall resolve each to
          // an Apple Music catalog artist identifier, preferring shared identifiers (MBID)
          // and falling back to normalized name matching.
          // Setup: Similar artist with MBID → route Apple Music search with MBID lookup.
          // Assert: Resolved artist has correct CatalogArtistId.
          ptestCase "DD-011 resolves similar artists to Apple Music catalog IDs" <| fun _ -> ()

          // DD-012: When dropd performs fallback name matching for artist resolution,
          // dropd shall normalize names by lowercasing, trimming whitespace, and
          // collapsing repeated internal whitespace.
          // Setup: Similar artist " The  Black  Keys " vs catalog "the black keys".
          // Assert: Names are considered equivalent after normalization.
          ptestCase "DD-012 normalizes artist names for fallback matching" <| fun _ -> ()

          // DD-013: When dropd resolves similar artists to Apple Music catalog artist
          // identifiers, dropd shall deduplicate the resolved artist set by catalog
          // artist identifier before release retrieval.
          // Setup: Two seed artists both return "Artist X" as similar, resolving to same ID.
          // Assert: Release query for "Artist X" is made exactly once.
          ptestCase "DD-013 deduplicates resolved similar artists by catalog ID" <| fun _ -> ()

          // DD-014: If a similar artist cannot be resolved to an Apple Music catalog
          // artist identifier, then dropd shall skip that artist.
          // Setup: Similar artist "NoMatch" → Apple Music search returns empty.
          //        Last.fm returns HTTP 200 with {"error":6,...} for nonexistent artist lookups.
          // Assert: "NoMatch" is not in the resolved set.
          ptestCase "DD-014 skips unresolvable similar artists" <| fun _ -> ()

          // DD-015: If a similar artist cannot be resolved, dropd shall continue
          // processing remaining similar artists.
          // Setup: "NoMatch" → empty, "RealArtist" → valid result.
          // Assert: "RealArtist" is in the resolved set.
          ptestCase "DD-015 continues after unresolvable similar artist" <| fun _ -> ()

          // DD-016: If the similar-artist data source is unavailable, then dropd shall
          // log an error.
          // Setup: Route Last.fm → Sequence [503 for all calls].
          // Assert: Logs contain entry with Code = SimilarArtistServiceUnavailable.
          ptestCase "DD-016 logs error when similar-artist source unavailable" <| fun _ -> ()

          // DD-017: If the similar-artist data source is unavailable, then dropd shall
          // continue the sync using only seed artists and label-based discovery.
          // Setup: Route Last.fm → 503 for all calls. Route Apple Music with valid data.
          // Assert: SyncOutcome is Success or PartialFailure (not Aborted).
          //         Playlists are updated from seed/label data.
          ptestCase "DD-017 continues sync without similar artists when source unavailable"
          <| fun _ -> ()

          // DD-018: dropd shall access similar-artist data through an abstracted interface
          // that is independent of any specific third-party provider.
          // Setup: Verify that the similar-artist query is dispatched through an interface/function
          //        type, not directly calling Last.fm HTTP endpoints.
          // Assert: The SyncConfig or harness setup can substitute a different provider without
          //         changing calling code.
          ptestCase "DD-018 similar-artist access is provider-independent" <| fun _ -> ()

          // DD-019: dropd shall limit tracks from similar artists to a configurable
          // maximum percentage of each playlist's total tracks.
          // Setup: SimilarArtistMaxPercent = 20. Playlist has 10 total tracks.
          //        Similar artists would contribute 5 tracks.
          // Assert: At most 2 tracks from similar artists in the final playlist.
          ptestCase "DD-019 limits similar-artist tracks to configured percentage" <| fun _ -> ()

          ]
