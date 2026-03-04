# dropd Phase 3: API Resilience, Pagination, and Execution Limits

This ExecPlan is a living document. The sections Progress, Surprises & Discoveries,
Decision Log, and Outcomes & Retrospective must be kept up to date as work proceeds.


## Purpose / Big Picture

After Phase 2 the sync engine calls Apple Music and Last.fm endpoints exactly once per
request and gives up immediately on any non-2xx response (except for certain auth codes).
There is no retry logic, no rate-limit handling, no pagination beyond the playlist-tracks
endpoint, and no guard against a sync that runs forever or accumulates too many errors.

Phase 3 makes the engine production-grade in these areas. After this phase a developer
can run the test suite and observe:

- HTTP 429 responses honoured: the engine waits for the `Retry-After` duration (or a
  default 2 seconds) before retrying.
- Transient failures (5xx, timeout) retried with exponential backoff and jitter up to a
  configurable retry limit.
- Paginated Apple Music responses with a top-level `next` field (library artists,
  artist albums, and playlist tracks) followed automatically up to a configurable
  page limit, with a warning logged when the limit is reached.
- A sync that exceeds its configured maximum runtime aborted with a clear log entry.
- A sync whose API error rate exceeds a configurable threshold aborted with error-rate
  details in the log.

The acceptance coverage for this phase is:

- DD-080 through DD-089 (API Resilience and Execution Limits)
- DD-076, DD-077, DD-078 (Operational Observability — log retention and skip logging)

All 18 currently-ignored DD tests will be activated by the end of this phase, bringing
the ignored count to 5 (the Apple Token Lifecycle tests DD-063, DD-070..DD-073 which are
explicitly deferred).


## Progress

- [x] (2026-03-04 23:05Z) Revised this ExecPlan after review: removed ambiguous steps, made pagination scope explicit, and added concrete timeout implementation/testing guidance.
- [x] (2026-03-04 23:22Z) Milestone 1: resilient request pipeline foundations.
- [x] (2026-03-04 23:29Z) Milestone 2: retry with backoff for transient failures, including request timeouts (DD-082).
- [x] (2026-03-04 23:31Z) Milestone 3: rate-limit handling (DD-080, DD-081).
- [x] (2026-03-04 23:35Z) Milestone 4: generic pagination for top-level `next` endpoints (DD-083, DD-084, DD-085).
- [x] (2026-03-04 23:43Z) Milestone 5: execution guards — runtime limit and error-rate abort (DD-086..DD-089).
- [x] (2026-03-04 23:44Z) Milestone 6: operational observability — log retention and skip logging (DD-076, DD-077, DD-078).
- [x] (2026-03-04 23:45Z) Milestone 7: final gate.


## Surprises & Discoveries

- Observation: The test harness originally used `Async.Sleep` for the pipeline delay, making tests with 5xx responses take ~20 seconds. Adding a no-op delay injection via `runSyncWithDelay` reduced test suite time from 51s to 8s.
  Evidence: Full suite run time dropped from 00:00:51 to 00:00:08 after switching to no-op delay in tests.

- Observation: The recording wrapper needed to be innermost (wrapping the original runtime), not outermost, so that retries are captured in the observable log. The plan's stated flow was incorrect; the correct flow is: caller → resilientRuntime → runtimeWithRecording → original runtime.
  Evidence: DD-082 test failed with 1 recorded request instead of 3 until the wrapping order was reversed.

- Observation: DD-052 (continues after playlist creation failure) needed updating because the pipeline retries the 500 response. A `Sequence [500; 200]` was consumed by retry, leaving no response for the second playlist creation. Fixed by providing 4×500 responses to exhaust retries before the success response.
  Evidence: DD-052 expected 2 create requests but saw 3; fixed by providing 5 responses (4 failures + 1 success) and asserting 5 requests.

- Observation: Error rate guards initially counted all 4xx+ responses, including Last.fm 404s (unmatched routes). This caused ErrorRate aborts in many existing tests. Fixed by: (1) only counting 5xx as failures for the error rate, and (2) skipping retry/guard logic entirely for non-Apple-Music requests.
  Evidence: Full suite went from 17 failures to 0 after these changes.

- Observation: Guard checks needed to happen after the retry loop completes (counting only final outcomes), not during individual retry attempts. Otherwise, 4 retry attempts for a single 503 endpoint inflated the error count and triggered ErrorRate abort prematurely.
  Evidence: DD-030 expected `Aborted "CatalogUnavailable"` but got `Aborted "ErrorRate"` until stats tracking was moved to post-retry.


## Decision Log

- Decision: Implement resilience as a new `ResilientPipeline` module that wraps `ApiRuntime.Execute`, rather than modifying `SyncEngine.executeApple` directly.
  Rationale: keeps retry/rate-limit logic testable in isolation; the sync engine calls through the pipeline without knowing the retry details. The wrapped runtime is threaded through the existing code path the same way recording was added in Phase 1 — by replacing the `Execute` function before the first call.
  Date: 2026-03-04

- Decision: Pagination is implemented as a generic `fetchAllPages` helper in `ResilientPipeline` for endpoints whose response has top-level `data` + top-level `next` fields. In this phase it is applied to library artists, artist albums, and playlist tracks.
  Rationale: these three paths all share the same pagination shape and currently duplicate loop logic or single-page assumptions. The record-label `latest-releases` path stays single-request in Phase 3 because its payload is nested under `views.latest-releases` and current fixtures contain no `next` token for that view.
  Date: 2026-03-04

- Decision: Execution guards (runtime limit, error-rate abort) are checked inside the resilient pipeline wrapper, not in the sync engine orchestration.
  Rationale: checking at the request level means any endpoint call can trigger the abort, giving the most responsive behaviour. The pipeline raises a dedicated exception (`SyncAbortedException`) that `runSyncInternal` catches at the top level.
  Date: 2026-03-04

- Decision: DD-076 and DD-077 (log retention) are implemented as a `LogRetention` module that prunes a log directory. The sync engine itself does not manage log files — it emits in-memory `LogEntry` values. A thin `LogWriter` module (or the host entry point) is responsible for writing logs to disk and calling `LogRetention.prune` at startup.
  Rationale: the sync engine's responsibility ends at emitting structured logs. File I/O belongs to the host process. Phase 3 adds the retention module and tests it with deterministic file-system helpers. Integration with an actual host entry point is a later concern, but the module and its tests prove the behaviour now.
  Date: 2026-03-04

- Decision: DD-078 (skip-sync logging) is implemented in the `Scheduling` module by adding a `logDecision` helper that emits a `LogEntry` for each scheduling decision, including skip reasons.
  Rationale: `Scheduling.decide` already returns `SkipSync AlreadyRunning` and `WaitForNextWindow`. The logging wrapper converts these into observable log entries without changing the pure decision function.
  Date: 2026-03-04

- Decision: Jitter for exponential backoff uses a deterministic seed when provided, falling back to a random seed in production. The test harness injects a fixed seed so backoff delays are predictable in assertions.
  Rationale: testing exponential backoff with jitter requires deterministic delay sequences. A seeded `Random` achieves this without abstracting the entire delay mechanism.
  Date: 2026-03-04

