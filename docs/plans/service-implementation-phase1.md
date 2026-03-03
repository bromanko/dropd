# dropd Service Implementation Roadmap and Phase 1 ExecPlan

This ExecPlan is a living document. The sections Progress, Surprises & Discoveries,
Decision Log, and Outcomes & Retrospective must be kept up to date as work proceeds.

## Purpose / Big Picture

Phase 1 makes dropd behave like a real sync engine in tests instead of returning a fixed stub result.
After this phase, running the test harness will exercise end-to-end behavior for seed artists, label discovery,
release retrieval, genre classification, playlist reconciliation, and baseline sync observability.

A developer should be able to prove this by running the requirement tests for DD-001..DD-008,
DD-023..DD-035, DD-045..DD-054, DD-060, DD-062, DD-064..DD-067, and DD-074, DD-075, DD-079
and seeing them active and passing.

## Progress

- [x] (2026-03-03 03:29Z) Reviewed and revised this plan for self-containment, granularity, milestones, commit points, and explicit interfaces.
- [x] (2026-03-03 15:20Z) Milestone 1 complete: foundations, fixtures, project wiring, and test harness runtime bridge.
- [x] (2026-03-03 15:34Z) Milestone 2 complete: normalization helpers and unit tests.
- [x] (2026-03-03 15:40Z) Milestone 3 complete: artist seeding + label discovery (DD-001..DD-008 active and passing).
- [x] (2026-03-03 15:46Z) Milestone 4 complete: release retrieval + genre classification (DD-023..DD-035 active and passing).
- [x] (2026-03-03 15:49Z) Milestone 5 complete: playlist reconciliation (DD-045..DD-054 active and passing).
- [x] (2026-03-03 15:51Z) Milestone 6 complete: auth + observability slice (DD-060, DD-062, DD-064..DD-067, DD-074, DD-075, DD-079 active and passing).
- [x] (2026-03-03 15:53Z) Milestone 7 complete: final full-suite gate and retrospective.

## Surprises & Discoveries

- Observation: The current baseline test suite was already stable and deterministic before implementation.
  Evidence: from repo root, `nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary` reported 30 passed, 75 ignored, 0 failed, 0 errored.

- Observation: `TestHarness.runSync` was a fixed stub and did not execute any sync logic.
  Evidence: prior to implementation, `packages/dropd/tests/Dropd.Tests/TestHarness.fs` returned `{ Requests = []; Logs = []; Outcome = Some Success }`.

- **Bug: `fetchFavoritedArtists` does not pass `ids` to `/v1/me/ratings/artists`.**
  Discovered during integration smoke spike (`make smoke`). The real Apple Music API
  returns 400 ("No ids supplied on the request") and the sync aborts. All existing tests
  pass because the test harness returns canned responses without validating query params.
  See BUG-001 in the Known Bugs section below for full analysis and remediation plan.

- Observation: Expecto `--filter` did not match DD-style names in this project configuration.
  Evidence: `nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-001"` returned 0 tests run, while full summary listed DD tests as active and passing.

## Decision Log

- Decision: Phase 1 implementation will be driven by requirement DD slices, with small helper unit tests added first for each slice.
  Rationale: this keeps requirement traceability and avoids large opaque commits.
  Date: 2026-03-02

- Decision: Similar-artist discovery (DD-009..DD-019), dislike filtering (DD-020..DD-022), and resilience/scheduling-runtime requirements beyond current passing scope remain out of Phase 1.
  Rationale: Phase 1 should deliver the first complete seed+label sync core with minimal moving parts.
  Date: 2026-03-02

- Decision: `SyncEngine.runSync` will accept `Config.ValidSyncConfig` instead of raw `Config.SyncConfig`.
  Rationale: config validation already exists; pushing invalid configs into engine logic adds avoidable branching.
  Date: 2026-03-03

- Decision: Shared structured log type moves to `Dropd.Core.ApiContracts` and test harness reuses it.
  Rationale: logging is domain behavior under test; duplicate `LogEntry` type in test project creates drift risk.
  Date: 2026-03-03

- Decision: The infra plans listed in Context are informational only, not prerequisites.
  Rationale: this plan must be implementable from this file and the working tree alone.
  Date: 2026-03-03

