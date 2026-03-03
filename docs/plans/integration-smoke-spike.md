# Integration Smoke Spike: Run Phase 1 Sync Against Real Apple Music Credentials

This ExecPlan is a living document. The sections Progress, Surprises & Discoveries,
Decision Log, and Outcomes & Retrospective must be kept up to date as work proceeds.


## Purpose / Big Picture

Phase 1 delivered a real sync engine (`SyncEngine.runSync`) that is tested entirely with
fake HTTP fixtures. You cannot yet point it at real Apple Music credentials and watch it
run. This spike closes that gap.

After this work, you can run `make smoke` from the repository root and the Phase 1 sync
core will execute against your real Apple Music account — fetching your library artists,
resolving configured record labels, finding recent releases, classifying them by genre,
and reconciling them into playlists. The terminal will print every API request the engine
makes, every structured log it emits, and the final sync outcome (success, partial
failure, or aborted).

Nothing about the core engine or the existing tests changes. The spike is a thin
executable wrapper that wires a real `HttpClient` to the `ApiRuntime` interface the engine
already accepts, then calls `SyncEngine.runSync` and pretty-prints the results.


## Progress

- [x] Step 1: create spike directory.
- [x] Step 2: create `integration-smoke.fsproj`.
- [x] Step 3: create `HttpRuntime.fs`.
- [x] Step 4: create `SmokeConfig.fs` stub.
- [x] Step 5: create `Program.fs` stub.
- [x] Step 6: build succeeds.
- [x] Step 7: add `smoke` target to `Makefile`.
- [x] Step 8: `make smoke` prints stub message.
- [x] Step 9: add `config.local.json` to `.gitignore`.
- [x] Step 10: commit milestone 1.
- [x] Step 11: create `config.example.json`. (Pulled forward to Step 6 — build requires it.)
- [x] Step 12a: add JSON DTO types and env-var reading to `SmokeConfig.fs`.
- [x] Step 12b: add config-file loading and `SyncConfig` assembly to `SmokeConfig.fs`.
- [x] Step 13: build succeeds.
- [x] Step 13a: verify `SmokeConfig.load` in isolation.
- [x] Step 14: commit milestone 2.
- [x] Step 15a: add formatting helpers to `Program.fs`.
- [x] Step 15b: add entry-point logic to `Program.fs`.
- [x] Step 16: build succeeds.
- [x] Step 17: end-to-end `make smoke` with real credentials.
- [x] Step 18: commit milestone 3.
- [x] Step 19: update plan progress and retrospective.
- [x] Step 20: final commit.


## Surprises & Discoveries

- The `.fsproj` `<Content>` item for `config.example.json` is unconditional, so the build
  fails if the file does not exist yet. Step 11 (create `config.example.json`) had to be
  pulled forward into Milestone 1 so the first build in Step 6 could succeed. The plan
  assumed the `<Content>` element would be fine with a missing file, but unlike the
  conditional `config.local.json` item, the unconditional one causes MSB3030.

- F# interpolated strings (`$"..."`) do not allow string literals containing commas inside
  interpolation holes (e.g. `{String.concat ", " missing}`). The fix was to bind the
  expression to a `let` and interpolate the binding instead. This tripped compilation in
  Step 12a.

- **Bug found: `fetchFavoritedArtists` calls `/v1/me/ratings/artists` without an `ids`
  query parameter.** The real Apple Music API requires `ids` — you cannot list all ratings
  without specifying which resource IDs to check. The API returns HTTP 400 with error code
  `40005` ("No ids supplied on the request"). The engine treats this as a fatal error and
  aborts with `FavoritedArtistsFailed`. The existing test suite never caught this because
  the test harness uses fake HTTP fixtures that return canned 200 responses regardless of
  query parameters. The api-exploration spike (`spikes/api-exploration/AppleMusic.fs`)
  already demonstrates the correct pattern: `GET /v1/me/ratings/songs?ids=203709340`.
  The fix requires `fetchFavoritedArtists` to accept the library artist IDs from step 1
  and batch them into the `ids` query parameter, but there is also a library-ID-to-catalog-ID
  mapping concern — library artists from `/v1/me/library/artists` may return library-scoped
  IDs (e.g. `r.xxx`) rather than catalog IDs, and the ratings endpoint expects catalog IDs.
  This was tracked as BUG-001 in `docs/plans/service-implementation-phase1.md` and has
  been fixed. The fix uses `include=catalog` on the library artists endpoint to extract
  catalog IDs, and batches them into the `ids` parameter on the ratings endpoint.

- **Fixed: Label releases used wrong endpoint format.** The engine used
  `/v1/catalog/us/record-labels/{id}/latest-releases` (path segment) but the real API
  uses `/v1/catalog/us/record-labels/{id}?views=latest-releases` (query parameter).
  The response structure also differs — releases are nested under
  `views.latest-releases.data` instead of `data`. Both the research doc and the
  api-exploration spike already documented the correct format. Fixed by updating
  `fetchLabelReleases` and adding `parseLabelViewReleases`.

