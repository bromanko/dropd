# Verification Infrastructure for dropd (Green CI + Executable Contract Catalog)

This ExecPlan is a living document. The sections Progress, Surprises & Discoveries,
Decision Log, and Outcomes & Retrospective must be kept up to date as work proceeds.


## Purpose / Big Picture

After this plan is complete, a developer can run `make build` and `make test` from the repository root and get a green result on every commit. The repository will contain a complete, executable catalog of requirement-oriented tests for DD-001 through DD-089, but requirements that depend on the future sync engine will be marked as **pending** (not failing) until implementation work begins.

This plan intentionally focuses on verification infrastructure only: solution/build wiring, test harness interfaces, requirement traceability, and deterministic test execution. API exploration scripts and broader developer tooling are moved to a companion plan at `docs/plans/api-exploration-and-dev-tooling.md`.

User-visible outcome now: reproducible build/test pipeline with explicit requirement coverage and no permanently red CI. User-visible outcome later (separate implementation plan): pending contract tests are converted to active tests and made to pass via TDD.


## Progress

- [ ] (2026-03-03 00:19Z) Create `dropd.sln` and make root build deterministic.
- [ ] (2026-03-03 00:19Z) Create `packages/dropd/src/Dropd.Core/` with `Types.fs`, `Config.fs`, and `Scheduling.fs` interfaces.
- [ ] (2026-03-03 00:19Z) Create `packages/dropd/tests/Dropd.Tests/` project with Expecto and harness dependencies.
- [ ] (2026-03-03 00:19Z) Add `Makefile` targets (`build`, `test`, `test-dd`, `clean`) with explicit project/solution paths.
- [ ] (2026-03-03 00:19Z) Add `.selfci` to run only green gates (`make build`, `make test`).
- [ ] (2026-03-03 00:19Z) Implement test harness route scripting model (supports sequential responses and request capture).
- [ ] (2026-03-03 00:19Z) Add harness unit tests that verify route matching, sequence behavior, and request capture.
- [ ] (2026-03-03 00:19Z) Add `packages/dropd/tests/Dropd.Tests/RequirementCatalog.fs` listing DD-001..DD-089 exactly once.
- [ ] (2026-03-03 00:19Z) Add requirement coverage test to fail if any DD ID is missing or duplicated.
- [ ] (2026-03-03 00:19Z) Add DD-036..DD-044 as active passing tests.
- [ ] (2026-03-03 00:19Z) Add DD-001..DD-035 and DD-045..DD-089 as `ptestCase` pending tests with concrete setup/assertion notes.
- [ ] (2026-03-03 00:19Z) Replace all "verified by comment/spike" placeholders with executable test cases (active or pending).
- [ ] (2026-03-03 00:19Z) Validate final test summary and DD coverage counts.


## Surprises & Discoveries

- Observation: Last.fm returns HTTP 200 for all error conditions, including invalid API keys and nonexistent artist lookups. Error detection requires parsing the response body for an `"error"` field rather than checking HTTP status codes. An invalid API key returns `{"error":10,"message":"Invalid API key - You must be granted a valid key by last.fm"}`. A nonexistent artist returns `{"error":6,"message":"The artist you supplied could not be found"}`.
  Evidence: Validated via `make spike` with a live Last.fm API key on 2026-03-02. See `docs/plans/api-exploration-and-dev-tooling.md` Outcomes section.


## Decision Log

- Decision: Keep default CI green by using pending contract tests instead of intentionally failing tests.
  Rationale: This preserves commit discipline while still codifying all requirement behaviors.
  Date: 2026-03-03

- Decision: Use a repository root solution file (`dropd.sln`) and explicit build/test paths.
  Rationale: `dotnet build` from root is ambiguous without a solution/project file and can fail for novices.
  Date: 2026-03-03

- Decision: Model fake API behavior as request-route scripts (including sequences), not a fixed endpoint record.
  Rationale: Several requirements need "first call 401, second call 200", retry/backoff, pagination, and other multi-response scenarios.
  Date: 2026-03-03

