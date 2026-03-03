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
- [ ] Milestone 1 complete: fixtures, contracts, and provider abstraction foundations.
- [ ] Milestone 2 complete: similar-artist discovery core (DD-009..DD-015, DD-018).
- [ ] Milestone 3 complete: similar-source resilience and Last.fm auth behavior (DD-016, DD-017, DD-061, DD-068, DD-069).
- [ ] Milestone 4 complete: similar-track percentage cap and activation (DD-019).
- [ ] Milestone 5 complete: artist dislike filtering and activation (DD-020..DD-022).
- [ ] Milestone 6 complete: full-suite gate, plan retrospective, and final commit.

## Surprises & Discoveries

- Observation: Last.fm API errors are encoded in JSON response bodies and can arrive with HTTP 200.
  Evidence: documented in `docs/research/lastfm-api.md`; existing harness test `Harness.Last.fm error scenarios use HTTP 200 with error payloads` is passing.

- Observation: DD-name filtering with Expecto is unreliable in this repository configuration.
  Evidence: from earlier Phase 1 runs, `--filter "DD-001"` selected zero tests while full test summary listed DD tests.

  <!-- FEEDBACK: Can we figure out why this is? And fix it. -->

- Observation: Current baseline before Phase 2 work is green with substantial pending scope.
  Evidence: `nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary` shows 101 passed, 35 ignored, 0 failed, 0 errored.

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

## Outcomes & Retrospective

(To be filled at major milestones and completion.)

## Context and Orientation

The repository is already on a working Phase 1 baseline. The sync path is:

- `packages/dropd/tests/Dropd.Tests/TestHarness.fs` -> runtime adapter and fake routes.
- `packages/dropd/src/Dropd.Core/SyncEngine.fs` -> orchestration and API calls.
- `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs` -> playlist plan/reconcile execution.

Requirement definitions live in `docs/ears/requirements.md`. Requirement tests live under
`packages/dropd/tests/Dropd.Tests/`.

Current pending requirement tests are concentrated in these files:

- `SimilarArtistTests.fs` (DD-009..DD-019)
- `ArtistFilteringTests.fs` (DD-020..DD-022)
- `AuthenticationTests.fs` (DD-061, DD-068, DD-069; plus later-scope auth DDs)

Current fixture data is Apple-only under:

- `packages/dropd/tests/Dropd.Tests/Fixtures/apple/`

Phase 2 introduces deterministic Last.fm fixtures under:

- `packages/dropd/tests/Dropd.Tests/Fixtures/lastfm/`

## Plan of Work

The phase proceeds in six milestones.

First, add deterministic Last.fm fixtures and explicit provider contracts so similar-artist
behavior can be tested without network calls. This also establishes the abstraction required
by DD-018.

Next, implement and test similar-artist retrieval and resolution in small TDD slices: query
similar artists for each seed artist, filter seeds from similar candidates, resolve to Apple
catalog artist IDs using MBID-first and normalized-name fallback, deduplicate, and continue
when individual similar artists cannot be resolved.

Then implement non-fatal failure handling for Last.fm outages and Last.fm auth failures, with
precise log codes and continuation semantics.

After similar discovery is stable, apply the configurable similar-artist track cap per playlist
(DD-019), then add dislike-based artist filtering from Apple ratings endpoints (DD-020..DD-022).

Finally, run the full suite, confirm all Phase 2 DD tests are active and passing, and update this
plan with completion evidence and retrospective notes.

## Milestones and Validation Targets

### Milestone 1: Foundations and Fixtures

Outcome: Last.fm fixture set exists, provider abstraction types compile, and tests can load Last.fm
fixtures deterministically.

Validation target:

- Build succeeds.
- Existing active tests remain green.
- No DD activation yet.

### Milestone 2: Similar Discovery Core

Outcome: DD-009..DD-015 and DD-018 are active and passing.

Validation target:

- New unit tests for provider and resolution helpers pass.
- `SimilarArtistTests.fs` selected DDs are active and passing.
- Full summary remains 0 failed and 0 errored.

### Milestone 3: Last.fm Failure Handling

Outcome: DD-016, DD-017, DD-061, DD-068, DD-069 are active and passing.

Validation target:

- Last.fm request includes `api_key` query parameter.
- Invalid key and unavailable-source paths log expected codes.
- Sync continues without similar artists in non-fatal cases.

### Milestone 4: Similar-Artist Track Cap

Outcome: DD-019 is active and passing with deterministic cap behavior.

Validation target:

- Per-playlist cap logic has unit coverage.
- DD-019 passes with a controlled 20% cap scenario.

### Milestone 5: Artist Dislike Filtering

Outcome: DD-020..DD-022 are active and passing.

Validation target:

- Ratings endpoints are queried.
- Disliked artists are excluded from playlist adds.
- Exclusion log entries include artist names.

### Milestone 6: Final Gate

Outcome: all Phase 2 scoped DD tests active/passing and full suite green.

Validation target:

- `nix develop -c dotnet build packages/dropd/dropd.sln` succeeds.
- Full test summary reports 0 failed and 0 errored.
- Remaining pending tests are only out-of-scope DDs.

## Concrete Steps

Run every command from repository root unless stated otherwise.

### Milestone 1 steps

1. Create Last.fm fixture directory:

       mkdir -p packages/dropd/tests/Dropd.Tests/Fixtures/lastfm

2. Add deterministic Last.fm fixtures:

   - `similar-bonobo.json`
   - `similar-radiohead.json`
   - `similar-invalid-key.json` (error code 10 payload)
   - `similar-not-found.json` (error code 6 payload)

3. Add Apple artist-resolution fixtures:

   - `artist-search-burial-by-mbid.json`
   - `artist-search-black-keys-by-name.json`
   - `artist-search-empty.json`

4. Add Apple ratings fixtures:

   - `ratings-songs-dislikes.json`
   - `ratings-albums-dislikes.json`

5. Update `packages/dropd/tests/Dropd.Tests/TestData.fs` with Last.fm fixture helpers:

   - `lastFmFixture : string -> string`
   - `okLastFmFixture : string -> CannedResponse`

6. Add provider contracts in `packages/dropd/src/Dropd.Core/ApiContracts.fs`:

   - `SimilarArtistProviderError`
   - `SimilarArtistProvider`
   - Extend `DiscoveryResult` with `SimilarArtists: DiscoveredArtist list`

7. Create `packages/dropd/src/Dropd.Core/SimilarArtists.fs` for provider parsing and Last.fm implementation.

8. Update `packages/dropd/src/Dropd.Core/Dropd.Core.fsproj` compile order so `SimilarArtists.fs` compiles before `SyncEngine.fs`.

9. Build:

       nix develop -c dotnet build packages/dropd/dropd.sln

10. Run full summary:

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

11. Commit:

       jj commit -m "phase2: add similar-artist provider contracts and deterministic fixtures"

### Milestone 2 steps (DD-009..DD-015, DD-018)

12. Add/extend unit tests in `packages/dropd/tests/Dropd.Tests/SyncEngineUnitTests.fs` under `Unit.SyncEngine.SimilarArtists`:

   - Last.fm request includes `method=artist.getSimilar` and `artist=<seed-name>`.
   - Similar candidates remove seed artists by normalized name.
   - Resolution prefers MBID lookup route when MBID exists.
   - Resolution falls back to normalized name matching when MBID missing.
   - Unresolvable similar artists are skipped without abort.
   - Resolved similar artists deduplicate by catalog artist ID.

13. Run unit filter (red phase expected initially):

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.SyncEngine.SimilarArtists"

14. Implement in `packages/dropd/src/Dropd.Core/SimilarArtists.fs`:

   - Last.fm JSON parsing for success and error payloads.
   - Mapping of error code 10 to auth failure.
   - Mapping of non-auth provider failures.

15. Implement in `packages/dropd/src/Dropd.Core/SyncEngine.fs`:

   - `runSyncWithProvider` orchestration entry point.
   - Similar retrieval for each seed artist.
   - Seed-filtering, MBID-first resolution, fallback name normalization.
   - Deduplication and unresolved-artist skip behavior.

16. Keep existing `runSync` as wrapper using default Last.fm provider.

17. Activate DD tests in `packages/dropd/tests/Dropd.Tests/SimilarArtistTests.fs`:

   - DD-009..DD-015 (`ptestCase` -> `testCase`)
   - DD-018 (`ptestCase` -> `testCase`)

18. Re-run unit filter; expect pass.

19. Run full summary; expect 0 failed and 0 errored.

20. Commit:

       jj commit -m "phase2: implement similar-artist discovery core DD-009..DD-015 and DD-018"

### Milestone 3 steps (DD-016, DD-017, DD-061, DD-068, DD-069)

21. Add unit tests in `Unit.SyncEngine.SimilarArtists` for failure paths:

   - Last.fm unavailable (HTTP 503) logs `SimilarArtistServiceUnavailable`.
   - Last.fm auth failure (error 10 in HTTP 200 body) logs `LastFmAuthFailure`.
   - Both cases continue sync without aborting.

