# dropd Service Implementation Roadmap and Phase 2 ExecPlan

This ExecPlan is a living document. The sections Progress, Surprises & Discoveries,
Decision Log, and Outcomes & Retrospective must be kept up to date as work proceeds.

## Purpose / Big Picture

Phase 2 extends the Phase 1 sync core so it can discover and use similar artists, apply
artist-level dislike filtering, and handle Last.fm authentication failures without aborting
the entire sync.

After this phase, a developer can run the Dropd test harness and observe a real end-to-end
flow that includes seed artists, label artists, similar artists, dislike-based artist
exclusion, and graceful degradation when Last.fm is unavailable or unauthenticated.

The required acceptance coverage for this phase is:

- DD-009..DD-019 (Similar Artist Discovery)
- DD-020..DD-022 (Artist Filtering)
- DD-061, DD-068, DD-069 (Last.fm authentication behavior)

## Progress

- [x] (2026-03-03 18:31Z) Drafted initial Phase 2 scope and milestone sequence.
- [x] (2026-03-04 16:30Z) Amended plan to resolve review gaps: MBID behavior, fixture schemas, granular TDD steps, DD test bodies, and cap-tracking design.
- [x] (2026-03-03 20:48Z) Milestone 1 complete: Last.fm + Apple Phase 2 fixtures added, provider contracts and harness entrypoint in place, compile order updated, build green at 109 passed / 35 ignored.
- [x] (2026-03-03 20:54Z) Milestone 2 complete: 10 unit tests + 8 DD tests (DD-009..DD-015, DD-018) active and passing. Suite at 128 passed / 27 ignored.
- [x] (2026-03-03 20:55Z) Milestone 3 complete: DD-016, DD-017, DD-061, DD-068, DD-069 activated and passing. Suite at 133 passed / 22 ignored.
- [x] (2026-03-03 20:58Z) Milestone 4 complete: deterministic similar-track cap implemented with 3 unit tests. DD-019 active/passing. Suite at 137 passed / 21 ignored.
- [x] (2026-03-03 21:00Z) Milestone 5 complete: dislike artist filtering with 4 unit tests. DD-020..DD-022 active/passing. Suite at 144 passed / 18 ignored.
- [x] (2026-03-03 21:00Z) Milestone 6 complete: full-suite gate passed. 144 passed, 18 ignored, 0 failed, 0 errored. All 18 remaining ignored tests are out-of-scope DDs.

## Surprises & Discoveries

- Observation: Last.fm API errors are encoded in JSON response bodies and can arrive with HTTP 200.
  Evidence: documented in `docs/research/lastfm-api.md`; existing harness test `Harness.Last.fm error scenarios use HTTP 200 with error payloads` is passing.

- Observation: Expecto `--filter` matches active test names (`testCase`), but pending tests (`ptestCase`) remain ignored even when listed in summary output.
  Evidence: `--filter "Unit.SyncEngine"` returns matching active tests; prior `--filter "DD-001"` behavior occurred when DD test was pending.

- [CLARIFY] If future runs show zero selected tests for active DD names, capture exact command/output and create a small reproducer in `packages/dropd/tests/Dropd.Tests/HarnessTests.fs`.

- Observation: Current baseline before Phase 2 work is green with substantial pending scope.
  Evidence: `nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary` shows 109 passed, 35 ignored, 0 failed, 0 errored.

- Observation: Default Last.fm provider must be created with the recording-wrapped runtime, not the original runtime, or Last.fm requests are not captured in the observable log.
  Evidence: DD-009 initially failed because `runSync` created the provider with the raw runtime before `runSyncWithProvider` wrapped it with recording. Fixed by restructuring to `runSyncInternal` which creates the recording runtime first, then resolves the provider.

- Observation: The `favorited-artists.json` fixture returns artist ID 29525428 which is not in the library artists, creating an unexpected third seed artist. This artifact is benign but must be accounted for in negative-list assertions (DD-014).
  Evidence: DD-014 initially failed due to an album request for `/v1/catalog/us/artists/29525428/albums`.

## Decision Log

- Decision: Phase 2 is scoped to similar-artist discovery, dislike filtering, and Last.fm auth degradation only.
  Rationale: these are the direct feature gaps left intentionally out of Phase 1 and are tightly related in execution flow.
  Date: 2026-03-03

