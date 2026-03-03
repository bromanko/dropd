module Dropd.Tests.NormalizationUnitTests

open System
open Expecto
open Dropd.Core.Types
open Dropd.Core.Normalization

[<Tests>]
let tests =
    testList
        "Unit.Normalization"
        [
          testCase "normalizeText trims, lowercases, and collapses whitespace"
          <| fun _ -> Expect.equal (normalizeText " Electronic  House ") "electronic house" "normalized value"

          testCase "normalizeText collapses repeated internal whitespace"
          <| fun _ -> Expect.equal (normalizeText "A   B") "a b" "collapsed spaces"

          testCase "isWithinLookback includes a date inside lookback window"
          <| fun _ ->
              let today = DateOnly(2026, 3, 1)
              let releaseDate = Some(DateOnly(2026, 2, 20))
              Expect.isTrue (isWithinLookback today 30 releaseDate) "release should be included"

          testCase "isWithinLookback excludes a date outside lookback window"
          <| fun _ ->
              let today = DateOnly(2026, 3, 1)
              let releaseDate = Some(DateOnly(2025, 12, 15))
              Expect.isFalse (isWithinLookback today 30 releaseDate) "release should be excluded"

          testCase "isWithinLookback excludes missing dates"
          <| fun _ ->
              let today = DateOnly(2026, 3, 1)
              Expect.isFalse (isWithinLookback today 30 None) "missing release dates should be excluded"

          testCase "dedupByArtistId removes duplicate artist IDs"
          <| fun _ ->
              let radiohead = { Id = CatalogArtistId "657515"; Name = "Radiohead" }
              let bonobo = { Id = CatalogArtistId "5765078"; Name = "Bonobo" }

              let deduped = dedupByArtistId [ radiohead; radiohead; bonobo ]
              Expect.equal deduped.Length 2 "duplicate artist should be removed"

          testCase "dedupByReleaseId removes duplicate release IDs"
          <| fun _ ->
              let releaseA =
                  { Id = CatalogAlbumId "9001"
                    Name = "Future Echoes"
                    ArtistName = "Bonobo"
                    GenreNames = [ "Electronic" ]
                    ReleaseDate = Some(DateOnly(2026, 2, 20))
                    TrackIds = [ CatalogTrackId "track-9001-a" ] }

              let releaseB =
                  { releaseA with
                      Id = CatalogAlbumId "9002"
                      Name = "Other" }

              let deduped = dedupByReleaseId [ releaseA; releaseA; releaseB ]
              Expect.equal deduped.Length 2 "duplicate release should be removed"

          testCase "dedupByTrackId removes duplicate track IDs"
          <| fun _ ->
              let deduped = dedupByTrackId [ CatalogTrackId "t1"; CatalogTrackId "t1"; CatalogTrackId "t2" ]
              Expect.equal deduped [ CatalogTrackId "t1"; CatalogTrackId "t2" ] "track IDs should be unique"

          // Finding 18: normalizeText must split on tabs and newlines, not only spaces.
          testCase "normalizeText collapses tabs and newlines"
          <| fun _ ->
              Expect.equal (normalizeText "A\tB\nC") "a b c" "tabs and newlines collapsed"

          testCase "normalizeText collapses mixed whitespace"
          <| fun _ ->
              Expect.equal (normalizeText "A\t\r\nB") "a b" "mixed whitespace collapsed to single space"

          // Finding 19: normalizeText must handle null input safely.
          testCase "normalizeText returns empty string for null input"
          <| fun _ ->
              Expect.equal (normalizeText null) "" "null treated as empty"

          // Finding 20: isWithinLookback boundary dates must be inclusive.
          testCase "isWithinLookback includes the cutoff boundary date"
          <| fun _ ->
              let today = DateOnly(2026, 3, 1)
              // 30 days back from 2026-03-01 is 2026-01-30.
              let cutoff = DateOnly(2026, 1, 30)
              Expect.isTrue (isWithinLookback today 30 (Some cutoff)) "cutoff date should be included"

          testCase "isWithinLookback includes today"
          <| fun _ ->
              let today = DateOnly(2026, 3, 1)
              Expect.isTrue (isWithinLookback today 30 (Some today)) "today should be included"

          testCase "isWithinLookback excludes the day before cutoff"
          <| fun _ ->
              let today = DateOnly(2026, 3, 1)
              let beforeCutoff = DateOnly(2026, 1, 29)
              Expect.isFalse (isWithinLookback today 30 (Some beforeCutoff)) "day before cutoff should be excluded"
        ]