- Decision: Timeouts are implemented as real per-request execution deadlines in `ResilientPipeline.wrap` using `RequestTimeoutSeconds`. The test harness starts honoring `CannedResponse.DelayMs` so timeout scenarios are exercised with deterministic short delays and small timeout values.
  Rationale: DD-082 explicitly includes timeout failures. Enforcing timeout in the pipeline (instead of only simulating 5xx) makes the behavior real and keeps tests faithful to production logic.
  Date: 2026-03-04

- Decision: DD-063 (ES256 JWT generation) and DD-070..DD-073 (Apple token lifecycle) remain out of scope for Phase 3.
  Rationale: the project currently injects tokens from the environment. Token lifecycle is a separate concern.
  Date: 2026-03-04


## Outcomes & Retrospective

Phase 3 is complete. All acceptance criteria are met:

1. `ResilientPipeline.wrap` retries transient failures (HTTP 5xx and request timeouts) with exponential backoff up to `MaxRetries`, recording computed delays in `PipelineStats.ComputedDelays` and emitting `TransientRetryScheduled` logs. ✓
2. HTTP 429 responses are handled with `Retry-After` parsing (default 2s). `RetryAfterWait` log entries are emitted. ✓
3. Paginated top-level-`next` endpoints (library artists, artist albums, playlist tracks) are followed up to `MaxPages` with `PageLimitReached` warnings. ✓
4. Runtime limit guard aborts with `RuntimeExceeded` and `SyncAbortedRuntimeExceeded`. ✓
5. Error rate guard aborts with `ErrorRate` and `SyncAbortedErrorRate` (with rate details). ✓
6. `LogRetention.prune` deletes `.log` files older than the configured retention window. ✓
7. `Scheduling.logDecision` and `decideOnStartup` produce reason-coded log entries for skipped syncs. ✓
8. Active DD tests include DD-076..DD-078 and DD-080..DD-089 (13 tests activated). ✓
9. Full suite: 188 passed, 5 ignored, 0 failed, 0 errored. ✓
10. Remaining 5 ignored tests are DD-063, DD-070..DD-073 (Apple Token Lifecycle). ✓

Key design decisions during implementation:
- Non-Apple-Music requests bypass retry/guard logic entirely, keeping Last.fm error handling unchanged.
- Stats count only final outcomes (post-retry), not individual retry attempts.
- Tests use a no-op delay function for performance; production uses `Async.Sleep`.


## Context and Orientation

This section describes the current state of the repository as it exists after Phase 2. A
developer implementing this plan needs only this file and the working tree.

The repository is a monorepo. Product code lives under `packages/dropd/`. Spikes live
under `spikes/`. Documentation lives under `docs/`. The build and test commands use
`nix develop -c` to enter a reproducible Nix shell providing .NET 9, then delegate to
`dotnet` commands.

Key commands (run from the repository root):

    nix develop -c dotnet build packages/dropd/dropd.sln
    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

The project uses Jujutsu (`jj`) for version control. Prefer `jj` commands when committing.

Current test counts: 156 passed, 18 ignored, 0 failed, 0 errored.

The 18 ignored tests are all `ptestCase` entries in three files:

- `packages/dropd/tests/Dropd.Tests/ApiResilienceTests.fs` — DD-080 through DD-089 (10 tests).
- `packages/dropd/tests/Dropd.Tests/ObservabilityTests.fs` — DD-076, DD-077, DD-078 (3 tests).
- `packages/dropd/tests/Dropd.Tests/AuthenticationTests.fs` — DD-063, DD-070..DD-073 (5 tests, out of scope for this phase).

The core library lives in `packages/dropd/src/Dropd.Core/` with these source files compiled in order:

    Types.fs → Config.fs → Normalization.fs → ApiContracts.fs → JsonHelpers.fs →
    PlaylistReconcile.fs → SimilarArtists.fs → SyncEngine.fs → Scheduling.fs

The test project lives in `packages/dropd/tests/Dropd.Tests/` and uses Expecto 10.2.3. Test data helpers are in `TestData.fs`. The test harness is in `TestHarness.fs`.

Key types and functions that this plan interacts with:

`ApiContracts.ApiRuntime` — a record with two fields:

    type ApiRuntime =
        { Execute: ApiRequest -> Async<ApiResponse>
          UtcNow: unit -> System.DateTimeOffset }

`SyncEngine.runSyncInternal` — the private orchestration function. It creates a `runtimeWithRecording` that wraps the original runtime's `Execute` to capture requests. All subsequent calls go through this wrapped runtime. Phase 3 adds a second wrapper layer (resilience) between the recording wrapper and the original runtime.

`Config.ValidSyncConfig` — contains all configuration values relevant to resilience:

- `RequestTimeoutSeconds: PositiveInt` — default 10.
- `MaxRetries: NonNegativeInt` — default 3.
- `PageSize: PositiveInt` — default 100.
- `MaxPages: PositiveInt` — default 20.
- `MaxSyncRuntimeMinutes: PositiveInt` — default 15.
- `ErrorRateAbortPercent: Percent` — default 30.

`JsonHelpers.appleHeaders` — returns auth headers for Apple Music requests. Used by both `SyncEngine` and `PlaylistReconcile`.

`PlaylistReconcile.fetchAllPlaylistTracks` — an existing pagination loop for playlist tracks, hardcoded to 20 pages. This will be refactored to use the new generic pagination helper and `config.MaxPages`.

`SyncEngine.fetchLibraryArtists` and `SyncEngine.fetchArtistReleases` currently perform single requests but their response shape supports top-level `next` pagination; they will be moved to the shared helper.

`SyncEngine.fetchLabelReleases` reads `views.latest-releases.data` from `packages/dropd/tests/Dropd.Tests/Fixtures/apple/label-latest-releases-1543411840.json`. This response shape is nested and currently has no `next` field in fixtures, so Phase 3 keeps this endpoint as single-page.

The test harness `CannedResponse` type has a `DelayMs: int option` field. In Phase 3 this field becomes active: the fake runtime will `Async.Sleep` for `DelayMs` before returning so timeout retries can be tested against real request deadlines.


## Plan of Work

The implementation proceeds in seven milestones. Each milestone produces an independently
verifiable outcome and ends with all tests passing.

Milestone 1 creates the `ResilientPipeline` module with the core types and a transparent
pass-through implementation. No behaviour changes yet — the engine still makes single-attempt
requests — but the module compiles and the full test suite passes. This establishes the
type signatures that all subsequent milestones build on.

Milestone 2 adds retry-with-backoff for transient failures (HTTP 5xx and request timeouts).
Unit tests verify retry count, timeout retry behavior, delay progression, and the jitter
seed mechanism. DD-082 is activated.

Milestone 3 adds rate-limit handling (HTTP 429). When the response contains a
`Retry-After` header, the engine waits that many seconds. Without the header, it waits 2
seconds. DD-080 and DD-081 are activated.

