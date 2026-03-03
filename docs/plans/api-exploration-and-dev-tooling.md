# API Exploration and Developer Tooling for dropd

This ExecPlan is a living document. The sections Progress, Surprises & Discoveries,
Decision Log, and Outcomes & Retrospective must be kept up to date as work proceeds.


## Purpose / Big Picture

This plan separates exploratory API scripts and optional developer process tooling from the verification infrastructure work. After completion, developers can run a reproducible local dev shell, optionally start multi-process development with Overmind, and execute API exploration scripts against Apple Music and Last.fm when credentials are available.

This plan does not gate CI correctness for product behavior. It is intentionally isolated from `docs/plans/verification-infrastructure.md` so experimentation and local workflow setup do not destabilize verification milestones.


## Progress

- [x] (2026-03-02 22:50Z) Add Nix flake for reproducible local tooling.
- [x] (2026-03-02 22:50Z) Add optional direnv integration with explicit fallback path.
- [x] (2026-03-02 22:52Z) Add `Procfile` and `make dev` workflow.
- [x] (2026-03-02 22:55Z) Scaffold `spikes/api-exploration/` project.
- [x] (2026-03-02 22:55Z) Implement Apple Music exploration script with credential checks.
- [x] (2026-03-02 22:55Z) Implement Last.fm exploration script with credential checks.
- [x] (2026-03-02 22:56Z) Validate `make spike` in no-credential path (exit 0, setup guidance printed).


## Surprises & Discoveries

- Observation: Last.fm returns HTTP 200 for all error conditions. An invalid API key returns `{"error":10,"message":"Invalid API key - You must be granted a valid key by last.fm"}`. A nonexistent artist returns `{"error":6,"message":"The artist you supplied could not be found"}`. Error detection must parse the response body for an `"error"` field, not rely on HTTP status codes.
  Evidence: Validated via `make spike` with live API key on 2026-03-02. All three Last.fm calls returned HTTP 200, including the nonexistent artist and (when tested with bad key) invalid auth scenarios.


## Decision Log

- Decision: Keep this plan separate from verification infrastructure.
  Rationale: Spikes and local process tooling are useful but should not block deterministic verification CI gates.
  Date: 2026-03-03

- Decision: Treat missing credentials as a successful, explicit outcome for spikes.
  Rationale: Contributors should be able to run exploration scripts without immediate access to private keys/tokens.
  Date: 2026-03-03

- Decision: Use a single pre-configured bearer token (`DROPD_APPLE_MUSIC_TOKEN`) instead of generating JWTs at runtime from Team ID, Key ID, and private key.
  Rationale: Simplifies credential surface to one env var. Token provisioning is handled externally (e.g., via a secret proxy).
  Date: 2026-03-02


## Outcomes & Retrospective

All milestones complete. The plan produced:

1. A reproducible Nix flake dev shell with .NET 9, gnumake, overmind, and tmux.
2. Optional direnv integration (`.envrc`) with documented `nix develop` fallback.
3. A `Procfile` + `make dev` workflow for multi-process development via Overmind.
4. An API exploration spike at `spikes/api-exploration/` covering Apple Music (library/catalog/label endpoints) and Last.fm (similar artists, artist search).
5. Graceful missing-credential behavior: `make spike` exits 0 and prints setup guidance when env vars are absent.

Credential-present validation is deferred to manual testing when credentials are configured. The no-credential path was fully validated.

Lesson learned: A single bearer token approach simplifies the credential surface significantly — one env var vs four — and offloads token provisioning to external tooling.


## Context and Orientation

Relevant files in this repository:

- `docs/research/apple-music-api.md`
- `docs/research/lastfm-api.md`
- `docs/research/credential-setup.md`
- `docs/plans/verification-infrastructure.md`

This repository is structured as a monorepo. Shared tooling lives at the repo root. Product packages live under `packages/`. Spikes live under `spikes/` at the root.

Artifacts created by this plan:

    flake.nix
    .envrc
    Procfile
    spikes/
      api-exploration/
        api-exploration.fsproj
        AppleMusic.fs
        LastFm.fs
        Program.fs


## Plan of Work

First create a reproducible shell using Nix so all contributors get the same dotnet/tooling versions. Then add optional direnv integration, with a documented fallback to `nix develop` for systems that do not use direnv.