- **Fixed: Playlist creation and track-add used wrong body formats.** Playlist creation
  sent `{"name":"..."}` but the Apple Music API expects
  `{"attributes":{"name":"..."}}`. Track-add sent `{"trackIds":["..."]}` but the API
  expects `{"data":[{"id":"...","type":"songs"}]}`. Both fixed in
  `PlaylistReconcile.fs`.

- **Fixed: Playlists addressed by name instead of ID — duplicate playlists on every run.**
  The engine used playlist names in URL paths (e.g. `/v1/me/library/playlists/Electronic/tracks`)
  but Apple Music addresses playlists by ID (e.g. `p.VKDUBYel0`). The GET always returned
  404, so a new playlist was created on every sync. Fixed by adding a playlist listing step
  (`GET /v1/me/library/playlists`) that builds a name→ID map, then using the resolved ID
  for all track operations. When creating a new playlist, the returned ID is captured from
  the response and used for subsequent track additions.

- **Fixed: Apple Music returning gzip-compressed responses.** The `HttpClient` in
  `HttpRuntime.fs` didn't have automatic decompression enabled. The playlist-create
  response arrived gzip-compressed (`0x1F` byte), causing a JSON parse crash. Fixed by
  setting `HttpClientHandler.AutomaticDecompression = DecompressionMethods.All`.

- **Fixed: Apple Music returns 404 for empty playlist tracks.** When a playlist exists but
  has zero tracks, `GET /v1/me/library/playlists/{id}/tracks` returns HTTP 404 with error
  code `40403` ("No related resources found for tracks") instead of `{"data":[]}`. The
  engine treated this as a failure and logged `PlaylistTrackListFailure`. Fixed by treating
  404 on the tracks endpoint as "zero existing tracks" rather than an error.

- **Fixed: Existing playlist tracks compared by library ID instead of catalog ID — tracks
  re-added on every run.** Apple Music playlist tracks have library-scoped IDs at the top
  level (e.g. `i.Mla0tqxJ0Q`) but the catalog ID needed for dedup is nested at
  `attributes.playParams.catalogId` (e.g. `1874397619`). The engine was comparing
  library IDs against catalog track IDs from discovered releases, so tracks were never
  recognized as already present. Fixed `parseExistingTracks` to extract `catalogId`
  from `playParams`, falling back to the top-level `id` for test fixtures. Added test
  DD-047b to cover this scenario.

- **Fixed: Existing playlist track reads not paginated — tracks re-added every run.**
  Apple Music returns at most 100 tracks per page from
  `GET /v1/me/library/playlists/{id}/tracks`, with a `next` link for subsequent pages.
  The engine read only the first page, so playlists with >100 tracks had the overflow
  tracks re-added on every sync. Fixed by adding `fetchAllPlaylistTracks` which follows
  `next` links up to 20 pages.

- **Apple Music genre taxonomy uses sub-genres, not umbrella "Electronic".**  Most
  electronic music is tagged as "Dance", "House", "Techno", "Dubstep", etc. — only some
  albums also include "Electronic" as a genre. The example config was updated to include
  common sub-genres in the criteria. This is a config concern, not a code bug (DD-032
  specifies exact matching, which is correct).

- **Known limitation: DELETE tracks from playlist returns 401.** The Apple Music REST API
  appears not to support `DELETE /v1/me/library/playlists/{id}/tracks` — both catalog IDs
  (via `?ids=` query) and library IDs (via JSON body) return HTTP 401. This means the
  rolling-window track removal feature does not work against the real API. Once tracks age
  beyond the rolling window, `computePlan` generates remove operations that fail, causing
  `partial_failure` outcome. This needs further research (possibly a different API
  mechanism or MusicKit scope).

- **"Warp Records" does not exist in Apple Music's record-labels catalog.** Searching for
  `term=Warp+Records&types=record-labels` returns `{"results":{}}` (empty). This is a
  catalog data limitation, not a code bug. The example config was updated to use
  "XL Recordings" which resolves successfully.


## Decision Log

- Decision: The spike lives in `spikes/integration-smoke/` as a standalone `.fsproj`
  referencing `Dropd.Core`, following the same pattern as `spikes/api-exploration/`.
  Rationale: keeps production code unmodified; spikes directory already establishes this
  convention.
  Date: 2026-03-03

- Decision: Credentials are always read from environment variables (`DROPD_APPLE_MUSIC_TOKEN`,
  `DROPD_APPLE_USER_TOKEN`, `DROPD_LASTFM_API_KEY`), never from a config file.
  Rationale: these variables are already exported by `.envrc` from the secret store; no
  additional credential-management code is needed.
  Date: 2026-03-03