- Decision: Last.fm fixtures are deferred to Phase 2.
  Rationale: Phase 1 scope does not execute similar-artist requirements, so Last.fm fixture work is unnecessary in this phase.
  Date: 2026-03-03

- Decision: DD range validation is gated by full-suite execution instead of `--filter`-based commands.
  Rationale: in this repository, Expecto filter arguments currently return zero selected tests for DD-name filters, so the reliable acceptance signal is full-suite output listing active DD tests.
  Date: 2026-03-03

- Decision: playlist reconciliation treats create/list/add/remove failures as partial failures and continues to remaining playlists.
  Rationale: this satisfies DD-050..DD-054 while keeping sync runs recoverable and idempotent.
  Date: 2026-03-03

## Known Bugs

### BUG-001: `fetchFavoritedArtists` calls `/v1/me/ratings/artists` without required `ids` parameter

**Discovered:** 2026-03-03, integration smoke spike (`docs/plans/integration-smoke-spike.md`).

**Symptom:** The real Apple Music API returns HTTP 400, error code `40005`
("No ids supplied on the request"). The engine treats this as a fatal error and aborts
with `FavoritedArtistsFailed`. The test suite does not catch this because the test harness
returns canned 200 responses regardless of query parameters.

**Root cause:** `SyncEngine.fetchFavoritedArtists` calls
`GET /v1/me/ratings/artists` with no `ids` query parameter. The Apple Music ratings
endpoints require an explicit `ids` parameter listing the catalog resource IDs to check.
The api-exploration spike (`spikes/api-exploration/AppleMusic.fs`) already demonstrates the
correct pattern: `GET /v1/me/ratings/songs?ids=203709340`.

**Additional concern — library IDs vs catalog IDs:** The library artists returned by
`GET /v1/me/library/artists` may use library-scoped IDs (e.g. `r.xxx` format) rather than
catalog artist IDs. The ratings endpoint expects catalog IDs. If library artists do carry
library-scoped IDs, then `fetchFavoritedArtists` cannot simply forward them — it must
first resolve them to catalog IDs (e.g. via the `catalog` relationship on library artist
resources) or use a different approach entirely.

**Impact:** DD-002 ("retrieve the list of favorited artists") passes in tests but fails
against the real Apple Music API. The entire sync aborts before reaching label discovery,
release retrieval, or playlist reconciliation.

**Required fix (scope TBD):**

1. **Investigate the ID format:** Run `GET /v1/me/library/artists?limit=5` against the
   real API and inspect whether the returned `id` values are catalog IDs (numeric strings
   like `"657515"`) or library-scoped IDs (like `"r.abcdef"`). If they are library-scoped,
   check whether the response includes a `relationships.catalog` link or attribute that
   provides the catalog ID.

2. **Fix `fetchFavoritedArtists`:** Depending on finding (1):
   - If library artists carry catalog IDs directly: change `fetchFavoritedArtists` to
     accept the library artist IDs, batch them into the `ids` query parameter
     (comma-separated), and handle the Apple Music batch-size limits (max 25–100 IDs per
     request depending on endpoint).
   - If library artists carry library-scoped IDs: either resolve to catalog IDs first
     (additional API call), or replace the ratings approach entirely with the library
     `favorite` flag if the API supports filtering by favorited status directly.

3. **Update test fixtures and harness:** The test harness route for
   `/v1/me/ratings/artists` should validate that `ids` is present in the query parameters.
   Update fixtures and DD-002 test assertions accordingly.

4. **Re-validate with integration smoke:** After the fix, `make smoke` should proceed past
   the favorited-artists step and continue into label discovery and release retrieval.

**Status:** Fixed — 2026-03-03.

**Resolution:** The investigation confirmed that library artist IDs are library-scoped
(e.g. `r.o9U81GC`), not catalog IDs. The `include=catalog` query parameter on
`/v1/me/library/artists` returns the catalog relationship with catalog artist IDs. The
fix:

1. `fetchLibraryArtists` now passes `include=catalog` and uses a new
   `parseLibraryArtistsWithCatalog` parser that extracts catalog IDs from
   `relationships.catalog.data[0]`. Library artists without a catalog mapping are skipped.
2. `fetchFavoritedArtists` now accepts `CatalogArtistId list`, batches them in groups of
   25, and passes them as the `ids` query parameter. A new `parseRatedArtistIds` parser
   extracts artist IDs with `attributes.value = 1` from the ratings response.