After the environment is stable, scaffold a small spike console app and implement read-only API checks for Apple Music and Last.fm. The scripts must emit concise endpoint/status summaries and fail gracefully when credentials are missing.

Finally, wire developer convenience commands (`make dev`, `make spike`) and validate both happy-path and missing-credential behavior.


## Concrete Steps

### Milestone 1 — Local tooling environment

1. Create `flake.nix` with packages:

   - `dotnet-sdk_9`
   - `gnumake`
   - `overmind`
   - `tmux`

2. Create `.envrc`:

       use flake

3. Add fallback note to `README.md`:

   - If `direnv` is unavailable, run commands via `nix develop -c <command>`.

4. Validate:

       nix develop -c dotnet --version

   Expected: outputs a .NET 9 version string.

5. Commit.

   Suggested message: `add reproducible nix shell and optional direnv setup`


### Milestone 2 — Procfile and developer process entry point

1. Create `Procfile` with initial process:

       selfci: selfci

2. Add the `dev` target to the existing `Makefile` (created by the verification infrastructure plan or by prior steps). Append after existing targets:

       dev:
       	overmind start

   Also add `dev` to the `.PHONY` line if one exists.

3. Validate:

       overmind start

   Expected: Overmind starts and shows the `selfci` process.

4. Commit.

   Suggested message: `add procfile and make dev workflow`


### Milestone 3 — API exploration spike project

1. Create `spikes/api-exploration/api-exploration.fsproj` (`net9.0`) with package references:

   - `System.Text.Json` 9.0.0

2. Create `spikes/api-exploration/AppleMusic.fs` with:

   - Env var read: `DROPD_APPLE_MUSIC_TOKEN` (a single bearer token used for both `Authorization` and `Music-User-Token` headers)
   - Missing-credential path that prints `See docs/research/credential-setup.md` and exits cleanly
   - Calls:
     - `GET /v1/me/library/artists?limit=5`
     - `GET /v1/me/ratings/songs?limit=5`
     - `GET /v1/catalog/us/artists/657515/albums?sort=-releaseDate&limit=3`
     - `GET /v1/catalog/us/search?term=Ninja+Tune&types=record-labels&limit=1`
     - `GET /v1/catalog/us/record-labels/{id}?views=latest-releases`

3. Create `spikes/api-exploration/LastFm.fs` with:

   - Env var read: `DROPD_LASTFM_API_KEY`
   - Calls:
     - `artist.getSimilar` for `Radiohead`
     - `artist.getSimilar` for nonexistent artist
     - `artist.search` for `Bonobo`

4. Create `spikes/api-exploration/Program.fs` that runs both modules and prints section headers.

5. Add the `spike` target to the existing `Makefile`. Append after existing targets:

       spike:
       	dotnet run --project spikes/api-exploration

   Also add `spike` to the `.PHONY` line if one exists.

6. Validate no-credential path:

       make spike

   Expected: explicit credential-missing guidance and exit code 0.

7. Validate credential-present path (only when credentials are configured):

       make spike

   Expected: each endpoint prints status code and one-line response summary.

8. Commit.

   Suggested message: `add apple music and lastfm API exploration spike`


## Validation and Acceptance

This plan is complete when:

1. `nix develop -c dotnet --version` succeeds.
2. `make dev` starts Overmind and shows configured processes.
3. `make spike` exits 0 when credentials are missing and prints setup guidance.
4. `make spike` (with credentials set) prints endpoint/status summaries for all scripted calls.
5. All artifacts listed in Context and Orientation exist at the specified paths.


## Idempotence and Recovery

- `flake.nix`, `.envrc`, `Procfile`, and spike source files are safe to overwrite.
- If spike dependencies break after package changes:

      dotnet clean spikes/api-exploration/api-exploration.fsproj
      dotnet restore spikes/api-exploration/api-exploration.fsproj

- If environment activation fails, bypass direnv:

      nix develop -c make spike


## Artifacts and Notes

Artifacts produced by this plan are non-production tooling and exploratory scripts only. They do not define product acceptance behavior.

The verification contract and CI-green behavior remain governed by:

- `docs/plans/verification-infrastructure.md`


## Interfaces and Dependencies

Dependencies:

- Nix flake input `nixpkgs` (unstable)
- .NET 9 SDK
- `System.Text.Json` 9.0.0

Environment variables used by spikes:

- `DROPD_APPLE_MUSIC_TOKEN`
- `DROPD_LASTFM_API_KEY`