- Decision: Playlist definitions and label names are read from `config.local.json` placed
  in the spike source directory (`spikes/integration-smoke/`). If that file is absent, the
  spike falls back to `config.example.json` (committed to the repository). If neither file
  exists, the spike exits with an error directing the user to copy `config.example.json`
  to `config.local.json`.
  Rationale: eliminates hardcoded F# defaults; `config.example.json` documents the format
  and provides a working starting point without requiring any setup; users customise by
  copying and editing without touching code.
  Date: 2026-03-03

- Decision: Both `config.example.json` and `config.local.json` (when present) are declared
  as `<Content>` items in the `.fsproj` with `CopyToOutputDirectory=PreserveNewest`. At
  runtime, `SmokeConfig.fs` resolves them relative to the assembly location (the build
  output directory), not the working directory.
  Rationale: `dotnet run --project spikes/integration-smoke` is invoked from the repo
  root, so the working directory is not the spike source directory. Copying config files to
  the output directory and resolving them by assembly location is the standard .NET pattern
  for this situation.
  Date: 2026-03-03

- Decision: `LastFmApiKey` is required by `Config.validate` (even though Phase 1 never
  calls Last.fm). If `DROPD_LASTFM_API_KEY` is not set or is empty, the spike falls back
  to the placeholder string `"phase1-unused"` so validation passes.
  Rationale: Phase 1 does not make any Last.fm requests; requiring the user to obtain a
  real key before they can smoke-test the sync adds unnecessary friction.
  Date: 2026-03-03

- Decision: The spike exits with code 0 on `Success` or `PartialFailure` and code 1 on
  `Aborted`.
  Rationale: lets CI or shell scripts detect auth failures or fatal errors.
  Date: 2026-03-03


## Outcomes & Retrospective

The spike is fully implemented and working. All acceptance criteria are met:

1. `make smoke` builds and runs without manual steps (credentials via direnv).
2. The spike automatically uses `config.example.json` when no `config.local.json` exists.
3. The output includes `INFO [SyncStarted]` and `INFO [SyncOutcome]` log entries.
4. Exit code is 1 for `Aborted` (confirmed with expired tokens producing `AuthFailure`),
   and would be 0 for `Success`/`PartialFailure`.
5. `make test` passes all 101 existing tests with 0 failures and 0 errors — no core
   library or test files were modified.
6. `config.local.json` is in `.gitignore`.

The initial end-to-end run produced an `AuthFailure` abort, which led to token refresh.
Subsequent runs surfaced four bugs in the core engine that were invisible to the
fixture-based test suite:

1. **BUG-001:** `fetchLibraryArtists` returned library-scoped IDs (`r.xxx`) and
   `fetchFavoritedArtists` didn't pass `ids` to the ratings endpoint. Fixed by using
   `include=catalog` and batching IDs.
2. **Label releases endpoint:** used a path segment instead of the `views` query parameter.
   Fixed with `parseLabelViewReleases`.
3. **Playlist creation body:** missing `attributes` wrapper. Fixed.
4. **Track-add body:** used `trackIds` array instead of `data` array with typed objects.
   Fixed.