- Decision: Similar-artist integration will be implemented behind a provider interface (`DD-018`) and not by embedding Last.fm endpoint calls directly in orchestration.
  Rationale: provider abstraction is an explicit requirement and reduces coupling for future provider swaps.
  Date: 2026-03-03

- Decision: Last.fm auth/service failures must be non-fatal for sync runs.
  Rationale: DD-017 and DD-069 require continuation with seed+label data when similar-artist discovery fails.
  Date: 2026-03-03

- Decision: DD-063 and DD-070..DD-073 remain out of Phase 2.
  Rationale: those requirements are Apple token lifecycle concerns and are best grouped with API resilience/runtime limits in a later hardening phase.
  Date: 2026-03-03

- Decision: MBID is used for Last.fm query reliability, but Apple Music artist resolution is performed by name search + normalized comparison.
  Rationale: Apple Music catalog search in this repository does not expose MBID lookup fields; DD-011 is satisfied by preferring MBID in upstream similar-artist retrieval and then resolving to Apple IDs via deterministic normalized name matching.
  Date: 2026-03-04

- Decision: Similar-track cap logic will use an explicit `similarArtistIds: Set<CatalogArtistId>` input to planning, not inferred heuristics.
  Rationale: `DiscoveredRelease` currently carries no source tag; explicit IDs are deterministic, testable, and avoid mutating release contracts.
  Date: 2026-03-04

- Decision: DD activation requires replacing `ptestCase` bodies with real assertions before switching to `testCase`.
  Rationale: activating empty `fun _ -> ()` tests gives false confidence and does not verify DD behavior.
  Date: 2026-03-04

## Outcomes & Retrospective

Phase 2 is complete. All acceptance criteria are met:

1. Similar-artist discovery executes via a provider abstraction (`SimilarArtistProvider`) — DD-018 verified by injecting a fake provider.
2. All 17 Phase 2 DD tests are active: DD-009..DD-019, DD-020..DD-022, DD-061, DD-068, DD-069.
3. Last.fm auth failure (`"error":10` in HTTP 200 body) logs `LastFmAuthFailure` and does not abort sync — DD-068, DD-069.
4. Last.fm unavailability (HTTP 503) logs `SimilarArtistServiceUnavailable` and sync continues — DD-016, DD-017.
5. Similar-artist track cap at `SimilarArtistMaxPercent` is deterministic and enforced — DD-019.
6. Ratings endpoints for songs and albums are queried; disliked artists excluded — DD-020, DD-021.
7. Excluded disliked artists are logged by normalized name — DD-022.
8. Full suite: 144 passed, 18 ignored, 0 failed, 0 errored.
9. Remaining 18 ignored tests are DD-063, DD-070..DD-073, DD-076..DD-078, DD-080..DD-089 — all out of scope.

Key implementation decisions:
- `runSyncInternal` pattern ensures recording runtime is shared by both Apple calls and the default Last.fm provider.
- `computePlan` threads explicit `similarArtistIds: Set<CatalogArtistId>` for deterministic cap computation without modifying release contracts.
- Ratings retrieval is non-fatal: if either endpoint fails, filtering is skipped silently.

Test coverage added: 35 new tests (109→144), 17 ignored→active DD conversions.

Date: 2026-03-03

## Context and Orientation

The repository is already on a working Phase 1 baseline. The core sync path is:

- `packages/dropd/tests/Dropd.Tests/TestHarness.fs` -> fake runtime and recorded requests.
- `packages/dropd/src/Dropd.Core/SyncEngine.fs` -> orchestration and API calls.
- `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs` -> playlist planning and apply.

Requirement definitions live in `docs/ears/requirements.md`. Requirement tests live under
`packages/dropd/tests/Dropd.Tests/`.

Current pending requirement tests are concentrated in these files:

- `packages/dropd/tests/Dropd.Tests/SimilarArtistTests.fs` (DD-009..DD-019)
- `packages/dropd/tests/Dropd.Tests/ArtistFilteringTests.fs` (DD-020..DD-022)
- `packages/dropd/tests/Dropd.Tests/AuthenticationTests.fs` (DD-061, DD-068, DD-069; plus later auth DDs)

Current fixture data is Apple-only under:

- `packages/dropd/tests/Dropd.Tests/Fixtures/apple/`

Phase 2 introduces deterministic Last.fm fixtures under:

- `packages/dropd/tests/Dropd.Tests/Fixtures/lastfm/`

Important response shapes used by this plan:

- Last.fm success (`artist.getSimilar`, `format=json`):

      {
        "similarartists": {
          "artist": [
            { "name": "Burial", "mbid": "...", "match": "0.82" },
            { "name": "Bonobo", "mbid": "...", "match": "0.67" }
          ]
        }
      }

- Last.fm auth error in HTTP 200 body:

      { "error": 10, "message": "Invalid API key - You must be granted a valid key by last.fm" }

- Last.fm artist-not-found in HTTP 200 body:

      { "error": 6, "message": "The artist you supplied could not be found" }

- Apple Music artist resolution endpoint used in this plan:

      GET /v1/catalog/us/search?term=<artist-name>&types=artists&limit=5

  Resolution strategy in this phase:
  1. Similar artist retrieved from Last.fm includes `mbid` when available.
  2. Apple catalog resolution still uses name search because MBID lookup is not available in this codebase.
  3. Compare candidate names using `Normalization.normalizeText` for fallback matching.

Current function signatures to preserve during refactor:

- `SyncEngine.runSync : Config.ValidSyncConfig -> ApiContracts.ApiRuntime -> Map<string,string> -> Types.SyncOutcome * ApiContracts.ObservedSync`
- `PlaylistReconcile.reconcilePlaylists : ... -> Async<ApiContracts.ReconcileResult * ApiContracts.LogEntry list>`

## Plan of Work

The phase proceeds in six milestones.

Milestone 1 (steps 1-16) establishes deterministic fixtures, provider contracts, and the extra harness entrypoint needed to test provider substitution. It also records filter behavior so later TDD loops use reliable commands.

Milestone 2 (steps 17-44) implements similar discovery in granular red/green slices: provider request/parsing, seed filtering, artist resolution, deduplication, and full DD test activation with real assertions.

Milestone 3 (steps 45-60) adds non-fatal Last.fm failure behavior and activates auth-related DD tests for Last.fm key and auth-failure continuation.

Milestone 4 (steps 61-70) adds deterministic similar-track capping by threading explicit similar-artist IDs into playlist planning.

Milestone 5 (steps 71-88) adds dislike-based artist exclusion from song/album ratings, including logging and DD activation.

Milestone 6 (steps 89-94) runs full validation, updates this living document, and finalizes commits.

## Milestones and Validation Targets

### Milestone 1: Foundations and Fixtures

Outcome: Last.fm fixtures exist, provider abstraction compiles, harness can run with explicit provider injection.

Validation target:

- Build succeeds.
- Full summary remains: 109 passed, 35 ignored, 0 failed, 0 errored.
- No DD activation yet.

### Milestone 2: Similar Discovery Core

Outcome: DD-009..DD-015 and DD-018 are active and passing with real assertions.

Validation target:

- Unit tests under `Unit.SyncEngine.SimilarArtists` pass.
- `SimilarArtistTests.fs` DD-009..DD-015 and DD-018 are `testCase` (not `ptestCase`) with non-empty assertions.
- Ignored count decreases by 8 (from 35 to 27), with additional pass-count increase from new unit tests.

### Milestone 3: Last.fm Failure Handling

Outcome: DD-016, DD-017, DD-061, DD-068, DD-069 are active and passing.

Validation target:

- Last.fm requests include `api_key`.
- Invalid key and unavailable-source paths log expected codes.
- Sync continues without similar artists in non-fatal cases.
- Ignored count decreases by 5 (from 27 to 22).

### Milestone 4: Similar-Artist Track Cap

Outcome: DD-019 active/passing with deterministic 20% cap behavior.

Validation target:

- Unit tests under `Unit.PlaylistReconcile.SimilarCap` pass.
- DD-019 passes with controlled 10-track/20%-cap scenario.
- Ignored count decreases by 1 (from 22 to 21).

### Milestone 5: Artist Dislike Filtering

Outcome: DD-020..DD-022 active and passing.

Validation target:

- Ratings endpoints queried.
- Disliked artists excluded from candidate releases.
- Exclusion logs contain artist names.
- Ignored count decreases by 3 (from 21 to 18).