- Decision: Standardize log assertions on a machine-readable `Code` field.
  Rationale: Avoid vague assertions like "or similar"; each requirement should target exact observable signals.
  Date: 2026-03-03

- Decision: Last.fm error detection must parse the response body for an `"error"` field, not rely on HTTP status codes.
  Rationale: Last.fm returns HTTP 200 for all responses, including authentication failures (error code 10) and missing artists (error code 6). The test harness fake for Last.fm should model this behavior — canned responses for Last.fm error scenarios should use status code 200 with error payloads in the body. Known error codes: 6 (artist not found), 10 (invalid API key).
  Date: 2026-03-02

- Decision: Split API exploration and development process tooling into a companion ExecPlan.
  Rationale: Verification infrastructure and exploratory tooling have different risk profiles and acceptance criteria.
  Date: 2026-03-03

- Decision: Structure as a monorepo with product packages under `packages/`.
  Rationale: dropd may coexist with other services or libraries in this repository. Placing product code under `packages/dropd/` keeps the root clean for shared tooling (Makefile, flake.nix, .selfci) and documentation, and allows additional packages to be added without restructuring.
  Date: 2026-03-03


## Outcomes & Retrospective

(To be filled at major milestones and at completion.)


## Context and Orientation

This repository is structured as a monorepo. Shared tooling, documentation, and configuration live at the repository root. Product packages live under `packages/`, each with its own source and test directories. This structure allows additional services or libraries to be added alongside dropd in the future without restructuring. The root `Makefile` provides unified entry points that delegate to the appropriate package.

This plan creates the first implementation-facing structure needed for repeatable verification within the `packages/dropd/` package.

Key source documents:

- `docs/ears/requirements.md` (89 requirements, DD-001..DD-089)
- `docs/plans/verification-infrastructure.md` (this file)
- `docs/plans/api-exploration-and-dev-tooling.md` (companion plan for spikes/tooling)

Host prerequisites (explicit):

- Nix installed and working (`nix --version`).
- .NET 9 SDK available (either host-installed or provided by Nix shell).
- `direnv` is optional. If unavailable, run all commands via `nix develop -c <command>`.

Target repository structure after this plan:

    (repo root)
      Makefile
      .gitignore
      .selfci
      docs/
        ears/
          requirements.md
        research/
          ...
        plans/
          verification-infrastructure.md
          api-exploration-and-dev-tooling.md
      packages/
        dropd/
          dropd.sln
          src/
            Dropd.Core/
              Dropd.Core.fsproj
              Types.fs
              Config.fs
              Scheduling.fs
          tests/
            Dropd.Tests/
              Dropd.Tests.fsproj
              RequirementCatalog.fs
              TestHarness.fs
              HarnessTests.fs
              ArtistSeedingTests.fs
              LabelDiscoveryTests.fs
              SimilarArtistTests.fs
              ArtistFilteringTests.fs
              NewReleaseTests.fs
              GenreClassificationTests.fs
              PlaylistConfigTests.fs
              PlaylistManagementTests.fs
              SchedulingTests.fs
              AuthenticationTests.fs
              ObservabilityTests.fs
              ApiResilienceTests.fs
              RequirementCoverageTests.fs
              Program.fs


## Plan of Work

The work proceeds in four increments that each end in a green `make build` and `make test` run.

First, establish deterministic build and test entry points from repository root (`dropd.sln`, explicit `Makefile`, and CI gate commands). This removes path ambiguity and prevents novice setup failures.

Second, define stable interfaces in `Dropd.Core` and `TestHarness` that all future implementation work must satisfy. The harness API is explicitly designed for retries, pagination, and auth-recovery flows by supporting response sequences and full request capture.

Third, encode requirement traceability as code. Every DD requirement gets a named test case immediately. Cases that depend on unimplemented runtime behavior are added as `ptestCase` with concrete setup/assertion intent (not comments), so coverage is complete and executable from day one.

Fourth, finalize deterministic validation: coverage test proves DD-001..DD-089 are present exactly once, and the test summary shows no failures on mainline CI.