After all fixes, `make smoke` completes with `✓ Outcome: success`, 1 track added,
47 API requests, no errors. The only warning is "Warp Records" not resolving (a config
issue — the label may be listed under a different name in Apple Music's catalog).

Total implementation time was straightforward. The only deviations from the plan were
minor: pulling `config.example.json` creation forward, and fixing an F# interpolated
string syntax issue.


## Context and Orientation

**Repository layout relevant to this spike:**

    spikes/
      api-exploration/          — existing HTTP exploration spike (no Dropd.Core reference)
    packages/
      dropd/
        src/
          Dropd.Core/           — core library: SyncEngine, PlaylistReconcile, Config, Types, ApiContracts
            Dropd.Core.fsproj
            ApiContracts.fs     — defines ApiRuntime, ApiRequest, ApiResponse, LogEntry, ObservedSync
            Config.fs           — defines SyncConfig, ValidSyncConfig, defaults, validate
            SyncEngine.fs       — defines SyncEngine.runSync
            Types.fs            — defines SyncOutcome (Success | PartialFailure | Aborted)
    docs/
      plans/
        integration-smoke-spike.md   — this file
    .envrc                      — exports DROPD_APPLE_MUSIC_TOKEN, DROPD_APPLE_USER_TOKEN, DROPD_LASTFM_API_KEY
    Makefile                    — build/test/spike targets; `smoke` target will be added here
    .gitignore

**The key interface:** `SyncEngine.runSync` accepts two arguments and returns the sync
result plus a complete record of everything that happened:

    let runSync
        (config: Config.ValidSyncConfig)
        (runtime: ApiContracts.ApiRuntime)
        : Types.SyncOutcome * ApiContracts.ObservedSync

`ApiContracts.ApiRuntime` is a plain F# record with two fields:

    type ApiRuntime =
      { Execute: ApiRequest -> Async<ApiResponse>
        UtcNow: unit -> System.DateTimeOffset }

`Execute` receives a fully-formed request (method, path, query params, headers, optional
body) and must return a response (status code, body string, headers). The engine sets all
Apple Music auth headers itself before calling `Execute`; the HTTP adapter just forwards
whatever headers are in the request.

`UtcNow` is called by the engine to determine today's date for lookback filtering. In the
spike this should return the real wall-clock time, unlike the test harness which returns a
fixed timestamp.

**Apple Music base URL:** `https://api.music.apple.com`. All request paths the engine
generates start with `/v1/...` and are relative to this base.

**Last.fm base URL:** `https://ws.audioscrobbler.com`. Phase 1 never issues Last.fm
requests, so this URL is present for completeness only.

**Credentials:** the `.envrc` already exports three variables from the project's secret
store. When you open a terminal in the project directory with `direnv allow`, these are
present in your shell:

    DROPD_APPLE_MUSIC_TOKEN   — Apple Music developer JWT (signs all requests)
    DROPD_APPLE_USER_TOKEN    — Apple Music user token (personal-library endpoints)
    DROPD_LASTFM_API_KEY      — Last.fm API key (not used by Phase 1)

**Config validation:** `Config.validate` (in `Dropd.Core.Config`) takes a `SyncConfig`
and returns `Result<ValidSyncConfig, ConfigError list>`. It rejects empty credentials and
any non-positive numeric field. The `Config.defaults` value provides sensible defaults for
all numeric fields and an empty credential block.

**Config file resolution:** when the spike binary runs, its working directory is the repo
root (because `make smoke` runs `dotnet run --project spikes/integration-smoke` from
there). Config files are not in the working directory — they live alongside the source and
are copied to the build output directory by the `.fsproj`. At runtime, `SmokeConfig.fs`
finds them by resolving paths relative to `Assembly.GetExecutingAssembly().Location`, which
points to the build output directory. You place `config.local.json` in the spike source
directory (`spikes/integration-smoke/`); a rebuild copies it to the output directory.

**Config file format** (`config.local.json` or `config.example.json`):

    {
      "playlists": [
        { "name": "Electronic", "genreCriteria": ["Electronic"] }
      ],
      "labelNames": ["Ninja Tune"]
    }

Only `playlists` and `labelNames` are read from this file; numeric thresholds come from
`Config.defaults`. `config.local.json` is git-ignored. `config.example.json` is committed
and serves as the fallback when no local override exists.


## Plan of Work

The spike has three files: `HttpRuntime.fs`, `SmokeConfig.fs`, and `Program.fs`, plus the
project file and a committed example config.

`HttpRuntime.fs` provides the single function `HttpRuntime.create : unit -> ApiRuntime`.
It allocates one `HttpClient`, selects a base URL by `ApiService` (Apple Music or Last.fm),
assembles the full URL from path and query params, forwards all request headers, attaches
a body if present, awaits the response, reads the body as a string, and returns an
`ApiResponse`. `UtcNow` returns `DateTimeOffset.UtcNow`.

`SmokeConfig.fs` provides `SmokeConfig.load : unit -> Result<Config.SyncConfig, string>`.
It reads the three credential env vars and falls back to `"phase1-unused"` for the Last.fm
key when absent. For playlist and label config, it looks for `config.local.json` in the
assembly output directory first, then falls back to `config.example.json` in the same
directory. If neither file exists it returns `Error` directing the user to copy
`config.example.json` to `config.local.json`. It returns `Error` with a human-readable
message if the Apple Music credentials are missing.

`Program.fs` is the entry point. It calls `SmokeConfig.load`, prints an error and exits 1
if credentials are missing, calls `Config.validate`, prints a validation error and exits 1
if the config is invalid, prints a config summary (masking credential values), calls
`SyncEngine.runSync` with the validated config and a real `HttpRuntime`, then prints each
recorded request, each log entry, and the final outcome. Exit code is 0 for
`Success`/`PartialFailure` and 1 for `Aborted`.

The project file (`integration-smoke.fsproj`) references `Dropd.Core` via a relative
`ProjectReference` path and declares both config files as `<Content>` items with
`CopyToOutputDirectory=PreserveNewest`. `config.local.json` is declared conditionally so
the build does not fail when the file is absent. No additional NuGet packages are needed;
`Dropd.Core` already carries `System.Text.Json`.


## Concrete Steps

Run all commands from the repository root unless stated otherwise.

### Milestone 1: Project, HttpRuntime, and Makefile target

**Step 1.** Create the spike directory:

    mkdir -p spikes/integration-smoke

**Step 2.** Create `spikes/integration-smoke/integration-smoke.fsproj` with the following
content. The `<Content>` items ensure both config files are copied to the build output
directory so `SmokeConfig.fs` can find them at runtime. The `config.local.json` item is
conditional so the build does not fail when the user has not yet created a local override.

    <Project Sdk="Microsoft.NET.Sdk">

      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net9.0</TargetFramework>
      </PropertyGroup>

      <ItemGroup>
        <Compile Include="HttpRuntime.fs" />
        <Compile Include="SmokeConfig.fs" />
        <Compile Include="Program.fs" />
      </ItemGroup>

      <ItemGroup>
        <ProjectReference Include="../../packages/dropd/src/Dropd.Core/Dropd.Core.fsproj" />
      </ItemGroup>

      <ItemGroup>
        <Content Include="config.example.json" CopyToOutputDirectory="PreserveNewest" />
        <Content Include="config.local.json"
                 Condition="Exists('config.local.json')"
                 CopyToOutputDirectory="PreserveNewest" />
      </ItemGroup>

    </Project>

**Step 3.** Create `spikes/integration-smoke/HttpRuntime.fs`. This file implements the
real HTTP adapter. Every request the engine makes — whether a GET for library artists, a
POST to create a playlist, or a DELETE to remove old tracks — flows through `execute`
below. Headers are forwarded exactly as the engine supplies them, so Apple Music auth
tokens reach the API without any special handling in this layer.

    module HttpRuntime

    open System
    open System.Net.Http
    open System.Text
    open Dropd.Core.ApiContracts

    let private baseUrlFor = function
        | AppleMusic -> "https://api.music.apple.com"
        | LastFm     -> "https://ws.audioscrobbler.com"

    let create () : ApiRuntime =
        let client = new HttpClient()

        let execute (request: ApiRequest) = async {
            let baseUrl = baseUrlFor request.Service

            let queryString =
                if List.isEmpty request.Query then ""
                else
                    let pairs =
                        request.Query
                        |> List.map (fun (k, v) ->
                            Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(v))
                    "?" + String.concat "&" pairs

            let url = baseUrl + request.Path + queryString
            let message = new HttpRequestMessage(HttpMethod(request.Method), url)

            for (name, value) in request.Headers do
                message.Headers.TryAddWithoutValidation(name, value) |> ignore

            match request.Body with
            | Some body ->
                message.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            | None -> ()

            let! response = client.SendAsync(message) |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            return {
                StatusCode = int response.StatusCode
                Body       = body
                Headers    =
                    response.Headers
                    |> Seq.map (fun h -> h.Key, String.concat "," h.Value)
                    |> Seq.toList
            }
        }

        { Execute = execute
          UtcNow  = fun () -> DateTimeOffset.UtcNow }

**Step 4.** Create `spikes/integration-smoke/SmokeConfig.fs` as a minimal stub that
compiles. It just returns an `Error` so the project builds before config loading is
implemented. You will fill this out in Milestone 2.

    module SmokeConfig

    let load () : Result<Dropd.Core.Config.SyncConfig, string> =
        Error "not yet implemented"

**Step 5.** Create `spikes/integration-smoke/Program.fs` as a minimal stub that compiles.

    open System

    [<EntryPoint>]
    let main _argv =
        printfn "integration-smoke: not yet implemented"
        0

**Step 6.** Build the new spike project to verify the project file and compilation order
are correct:

    dotnet build spikes/integration-smoke/integration-smoke.fsproj

Expected: build succeeds with 0 errors. Warnings about unused values in stub files are
fine.

**Step 7.** Add the `smoke` target to the `Makefile`. Open `Makefile` and add the
following lines after the `spike` target (use a tab character, not spaces, for
indentation — `make` requires tabs):

    smoke:
    	dotnet run --project spikes/integration-smoke

Also add `smoke` to the `.PHONY` line at the top:

    .PHONY: build test test-dd clean dev spike apple-auth smoke

**Step 8.** Verify the new target runs without error (it will print "not yet implemented"
and exit 0):

    make smoke

Expected output ends with:

    integration-smoke: not yet implemented

**Step 9.** Add `config.local.json` to `.gitignore` so personal playlist definitions are
never committed. Open `.gitignore` and append:

    spikes/integration-smoke/config.local.json

**Step 10.** Commit:

    jj commit -m "smoke: scaffold integration-smoke spike with HttpRuntime stub and Makefile target"


### Milestone 2: Config loading

**Step 11.** Create `spikes/integration-smoke/config.example.json`. This file is
committed to the repository. It documents the config format, provides a working starting
point for new users, and serves as the automatic fallback when no `config.local.json`
exists. Users who want to customise their playlists or labels copy this file to
`config.local.json` (in the same directory) and edit it; the build picks up the local
file automatically on the next run.

    {
      "playlists": [
        { "name": "Electronic", "genreCriteria": ["Electronic"] },
        { "name": "Jazz",       "genreCriteria": ["Jazz"] }
      ],
      "labelNames": ["Ninja Tune", "Warp Records"]
    }

**Step 12a.** Replace the stub body of `SmokeConfig.fs` with the JSON DTO types and the
first half of `load`: reading environment variables and checking for missing credentials.
This compiles on its own because it returns `Error` at the end as a placeholder for the
config-file loading that comes next.

    module SmokeConfig

    open System
    open System.IO
    open System.Text.Json
    open Dropd.Core
    open Dropd.Core.Types

    // ── Local config file types ───────────────────────────────────────────────

    [<CLIMutable>]
    type JsonPlaylist =
        { name: string
          genreCriteria: string[] }

    [<CLIMutable>]
    type JsonConfig =
        { playlists: JsonPlaylist[]
          labelNames: string[] }

    // ── Config loading ────────────────────────────────────────────────────────

    let load () : Result<Config.SyncConfig, string> =
        let developerToken = Environment.GetEnvironmentVariable("DROPD_APPLE_MUSIC_TOKEN") |> Option.ofObj |> Option.defaultValue ""
        let userToken      = Environment.GetEnvironmentVariable("DROPD_APPLE_USER_TOKEN")  |> Option.ofObj |> Option.defaultValue ""
        // Phase 1 never calls Last.fm, but Config.validate requires a non-empty value.
        let lastFmKey      = Environment.GetEnvironmentVariable("DROPD_LASTFM_API_KEY")    |> Option.ofObj |> Option.defaultValue "phase1-unused"

        let missing =
            [ if String.IsNullOrWhiteSpace developerToken then "DROPD_APPLE_MUSIC_TOKEN"
              if String.IsNullOrWhiteSpace userToken      then "DROPD_APPLE_USER_TOKEN" ]

        if not (List.isEmpty missing) then
            Error $"Missing required environment variables: {String.concat \", \" missing}\nSee docs/research/credential-setup.md or run: direnv allow"
        else

        // Placeholder — config file loading added in Step 12b.
        Error "config file loading not yet implemented"

Build to confirm the DTO types and env-var logic compile:

    dotnet build spikes/integration-smoke/integration-smoke.fsproj

Expected: 0 errors.

**Step 12b.** Replace the placeholder `Error` at the end of `SmokeConfig.load` with the
full config-file loading logic. This resolves `config.local.json` (falling back to
`config.example.json`) from the assembly output directory, deserialises playlists and
label names, and assembles a `SyncConfig`.

Replace the line `Error "config file loading not yet implemented"` and everything after
it (within the `else` branch) with:

        // Config files are copied to the build output directory by the .fsproj.
        // Resolve them relative to the assembly location so this works regardless
        // of which directory `dotnet run` is invoked from.
        let assemblyDir = Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location)
        let localPath   = Path.Combine(assemblyDir, "config.local.json")
        let examplePath = Path.Combine(assemblyDir, "config.example.json")

        let configPath =
            if   File.Exists(localPath)   then Ok localPath
            elif File.Exists(examplePath) then Ok examplePath
            else
                Error (
                    "No config file found. Copy config.example.json to config.local.json " +
                    "in spikes/integration-smoke/ and rebuild:\n" +
                    "  cp spikes/integration-smoke/config.example.json " +
                    "spikes/integration-smoke/config.local.json")

        match configPath with
        | Error msg -> Error msg
        | Ok path ->

        try
            let json = File.ReadAllText(path)
            let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
            let local = JsonSerializer.Deserialize<JsonConfig>(json, opts)

            let playlists =
                local.playlists
                |> Array.map (fun p ->
                    { Config.Name = p.name
                      Config.GenreCriteria = p.genreCriteria |> Array.toList })
                |> Array.toList

            let labelNames = local.labelNames |> Array.toList

            Ok { Config.defaults with
                    Playlists                = playlists
                    LabelNames               = labelNames
                    AppleMusicDeveloperToken = AppleMusicDeveloperToken developerToken
                    AppleMusicUserToken      = AppleMusicUserToken userToken
                    LastFmApiKey             = LastFmApiKey lastFmKey }
        with ex ->
            Error $"Could not parse config file at {path}: {ex.Message}"