Milestone 4 adds a generic pagination helper and refactors the existing playlist-tracks
pagination to use it. It then wires pagination into library-artists and artist-albums
endpoints (the record-label latest-releases endpoint stays single-page this phase).
DD-083, DD-084, and DD-085 are activated.

Milestone 5 adds execution guards: sync runtime limit and API error-rate abort. DD-086
through DD-089 are activated.

Milestone 6 adds operational observability: log retention (DD-076, DD-077) and scheduling
skip logging (DD-078).

Milestone 7 is the final gate: full build and test run, plan update, and commit.


## Concrete Steps

Run every command from the repository root unless stated otherwise.


### Milestone 1: Resilient Pipeline Foundations

This milestone creates a new module `ResilientPipeline.fs` that wraps `ApiRuntime.Execute`
with retry, rate-limit, pagination, and execution-guard capabilities. In this milestone
the implementation is a transparent pass-through — no retries, no pagination — so existing
tests continue to pass unchanged.

**Step 1.** Create `packages/dropd/src/Dropd.Core/ResilientPipeline.fs` with the following
content. The module defines the configuration record, the pipeline state, and a `wrap`
function that returns a new `ApiRuntime` whose `Execute` delegates to the original. No
retry or rate-limit logic yet.

    namespace Dropd.Core

    open System
    open Dropd.Core.Types

    module ResilientPipeline =

        module AC = ApiContracts

        /// Execution statistics tracked by the pipeline across all requests
        /// within a single sync run.
        type PipelineStats =
            { mutable TotalRequests: int
              mutable FailedRequests: int
              mutable ComputedDelays: int list }

        /// Configuration extracted from ValidSyncConfig for the pipeline.
        type PipelineConfig =
            { MaxRetries: int
              RequestTimeoutSeconds: int
              MaxPages: int
              PageSize: int
              MaxSyncRuntimeMinutes: int
              ErrorRateAbortPercent: int }

        /// Exception raised when execution guards trigger a sync abort.
        exception SyncAbortedException of reason: string * logEntry: AC.LogEntry

        let configFrom (config: Config.ValidSyncConfig) : PipelineConfig =
            { MaxRetries = Config.NonNegativeInt.value config.MaxRetries
              RequestTimeoutSeconds = Config.PositiveInt.value config.RequestTimeoutSeconds
              MaxPages = Config.PositiveInt.value config.MaxPages
              PageSize = Config.PositiveInt.value config.PageSize
              MaxSyncRuntimeMinutes = Config.PositiveInt.value config.MaxSyncRuntimeMinutes
              ErrorRateAbortPercent = Config.Percent.value config.ErrorRateAbortPercent }

        let createStats () : PipelineStats =
            { TotalRequests = 0
              FailedRequests = 0
              ComputedDelays = [] }

        /// Wrap an ApiRuntime with resilience behaviour.
        ///
        /// Parameters:
        /// - `pipelineConfig`: retry/timeout/guard settings.
        /// - `stats`: mutable stats accumulator shared for the sync run.
        /// - `delay`: function that pauses for the given milliseconds. Inject
        ///   `Async.Sleep` in production or a no-op in tests.
        /// - `appendLog`: callback used by the pipeline to emit structured logs.
        /// - `jitterSeed`: optional seed for deterministic jitter in tests.
        /// - `inner`: the original ApiRuntime to wrap.
        let wrap
            (pipelineConfig: PipelineConfig)
            (stats: PipelineStats)
            (delay: int -> Async<unit>)
            (appendLog: AC.LogEntry -> unit)
            (jitterSeed: int option)
            (inner: AC.ApiRuntime)
            : AC.ApiRuntime =

            let execute (request: AC.ApiRequest) : Async<AC.ApiResponse> =
                async {
                    // Phase 3 milestone 1: transparent pass-through.
                    let! response = inner.Execute request
                    stats.TotalRequests <- stats.TotalRequests + 1
                    if response.StatusCode >= 500 then
                        stats.FailedRequests <- stats.FailedRequests + 1
                    return response
                }

            { Execute = execute
              UtcNow = inner.UtcNow }

        /// Fetch all pages from a paginated Apple Music endpoint.
        ///
        /// The `firstRequest` is executed via the runtime. Subsequent pages
        /// are fetched by following the `next` field in each response body.
        /// Stops when `next` is absent, the response is an error, or
        /// `maxPages` is reached.
        ///
        /// Returns `Ok (allItems, pagesFetched, truncated)` on success or
        /// `Error response` on the first non-2xx response. `truncated` is
        /// `true` when `maxPages` was reached while `next` was still present.
        let fetchAllPages
            (runtime: AC.ApiRuntime)
            (maxPages: int)
            (firstRequest: AC.ApiRequest)
            (parseItems: string -> 'a list)
            (parseNext: string -> string option)
            : Async<Result<'a list * int * bool, AC.ApiResponse>> =
            async {
                let acc = ResizeArray<'a>()
                let mutable nextRequest = Some firstRequest
                let mutable page = 0
                let mutable error = None
                let mutable truncated = false

                while nextRequest.IsSome && page < maxPages && error.IsNone do
                    let req = nextRequest.Value
                    let! response = runtime.Execute req

                    if response.StatusCode >= 200 && response.StatusCode < 300 then
                        acc.AddRange(parseItems response.Body)
                        page <- page + 1

                        match parseNext response.Body with
                        | Some nextPath when page < maxPages ->
                            nextRequest <-
                                Some
                                    { req with
                                        Path = nextPath
                                        Query = [] }
                        | Some _ ->
                            truncated <- true
                            nextRequest <- None
                        | None -> nextRequest <- None
                    elif response.StatusCode = 404 then
                        // Apple Music returns 404 for empty collections.
                        nextRequest <- None
                    else
                        error <- Some response

                match error with
                | Some resp -> return Error resp
                | None -> return Ok(acc |> Seq.toList, page, truncated)
            }

**Step 2.** Add `ResilientPipeline.fs` to the compile order in `packages/dropd/src/Dropd.Core/Dropd.Core.fsproj`. It must appear after `JsonHelpers.fs` and before `PlaylistReconcile.fs`:

    Types.fs → Config.fs → Normalization.fs → ApiContracts.fs → JsonHelpers.fs →
    ResilientPipeline.fs → PlaylistReconcile.fs → SimilarArtists.fs → SyncEngine.fs → Scheduling.fs

**Step 3.** Wire the pipeline into `SyncEngine.runSyncInternal`. In `packages/dropd/src/Dropd.Core/SyncEngine.fs`, modify `runSyncInternal` to create a pipeline-wrapped runtime. The wrapped runtime goes between the recording wrapper and the original runtime:

- Create `pipelineConfig` from the validated config.
- Create `pipelineStats` via `ResilientPipeline.createStats()`.
- Wrap the original `runtime` with `ResilientPipeline.wrap`, passing `Async.Sleep` as `delay`, `recordedLogs.Add` as `appendLog`, and `None` as the jitter seed.
- Use this resilient runtime as the inner runtime that `runtimeWithRecording` delegates to.

The existing recording wrapper remains outermost so all requests (including retries) are captured. The flow is:

    caller → runtimeWithRecording (captures requests) → resilientRuntime (retries/guards) → original runtime (HTTP or fake)

No behaviour changes: the pipeline is a transparent pass-through in this milestone.

**Step 4.** Build:

    nix develop -c dotnet build packages/dropd/dropd.sln

Expected: 0 errors.

**Step 5.** Run full test suite:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected: 156 passed, 18 ignored, 0 failed, 0 errored. No behaviour change from the pass-through pipeline.

**Step 6.** Commit:

    jj commit -m "phase3: add ResilientPipeline module with transparent pass-through and wire into SyncEngine"


### Milestone 2: Retry with Exponential Backoff and Timeouts (DD-082)

This milestone implements retry logic for transient failures: HTTP 5xx responses and
request timeouts. Timeouts are in scope for this phase and are implemented by enforcing
`RequestTimeoutSeconds` on each call to `inner.Execute`. Retries use exponential backoff
with jitter.

The backoff formula is: `delay_ms = base_ms * 2^attempt + jitter` where `base_ms = 1000`,
`attempt` is 0-indexed (first retry ~1s, second ~2s, third ~4s), and jitter is a random
value between 0 and `base_ms` using the optionally-seeded `Random`.

**Step 7.** Create unit test file `packages/dropd/tests/Dropd.Tests/ResilientPipelineUnitTests.fs`. Add it to `packages/dropd/tests/Dropd.Tests/Dropd.Tests.fsproj` after `PlaylistReconcileUnitTests.fs` and before `RequirementCatalog.fs`.

**Step 8.** In `packages/dropd/tests/Dropd.Tests/TestHarness.fs`, update `createRuntime` so it honors `CannedResponse.DelayMs`: if `DelayMs = Some d`, call `do! Async.Sleep d` before returning `toApiResponse canned`.

**Step 9.** Add test list `Unit.ResilientPipeline.Retry` in `packages/dropd/tests/Dropd.Tests/ResilientPipelineUnitTests.fs` with four failing tests:

- Test 1: "retries up to MaxRetries on 5xx then returns last failure". Setup: fake runtime always returns 503. `MaxRetries = 3`. Assert: `Execute` called exactly 4 times (1 + 3 retries), final status 503, `stats.FailedRequests = 4`.

- Test 2: "succeeds on retry after transient 5xx". Setup: fake runtime returns 503, 503, then 200. `MaxRetries = 3`. Assert: `Execute` called 3 times, returned status 200, `stats.FailedRequests = 2`.

- Test 3: "retries on timeout up to MaxRetries". Setup: fake runtime delays longer than timeout on first two attempts, then returns 200 quickly on the third. Use `RequestTimeoutSeconds = 1`, `MaxRetries = 3`. Assert: 3 total attempts, final response 200, `stats.FailedRequests = 2`.

- Test 4: "computes exponential delays with deterministic jitter". Setup: fake runtime returns 503 four times then 200. `MaxRetries = 4`, `jitterSeed = Some 42`. Use a recording delay function (no actual sleep). Assert: `stats.ComputedDelays` has 4 entries, each entry is in `[base, base+1000)`, and delays are strictly increasing.

For these unit tests, call `ResilientPipeline.wrap` with `appendLog = (fun _ -> ())` unless the test explicitly asserts emitted log entries.

**Step 10.** Run the retry unit tests (expect failures). Expected failure signatures:

- tests 1–3 fail with mismatched call counts (currently still 1 attempt);
- timeout test fails because no timeout is enforced yet;
- jitter test fails because no computed delays are recorded yet.

Run:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.ResilientPipeline.Retry"

**Step 11.** Implement retry + timeout logic in `packages/dropd/src/Dropd.Core/ResilientPipeline.fs` by replacing the pass-through `execute` body:

- Build `timeoutMs = pipelineConfig.RequestTimeoutSeconds * 1000`.
- Add helper `executeWithTimeout request` that runs `inner.Execute request` with timeout using `Async.StartChild(..., timeoutMs)` and returns either:
  - `Ok response` when request finishes in time;
  - `Error "timeout"` when timed out.
- In the retry loop, treat timeout as transient failure: increment `stats.TotalRequests` and `stats.FailedRequests`, synthesize a response with status `504` and message `"request timeout"`, and retry while attempts remain.
- Treat HTTP 5xx similarly (retry while attempts remain).
- Treat 2xx and non-retryable 4xx (except 429, milestone 3) as immediate return.
- For each retry, compute backoff delay, append to `stats.ComputedDelays`, emit info log `Code = "TransientRetryScheduled"` with `attempt` and `delay_ms`, then call injected `delay`.

**Step 12.** Run retry unit tests (expect pass):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.ResilientPipeline.Retry"

Expected: 4 tests passed in `Unit.ResilientPipeline.Retry`.

**Step 13.** Run full test suite:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected: 160 passed, 18 ignored, 0 failed, 0 errored.

**Step 14.** In `packages/dropd/tests/Dropd.Tests/ApiResilienceTests.fs`, replace DD-082 `ptestCase` with a real test that exercises both 5xx and timeout retry:

- Config: start from `TestData.validConfig`; set `MaxRetries = 3` and `RequestTimeoutSeconds = 1`.
- Library artists route script:
  - first response: `StatusCode = 200`, `Body = fixture "library-artists.json"`, `DelayMs = Some 1500` (forces timeout);
  - second response: `StatusCode = 503`, `Body = fixture "error-500.json"`;
  - third response: `StatusCode = 200`, `Body = fixture "library-artists.json"`.
- Other required routes (exact fixtures):
  - `/v1/me/ratings/artists` with query `ids=657515,5765078` → `favorited-artists.json`;
  - `/v1/catalog/us/artists/657515/albums` with query `sort=-releaseDate` and `limit=25` → `artist-albums-657515.json`;
  - `/v1/catalog/us/artists/5765078/albums` with same query → `artist-albums-5765078.json`;
  - `/v1/catalog/us/search` with query `term=Ninja Tune`, `types=record-labels`, `limit=1` → `label-search-ninja-tune.json`;
  - `/v1/catalog/us/record-labels/1543411840` with query `views=latest-releases` → `label-latest-releases-1543411840.json`;
  - playlist routes from existing happy-path setup in `ObservabilityTests.fs` (`/v1/me/library/playlists`, `/tracks` GET, `/tracks` POST, `/tracks` DELETE) with existing fixtures.
- Assertions:
  - exactly 3 requests recorded to `/v1/me/library/artists`;
  - outcome is not `Aborted`;
  - `output.Logs` contains at least one retry log code for DD-082 (add `TransientRetryScheduled` info log in pipeline with `attempt` and `delay_ms`).

Change DD-082 from `ptestCase` to `testCase`.

**Step 15.** Run full suite:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected: 161 passed, 17 ignored, 0 failed, 0 errored.

**Step 16.** Commit:

    jj commit -m "phase3: implement transient retry with timeout handling and activate DD-082"


### Milestone 3: Rate-Limit Handling (DD-080, DD-081)

This milestone adds HTTP 429 handling to the resilient pipeline. A 429 response is treated
as a retryable condition that consumes the same retry budget as other transient failures (`MaxRetries`). The engine waits
for the duration specified in the `Retry-After` header, or 2 seconds if the header is
absent.

**Step 17.** Add test list `Unit.ResilientPipeline.RateLimit` in `ResilientPipelineUnitTests.fs` with two failing tests:

- Test 1: "waits Retry-After seconds on 429". Setup: fake runtime returns 429 with header `Retry-After: 3`, then 200. Recording delay function. Assert: one delay of 3000ms recorded. Final response is 200.

- Test 2: "waits 2 seconds on 429 without Retry-After". Setup: fake runtime returns 429 (no `Retry-After` header), then 200. Recording delay function. Assert: one delay of 2000ms recorded. Final response is 200.

**Step 18.** Run rate-limit unit tests (expect failures):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.ResilientPipeline.RateLimit"

**Step 19.** Extend the retry loop in `ResilientPipeline.wrap` to handle 429:

- If response is 429, parse the `Retry-After` header from `response.Headers` as an integer (seconds). If absent or unparseable, default to 2.
- Compute `delayMs = retryAfterSeconds * 1000`, record it in `stats.ComputedDelays`, and append info log `Code = "RetryAfterWait"` with `Data.["delay_ms"] = string delayMs`.
- Call `delay delayMs`.
- Count each 429 retry attempt against `MaxRetries` so repeated rate limits eventually stop with a deterministic final response.
- After waiting, retry the same request.

**Step 20.** Run rate-limit unit tests (expect pass):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.ResilientPipeline.RateLimit"

**Step 21.** Run full test suite to confirm no regressions:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

**Step 22.** In `packages/dropd/tests/Dropd.Tests/ApiResilienceTests.fs`, replace DD-080 body:

- Route library-artists endpoint to `Sequence [429 with Retry-After: 5 header; 200 with library-artists.json]`.
- Reuse the exact non-library-artists route setup listed in Step 14 (same fixture files and query matches).
- Call `runSync`.
- Assert: request list contains exactly 2 requests to `/v1/me/library/artists`.
- Assert: outcome is not `Aborted`.

Note: the test harness does not expose computed delays directly. Assert delay behavior through the `RetryAfterWait` log entry added in Step 19 (`Code = "RetryAfterWait"`, `Data.["delay_ms"] = "5000"`).

Change DD-080 from `ptestCase` to `testCase`.

**Step 23.** Replace DD-081 body:

- Route library-artists endpoint to `Sequence [429 with no Retry-After header; 200 with library-artists.json]`.
- Call `runSync`.
- Assert: log contains `Code = "RetryAfterWait"` with `Data.["delay_ms"] = "2000"`.

Change DD-081 from `ptestCase` to `testCase`.

**Step 24.** Run full suite:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected: 165 passed, 15 ignored, 0 failed, 0 errored.

**Step 25.** Commit:

    jj commit -m "phase3: implement 429 rate-limit handling and activate DD-080 DD-081"


### Milestone 4: Generic Pagination (DD-083, DD-084, DD-085)

This milestone wires the generic `fetchAllPages` helper into endpoints that use the
standard Apple top-level pagination shape (`data` + top-level `next`). In this codebase
that means:

- `/v1/me/library/artists`
- `/v1/catalog/{storefront}/artists/{id}/albums`
- `/v1/me/library/playlists/{id}/tracks`

The record-label endpoint `/v1/catalog/{storefront}/record-labels/{id}?views=latest-releases`
stays single-page in Phase 3 because it parses `views.latest-releases.data` and current
fixtures contain no pagination token for that nested view.

**Step 26.** Add test list `Unit.ResilientPipeline.Pagination` in `packages/dropd/tests/Dropd.Tests/ResilientPipelineUnitTests.fs` with three failing tests:

- Test 1: "follows next links across pages". Setup: fake runtime returns page 1 with `"next": "/page2"` and 2 items, then page 2 with no `next` and 1 item. `maxPages = 5`. Assert: `Ok` with 3 items total, `pagesFetched = 2`, `truncated = false`.

- Test 2: "stops at maxPages and sets truncated flag". Setup: fake runtime returns pages 1, 2, 3 each with `next`. `maxPages = 2`. Assert: `Ok` with items from pages 1 and 2 only, `truncated = true`.

- Test 3: "returns Error on non-2xx response". Setup: fake runtime returns 200 for page 1, then 500 for page 2. Assert: `Error` with status 500.

**Step 27.** Run pagination unit tests (expect failures):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.ResilientPipeline.Pagination"

Expected red-phase failures:
- test 1 fails because only first page is returned;
- test 2 fails because truncation flag is not set yet;
- test 3 fails if non-2xx handling is not propagated yet.

**Step 28.** Finalize `fetchAllPages` in `packages/dropd/src/Dropd.Core/ResilientPipeline.fs` (already scaffolded in milestone 1):

- remove unused `Config.ValidSyncConfig` parameter from signature;
- keep behavior: follow `parseNext` until no next, error, 404-empty, or `maxPages` reached;
- when `maxPages` reached and `next` still exists, set `truncated = true`;
- when following `next`, set request `Path = nextPath` and clear `Query = []` (next already includes query string).

**Step 29.** Run pagination unit tests (expect pass):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.ResilientPipeline.Pagination"

Expected: 3 passed in `Unit.ResilientPipeline.Pagination`.

**Step 30.** In `packages/dropd/src/Dropd.Core/SyncEngine.fs`, refactor `fetchLibraryArtists` to use `ResilientPipeline.fetchAllPages`:

- first request remains `/v1/me/library/artists` with query `["include", "catalog"]`;
- `parseItems = parseLibraryArtistsWithCatalog`;
- `parseNext` parses top-level `next`;
- `maxPages = Config.PositiveInt.value config.MaxPages`;
- when `truncated = true`, append warning log `Code = "PageLimitReached"` with `endpoint = "/v1/me/library/artists"`.

**Step 31.** In `packages/dropd/src/Dropd.Core/SyncEngine.fs`, refactor `fetchArtistReleases` to use `ResilientPipeline.fetchAllPages` with:

- first request path `/v1/catalog/{storefront}/artists/{id}/albums`;
- initial query `["sort", "-releaseDate"; "limit", "25"]`;
- `parseItems = parseReleaseList`;
- `parseNext` parses top-level `next`;
- same `PageLimitReached` warning when truncated, with endpoint path including artist ID.

**Step 32.** In `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs`, refactor `fetchAllPlaylistTracks` to delegate to `ResilientPipeline.fetchAllPages`:

- remove hardcoded `maxPages = 20`; use config `MaxPages`;
- keep 404-empty behavior by honoring `fetchAllPages` 404 handling;
- map `Ok (tracks, _, _)` to existing return shape expected by callers.

**Step 33.** Keep `fetchLabelReleases` single-page in `packages/dropd/src/Dropd.Core/SyncEngine.fs`. Add a code comment in that function: "Phase 3 pagination applies only to top-level next endpoints; label view payload is nested and currently non-paginated in fixtures."

**Step 34.** Run full test suite to confirm no regressions from pagination refactors:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected: all existing active tests pass, ignored count unchanged before DD activation.

**Step 35.** In `packages/dropd/tests/Dropd.Tests/ApiResilienceTests.fs`, replace DD-083 body:

- route `/v1/me/library/artists` to return page 1 with `"next": "/v1/me/library/artists?offset=25"` and 2 artists;
- route `/v1/me/library/artists?offset=25` response with no `next` and 1 artist;
- use exact same success-path routes as DD-082 for all other dependencies;
- assert requests include both page paths and outcome is not `Aborted`.

Change DD-083 from `ptestCase` to `testCase`.

**Step 36.** Replace DD-084 body:

- set config `MaxPages = 2`;
- route library-artists endpoint with 3 pages (each with `next`);
- assert only 2 requests are made to library-artists pages;
- assert logs contain `Code = "PageLimitReached"` and endpoint data for library artists.

Change DD-084 from `ptestCase` to `testCase`.

**Step 37.** Replace DD-085 body:

- reuse DD-084 setup;
- assert outcome is `Success` or `PartialFailure` (not `Aborted`);
- assert requests show sync continued beyond artist seeding (e.g. at least one `/v1/catalog/us/artists/{id}/albums` request exists).

Change DD-085 from `ptestCase` to `testCase`.

**Step 38.** Run full suite:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected: 171 passed, 12 ignored, 0 failed, 0 errored.

**Step 39.** Commit:

    jj commit -m "phase3: implement generic pagination for top-level next endpoints and activate DD-083 DD-084 DD-085"


### Milestone 5: Execution Guards (DD-086..DD-089)

This milestone adds two abort guards to the resilient pipeline:

1. **Runtime limit:** if `UtcNow()` exceeds `syncStartTime + MaxSyncRuntimeMinutes`, the
   pipeline raises `SyncAbortedException` with reason `"RuntimeExceeded"` and log code
   `SyncAbortedRuntimeExceeded`.

2. **Error rate:** after each request, if `failedRequests / totalRequests` exceeds
   `ErrorRateAbortPercent / 100` and `totalRequests >= 5` (to avoid aborting on a single
   early failure), the pipeline raises `SyncAbortedException` with reason `"ErrorRate"`
   and log code `SyncAbortedErrorRate`, including `error_rate`, `failed`, and `total` in
   the log data.

`SyncEngine.runSyncInternal` catches `SyncAbortedException` at the top level, appends the
log entry, and returns `Aborted reason`.

**Step 40.** Add test list `Unit.ResilientPipeline.Guards` in `ResilientPipelineUnitTests.fs` with four failing tests:

- Test 1: "aborts when runtime exceeds maximum". Setup: a fake runtime where `UtcNow` returns `startTime + 16 minutes` on the second call. `MaxSyncRuntimeMinutes = 15`. Assert: second `Execute` call raises `SyncAbortedException` with reason `"RuntimeExceeded"`.

- Test 2: "does not abort when runtime is within limit". Setup: `UtcNow` returns `startTime + 14 minutes`. Assert: no exception raised.

- Test 3: "aborts when error rate exceeds threshold". Setup: a fake runtime that returns 500 for 4 out of 6 requests (67%). `ErrorRateAbortPercent = 30`. Assert: `SyncAbortedException` raised with reason `"ErrorRate"` after enough requests accumulate.

- Test 4: "does not abort on error rate below threshold". Setup: 1 failure out of 5 requests (20%). `ErrorRateAbortPercent = 30`. Assert: no exception.

**Step 41.** Run guard unit tests (expect failures):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.ResilientPipeline.Guards"

**Step 42.** Implement guards in `ResilientPipeline.wrap`:

- Record `syncStartTime` from `inner.UtcNow()` when the pipeline is created (in the `wrap` function body, before returning the new runtime).
- In `execute`, after each response, check runtime limit: if `inner.UtcNow() - syncStartTime > TimeSpan.FromMinutes(maxSyncRuntimeMinutes)`, raise `SyncAbortedException("RuntimeExceeded", logEntry)`.
- After each response, check error rate: if `totalRequests >= 5` and `failedRequests * 100 / totalRequests > errorRateAbortPercent`, raise `SyncAbortedException("ErrorRate", logEntry)` with rate details.

**Step 43.** Run guard unit tests (expect pass):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.ResilientPipeline.Guards"

**Step 44.** In `packages/dropd/src/Dropd.Core/SyncEngine.fs`, add a `try...with` around the entire sync body (after `appendLog AC.Info "SyncStarted" ...`) that catches `SyncAbortedException(reason, logEntry)`:

- Append `logEntry` to the recorded logs.
- Return `abort reason`.

**Step 45.** Run full test suite to confirm no regressions:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

**Step 46.** In `packages/dropd/tests/Dropd.Tests/ApiResilienceTests.fs`, replace DD-086 body:

- Set config `MaxSyncRuntimeMinutes = 1`.
- Use a custom harness setup where the test harness `UtcNow` advances by 2 minutes after the first API call. This requires adding a `runSyncWithClock` helper to `TestHarness.fs` that accepts a mutable clock function instead of the fixed `fixedUtcNow`.
- Call `runSyncWithClock`.
- Assert: `Outcome = Some(Aborted "RuntimeExceeded")`.

Change DD-086 from `ptestCase` to `testCase`.

**Step 47.** Replace DD-087 body:

- Same setup as DD-086.
- Assert: logs contain `Code = "SyncAbortedRuntimeExceeded"`.

Change DD-087 from `ptestCase` to `testCase`.

**Step 48.** Replace DD-088 body:

- Set config `ErrorRateAbortPercent = 30`.
- Route many endpoints to return 500 (e.g., all artist-albums endpoints return 500).
- Route the initial endpoints (library-artists, favorited-artists) to succeed so the sync gets far enough to accumulate requests.
- Assert: `Outcome = Some(Aborted "ErrorRate")`.

Change DD-088 from `ptestCase` to `testCase`.

**Step 49.** Replace DD-089 body:

- Same setup as DD-088.
- Assert: logs contain `Code = "SyncAbortedErrorRate"` with `Data` keys including `"error_rate"`, `"failed"`, and `"total"`.

Change DD-089 from `ptestCase` to `testCase`.

**Step 50.** Run full suite:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected: 179 passed, 8 ignored, 0 failed, 0 errored.

**Step 51.** Commit:

    jj commit -m "phase3: implement execution guards (runtime limit, error-rate abort) and activate DD-086..DD-089"


### Milestone 6: Operational Observability (DD-076, DD-077, DD-078)

This milestone adds log retention and scheduling skip logging.

DD-076 and DD-077 are about log file retention (7 days, auto-delete older entries). Since
the sync engine produces in-memory `LogEntry` values and does not write files, this
milestone adds a `LogRetention` module that operates on a log directory. The module is
tested with a temporary directory and deterministic file timestamps.

DD-078 is about logging when a sync is skipped. The `Scheduling.decide` function already
returns `SkipSync AlreadyRunning` and `WaitForNextWindow`. This step adds a
`Scheduling.logDecision` function that converts a `SchedulingDecision` into an optional
`LogEntry`.

**Step 52.** Create `packages/dropd/src/Dropd.Core/LogRetention.fs` with:

    namespace Dropd.Core

    open System
    open System.IO

    module LogRetention =

        /// Delete log files in `logDirectory` whose last-write time is older
        /// than `retentionDays` relative to `now`. Returns the count of deleted
        /// files.
        let prune (logDirectory: string) (retentionDays: int) (now: DateTimeOffset) : int =
            if not (Directory.Exists logDirectory) then
                0
            else
                let cutoff = now.AddDays(- float retentionDays)
                let files = Directory.GetFiles(logDirectory, "*.log")
                let mutable deleted = 0

                for file in files do
                    let lastWrite = File.GetLastWriteTimeUtc(file) |> DateTimeOffset
                    if lastWrite < cutoff then
                        File.Delete(file)
                        deleted <- deleted + 1

                deleted

**Step 53.** Add `LogRetention.fs` to the compile order in `Dropd.Core.fsproj` after `Scheduling.fs` (it has no dependencies on other core modules beyond `System`).

**Step 54.** In `packages/dropd/src/Dropd.Core/Types.fs`, add `MissedWhileUnavailable` to the `SyncSkipReason` discriminated union so `Scheduling.decideOnStartup` and `Scheduling.logDecision` can reference it:

    type SyncSkipReason =
        | AlreadyRunning
        | MissedWhileUnavailable

This is the only change to `Types.fs` in this phase. Existing code that pattern-matches on `SyncSkipReason` (the `Scheduling.decide` function and the DD-058/DD-059 tests) already handles `AlreadyRunning` explicitly and will need a wildcard or explicit `MissedWhileUnavailable` arm to compile. Since `decide` never returns the new variant, adding a catch-all `| _ ->` arm or an explicit `| MissedWhileUnavailable ->` that maps to `WaitForNextWindow` keeps existing behavior unchanged.

**Step 55.** In `packages/dropd/src/Dropd.Core/Scheduling.fs`, add two concrete helpers without changing existing `decide` behavior for DD-059:

1) `decideOnStartup` for missed-window detection at service start:

    let decideOnStartup
        (nowUtc: DateTimeOffset)
        (syncTimeUtc: TimeOnly)
        (lastSyncDateUtc: DateOnly option)
        : SchedulingDecision =
        let nowDate = DateOnly.FromDateTime(nowUtc.UtcDateTime)
        let nowTime = TimeOnly.FromDateTime(nowUtc.UtcDateTime)

        if nowTime > syncTimeUtc && lastSyncDateUtc <> Some nowDate then
            SkipSync MissedWhileUnavailable
        else
            WaitForNextWindow