## Concrete Steps

### Milestone 1 - Deterministic root build/test wiring

1. Create the `packages/dropd/` directory and the solution file inside it:

       mkdir -p packages/dropd
       cd packages/dropd
       dotnet new sln --name dropd

   Expected output includes `The template "Solution File" was created successfully.`

2. Create `packages/dropd/src/Dropd.Core/Dropd.Core.fsproj` with `net9.0` and compile order:

       Types.fs
       Config.fs
       Scheduling.fs

3. Create `packages/dropd/tests/Dropd.Tests/Dropd.Tests.fsproj` with package references:

       Expecto 10.2.3
       Microsoft.AspNetCore.TestHost 9.0.0
       System.Text.Json 9.0.0

   Add project reference to `../../src/Dropd.Core/Dropd.Core.fsproj` (relative to the test project).

4. Add both projects to the solution (from `packages/dropd/`):

       cd packages/dropd
       dotnet sln dropd.sln add src/Dropd.Core/Dropd.Core.fsproj
       dotnet sln dropd.sln add tests/Dropd.Tests/Dropd.Tests.fsproj

   Expected output includes `Project ... added to the solution.` for both commands.

5. Create `Makefile` at repo root. The Makefile delegates to the dropd package. As additional packages are added to the monorepo, their build/test commands are added here:

       .PHONY: build test test-dd clean

       build:
       	dotnet build packages/dropd/dropd.sln

       test:
       	dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary

       test-dd:
       	dotnet run --project packages/dropd/tests/Dropd.Tests -- --summary --filter "DD-"

       clean:
       	dotnet clean packages/dropd/dropd.sln

   Use literal tab characters before command lines.

6. Create `.gitignore` at repo root:

       bin/
       obj/
       .ionide/
       .direnv/
       *.user
       *.suo
       result

7. Create `.selfci` at repo root:

       make build
       make test

8. Run:

       make build

   Expected: exit code 0; solution build succeeds.

9. Run:

       make test

   Expected: exit code 0 (initially with only smoke/coverage scaffolding tests).

10. Commit.

    Suggested message: `scaffold deterministic solution and root build/test commands`


### Milestone 2 - Core and harness interfaces with no ambiguity

1. Replace `packages/dropd/src/Dropd.Core/Types.fs` with domain types from EARS context, including:

   - `CatalogArtistId`, `CatalogAlbumId`, `CatalogTrackId`, `CatalogRecordLabelId`, `LibraryPlaylistId`
   - `Artist`, `Track`, `Album`, `Rating`, `RatingValue`
   - `SimilarArtist`, `SyncOutcome`, `SyncSkipReason`

2. Replace `packages/dropd/src/Dropd.Core/Config.fs` with `PlaylistDefinition`, `SyncConfig`, and `defaults` values for DD-036..DD-044.

3. Create `packages/dropd/src/Dropd.Core/Scheduling.fs` with explicit scheduling interface:

       module Dropd.Core.Scheduling

       open System
       open Dropd.Core.Types

       type SchedulingDecision =
           | StartSync
           | SkipSync of SyncSkipReason
           | WaitForNextWindow

       let decide
           (nowUtc: DateTimeOffset)
           (syncTimeUtc: TimeOnly)
           (isSyncRunning: bool)
           (lastSyncDateUtc: DateOnly option)
           : SchedulingDecision =
           failwith "not implemented"

   Keep this as a stub for now; tests depending on runtime behavior remain pending.