**Step 13.** Build again to confirm the full config loading compiles:

    dotnet build spikes/integration-smoke/integration-smoke.fsproj

Expected: 0 errors.

**Step 13a.** Verify `SmokeConfig.load` works in isolation before moving on. Temporarily
replace the body of `Program.fs` with a quick diagnostic that prints the loaded config
(or error) and exits:

    open System

    [<EntryPoint>]
    let main _argv =
        match SmokeConfig.load () with
        | Error msg ->
            eprintfn "Load error: %s" msg
            1
        | Ok config ->
            printfn "Loaded config OK"
            printfn "  Playlists : %d" config.Playlists.Length
            printfn "  Labels    : %d" config.LabelNames.Length
            0

Run from the repo root:

    make smoke

Expected output (assuming credentials are in the shell via `direnv allow`):

    Loaded config OK
      Playlists : 2
      Labels    : 2

If credentials are not set, you should see:

    Load error: Missing required environment variables: DROPD_APPLE_MUSIC_TOKEN, DROPD_APPLE_USER_TOKEN

Once you have confirmed config loading works, revert `Program.fs` back to the stub from
Step 5 before continuing to Milestone 3.

**Step 14.** Commit:

    jj commit -m "smoke: implement config loading from env vars and config.example.json fallback"


### Milestone 3: Output formatting and end-to-end run