2) `logDecision` mapping skip reasons to observability logs:

    let logDecision (decision: SchedulingDecision) : ApiContracts.LogEntry option =
        match decision with
        | StartSync
        | WaitForNextWindow -> None
        | SkipSync reason ->
            let reasonCode, message =
                match reason with
                | AlreadyRunning ->
                    "SyncSkippedOverlap", "Scheduled sync skipped because a sync is already in progress."
                | MissedWhileUnavailable ->
                    "SyncSkippedMissed", "Scheduled sync skipped because service was unavailable during the sync window."

            Some
                { Level = ApiContracts.Warning
                  Code = reasonCode
                  Message = message
                  Data = Map.empty }

This keeps DD-059 semantics intact (`decide` still returns `WaitForNextWindow` for normal polling after missed time) while adding a dedicated startup path for DD-078 logging requirements.

**Step 56.** Add unit tests for log retention. Create them in `ResilientPipelineUnitTests.fs` (or a new `LogRetentionUnitTests.fs` — use the existing file for simplicity) under test list `Unit.LogRetention`:

- Test 1: "deletes files older than retention window". Setup: create a temp directory with two `.log` files: one with last-write 10 days ago, one with last-write 3 days ago. Call `prune logDir 7 now`. Assert: returns 1. Only the 3-day-old file remains.