3. `runSync` threads the library artist catalog IDs into `fetchFavoritedArtists`.
4. Test fixtures updated: `library-artists.json` now uses the real API format with
   library-scoped IDs and catalog relationships; `favorited-artists.json` now uses the
   real ratings format (`type: "ratings"`, `attributes.value: 1`). All inline test bodies
   in `NewReleaseTests.fs`, `GenreClassificationTests.fs`, `PlaylistManagementTests.fs`,
   and `ArtistSeedingTests.fs` updated accordingly.
5. All 101 tests pass with 0 failures. `make smoke` proceeds past the favorited-artists
   step into label discovery, release retrieval, genre classification, and playlist
   reconciliation.

## Outcomes & Retrospective

Phase 1 delivered an executable sync path from `TestHarness.runSync` through `SyncEngine.runSync` and `PlaylistReconcile.reconcilePlaylists`.
The previous stubbed harness behavior was removed. The core now emits real request traces, structured logs, and sync outcomes.

Completed outcomes by milestone:

- Foundations: added `Normalization.fs`, `ApiContracts.fs`, `PlaylistReconcile.fs`, and `SyncEngine.fs`; updated `Dropd.Core.fsproj` compile order; added deterministic Apple fixture set; added root `AGENTS.md`.
- Helper coverage: added `NormalizationUnitTests.fs`, `SyncEngineUnitTests.fs`, and `PlaylistReconcileUnitTests.fs` and wired them into `Dropd.Tests.fsproj`.
- DD activation: DD-001..DD-008, DD-023..DD-035, DD-045..DD-054, DD-060, DD-062, DD-064..DD-067, DD-074, DD-075, DD-079 are active (`testCase`) and passing.
- Observability research note: added `docs/research/structured-logging.md` documenting typed in-memory logging for Phase 1 and Phase 4 sink-selection criteria.

Final verification evidence (repo root):

    nix develop -c dotnet build packages/dropd/dropd.sln
    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Observed final result: build succeeds; tests report 92 passed, 35 ignored, 0 failed, 0 errored.
Out-of-scope DD tests remain pending (`ptestCase`) and therefore ignored.

## Context and Orientation

The relevant repository areas are:

- Requirements source: `docs/ears/requirements.md`.
- Phase plan file: `docs/plans/service-implementation-phase1.md` (this file).
- Core library project: `packages/dropd/src/Dropd.Core/`.
  - Existing modules: `Types.fs`, `Config.fs`, `Normalization.fs`, `ApiContracts.fs`, `PlaylistReconcile.fs`, `SyncEngine.fs`, `Scheduling.fs`.
  - Project compile order file: `packages/dropd/src/Dropd.Core/Dropd.Core.fsproj`.
- Test project: `packages/dropd/tests/Dropd.Tests/`.
  - Harness: `TestHarness.fs`.
  - Requirement tests: `*Tests.fs`.
  - Test project file: `Dropd.Tests.fsproj`.
- API exploration references for realistic payload shape:
  - `spikes/api-exploration/AppleMusic.fs`
  - `spikes/api-exploration/LastFm.fs`

Definitions used in this plan:

- **DD-###**: a requirement identifier from `docs/ears/requirements.md`.
- **`ptestCase`** (Expecto): pending test; skipped/ignored by runner.
- **`testCase`** (Expecto): active test; executed and counted as pass/fail.
- **Jujutsu (`jj`)**: version control tool used in this repository when `.jj/` exists.

Informational files that are *not* required to execute this plan:

- `docs/plans/verification-infrastructure.md`
- `docs/plans/api-exploration-and-dev-tooling.md`

## Plan of Work

The implementation proceeds in seven milestones. Each milestone produces an independently verifiable outcome and ends in a passing test state before commit.

Milestone 1 creates the foundation: new core module files, deterministic JSON fixtures, explicit interface types, and harness wiring so `runSync` can call a real engine path through fake routes. Milestone 2 adds normalization helpers and unit tests that all later slices depend on. Milestone 3 activates seed and label discovery behaviors. Milestone 4 activates release retrieval and genre classification. Milestone 5 activates playlist reconciliation. Milestone 6 activates scoped authentication and observability behavior. Milestone 7 performs final integration validation and records retrospective notes.

This sequence is intentionally additive: helper modules and fixtures first, orchestration second, requirement activation in small slices, and only then final cleanup.