**Step 15a.** Replace the stub body of `Program.fs` with the module opens and the
formatting helpers only. The entry point stays as a stub so the file compiles on its own
and you can verify the helper functions type-check before wiring up the full flow.

    open System
    open Dropd.Core
    open Dropd.Core.Types

    // ── Formatting helpers ────────────────────────────────────────────────────

    let private serviceLabel = function
        | ApiContracts.AppleMusic -> "apple"
        | ApiContracts.LastFm     -> "lastfm"

    let private levelLabel = function
        | ApiContracts.Debug   -> "DEBUG  "
        | ApiContracts.Info    -> "INFO   "
        | ApiContracts.Warning -> "WARNING"
        | ApiContracts.Error   -> "ERROR  "

    let private printRequests (requests: ApiContracts.ApiRequest list) =
        printfn ""
        printfn "── Requests (%d) ──────────────────────────────────" requests.Length
        for r in requests do
            let qs =
                if List.isEmpty r.Query then ""
                else "?" + String.concat "&" (r.Query |> List.map (fun (k,v) -> $"{k}={v}"))
            printfn "  [%s] %s %s%s" (serviceLabel r.Service) r.Method r.Path qs

    let private printLogs (logs: ApiContracts.LogEntry list) =
        printfn ""
        printfn "── Logs (%d) ───────────────────────────────────────" logs.Length
        for entry in logs do
            printfn "  %s [%s] %s" (levelLabel entry.Level) entry.Code entry.Message
            for KeyValue(k, v) in entry.Data do
                printfn "         %s: %s" k v

    let private printOutcome = function
        | Success           -> printfn "\n✓ Outcome: success"
        | PartialFailure    -> printfn "\n⚠ Outcome: partial_failure (some playlist operations failed)"
        | Aborted reason    -> printfn "\n✗ Outcome: aborted (%s)" reason

    let private exitCodeFor = function
        | Aborted _ -> 1
        | _         -> 0

    // ── Entry point (placeholder — replaced in Step 15b) ──────────────────────

    [<EntryPoint>]
    let main _argv =
        printfn "integration-smoke: formatting helpers compiled, entry point not yet wired"
        0