- Test 2: "does nothing when directory does not exist". Call `prune "/nonexistent/path" 7 now`. Assert: returns 0, no exception.

- Test 3: "ignores non-log files". Setup: temp directory with a `.txt` file older than 7 days. Call `prune`. Assert: returns 0, `.txt` file still exists.

**Step 57.** Add unit tests for `Scheduling.logDecision` under test list `Unit.Scheduling.LogDecision`:

- Test 1: "SkipSync AlreadyRunning produces SyncSkippedOverlap log". Assert: `logDecision (SkipSync AlreadyRunning)` returns `Some` with `Code = "SyncSkippedOverlap"`.

- Test 2: "decideOnStartup returns MissedWhileUnavailable when service starts after window". Use `nowUtc` after `syncTimeUtc` with `lastSyncDateUtc = None`; assert result is `SkipSync MissedWhileUnavailable`, then assert `logDecision` emits `Code = "SyncSkippedMissed"`.

- Test 3: "StartSync produces no log". Assert: `logDecision StartSync` returns `None`.

**Step 58.** Run new unit tests (expect failures):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.LogRetention"
    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.Scheduling.LogDecision"

**Step 59.** Implement `LogRetention.prune` (already written in step 52), `Scheduling.decideOnStartup`, and `Scheduling.logDecision`. Do not change the existing `Scheduling.decide` signature or DD-059 behavior.