## Milestones and Validation Targets

Test-count totals can change as unit tests are added. Milestone gates are therefore based on behavior and DD coverage, not fixed passed/ignored counts.

### Milestone 1: Foundations and Runtime Bridge

Outcome: new core modules and fixtures exist, project compile order is valid, harness can invoke a non-stub `runSync` path.

Validation target:

- Build succeeds.
- Existing active tests pass.
- No new DD tests are activated yet.
- Test summary shows 0 failed and 0 errored.

### Milestone 2: Normalization Helpers

Outcome: normalization/date-window/dedup helpers exist with unit tests.

Validation target:

- `Unit.Normalization` tests pass.
- Full test summary shows 0 failed and 0 errored.

### Milestone 3: Artist Seeding + Label Discovery

Outcome: DD-001..DD-008 are active and passing.

Validation target:

- `--filter "DD-00"` includes active DD-001..DD-008 and they all pass.
- Full test summary shows 0 failed and 0 errored.

### Milestone 4: New Releases + Genre Classification

Outcome: DD-023..DD-035 are active and passing.

Validation target:

- `--filter "DD-02"` and `--filter "DD-03"` pass for scoped active tests.
- Full test summary shows 0 failed and 0 errored.

### Milestone 5: Playlist Reconciliation

Outcome: DD-045..DD-054 are active and passing.

Validation target:

- `--filter "DD-04"` and `--filter "DD-05"` pass for scoped active tests.
- Full test summary shows 0 failed and 0 errored.

### Milestone 6: Authentication + Observability (Phase 1 scope)

Outcome: DD-060, DD-062, DD-064..DD-067, DD-074, DD-075, DD-079 are active and passing.

Validation target:

- `--filter "DD-06"` and `--filter "DD-07"` pass for scoped active tests.
- Full test summary shows 0 failed and 0 errored.

### Milestone 7: Final Gate

Outcome: all Phase 1 scoped DD tests active/passing, out-of-scope DD tests still pending.

Validation target:

- Full build succeeds.
- Full test summary shows 0 failed and 0 errored.
- Out-of-scope DD tests remain pending (`ptestCase`).

## Concrete Steps

Run every command from repository root unless explicitly stated otherwise.

### Milestone 1 steps

1. Create `AGENTS.md` at repo root with two rules:
   - prefer `jj` commands when `.jj/` exists;
   - use `git` only as fallback.

2. Create new core module files:
   - `packages/dropd/src/Dropd.Core/Normalization.fs`
   - `packages/dropd/src/Dropd.Core/ApiContracts.fs`
   - `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`
   - `packages/dropd/src/Dropd.Core/SyncEngine.fs`

3. Edit `packages/dropd/src/Dropd.Core/Dropd.Core.fsproj` compile order to:
   - `Types.fs`
   - `Config.fs`
   - `Normalization.fs`
   - `ApiContracts.fs`
   - `PlaylistReconcile.fs`
   - `SyncEngine.fs`
   - `Scheduling.fs`