### Milestone 6: Final Gate

Outcome: all Phase 2-scoped DD tests active/passing and full suite green.

Validation target:

- `nix develop -c dotnet build packages/dropd/dropd.sln` succeeds.
- Full summary reports 0 failed and 0 errored.
- Remaining 18 ignored tests correspond only to out-of-scope DDs.

## Concrete Steps

Run every command from repository root unless stated otherwise.

### Milestone 1 steps (foundations)

1. Verify baseline and save current summary counts:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

   Expected: 109 passed, 35 ignored, 0 failed, 0 errored.

2. Create Last.fm fixture directory:

       mkdir -p packages/dropd/tests/Dropd.Tests/Fixtures/lastfm

3. Create `packages/dropd/tests/Dropd.Tests/Fixtures/lastfm/similar-bonobo.json` with:

       {
         "similarartists": {
           "artist": [
             { "name": "Burial", "mbid": "9ea80bb8-4bcb-4188-9e0d-4156b187c6f9", "match": "0.91" },
             { "name": "The  Black  Keys", "mbid": "d15721d3-c0bc-4834-be19-e8d5623cb4b9", "match": "0.88" },
             { "name": "Bonobo", "mbid": "29525428-8f2e-4f62-9c12-9d4f0f6a9e70", "match": "0.55" },
             { "name": "NoMatch", "mbid": "", "match": "0.44" }
           ]
         }
       }

4. Create `packages/dropd/tests/Dropd.Tests/Fixtures/lastfm/similar-radiohead.json` with:

       {
         "similarartists": {
           "artist": [
             { "name": "Burial", "mbid": "9ea80bb8-4bcb-4188-9e0d-4156b187c6f9", "match": "0.77" },
             { "name": " Four   Tet ", "mbid": "f64deeb5-14e1-4c10-9ecf-18cf7f4de2bd", "match": "0.74" }
           ]
         }
       }

5. Create `packages/dropd/tests/Dropd.Tests/Fixtures/lastfm/similar-invalid-key.json` with:

       {
         "error": 10,
         "message": "Invalid API key - You must be granted a valid key by last.fm"
       }

6. Create `packages/dropd/tests/Dropd.Tests/Fixtures/lastfm/similar-not-found.json` with:

       {
         "error": 6,
         "message": "The artist you supplied could not be found"
       }

7. Create Apple artist search fixtures:

   - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/artist-search-burial.json`

         {
           "results": {
             "artists": {
               "data": [
                 { "id": "14294754", "type": "artists", "attributes": { "name": "Burial" } },
                 { "id": "999", "type": "artists", "attributes": { "name": "Burial (DJ)" } }
               ]
             }
           }
         }

   - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/artist-search-black-keys.json`

         {
           "results": {
             "artists": {
               "data": [
                 { "id": "136975", "type": "artists", "attributes": { "name": "the black keys" } }
               ]
             }
           }
         }

   - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/artist-search-four-tet.json`

         {
           "results": {
             "artists": {
               "data": [
                 { "id": "390999", "type": "artists", "attributes": { "name": "Four Tet" } }
               ]
             }
           }
         }

   - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/artist-search-empty.json`

         {
           "results": {
             "artists": {
               "data": []
             }
           }
         }

8. Create Apple ratings fixtures:

   - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/ratings-songs-dislikes.json`

         {
           "data": [
             {
               "id": "song-1",
               "type": "ratings",
               "attributes": {
                 "value": -1,
                 "artistName": "BadArtist"
               }
             },
             {
               "id": "song-2",
               "type": "ratings",
               "attributes": {
                 "value": 1,
                 "artistName": "GoodArtist"
               }
             }
           ]
         }

   - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/ratings-albums-dislikes.json`

         {
           "data": [
             {
               "id": "album-1",
               "type": "ratings",
               "attributes": {
                 "value": -1,
                 "artistName": "BadArtist"
               }
             },
             {
               "id": "album-2",
               "type": "ratings",
               "attributes": {
                 "value": 1,
                 "artistName": "OtherArtist"
               }
             }
           ]
         }

9. Update `packages/dropd/tests/Dropd.Tests/TestData.fs` with helper functions:

   - `lastFmFixture : string -> string`
   - `okLastFmFixture : string -> CannedResponse`