**Step 60.** Run new unit tests (expect pass):

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.LogRetention"
    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "Unit.Scheduling.LogDecision"

**Step 61.** Run full test suite to confirm DD-059 still passes:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

**Step 62.** In `packages/dropd/tests/Dropd.Tests/ObservabilityTests.fs`, replace DD-076 body:

- Create a temp directory with two `.log` files: one 3 days old, one 10 days old.
- Call `LogRetention.prune` with `retentionDays = 7`.
- Assert: returns 1 (one file deleted).
- Assert: the 3-day-old file still exists.

Change DD-076 from `ptestCase` to `testCase`.

**Step 63.** Replace DD-077 body:

- Create a temp directory with three `.log` files: 2 days old, 8 days old, 14 days old.
- Call `LogRetention.prune` with `retentionDays = 7`.
- Assert: returns 2.
- Assert: only the 2-day-old file remains.

Change DD-077 from `ptestCase` to `testCase`.

**Step 64.** Replace DD-078 body:

- Call `Scheduling.decide` with `isSyncRunning = true` at sync time. Get `SkipSync AlreadyRunning`.
- Call `Scheduling.logDecision`. Assert: returns `Some` with `Code = "SyncSkippedOverlap"`.
- Call `Scheduling.decideOnStartup` with `nowTime` past `syncTimeUtc`, `lastSyncDateUtc = None`. Get `SkipSync MissedWhileUnavailable`.
- Call `Scheduling.logDecision`. Assert: returns `Some` with `Code = "SyncSkippedMissed"`.

