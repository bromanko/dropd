namespace Dropd.Core

open System
open System.Text.Json
open System.Text.Json.Nodes
open Dropd.Core.Types

module PlaylistReconcile =

    module AC = ApiContracts

    // Re-export shared helpers under short aliases for local use.
    let private mkLog = JsonHelpers.mkLog
    let private tryGetProperty = JsonHelpers.tryGetProperty
    let private tryGetString = JsonHelpers.tryGetString
    let private tryParseDateOnly = JsonHelpers.tryParseDateOnly

    type ExistingTrack =
        { Id: CatalogTrackId
          ReleaseDate: DateOnly option }

    let private trackIdValue (CatalogTrackId value) = value
    let private withTrackId value = CatalogTrackId value

    let private tryParseExistingTrack (item: JsonElement) : ExistingTrack option =
        let attrs = item |> tryGetProperty "attributes"

        // Library playlist tracks have library-scoped IDs (e.g. "i.Mla0tqxJ0Q")
        // at the top level, but the catalog ID we need for dedup lives at
        // attributes.playParams.catalogId.  Fall back to the top-level id when
        // catalogId is absent (e.g. in test fixtures).
        let catalogId =
            attrs
            |> Option.bind (tryGetProperty "playParams")
            |> Option.bind (tryGetString "catalogId")
            |> Option.orElse (tryGetString "id" item)

        match catalogId with
        | None -> None
        | Some id ->
            let releaseDate =
                attrs
                |> Option.bind (tryGetString "releaseDate")
                |> Option.bind tryParseDateOnly

            Some
                { Id = withTrackId id
                  ReleaseDate = releaseDate }

    let private parseExistingTracksPage (body: string) : ExistingTrack list * string option =
        use document = JsonDocument.Parse(body)
        let root = document.RootElement

        let tracks =
            match tryGetProperty "data" root with
            | Some value when value.ValueKind = JsonValueKind.Array ->
                value.EnumerateArray()
                |> Seq.choose tryParseExistingTrack
                |> Seq.toList
            | _ -> []

        tracks, (tryGetString "next" root)

    let private playlistTracksPath (playlistId: string) =
        $"/v1/me/library/playlists/{playlistId}/tracks"

    /// Parse the library playlists listing and return a name → ID map.
    let private parsePlaylistMap (body: string) : Map<string, string> =
        use document = JsonDocument.Parse(body)

        let items =
            match tryGetProperty "data" document.RootElement with
            | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
            | _ -> []

        items
        |> List.choose (fun item ->
            match tryGetString "id" item with
            | None -> None
            | Some id ->
                let name =
                    item
                    |> tryGetProperty "attributes"
                    |> Option.bind (tryGetString "name")

                match name with
                | Some n -> Some(n, id)
                | None -> None)
        // Use the first occurrence for each name (in case of duplicates).
        |> List.rev
        |> Map.ofList

    /// Parse the playlist ID from a create-playlist response.
    let private parseCreatedPlaylistId (body: string) : string option =
        use document = JsonDocument.Parse(body)

        let dataElement = tryGetProperty "data" document.RootElement

        // The response may be a single object or an array with one element.
        let firstItem =
            match dataElement with
            | Some value when value.ValueKind = JsonValueKind.Array ->
                value.EnumerateArray() |> Seq.tryHead
            | Some value when value.ValueKind = JsonValueKind.Object ->
                Some value
            | _ -> None

        firstItem |> Option.bind (tryGetString "id")

    // Finding 12: delegate to the single, path-aware implementation in JsonHelpers
    // so Music-User-Token is included only for /v1/me paths, consistently with
    // SyncEngine.
    let private appleHeaders (config: Config.ValidSyncConfig) (path: string) =
        JsonHelpers.appleHeaders config path

    /// Fetch all library playlists and return a name → ID map.
    let private fetchPlaylistMap
        (config: Config.ValidSyncConfig)
        (runtime: AC.ApiRuntime)
        : Async<Result<Map<string, string>, AC.ApiResponse>> =
        async {
            let path = "/v1/me/library/playlists"

            let request: AC.ApiRequest =
                { Service = AC.AppleMusic
                  Method = "GET"
                  Path = path
                  Query = []
                  Headers = appleHeaders config path
                  Body = None }

            let! response = runtime.Execute request

            if response.StatusCode >= 200 && response.StatusCode < 300 then
                return Ok(parsePlaylistMap response.Body)
            else
                return Error response
        }

    /// Fetch all tracks from a playlist, following pagination `next` links.
    /// Returns Ok tracks on success, or Error on the first failed page request.
    /// A 404 response (empty playlist) is treated as zero tracks via fetchAllPages.
    let private fetchAllPlaylistTracks
        (config: Config.ValidSyncConfig)
        (runtime: AC.ApiRuntime)
        (playlistId: string)
        : Async<Result<ExistingTrack list, int>> =
        async {
            let maxPages = Config.PositiveInt.value config.MaxPages
            let path = playlistTracksPath playlistId

            let firstRequest: AC.ApiRequest =
                { Service = AC.AppleMusic
                  Method = "GET"
                  Path = path
                  Query = []
                  Headers = appleHeaders config path
                  Body = None }

            let! result =
                ResilientPipeline.fetchAllPages runtime maxPages firstRequest parseExistingTracksPage

            match result with
            | Ok (tracks, _pages, _truncated) -> return Ok tracks
            | Error response -> return Error response.StatusCode
        }

    let computePlan
        (today: DateOnly)
        (rollingWindowDays: int)
        (similarArtistMaxPercent: int)
        (similarArtistIds: Set<CatalogArtistId>)
        (playlist: Config.PlaylistDefinition)
        (releases: AC.DiscoveredRelease list)
        (existingTracks: ExistingTrack list)
        : AC.PlaylistPlan =
        let criteria = playlist.GenreCriteria |> List.map Normalization.normalizeText |> Set.ofList

        // Build desired tracks as (trackId, isSimilar) pairs in stable release order.
        let desiredTracksWithSource =
            releases
            |> List.filter (fun (release: AC.DiscoveredRelease) ->
                release.GenreNames
                |> List.map Normalization.normalizeText
                |> List.exists criteria.Contains)
            |> List.collect (fun release ->
                let isSimilar = similarArtistIds.Contains release.ArtistId
                release.TrackIds |> List.map (fun tid -> tid, isSimilar))

        // Deduplicate by track ID, preserving first occurrence.
        // Uses a fold with an immutable Set to avoid mutable HashSet allocations.
        let deduped =
            desiredTracksWithSource
            |> List.fold
                (fun (seen, acc) (tid, isSimilar) ->
                    if Set.contains tid seen then (seen, acc)
                    else (Set.add tid seen, (tid, isSimilar) :: acc))
                (Set.empty, [])
            |> snd
            |> List.rev

        // Apply similar-artist cap: limit similar tracks to configured percentage.
        let totalDesired = deduped.Length
        // Floor-truncated intentionally: for small playlists (e.g. 3 tracks at 30%)
        // this means 0 similar tracks, erring on the side of seed-artist content.
        let allowedSimilar =
            if totalDesired = 0 then 0
            else int (float similarArtistMaxPercent * float totalDesired / 100.0)

        // Use a fold to make the stateful similar-count tracking explicit and pure.
        let cappedTracks =
            deduped
            |> List.fold
                (fun (similarCount, acc) (tid, isSimilar) ->
                    if isSimilar then
                        if similarCount < allowedSimilar then (similarCount + 1, (tid, isSimilar) :: acc)
                        else (similarCount, acc)
                    else (similarCount, (tid, isSimilar) :: acc))
                (0, [])
            |> snd
            |> List.rev

        let desiredTrackIds = cappedTracks |> List.map fst

        let existingIds = existingTracks |> List.map (fun track -> track.Id) |> Set.ofList

        let addTracks = desiredTrackIds |> List.filter (fun trackId -> not (existingIds.Contains trackId))

        let removeCutoff = today.AddDays(-rollingWindowDays)

        let removeTracks =
            existingTracks
            |> List.choose (fun track ->
                match track.ReleaseDate with
                | Some date when date < removeCutoff -> Some track.Id
                | _ -> None)
            |> Normalization.dedupByTrackId

        { PlaylistName = playlist.Name
          AddTracks = addTracks
          RemoveTracks = removeTracks }

    // Findings 1 / 8: applyPlan is now fully async; no Async.RunSynchronously.
    // Findings 4 / 11: request bodies are built with System.Text.Json so special
    //   characters in names or IDs cannot corrupt the JSON.
    let applyPlan
        (config: Config.ValidSyncConfig)
        (runtime: AC.ApiRuntime)
        (playlistId: string)
        (plan: AC.PlaylistPlan)
        : Async<AC.ReconcileResult * AC.LogEntry list> =
        async {
            let! addCount, addLogs =
                async {
                    if List.isEmpty plan.AddTracks then
                        return 0, []
                    else
                        // Finding 4 / 11: JsonObject prevents JSON injection from
                        // track IDs containing quotes or backslashes.
                        // Apple Music API expects: {"data":[{"id":"...","type":"songs"}]}
                        let arr = JsonArray()

                        plan.AddTracks
                        |> List.iter (fun t ->
                            let item = JsonObject()
                            item["id"] <- JsonValue.Create(trackIdValue t)
                            item["type"] <- JsonValue.Create("songs")
                            arr.Add(item))

                        let node = JsonObject()
                        node["data"] <- arr
                        let body = node.ToJsonString()

                        let path = playlistTracksPath playlistId

                        let request: AC.ApiRequest =
                            { Service = AC.AppleMusic
                              Method = "POST"
                              Path = path
                              Query = []
                              Headers = appleHeaders config path
                              Body = Some body }

                        let! response = runtime.Execute request

                        if response.StatusCode >= 200 && response.StatusCode < 300 then
                            return plan.AddTracks.Length, []
                        else
                            return
                                0,
                                [ mkLog
                                      AC.Error
                                      "PlaylistTrackAddFailure"
                                      $"Failed to add tracks to playlist '{plan.PlaylistName}'."
                                      (Map
                                          [ "playlist", plan.PlaylistName
                                            "status", string response.StatusCode
                                            "trackIds",
                                            plan.AddTracks |> List.map trackIdValue |> String.concat "," ]) ]
                }

            // Track removal is not yet supported by the Apple Music REST API
            // (DELETE /v1/me/library/playlists/{id}/tracks returns 401).
            // Log the stale tracks at Info level so the plan is visible, but
            // do not attempt the API call or count it as a failure.
            let removeLogs =
                if List.isEmpty plan.RemoveTracks then
                    []
                else
                    let ids = plan.RemoveTracks |> List.map trackIdValue |> String.concat ","

                    [ mkLog
                          AC.Info
                          "PlaylistTrackRemoveSkipped"
                          $"Skipped removing {plan.RemoveTracks.Length} stale track(s) from playlist '{plan.PlaylistName}' (not supported by Apple Music REST API)."
                          (Map [ "playlist", plan.PlaylistName; "trackIds", ids ]) ]

            let result: AC.ReconcileResult =
                { Plans = [ plan ]
                  AddedCount = addCount
                  RemovedCount = 0
                  HadPlaylistFailures = not (List.isEmpty addLogs)
                  ResolvedPlaylistIds = Map.empty }

            return result, addLogs @ removeLogs
        }

    // Findings 1 / 2 / 8: reconcilePlaylists is now fully async (no
    // Async.RunSynchronously anywhere) and uses ResizeArray accumulators to
    // avoid the O(n²) list-append pattern in the fold.
    let reconcilePlaylists
        (config: Config.ValidSyncConfig)
        (discovery: AC.DiscoveryResult)
        (runtime: AC.ApiRuntime)
        (knownPlaylistIds: Map<string, string>)
        : Async<AC.ReconcileResult * AC.LogEntry list> =
        async {
            let today = runtime.UtcNow() |> fun now -> DateOnly.FromDateTime(now.UtcDateTime)
            let rollingWindowDays = config.RollingWindowDays |> Config.PositiveInt.value

            // Finding 2: ResizeArray accumulators eliminate the O(n²) @ appends.
            let planAcc = ResizeArray<AC.PlaylistPlan>()
            let logAcc = ResizeArray<AC.LogEntry>()
            let mutable addedTotal = 0
            let mutable removedTotal = 0
            let mutable hadFailures = false

            // Fetch all library playlists once and build a name → ID map so we
            // can look up existing playlists by name and use their IDs for all
            // subsequent API calls. Apple Music playlists are addressed by ID,
            // not by name.
            //
            // The API listing endpoint has severe eventual consistency issues —
            // newly created playlists may not appear for minutes or may lose
            // their names. Merge the API listing with the caller-supplied
            // knownPlaylistIds (which may come from a local cache) so that
            // playlists created in previous runs are found reliably.
            let! apiMap =
                async {
                    let! result = fetchPlaylistMap config runtime

                    match result with
                    | Ok m -> return m
                    | Error response ->
                        logAcc.Add(
                            mkLog
                                AC.Error
                                "PlaylistListFailure"
                                "Failed to list library playlists."
                                (Map [ "status", string response.StatusCode ])
                        )

                        return Map.empty
                }

            // API listing wins when both sources have a mapping for the same
            // name (the API may have a newer ID after user actions).
            let playlistMap =
                knownPlaylistIds
                |> Map.fold (fun acc name id ->
                    if Map.containsKey name acc then acc
                    else Map.add name id acc) apiMap

            // Track all resolved IDs so the caller can persist them.
            let resolvedIds = ResizeArray<string * string>()

            for playlist in config.Playlists do
                // Resolve the playlist ID: look up by name, or create if missing.
                let! resolvedId, existingTracks, createFailure =
                    async {
                        match playlistMap |> Map.tryFind playlist.Name with
                        | Some existingId ->
                            // Playlist exists — fetch all pages of current tracks.
                            let! tracksResult = fetchAllPlaylistTracks config runtime existingId

                            match tracksResult with
                            | Ok tracks -> return Some existingId, tracks, false
                            | Error status ->
                                logAcc.Add(
                                    mkLog
                                        AC.Error
                                        "PlaylistTrackListFailure"
                                        $"Failed to read playlist '{playlist.Name}'."
                                        (Map [ "playlist", playlist.Name; "status", string status ])
                                )

                                return Some existingId, [], true

                        | None ->
                            // Playlist does not exist — create it.
                            // Apple Music API requires: {"attributes":{"name":"..."}}
                            let attrs = JsonObject()
                            attrs["name"] <- JsonValue.Create(playlist.Name)
                            let node = JsonObject()
                            node["attributes"] <- attrs
                            let createBody = node.ToJsonString()

                            let createPath = "/v1/me/library/playlists"

                            let createRequest: AC.ApiRequest =
                                { Service = AC.AppleMusic
                                  Method = "POST"
                                  Path = createPath
                                  Query = []
                                  Headers = appleHeaders config createPath
                                  Body = Some createBody }

                            let! createResponse = runtime.Execute createRequest

                            if createResponse.StatusCode >= 200 && createResponse.StatusCode < 300 then
                                let newId = parseCreatedPlaylistId createResponse.Body
                                return newId, [], false
                            else
                                logAcc.Add(
                                    mkLog
                                        AC.Error
                                        "PlaylistCreateFailure"
                                        $"Failed to create playlist '{playlist.Name}'."
                                        (Map
                                            [ "playlist", playlist.Name
                                              "status", string createResponse.StatusCode ])
                                )

                                return None, [], true
                    }

                let similarArtistIds =
                    discovery.SimilarArtists
                    |> List.map (fun a -> a.Id)
                    |> Set.ofList

                let similarArtistMaxPercent = config.SimilarArtistMaxPercent |> Config.Percent.value

                let plan = computePlan today rollingWindowDays similarArtistMaxPercent similarArtistIds playlist discovery.Releases existingTracks
                planAcc.Add plan

                match resolvedId with
                | Some pid ->
                    resolvedIds.Add(playlist.Name, pid)
                    let! applyResult, applyLogs = applyPlan config runtime pid plan
                    logAcc.AddRange applyLogs
                    addedTotal <- addedTotal + applyResult.AddedCount
                    removedTotal <- removedTotal + applyResult.RemovedCount
                    hadFailures <- hadFailures || createFailure || applyResult.HadPlaylistFailures
                | None ->
                    // No playlist ID available (create failed) — skip apply but
                    // mark as failure so the overall outcome is partial_failure.
                    hadFailures <- true

            let finalResult: AC.ReconcileResult =
                { Plans = planAcc |> Seq.toList
                  AddedCount = addedTotal
                  RemovedCount = removedTotal
                  HadPlaylistFailures = hadFailures
                  ResolvedPlaylistIds = resolvedIds |> Seq.toList |> Map.ofList }

            return finalResult, logAcc |> Seq.toList
        }