10. Update `packages/dropd/src/Dropd.Core/ApiContracts.fs`:

    - add `SimilarArtistProviderError`
    - add `SimilarArtistProvider`
    - extend `DiscoveryResult` with `SimilarArtists: DiscoveredArtist list`

11. Create `packages/dropd/src/Dropd.Core/SimilarArtists.fs` with:

    - Last.fm request builder
    - Last.fm response parser
    - provider constructor

12. Update `packages/dropd/src/Dropd.Core/Dropd.Core.fsproj` compile order so `SimilarArtists.fs` is compiled before `SyncEngine.fs`.

13. Add `runSyncWithProvider` to `packages/dropd/tests/Dropd.Tests/TestHarness.fs`:

    - same validation/runtime path as `runSync`
    - calls `SyncEngine.runSyncWithProvider`
    - preserves `knownPlaylistIds` as `Map.empty` for harness runs

14. Confirm filter behavior with active test name:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.SyncEngine.AuthAndLogs"

15. Build and full-summary check:

       nix develop -c dotnet build packages/dropd/dropd.sln
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

16. Commit:

       jj commit -m "phase2: add similar-artist contracts, fixtures, and harness provider entrypoint"

### Milestone 2 steps (DD-009..DD-015, DD-018)

17. In `packages/dropd/tests/Dropd.Tests/SyncEngineUnitTests.fs`, add `testList "Unit.SyncEngine.SimilarArtists"` with test 1 (red): Last.fm request query contains `method=artist.getSimilar`, `artist=<seed name>`, `format=json`, `limit=10`.

18. Run only this test list (expect fail/red):

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.SyncEngine.SimilarArtists"

19. In `packages/dropd/src/Dropd.Core/SimilarArtists.fs`, implement request builder and provider call; rerun filter (expect at least first test green).

20. Add unit test 2 (red): parser maps Last.fm error code 10 to `AuthFailure`.

21. Implement parser branch for code 10; rerun filter.

22. Add unit test 3 (red): parser maps non-auth error payloads or non-2xx status to `Unavailable`/`MalformedResponse`.

23. Implement non-auth error mapping; rerun filter.

24. Add unit test 4 (red): seed filtering removes normalized-name matches (e.g., seed `"Bonobo"`, candidate `" bonobo "`).

25. In `SyncEngine.fs`, implement helper to exclude seed artists by `Normalization.normalizeText`; rerun filter.

26. Add unit test 5 (red): artist resolution queries `/v1/catalog/us/search` with `types=artists` and `limit=5`.

27. Implement search request helper in `SyncEngine.fs`; rerun filter.

28. Add unit test 6 (red): fallback name matching resolves `" The  Black  Keys "` against catalog `"the black keys"`.

29. Implement normalized fallback matching; rerun filter.

30. Add unit test 7 (red): unresolved artist (empty search results) is skipped without abort.

31. Implement skip-on-unresolved behavior; rerun filter.

32. Add unit test 8 (red): deduplicate resolved similar artists by catalog ID.

33. Implement dedup by catalog ID before release fetch; rerun filter.

34. Add new orchestration entrypoint in `SyncEngine.fs`:

    - `runSyncWithProvider provider config runtime knownPlaylistIds`
    - existing `runSync` wraps this with default Last.fm provider

35. Keep existing `runSync` signature intact:

    - includes `knownPlaylistIds: Map<string,string>`

36. Update all `DiscoveryResult` constructors in tests and source to include `SimilarArtists`.

37. In `packages/dropd/tests/Dropd.Tests/SimilarArtistTests.fs`, replace DD-009 body (still `ptestCase`):

    - setup Last.fm route for Bonobo and base Apple routes
    - run sync
    - assert request contains `Service = "lastfm"`, `Path = "/2.0"`, `("method","artist.getSimilar")`, `("artist","Bonobo")`

38. DD-010 body:

    - Last.fm returns `Bonobo` as similar
    - assert no Apple catalog artist query for seed artist duplicate caused by similar flow

39. DD-011 body:

    - Last.fm returns `Burial` with MBID
    - Apple search fixture returns `id = 14294754`
    - assert release request includes `/v1/catalog/us/artists/14294754/albums`