Change DD-078 from `ptestCase` to `testCase`.

**Step 65.** Run full suite:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected: 188 passed, 5 ignored, 0 failed, 0 errored. The remaining 5 ignored tests are DD-063, DD-070..DD-073 (Apple Token Lifecycle, out of scope).

**Step 66.** Commit:

    jj commit -m "phase3: implement log retention, scheduling skip logging, and activate DD-076 DD-077 DD-078"


### Milestone 7: Final Gate

**Step 67.** Full build and tests:

    nix develop -c dotnet build packages/dropd/dropd.sln
    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

Expected:
- Build succeeds.
- 188 passed, 5 ignored, 0 failed, 0 errored.
- Exactly 5 ignored tests remaining (DD-063, DD-070, DD-071, DD-072, DD-073).
- All Phase 3 DD tests active and passing: DD-076..DD-078, DD-080..DD-089.

**Step 68.** Verify the 5 remaining ignored tests:

    nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary 2>&1 | grep "ptestCase\|Ignored"

**Step 69.** Update this plan's Progress, Surprises & Discoveries, Decision Log, and Outcomes & Retrospective sections with timestamps and evidence.

**Step 70.** Final commit:

    jj commit -m "phase3: complete API resilience, execution guards, and operational observability"


## Validation and Acceptance