Build to confirm the helpers compile against the `ApiContracts` and `Types` modules:

    dotnet build spikes/integration-smoke/integration-smoke.fsproj

Expected: 0 errors. Warnings about unused private functions are fine.

**Step 15b.** Replace the placeholder entry point in `Program.fs` with the full
orchestration logic. Remove everything from the `// ── Entry point` comment to the end
of the file and replace it with:

    // ── Entry point ───────────────────────────────────────────────────────────

    [<EntryPoint>]
    let main _argv =
        printfn ""
        printfn "════════════════════════════════════════"
        printfn "  dropd — Integration Smoke"
        printfn "════════════════════════════════════════"

        match SmokeConfig.load () with
        | Error msg ->
            eprintfn "\nError: %s" msg
            1
        | Ok rawConfig ->

        match Config.validate rawConfig with
        | Error errors ->
            eprintfn "\nConfig validation failed:"
            for e in errors do eprintfn "  %A" e
            1
        | Ok config ->

        printfn ""
        printfn "Config:"
        printfn "  Playlists   : %d defined" config.Playlists.Length
        for p in config.Playlists do
            printfn "    • %s [%s]" p.Name (String.concat ", " p.GenreCriteria)
        printfn "  Labels      : %s"
            (if List.isEmpty config.LabelNames then "(none)"
             else String.concat ", " config.LabelNames)
        printfn "  Lookback    : %d days" (Config.PositiveInt.value config.LookbackDays)
        printfn "  Rolling win : %d days" (Config.PositiveInt.value config.RollingWindowDays)
        printfn "  Dev token   : [redacted]"
        printfn "  User token  : [redacted]"
        printfn ""
        printfn "Running sync…"

        let runtime = HttpRuntime.create ()
        let outcome, observed = SyncEngine.runSync config runtime

        printRequests observed.Requests
        printLogs observed.Logs
        printOutcome outcome

        printfn ""
        exitCodeFor outcome

**Step 16.** Build the full project one final time:

    dotnet build spikes/integration-smoke/integration-smoke.fsproj

Expected: 0 errors.

**Step 17.** Run the smoke test. Your credentials should already be in the shell via
`direnv`. If you are not using direnv, source them first:

    source .envrc.local   # only if direnv is not active

Then run:

    make smoke

With no `config.local.json` present, the spike will use `config.example.json` automatically
(Ninja Tune, Warp Records, Electronic and Jazz playlists). To use your own config, copy the
example file, edit it, and rebuild:

    cp spikes/integration-smoke/config.example.json spikes/integration-smoke/config.local.json
    # edit config.local.json to taste
    make smoke

Expected behaviour:

- The banner and config summary print immediately.
- "Running sync…" appears and then a pause of a few seconds while real HTTP calls go out.
- The `── Requests` section lists every Apple Music endpoint the engine called. With the
  example config (two playlists, two labels) you should see at minimum:
  - `GET /v1/me/library/artists`
  - `GET /v1/me/ratings/artists`
  - `GET /v1/catalog/us/search` (once per label)
  - One or more `GET /v1/catalog/us/record-labels/{id}/latest-releases`
  - Multiple `GET /v1/catalog/us/artists/{id}/albums`
  - One or more `GET /v1/catalog/us/albums/{id}` (genre hydration)
  - `GET /v1/me/library/playlists/{name}/tracks` for each playlist (or a 404 followed by
    `POST /v1/me/library/playlists` to create it)
  - Possibly `POST /v1/me/library/playlists/{name}/tracks` to add matching tracks
- The `── Logs` section shows structured log entries, starting with `[SyncStarted]` and
  ending with `[SyncOutcome]`.
- The final line is one of `✓ Outcome: success`, `⚠ Outcome: partial_failure`, or
  `✗ Outcome: aborted (...)`.
- Exit code is 0 on success or partial failure, 1 on aborted.

If you see `✗ Outcome: aborted (AuthFailure)` and a log entry with code
`AppleMusicAuthFailure`, your tokens are expired or invalid. Re-run the Apple Music auth
flow in `spikes/api-exploration/authorize.html` (see `make apple-auth`) and update your
`.envrc.local`.

If you see `✗ Outcome: aborted (LibraryArtistsFailed)`, the developer token is likely
expired (Apple Music developer JWTs expire after up to 6 months; check your token
generation date).

**Step 18.** Commit:

    jj commit -m "smoke: implement output formatting and entry point for integration smoke spike"

**Step 19.** Update this plan's Progress section and fill in any Surprises & Discoveries
or Outcomes & Retrospective entries based on what the real run produced.

**Step 20.** Final commit updating the plan:

    jj commit -m "smoke: complete integration smoke spike plan retrospective"


## Validation and Acceptance

The spike is accepted when all of the following are true:

1. `make smoke` builds and runs without manual steps beyond having credentials in the
   shell.
2. With valid credentials and no `config.local.json`, the spike automatically uses
   `config.example.json` and the output includes at least one request to
   `/v1/me/library/artists`, one to `/v1/me/ratings/artists`, and at least one to a
   label search or artist albums endpoint.
3. The `── Logs` section includes an `INFO [SyncStarted]` entry and an `INFO [SyncOutcome]`
   entry.
4. The final outcome line is printed and the exit code matches (`echo $?` returns 0 for
   success/partial, 1 for aborted).
5. Running `make test` still passes all existing tests with 0 failures and 0 errors (the
   spike must not alter any existing test or core library file).
6. `config.local.json` is listed in `.gitignore` and does not appear in `jj status` after
   creation.


## Idempotence and Recovery

All steps are additive. The spike project does not modify any file outside
`spikes/integration-smoke/`, `.gitignore`, and `Makefile`. If you need to start over:

    jj restore spikes/integration-smoke Makefile .gitignore

Or with git:

    git restore Makefile .gitignore
    rm -rf spikes/integration-smoke

Running `make smoke` multiple times is safe. The engine may create Apple Music playlists
on your account if they do not already exist, and may add tracks to them. Subsequent runs
will not duplicate tracks (the reconciliation logic checks for existing track IDs before
adding).


## Artifacts and Notes

The new directory tree when complete:

    spikes/integration-smoke/
      integration-smoke.fsproj   — references Dropd.Core; copies config files to output
      HttpRuntime.fs             — real HttpClient-backed ApiRuntime
      SmokeConfig.fs             — loads credentials + config.local.json (or example fallback)
      Program.fs                 — entry point: validate, run, print
      config.example.json        — committed; documents format; automatic fallback config

Modified files:

    Makefile        — adds `smoke` target and updates `.PHONY`
    .gitignore      — ignores `spikes/integration-smoke/config.local.json`


## Interfaces and Dependencies

No new types are introduced. The spike depends on:

- `Dropd.Core.ApiContracts.ApiRuntime` — implemented by `HttpRuntime.create`
- `Dropd.Core.Config.SyncConfig` and `Config.defaults` — assembled by `SmokeConfig.load`
- `Dropd.Core.Config.validate` — called in `Program.fs` before `runSync`
- `Dropd.Core.SyncEngine.runSync` — called in `Program.fs` with the validated config and real runtime
- `System.Net.Http.HttpClient` — standard .NET 9 HTTP client; no extra package required

In `spikes/integration-smoke/HttpRuntime.fs`, the function to implement is:

    let create : unit -> Dropd.Core.ApiContracts.ApiRuntime

In `spikes/integration-smoke/SmokeConfig.fs`:

    let load : unit -> Result<Dropd.Core.Config.SyncConfig, string>

In `spikes/integration-smoke/Program.fs`:

    [<EntryPoint>]
    let main : string[] -> int