40. DD-012 body:

    - Last.fm returns `" The  Black  Keys "`
    - Apple search returns `"the black keys"`
    - assert resolved artist release request uses `id = 136975`

41. DD-013/DD-014/DD-015/DD-018 bodies:

    - DD-013: two seeds map to same similar artist; assert only one release query for that artist ID
    - DD-014: `NoMatch` uses `artist-search-empty.json`; assert no release query for unresolved artist
    - DD-015: mixed unresolved + resolvable; assert resolvable artist still queried
    - DD-018: call `runSyncWithProvider` with fake provider returning known artist; assert no Last.fm request was made and fake provider artist ID is queried

42. Convert DD-009..DD-015 and DD-018 from `ptestCase` to `testCase` only after bodies/assertions are complete.

43. Run validation:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.SyncEngine.SimilarArtists"
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Similar Artist Discovery"
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

44. Commit in two logical commits:

       jj commit -m "phase2: implement similar-provider requests and parsing"
       jj commit -m "phase2: implement similar discovery orchestration and activate DD-009..DD-015 DD-018"

### Milestone 3 steps (DD-016, DD-017, DD-061, DD-068, DD-069)

45. Add unit test (red) in `Unit.SyncEngine.SimilarArtists`: Last.fm HTTP 503 logs `SimilarArtistServiceUnavailable` and sync outcome is not `Aborted`.

46. Implement this logging branch in `SyncEngine.fs`; rerun unit filter.

47. Add unit test (red): Last.fm HTTP 200 with error code 10 logs `LastFmAuthFailure` and sync continues.

48. Implement auth-failure logging branch; rerun unit filter.

49. Add unit test (red) in `Unit.SyncEngine.AuthAndLogs`: Last.fm requests include `api_key` from config.

50. Implement `api_key` query inclusion in `SimilarArtists.fs`; rerun unit filter.

51. In `packages/dropd/tests/Dropd.Tests/AuthenticationTests.fs`, implement DD-061 body:

    - setup includes one Last.fm route
    - run sync
    - assert Last.fm request query contains `("api_key","lastfm-key")`

52. Implement DD-068 body:

    - Last.fm route returns `similar-invalid-key.json`
    - assert logs contain `LastFmAuthFailure`

53. Implement DD-069 body:

    - same invalid key route + valid Apple data
    - assert outcome is `Success` or `PartialFailure`, not `Aborted`

54. In `SimilarArtistTests.fs`, implement DD-016 body:

    - Last.fm route returns status 503
    - assert `SimilarArtistServiceUnavailable` log exists

55. Implement DD-017 body:

    - Last.fm 503 + valid seed/label Apple routes + playlist routes
    - assert sync still reaches playlist add/create requests

56. Convert DD-016 and DD-017 from `ptestCase` to `testCase`.

57. Convert DD-061, DD-068, DD-069 from `ptestCase` to `testCase`.

58. Run targeted validation:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.SyncEngine.SimilarArtists"
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Authentication.DD-06"
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Similar Artist Discovery.DD-01"

59. Run full summary.

60. Commit:

       jj commit -m "phase2: add non-fatal lastfm failure handling and activate DD-016 DD-017 DD-061 DD-068 DD-069"

### Milestone 4 steps (DD-019 similar cap)

61. In `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`, change planning API to thread similar-source identity:

    - add `similarArtistIds: Set<CatalogArtistId>` parameter to `computePlan`
    - add `similarArtistMaxPercent: int` parameter to `computePlan`

62. Add `Unit.PlaylistReconcile.SimilarCap` tests in `packages/dropd/tests/Dropd.Tests/PlaylistReconcileUnitTests.fs`:

    - case A: desired tracks = 10 total, 5 from similar artists, cap=20 => add at most 2 similar tracks
    - case B: repeated runs are deterministic (same kept similar track IDs in same order)
    - case C: non-similar tracks are never removed to satisfy cap

63. Run SimilarCap tests (expect red):

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.PlaylistReconcile.SimilarCap"

64. Implement capping algorithm in `computePlan`:

    - build desired track list in stable release order
    - partition desired tracks into similar/non-similar by release artist ID in `similarArtistIds`
    - compute `allowedSimilar = floor((similarArtistMaxPercent * totalDesired) / 100)`
    - keep first `allowedSimilar` similar tracks in stable order
    - include all non-similar tracks