4. Create fixture directory:
   - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/`

5. Add Apple fixture files with deterministic IDs and explicit dates. Use fixed test clock `2026-03-01T00:00:00Z` for all date-window assertions:
   - `library-artists.json` (artists: Radiohead=657515, Bonobo=5765078)
   - `favorited-artists.json` (artists: Radiohead=657515, Four Tet=29525428)
   - `label-search-ninja-tune.json` (record-label id 1543411840)
   - `label-search-empty.json` (no results)
   - `label-latest-releases-1543411840.json` containing:
     - release `9001` date `2026-02-20` (inside 30-day lookback),
     - release `9002` date `2025-12-15` (outside 30-day lookback)
   - `artist-albums-657515.json` with mixed release dates using the same boundaries
   - `artist-albums-5765078.json` with mixed release dates using the same boundaries
   - `album-9001-with-genres.json` (genreNames includes `Electronic`)
   - `album-9002-no-genres.json` (genreNames empty)
   - `playlist-create-success.json`
   - `playlist-tracks-existing.json`
   - `error-401.json`
   - `error-500.json`

6. Defer Last.fm fixtures to Phase 2 (similar-artist scope). Do not add Last.fm fixture files in Phase 1.

7. In `packages/dropd/src/Dropd.Core/ApiContracts.fs`, define exact core types:

       type ApiService = AppleMusic | LastFm
       type ApiRequest =
         { Service: ApiService
           Method: string
           Path: string
           Query: (string * string) list
           Headers: (string * string) list
           Body: string option }
       type ApiResponse =
         { StatusCode: int
           Body: string
           Headers: (string * string) list }
       type LogLevel = Debug | Info | Warning | Error
       type LogEntry =
         { Level: LogLevel
           Code: string
           Message: string
           Data: Map<string, string> }
       type ObservedSync =
         { Requests: ApiRequest list
           Logs: LogEntry list }
       type ApiRuntime =
         { Execute: ApiRequest -> Async<ApiResponse>
           UtcNow: unit -> System.DateTimeOffset }

8. In `ApiContracts.fs`, define discovery/reconcile records used in later milestones:

       type DiscoveredArtist = { Id: Types.CatalogArtistId; Name: string }
       type DiscoveredRelease =
         { Id: Types.CatalogAlbumId
           ArtistId: Types.CatalogArtistId
           ArtistName: string
           Name: string
           ReleaseDate: System.DateOnly option
           GenreNames: string list
           TrackIds: Types.CatalogTrackId list }
       type DiscoveryResult =
         { SeedArtists: DiscoveredArtist list
           LabelArtists: DiscoveredArtist list
           Releases: DiscoveredRelease list }
       type PlaylistPlan =
         { PlaylistName: string
           AddTracks: Types.CatalogTrackId list
           RemoveTracks: Types.CatalogTrackId list }
       type ReconcileResult =
         { Plans: PlaylistPlan list
           AddedCount: int
           RemovedCount: int
           HadPlaylistFailures: bool }

9. In `packages/dropd/src/Dropd.Core/SyncEngine.fs`, add a compilable stub function (no behavior yet):

       let runSync
           (_config: Config.ValidSyncConfig)
           (_runtime: ApiContracts.ApiRuntime)
           : Types.SyncOutcome * ApiContracts.ObservedSync =
           failwith "not implemented"

10. In `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`, add a compilable stub function:

       let reconcilePlaylists
           (_config: Config.ValidSyncConfig)
           (_discovery: ApiContracts.DiscoveryResult)
           (_runtime: ApiContracts.ApiRuntime)
           : ApiContracts.ReconcileResult * ApiContracts.LogEntry list =
           failwith "not implemented"

11. In `packages/dropd/tests/Dropd.Tests/TestHarness.fs`:
    - replace local `LogEntry` with `Dropd.Core.ApiContracts.LogEntry`.
    - add conversion helper from harness request fields to `ApiContracts.ApiRequest`.
    - keep route matching behavior unchanged.

12. In `TestHarness.fs`, implement a runtime adapter:
    - `Execute` records each request,
    - route lookup uses existing `findRoute`,
    - unmatched routes return 404 JSON body,
    - sequence scripts advance state per endpoint key,
    - `UtcNow` always returns fixed value `DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)` for deterministic lookback/rolling-window tests.

13. In `TestHarness.fs`, update `runSync` flow:
    - validate config via `Config.validate`;
    - if invalid, return `Outcome = Some (Aborted "InvalidConfig")` and one error log with code `InvalidConfig`;
    - if valid, call `SyncEngine.runSync` with runtime adapter and map result into `ObservedOutput`.

14. Run build:

       nix develop -c dotnet build packages/dropd/dropd.sln

    Expected: build succeeds, 0 errors.

15. Run baseline tests:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

    Expected: 0 failed, 0 errored. (Pass/ignored totals may vary as unit tests are added.)

16. Commit milestone:

       jj commit -m "phase1: add sync core foundations, contracts, fixtures, and harness runtime bridge"

### Milestone 2 steps

17. Add test file `packages/dropd/tests/Dropd.Tests/NormalizationUnitTests.fs` with test list name `Unit.Normalization`.

18. Add compile entry for `NormalizationUnitTests.fs` in `packages/dropd/tests/Dropd.Tests/Dropd.Tests.fsproj` before DD files.

19. Write failing unit tests for normalization helpers:
    - `normalizeText " Electronic  House " = "electronic house"`
    - `normalizeText "A   B" = "a b"`
    - lookback edges with fixed `today = DateOnly(2026, 3, 1)` and `lookback = 30`:
      - `releaseDate = Some(DateOnly(2026, 2, 20))` returns `true`,
      - `releaseDate = Some(DateOnly(2025, 12, 15))` returns `false`,
      - `releaseDate = None` returns `false`
    - dedup by ID for artists
    - dedup by ID for releases
    - dedup by ID for tracks

20. Run only unit normalization tests:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.Normalization"

    Expected: failing tests (red phase).

21. Implement in `packages/dropd/src/Dropd.Core/Normalization.fs`:
    - `normalizeText : string -> string`
    - `dedupByArtistId`, `dedupByReleaseId`, `dedupByTrackId`
    - `isWithinLookback : System.DateOnly -> int -> System.DateOnly option -> bool`

22. Re-run `Unit.Normalization` filter; expect all tests pass.

23. Run full summary; expect 0 failed and 0 errored.

24. Commit milestone:

       jj commit -m "phase1: add normalization and dedup helpers with unit coverage"

### Milestone 3 steps (DD-001..DD-008)

25. Add `packages/dropd/tests/Dropd.Tests/SyncEngineUnitTests.fs` with test list `Unit.SyncEngine.SeedingAndLabels`.

26. Add compile entry for this file before DD files in `Dropd.Tests.fsproj`.

27. Write failing unit tests for API read helpers in `SyncEngine.fs`:
    - library artists request path `/v1/me/library/artists`
    - favorited artists request path `/v1/me/ratings/artists`
    - label search request path `/v1/catalog/us/search` with query `types=record-labels`
    - unresolved label logs warning code `UnknownLabel`.

28. Implement minimal helper functions in `SyncEngine.fs` to satisfy these unit tests:
    - `fetchLibraryArtists`
    - `fetchFavoritedArtists`
    - `resolveLabelId`
    - `resolveLabels`

29. Re-run `--filter "Unit.SyncEngine.SeedingAndLabels"`; expect pass.

30. Activate DD tests in `packages/dropd/tests/Dropd.Tests/ArtistSeedingTests.fs`:
    - DD-001, DD-002, DD-003 (`ptestCase` -> `testCase`) with concrete observable assertions:
      - DD-001: request list contains `GET /v1/me/library/artists`.
      - DD-002: request list contains `GET /v1/me/ratings/artists`.
      - DD-003: using fixtures where `Radiohead` appears in both sources, assert dedup indirectly by downstream behavior:
        - exactly one release-fetch request is emitted for catalog artist `657515` (not two), and
        - no duplicate add-track request contains duplicate track IDs for that artist.

31. Activate DD tests in `packages/dropd/tests/Dropd.Tests/LabelDiscoveryTests.fs`:
    - DD-004..DD-008 with concrete assertions:
      - DD-004: `SyncConfig.LabelNames` persistence.
      - DD-005: label search request contains configured label term.
      - DD-006: latest-releases endpoint called with resolved id.
      - DD-007: warning log code `UnknownLabel` includes unresolved label name.
      - DD-008: processing continues to second label after unresolved first.

32. Run targeted DD filters:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-00"

    Expected: DD-001..DD-008 passing; no new failures.

33. Run full summary; expect DD-001..DD-008 active/passing and overall 0 failed, 0 errored.

34. Commit milestone:

       jj commit -m "phase1: implement artist seeding and label discovery requirements DD-001..DD-008"

### Milestone 4 steps (DD-023..DD-035)

35. Add/extend `SyncEngineUnitTests.fs` with test list `Unit.SyncEngine.ReleasesAndGenres` and four failing tests:
    - request includes `sort=-releaseDate` for artist albums,
    - lookback filtering removes old releases using fixed clock `2026-03-01T00:00:00Z` and fixture dates (`2026-02-20` kept, `2025-12-15` dropped),
    - release dedup by album id,
    - empty genre list emits warning and exclusion.

36. Implement minimal release functions in `SyncEngine.fs`:
    - `fetchArtistReleases`
    - `filterByLookback`
    - `dedupReleases`
    - `classifyByGenres`

37. Re-run `--filter "Unit.SyncEngine.ReleasesAndGenres"`; expect pass.

38. Activate in `packages/dropd/tests/Dropd.Tests/NewReleaseTests.fs`:
    - DD-023..DD-030 as active `testCase` with assertions from file comments.

39. Activate in `packages/dropd/tests/Dropd.Tests/GenreClassificationTests.fs`:
    - DD-031..DD-035 as active `testCase` with assertions from file comments.

40. Ensure DD-029/DD-030 behavior is explicit in tests:
    - 503 response logs API failure code and sets `Outcome = Aborted`.

41. Run targeted filter:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-02"

    and

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-03"

    Expected: DD-023..DD-035 pass.

42. Run full summary; expect DD-023..DD-035 active/passing and overall 0 failed, 0 errored.

43. Commit milestone:

       jj commit -m "phase1: implement release retrieval and genre classification DD-023..DD-035"

### Milestone 5 steps (DD-045..DD-054)

44. Add `packages/dropd/tests/Dropd.Tests/PlaylistReconcileUnitTests.fs` with test list `Unit.PlaylistReconcile`.

45. Add compile entry for this unit file before DD files in `Dropd.Tests.fsproj`.

46. Write four failing unit tests in `Unit.PlaylistReconcile`:
    - computes add/remove sets,
    - does not add duplicates,
    - removes out-of-window tracks,
    - continues remaining playlists after failure while flagging partial failure.

47. Implement in `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`:
    - `computePlan`
    - `applyPlan`
    - `reconcilePlaylists`

48. Re-run `--filter "Unit.PlaylistReconcile"`; expect pass.

49. Activate DD-045..DD-054 in `packages/dropd/tests/Dropd.Tests/PlaylistManagementTests.fs` with route setups and assertions per comment contracts.

50. Run targeted filter:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-04"

    and

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-05"

    Expected: DD-045..DD-054 pass.

51. Run full summary; expect DD-045..DD-054 active/passing and overall 0 failed, 0 errored.

52. Commit milestone:

       jj commit -m "phase1: implement playlist reconciliation DD-045..DD-054"

### Milestone 6 steps (auth + observability)

53. Add two failing unit tests under `Unit.SyncEngine.AuthAndLogs`:
    - all Apple Music requests include `Authorization: Bearer <token>` (DD-064),
    - `/v1/me` requests include `Music-User-Token` (DD-065).

54. Implement request-header builder in `SyncEngine.fs` and use it for all Apple requests.

55. Activate in `packages/dropd/tests/Dropd.Tests/AuthenticationTests.fs`:
    - DD-060, DD-062, DD-064, DD-065, DD-066, DD-067.

56. For DD-062 test, implement deterministic assertion:
    - `Config.defaults` tokens are empty placeholders,
    - production code paths read credentials from `SyncConfig`,
    - no literal non-empty credential value appears in source under `packages/dropd/src/Dropd.Core/`.

57. Activate in `packages/dropd/tests/Dropd.Tests/ObservabilityTests.fs`:
    - DD-074, DD-075, DD-079.

58. Implement logging in `SyncEngine.fs` and `PlaylistReconcile.fs`:
    - start and completion logs with add/remove counts,
    - API failure log with endpoint + status + message,
    - final outcome log `success|partial_failure|aborted`.

59. Add research note `docs/research/structured-logging.md` documenting:
    - Phase 1 choice: typed in-memory `LogEntry` only,
    - Phase 4 follow-up criteria for sink library selection.

60. Run targeted filters:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-06"

    and

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-07"

    Expected: scoped auth and observability DD tests pass.

61. Run full summary; expect scoped auth/observability DD tests active/passing and overall 0 failed, 0 errored.

62. Commit milestone:

       jj commit -m "phase1: implement auth and observability requirements in phase scope"

### Milestone 7 steps (final gate)

63. Run full build and all tests:

       nix develop -c dotnet build packages/dropd/dropd.sln
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

    Expected:
    - build succeeds,
    - 0 failed,
    - 0 errored,
    - all Phase 1 DD ranges are active/passing,
    - remaining out-of-scope DD tests are still pending/ignored.

64. Update this plan’s sections:
    - mark completed Progress items with timestamps,
    - add any implementation discoveries,
    - add final decisions made during coding,
    - fill Outcomes & Retrospective.

65. Final commit:

       jj commit -m "phase1: complete seed+label sync core and activate scoped DD requirements"

## Validation and Acceptance

Phase 1 is accepted only when all checks below are true:

1. `TestHarness.runSync` executes real orchestration logic and emits non-empty request traces for seed, label, release, and playlist endpoints.
2. Apple fixtures exist at the exact paths listed in Milestone 1 and are used in active tests.
3. Test runtime clock is fixed at `2026-03-01T00:00:00Z` for deterministic lookback/rolling-window behavior.
4. Active DD tests and expected coverage:
   - DD-001..DD-008
   - DD-023..DD-035
   - DD-045..DD-054
   - DD-060, DD-062, DD-064..DD-067
   - DD-074, DD-075, DD-079
5. `nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary` reports 0 failed, 0 errored.
6. Out-of-scope DD tests remain pending (`ptestCase`) and therefore ignored.

## Idempotence and Recovery

All file creation and code steps are additive. If a milestone fails midway, recover by restoring only touched paths for that milestone.

When `.jj/` exists:

    jj restore packages/dropd/src/Dropd.Core packages/dropd/tests/Dropd.Tests docs/research/structured-logging.md docs/plans/service-implementation-phase1.md AGENTS.md

Fallback with git:

    git restore packages/dropd/src/Dropd.Core packages/dropd/tests/Dropd.Tests docs/research/structured-logging.md docs/plans/service-implementation-phase1.md AGENTS.md

If test activation breaks many DD tests at once, revert only the specific test file and re-activate one DD file per commit.

## Artifacts and Notes

Expected artifacts at phase end:

- `AGENTS.md`
- `docs/research/structured-logging.md`
- updated `docs/plans/service-implementation-phase1.md`
- new core files:
  - `packages/dropd/src/Dropd.Core/Normalization.fs`
  - `packages/dropd/src/Dropd.Core/ApiContracts.fs`
  - `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`
  - `packages/dropd/src/Dropd.Core/SyncEngine.fs`
- fixture files under:
  - `packages/dropd/tests/Dropd.Tests/Fixtures/apple/`
- new unit test files:
  - `packages/dropd/tests/Dropd.Tests/NormalizationUnitTests.fs`
  - `packages/dropd/tests/Dropd.Tests/SyncEngineUnitTests.fs`
  - `packages/dropd/tests/Dropd.Tests/PlaylistReconcileUnitTests.fs`

## Interfaces and Dependencies

Phase 1 implementation must end with these stable interfaces.

In `packages/dropd/src/Dropd.Core/ApiContracts.fs`:

    type ApiService = AppleMusic | LastFm

    type ApiRequest =
      { Service: ApiService
        Method: string
        Path: string
        Query: (string * string) list
        Headers: (string * string) list
        Body: string option }

    type ApiResponse =
      { StatusCode: int
        Body: string
        Headers: (string * string) list }

    type LogLevel = Debug | Info | Warning | Error

    type LogEntry =
      { Level: LogLevel
        Code: string
        Message: string
        Data: Map<string, string> }

    type ObservedSync =
      { Requests: ApiRequest list
        Logs: LogEntry list }

    type ApiRuntime =
      { Execute: ApiRequest -> Async<ApiResponse>
        UtcNow: unit -> System.DateTimeOffset }

    type DiscoveryResult =
      { SeedArtists: DiscoveredArtist list
        LabelArtists: DiscoveredArtist list
        Releases: DiscoveredRelease list }

    type ReconcileResult =
      { Plans: PlaylistPlan list
        AddedCount: int
        RemovedCount: int
        HadPlaylistFailures: bool }

In `packages/dropd/src/Dropd.Core/SyncEngine.fs`:

    let runSync
        (config: Config.ValidSyncConfig)
        (runtime: ApiContracts.ApiRuntime)
        : Types.SyncOutcome * ApiContracts.ObservedSync =
        ...

In `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`:

    let reconcilePlaylists
        (config: Config.ValidSyncConfig)
        (discovery: ApiContracts.DiscoveryResult)
        (runtime: ApiContracts.ApiRuntime)
        : ApiContracts.ReconcileResult * ApiContracts.LogEntry list =
        ...

In `packages/dropd/tests/Dropd.Tests/TestHarness.fs`:

    let runSync
        (config: Config.SyncConfig)
        (setup: FakeApiSetup)
        : ObservedOutput =
        ...

Dependencies for this phase remain:

- .NET 9
- Expecto 10.2.3
- System.Text.Json 9.0.0
- Microsoft.AspNetCore.TestHost 9.0.0 (already present in tests project)

No additional external runtime logging package is introduced in Phase 1.