4. Replace `packages/dropd/tests/Dropd.Tests/TestHarness.fs` with a route-script model that supports sequence behavior.

   Required types:

       type CannedResponse =
           { StatusCode: int
             Body: string
             Headers: (string * string) list
             DelayMs: int option }

       type RecordedRequest =
           { Service: string   // "apple" | "lastfm"
             Method: string
             Path: string
             Query: (string * string) list
             Headers: (string * string) list
             Body: string option }

       type EndpointKey =
           { Service: string
             Method: string
             Path: string
             /// Optional query parameter constraints for routing. When present,
             /// a request matches only if all specified key-value pairs appear in
             /// the request query string. Unspecified query parameters are ignored.
             /// This is needed because some endpoints share a path and are
             /// differentiated only by query parameters (e.g., Apple Music catalog
             /// search uses /v1/catalog/{storefront}/search for both artist and
             /// record-label searches, distinguished by the "types" parameter).
             QueryMatch: (string * string) list }

       type ResponseScript =
           | Always of CannedResponse
           | Sequence of CannedResponse list

       type FakeApiSetup =
           { Routes: Map<EndpointKey, ResponseScript> }

       type LogEntry =
           { Level: string
             Code: string
             Message: string }

       type ObservedOutput =
           { Requests: RecordedRequest list
             Logs: LogEntry list
             Outcome: SyncOutcome option }

       let runSync (config: SyncConfig) (setup: FakeApiSetup) : ObservedOutput =
           failwith "not implemented"

5. In `TestHarness.fs`, define exact log codes used by tests (constants), at minimum:

   - `UnknownLabel`
   - `SimilarArtistServiceUnavailable`
   - `AppleMusicAuthFailure`
   - `LastFmAuthFailure`
   - `PageLimitReached`
   - `SyncAbortedRuntimeExceeded`
   - `SyncAbortedErrorRate`

6. Add `packages/dropd/tests/Dropd.Tests/HarnessTests.fs` with active passing tests that verify:

   - `Sequence [401; 200]` serves 401 then 200 then repeats last response.
   - Request recorder captures method/path/query/body exactly.
   - Missing route returns 404.
   - Route with `QueryMatch = [("types", "record-labels")]` matches a request to the same path with `?types=record-labels&limit=1` but does not match a request with `?types=artists&limit=1`.
   - Route with `QueryMatch = []` matches any request to that path regardless of query parameters.
   - Last.fm error scenarios use HTTP 200 with error payloads in the body (e.g., `{"error":10,"message":"Invalid API key ..."}` for auth failure, `{"error":6,"message":"The artist you supplied could not be found"}` for missing artist). Verify that the harness correctly serves these as 200 OK responses, not 4xx.

7. Run:

       make build
       make test

   Expected: exit code 0.

8. Commit.

   Suggested message: `define core contracts and sequence-capable test harness interfaces`


### Milestone 3 - Requirement catalog and executable DD traceability

1. Create `packages/dropd/tests/Dropd.Tests/RequirementCatalog.fs` with a canonical list:

       let allRequirementIds =
           [ "DD-001"; "DD-002"; ...; "DD-089" ]

   The list must contain exactly 89 unique IDs.

2. Add `packages/dropd/tests/Dropd.Tests/RequirementCoverageTests.fs` with active passing checks:

   - Count is exactly 89.
   - First is `DD-001`, last is `DD-089` after sorting.
   - No duplicates.

3. For each requirement section file (`ArtistSeedingTests.fs`, ..., `ApiResilienceTests.fs`), add named test cases for each DD ID.

   Rules:

   - DD-036..DD-044 are active `testCase` assertions and must pass now.
   - All remaining DD IDs are `ptestCase` (pending), not comments.
   - Every pending test includes concrete setup and expected assertions in code comments directly above the case (request path, response script, expected log code, expected outcome).

4. Replace all prior non-executable placeholders. Specifically, add test cases (active or pending) for DD-018, DD-024, DD-055, DD-060, DD-063, DD-070, DD-076, DD-077, and DD-083.

5. Update `packages/dropd/tests/Dropd.Tests/Program.fs` to include all test lists and ensure `runTestsInAssemblyWithCLIArgs` executes them.

6. Run:

       make build
       make test

   Expected summary:

   - Exit code 0.
   - No failed tests.
   - Exactly 89 DD-prefixed test names discovered.
   - DD-036..DD-044 active and passing.
   - All other DD tests marked pending/ignored.

7. Verify DD discovery count deterministically:

       dotnet run --project packages/dropd/tests/Dropd.Tests -- --list-tests | rg -o "DD-[0-9]{3}" | sort -u | wc -l

   Expected output:

       89