65. Update `reconcilePlaylists` call site to pass:

    - `similarArtistMaxPercent` from config
    - `similarArtistIds` built from `discovery.SimilarArtists`

66. Update all tests constructing `DiscoveryResult` to include `SimilarArtists`.

67. In `packages/dropd/tests/Dropd.Tests/SimilarArtistTests.fs`, implement DD-019 body:

    - setup produces 10 desired tracks with 5 from similar artists
    - config `SimilarArtistMaxPercent = 20`
    - assert playlist-add request body includes at most 2 similar-artist track IDs

68. Convert DD-019 from `ptestCase` to `testCase`.

69. Run validation (unit + Similar Artist Discovery + full summary).

70. Commit:

       jj commit -m "phase2: enforce deterministic similar-artist cap and activate DD-019"

### Milestone 5 steps (DD-020..DD-022 artist filtering)

71. In `packages/dropd/tests/Dropd.Tests/SyncEngineUnitTests.fs`, add `testList "Unit.SyncEngine.ArtistFiltering"` test 1 (red): requests `/v1/me/ratings/songs` and `/v1/me/ratings/albums`.

72. Run filter (expect red):

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.SyncEngine.ArtistFiltering"

73. In `SyncEngine.fs`, implement `fetchSongRatings` and `fetchAlbumRatings`; rerun filter.

74. Add unit test 2 (red): parse dislikes (`value = -1`) and collect artist names from `attributes.artistName`.

75. Implement `collectExcludedArtists`; rerun filter.

76. Add unit test 3 (red): candidate releases by excluded artists are filtered out before reconciliation.

77. Implement candidate-release filtering; rerun filter.

78. Add unit test 4 (red): each excluded artist emits `ExcludedDislikedArtist` log with `artist` in data.

79. Implement exclusion logging; rerun filter.

80. In `packages/dropd/tests/Dropd.Tests/ArtistFilteringTests.fs`, implement DD-020 body:

    - setup ratings endpoints using new fixtures
    - assert requests include both ratings endpoints

81. Implement DD-021 body:

    - disliked artist appears in discovered releases
    - assert playlist add payload excludes tracks by `BadArtist`

82. Implement DD-022 body:

    - assert log includes `Code = ExcludedDislikedArtist` and `Data.["artist"] = "BadArtist"`

83. Convert DD-020..DD-022 from `ptestCase` to `testCase`.

84. Run targeted filters:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.SyncEngine.ArtistFiltering"
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Artist Filtering"

85. Run full summary.

86. Commit (implementation):

       jj commit -m "phase2: add ratings retrieval and disliked-artist exclusion"

87. Commit (DD activation):

       jj commit -m "phase2: activate DD-020 DD-021 DD-022 with asserted behavior"

88. Verify ignored count is now 18.

### Milestone 6 steps (final gate)

89. Full build + tests:

       nix develop -c dotnet build packages/dropd/dropd.sln
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

90. Confirm active/passing coverage includes:

    - DD-009..DD-019
    - DD-020..DD-022
    - DD-061, DD-068, DD-069

91. Confirm remaining ignored tests are only out-of-scope DDs.

92. Update this plan sections with timestamps and evidence:

    - Progress
    - Surprises & Discoveries
    - Decision Log
    - Outcomes & Retrospective

93. Final commit:

       jj commit -m "phase2: complete similar discovery, artist filtering, and lastfm auth requirements"

94. Optional: create follow-up plan ticket for DD-063/DD-070..DD-073 and resilience DDs.

## Validation and Acceptance

Phase 2 is accepted only when all checks below are true:

1. Similar-artist discovery executes via a provider abstraction, not direct inline Last.fm logic in orchestration (DD-018).
2. Active DD tests include DD-009..DD-019, DD-020..DD-022, DD-061, DD-068, DD-069.
3. Last.fm auth failure (`"error":10` in HTTP 200 body) logs `LastFmAuthFailure` and does not abort sync.
4. Last.fm unavailability logs `SimilarArtistServiceUnavailable` and sync continues with seed+label data.
5. Similar-artist track contributions are capped by `SimilarArtistMaxPercent` per playlist (DD-019).
6. Ratings endpoints for songs and albums are requested and disliked artists are excluded from playlist population (DD-020, DD-021).
7. Excluded disliked artists are logged by name (DD-022).
8. `nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary` reports 0 failed and 0 errored.
9. Remaining pending tests correspond only to out-of-scope DDs for later phases.