Phase 3 is accepted only when all checks below are true:

1. `ResilientPipeline.wrap` retries transient failures (HTTP 5xx and request timeouts) with exponential backoff up to `MaxRetries`, recording computed delays in `PipelineStats.ComputedDelays` and emitting `TransientRetryScheduled` logs.

2. HTTP 429 responses are handled: the engine waits `Retry-After` seconds (or 2 seconds by default) and retries. A `RetryAfterWait` log entry is emitted with the delay value.

3. Paginated top-level-`next` endpoints (`/v1/me/library/artists`, `/v1/catalog/{storefront}/artists/{id}/albums`, `/v1/me/library/playlists/{id}/tracks`) are followed up to `MaxPages`. When the page limit is reached, a `PageLimitReached` warning is logged and the sync continues with partial data.

4. When sync runtime exceeds `MaxSyncRuntimeMinutes`, the sync is aborted with reason `"RuntimeExceeded"` and log code `SyncAbortedRuntimeExceeded`.

5. When the API error rate exceeds `ErrorRateAbortPercent` (and at least 5 requests have been made), the sync is aborted with reason `"ErrorRate"` and log code `SyncAbortedErrorRate` including rate details.

6. `LogRetention.prune` deletes `.log` files older than the configured retention window.

7. `Scheduling.logDecision` produces reason-coded log entries for skipped syncs.

8. Active DD tests include DD-076..DD-078 and DD-080..DD-089.

9. `nix develop -c dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary` reports 0 failed and 0 errored.

10. Remaining pending tests are exactly DD-063, DD-070, DD-071, DD-072, DD-073 (5 tests, Apple Token Lifecycle, out of scope).


## Idempotence and Recovery

All file creation and code steps are additive. If a milestone fails midway, restore only touched paths for that milestone.

When `.jj/` exists:

    jj restore packages/dropd/src/Dropd.Core packages/dropd/tests/Dropd.Tests docs/plans/service-implementation-phase3.md

Fallback with git:

    git restore packages/dropd/src/Dropd.Core packages/dropd/tests/Dropd.Tests docs/plans/service-implementation-phase3.md

If multiple DD activations fail at once, revert only the specific test file and re-activate one DD at a time.


## Artifacts and Notes

Expected artifacts at phase end:

- `docs/plans/service-implementation-phase3.md` (this file, updated).
- New core files:
  - `packages/dropd/src/Dropd.Core/ResilientPipeline.fs`
  - `packages/dropd/src/Dropd.Core/LogRetention.fs`
- Updated core files:
  - `packages/dropd/src/Dropd.Core/Types.fs` (MissedWhileUnavailable added to SyncSkipReason)
  - `packages/dropd/src/Dropd.Core/Dropd.Core.fsproj` (compile order)
  - `packages/dropd/src/Dropd.Core/SyncEngine.fs` (pipeline wiring, abort catch)
  - `packages/dropd/src/Dropd.Core/PlaylistReconcile.fs` (pagination refactor)
  - `packages/dropd/src/Dropd.Core/Scheduling.fs` (decideOnStartup, logDecision)
- New test files:
  - `packages/dropd/tests/Dropd.Tests/ResilientPipelineUnitTests.fs`
- Updated test files:
  - `packages/dropd/tests/Dropd.Tests/Dropd.Tests.fsproj` (compile order)
  - `packages/dropd/tests/Dropd.Tests/ApiResilienceTests.fs` (DD-080..DD-089 activated)
  - `packages/dropd/tests/Dropd.Tests/ObservabilityTests.fs` (DD-076..DD-078 activated)
  - `packages/dropd/tests/Dropd.Tests/TestHarness.fs` (DelayMs support for timeout tests; clock helper for DD-086)


## Interfaces and Dependencies

Phase 3 must end with these stable contracts.

In `packages/dropd/src/Dropd.Core/ResilientPipeline.fs`:

    type PipelineStats =
        { mutable TotalRequests: int
          mutable FailedRequests: int
          mutable ComputedDelays: int list }

    type PipelineConfig =
        { MaxRetries: int
          RequestTimeoutSeconds: int
          MaxPages: int
          PageSize: int
          MaxSyncRuntimeMinutes: int
          ErrorRateAbortPercent: int }

    exception SyncAbortedException of reason: string * logEntry: AC.LogEntry

    val configFrom : Config.ValidSyncConfig -> PipelineConfig

    val createStats : unit -> PipelineStats

    val wrap :
        PipelineConfig ->
        PipelineStats ->
        (int -> Async<unit>) ->
        (AC.LogEntry -> unit) ->
        int option ->
        AC.ApiRuntime ->
        AC.ApiRuntime

    val fetchAllPages :
        AC.ApiRuntime ->
        int ->
        AC.ApiRequest ->
        (string -> 'a list) ->
        (string -> string option) ->
        Async<Result<'a list * int * bool, AC.ApiResponse>>

In `packages/dropd/src/Dropd.Core/Types.fs`, `SyncSkipReason` gains a new case:

    type SyncSkipReason =
        | AlreadyRunning
        | MissedWhileUnavailable

In `packages/dropd/src/Dropd.Core/LogRetention.fs`:

    val prune : string -> int -> DateTimeOffset -> int

In `packages/dropd/src/Dropd.Core/Scheduling.fs`:

    val decideOnStartup : DateTimeOffset -> TimeOnly -> DateOnly option -> SchedulingDecision

    val logDecision : SchedulingDecision -> AC.LogEntry option

Dependencies remain:

- .NET 9
- Expecto 10.2.3
- System.Text.Json 9.0.0
- No additional runtime packages required.