8. Commit.

   Suggested message: `add executable DD requirement catalog with active and pending tests`


### Milestone 4 - Final validation and handoff state

1. Run full gate:

       make clean
       make build
       make test

   Expected: all commands exit 0.

2. Confirm CI script still matches green gates (`.selfci`):

       make build
       make test

3. Ensure this plan's Progress section is updated with timestamps and completed checkboxes.

4. Commit.

   Suggested message: `finalize verification infrastructure with deterministic green CI`


## Validation and Acceptance

This plan is complete only when all conditions below are true.

1. `packages/dropd/dropd.sln` exists and `make build` succeeds from repo root without changing directories.

2. `make test` exits 0 and reports no failed tests.

3. `packages/dropd/tests/Dropd.Tests/RequirementCatalog.fs` exists and `RequirementCoverageTests` prove exactly 89 unique DD IDs.

4. Every DD requirement has an executable test case name in the suite (active `testCase` or pending `ptestCase`), including formerly comment-only items (DD-018, DD-024, DD-055, DD-060, DD-063, DD-070, DD-076, DD-077, DD-083).

5. DD-036 through DD-044 are active passing tests (not pending).

6. Fake API setup supports sequential responses and full request capture via `ResponseScript` and `RecordedRequest`, enabling future retry/auth/pagination tests without redesign.

7. CI (`.selfci`) runs only green gates (`make build`, `make test`) and remains green at every commit point in this plan.


## Idempotence and Recovery

All commands in this plan are safe to rerun.

- `dotnet new sln --name dropd` should be run once inside `packages/dropd/`. If rerun is attempted, delete `packages/dropd/dropd.sln` first.
- `dotnet sln ... add ...` is idempotent for existing project entries.
- If project files drift, restore a clean state with:

      git restore Makefile .selfci packages/dropd

- If package restore/build cache causes confusion:

      make clean
      dotnet nuget locals all --clear
      make build

No databases or external mutable infrastructure are created by this plan.


## Artifacts and Notes

Primary artifacts:

- `packages/dropd/dropd.sln` - deterministic package build entry point.
- `Makefile` - canonical monorepo-level build/test commands.
- `.selfci` - green CI gate commands.
- `packages/dropd/src/Dropd.Core/Types.fs`, `Config.fs`, `Scheduling.fs` - stable contracts.
- `packages/dropd/tests/Dropd.Tests/TestHarness.fs` - sequence-capable fake API model and observable output model.
- `packages/dropd/tests/Dropd.Tests/RequirementCatalog.fs` - canonical DD ID inventory.
- `packages/dropd/tests/Dropd.Tests/*Tests.fs` - requirement-linked executable test catalog.

Companion plan:

- `docs/plans/api-exploration-and-dev-tooling.md` contains API spike scripts and optional process-management tooling; it is intentionally out of scope for this plan.


## Interfaces and Dependencies

NuGet dependencies in `packages/dropd/tests/Dropd.Tests/Dropd.Tests.fsproj`:

- `Expecto` 10.2.3
- `Microsoft.AspNetCore.TestHost` 9.0.0
- `System.Text.Json` 9.0.0

Required function and type contracts:

- `packages/dropd/src/Dropd.Core/Scheduling.fs`

      let decide
          (nowUtc: DateTimeOffset)
          (syncTimeUtc: TimeOnly)
          (isSyncRunning: bool)
          (lastSyncDateUtc: DateOnly option)
          : SchedulingDecision =
          failwith "not implemented"

- `packages/dropd/tests/Dropd.Tests/TestHarness.fs`

      type ResponseScript = Always of CannedResponse | Sequence of CannedResponse list

      let runSync (config: SyncConfig) (setup: FakeApiSetup) : ObservedOutput =
          failwith "not implemented"

Test execution commands:

- Full test suite (green gate): `make test`
- Requirement-focused subset: `make test-dd`
- Single requirement example:

      dotnet run --project packages/dropd/tests/Dropd.Tests -- --filter "DD-024"