## Idempotence and Recovery

All file creation steps are additive and safe to repeat.

If a milestone fails midway and `.jj/` exists, restore only milestone-touched paths:

    jj restore packages/dropd/src/Dropd.Core packages/dropd/tests/Dropd.Tests docs/plans/service-implementation-phase2.md

Fallback with git:

    git restore packages/dropd/src/Dropd.Core packages/dropd/tests/Dropd.Tests docs/plans/service-implementation-phase2.md

If multiple DD activations fail at once, revert only the affected test file and reactivate in
smaller slices (one DD group per commit).

## Artifacts and Notes

Expected phase-end artifacts:

- `docs/plans/service-implementation-phase2.md` (updated living document)
- `packages/dropd/src/Dropd.Core/SimilarArtists.fs`
- updated:
  - `packages/dropd/src/Dropd.Core/ApiContracts.fs`
  - `packages/dropd/src/Dropd.Core/SyncEngine.fs`
  - `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`
  - `packages/dropd/src/Dropd.Core/Dropd.Core.fsproj`
  - `packages/dropd/tests/Dropd.Tests/TestData.fs`
  - `packages/dropd/tests/Dropd.Tests/TestHarness.fs`
  - `packages/dropd/tests/Dropd.Tests/SyncEngineUnitTests.fs`
  - `packages/dropd/tests/Dropd.Tests/PlaylistReconcileUnitTests.fs`
  - `packages/dropd/tests/Dropd.Tests/SimilarArtistTests.fs`
  - `packages/dropd/tests/Dropd.Tests/ArtistFilteringTests.fs`
  - `packages/dropd/tests/Dropd.Tests/AuthenticationTests.fs`
- fixture files under:
  - `packages/dropd/tests/Dropd.Tests/Fixtures/lastfm/`
  - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/` (new artist-search and ratings fixtures)

## Interfaces and Dependencies

Phase 2 must end with these stable contracts.

In `packages/dropd/src/Dropd.Core/ApiContracts.fs`, define:

    type SimilarArtistProviderError =
      | AuthFailure of message: string
      | Unavailable of statusCode: int * message: string
      | MalformedResponse of message: string

    type SimilarArtistProvider =
      { Name: string
        GetSimilar: artistName: string * mbid: string option -> Async<Result<Types.SimilarArtist list, SimilarArtistProviderError>> }

    type DiscoveryResult =
      { SeedArtists: DiscoveredArtist list
        SimilarArtists: DiscoveredArtist list
        LabelArtists: DiscoveredArtist list
        Releases: DiscoveredRelease list }

In `packages/dropd/src/Dropd.Core/SimilarArtists.fs`, define:

    val createLastFmProvider : Config.ValidSyncConfig -> ApiContracts.ApiRuntime -> ApiContracts.SimilarArtistProvider

In `packages/dropd/src/Dropd.Core/SyncEngine.fs`, define:

    val runSyncWithProvider :
      ApiContracts.SimilarArtistProvider ->
      Config.ValidSyncConfig ->
      ApiContracts.ApiRuntime ->
      Map<string,string> ->
      Types.SyncOutcome * ApiContracts.ObservedSync

And keep:

    val runSync :
      Config.ValidSyncConfig ->
      ApiContracts.ApiRuntime ->
      Map<string,string> ->
      Types.SyncOutcome * ApiContracts.ObservedSync

`runSync` must call `runSyncWithProvider` with the default Last.fm provider.

In `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`, update:

    val computePlan :
      DateOnly ->
      int ->                    // rollingWindowDays
      int ->                    // similarArtistMaxPercent
      Set<Types.CatalogArtistId> ->
      Config.PlaylistDefinition ->
      ApiContracts.DiscoveredRelease list ->
      ExistingTrack list ->
      ApiContracts.PlaylistPlan

Dependencies remain:

- .NET 9
- Expecto 10.2.3
- System.Text.Json 9.0.0
- no additional runtime HTTP/logging libraries required in this phase

Out-of-scope for this plan (deferred):

- DD-063
- DD-070..DD-073
- DD-076..DD-078
- DD-080..DD-089