22. Add auth-specific unit test in `Unit.SyncEngine.AuthAndLogs`:

   - Last.fm requests include `api_key` from config (DD-061).

23. Implement Last.fm request/query construction in `SimilarArtists.fs` to always include:

   - `method=artist.getSimilar`
   - `artist=<name>`
   - `api_key=<configured key>`
   - `format=json`
   - `limit=10`

24. Implement failure logging in `SyncEngine.fs`:

   - `SimilarArtistServiceUnavailable` for transport/status failures.
   - `LastFmAuthFailure` for Last.fm body error code 10.

25. Activate tests:

   - `SimilarArtistTests.fs`: DD-016, DD-017
   - `AuthenticationTests.fs`: DD-061, DD-068, DD-069

26. Run full summary and verify these tests are active and passing.

27. Commit:

       jj commit -m "phase2: add lastfm auth handling and non-fatal similar-source failure behavior"

### Milestone 4 steps (DD-019)

28. Add unit tests in `packages/dropd/tests/Dropd.Tests/PlaylistReconcileUnitTests.fs` under `Unit.PlaylistReconcile.SimilarCap`:

   - For max 20% and 10 desired tracks with 5 similar tracks, include at most 2 similar tracks.
   - Cap calculation is deterministic and stable across runs.
   - Non-similar tracks are not removed to satisfy cap.

29. Implement cap logic in `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`:

   - Use `config.SimilarArtistMaxPercent`.
   - Use `discovery.SimilarArtists` to identify similar-source releases by artist ID.
   - Limit similar tracks per playlist before add/remove plan finalization.

30. Update any affected call sites and unit record construction for `DiscoveryResult.SimilarArtists`.

31. Activate DD-019 in `SimilarArtistTests.fs`.

32. Run full summary; verify DD-019 passes.

33. Commit:

       jj commit -m "phase2: enforce similar-artist playlist percentage cap DD-019"

### Milestone 5 steps (DD-020..DD-022)

34. Add unit tests in `Unit.SyncEngine.ArtistFiltering` for:

   - Fetching `/v1/me/ratings/songs` and `/v1/me/ratings/albums`.
   - Parsing dislike ratings (`value = -1`) into excluded artist names.
   - Filtering candidate releases by excluded artist names.

35. Run unit filter (red expected initially):

       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.SyncEngine.ArtistFiltering"

36. Implement in `SyncEngine.fs`:

   - `fetchSongRatings`
   - `fetchAlbumRatings`
   - `collectExcludedArtists`
   - filtering of release candidates before playlist reconciliation

37. Add log emission for excluded artists with code `ExcludedDislikedArtist` and artist name in log data.

38. Activate DD tests in `packages/dropd/tests/Dropd.Tests/ArtistFilteringTests.fs`:

   - DD-020, DD-021, DD-022 (`ptestCase` -> `testCase`)

39. Re-run unit filter; expect pass.

40. Run full summary; expect DD-020..DD-022 active/passing and overall 0 failed, 0 errored.

41. Commit:

       jj commit -m "phase2: implement dislike-based artist filtering DD-020..DD-022"

### Milestone 6 steps (final gate)

42. Run full build and tests:

       nix develop -c dotnet build packages/dropd/dropd.sln
       nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

43. Confirm active/passing coverage now includes:

   - DD-009..DD-019
   - DD-020..DD-022
   - DD-061, DD-068, DD-069

44. Update this plan sections:

   - Progress (with timestamps)
   - Surprises & Discoveries
   - Decision Log
   - Outcomes & Retrospective

45. Final commit:

       jj commit -m "phase2: complete similar discovery, artist filtering, and lastfm auth requirements"

## Validation and Acceptance

Phase 2 is accepted only when all checks below are true:

1. Similar-artist discovery executes via a provider abstraction, not direct inline Last.fm endpoint logic in orchestration code (DD-018).
2. Active DD tests include DD-009..DD-019, DD-020..DD-022, DD-061, DD-068, DD-069.
3. Last.fm auth failure (`error":10` in HTTP 200 body) logs `LastFmAuthFailure` and does not abort sync.
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
      Types.SyncOutcome * ApiContracts.ObservedSync

And keep:

    val runSync :
      Config.ValidSyncConfig ->
      ApiContracts.ApiRuntime ->
      Types.SyncOutcome * ApiContracts.ObservedSync

`runSync` must call `runSyncWithProvider` with the default Last.fm provider.

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
