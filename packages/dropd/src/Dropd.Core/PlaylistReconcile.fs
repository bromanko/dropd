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

    let private parseExistingTracks (body: string) : ExistingTrack list =
        use document = JsonDocument.Parse(body)

        let data =
            match tryGetProperty "data" document.RootElement with
            | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
            | _ -> []

        data
        |> List.choose (fun item ->
            match tryGetString "id" item with
            | None -> None
            | Some id ->
                let releaseDate =
                    item
                    |> tryGetProperty "attributes"
                    |> Option.bind (tryGetString "releaseDate")
                    |> Option.bind tryParseDateOnly

                Some
                    { Id = withTrackId id
                      ReleaseDate = releaseDate })

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

    let computePlan
        (today: DateOnly)
        (rollingWindowDays: int)
        (playlist: Config.PlaylistDefinition)
        (releases: AC.DiscoveredRelease list)
        (existingTracks: ExistingTrack list)
        : AC.PlaylistPlan =
        let criteria = playlist.GenreCriteria |> List.map Normalization.normalizeText |> Set.ofList

        let desiredTrackIds =
            releases
            |> List.filter (fun (release: AC.DiscoveredRelease) ->
                release.GenreNames
                |> List.map Normalization.normalizeText
                |> List.exists criteria.Contains)
            |> List.collect (fun release -> release.TrackIds)
            |> Normalization.dedupByTrackId

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

            let! removeCount, removeLogs =
                async {
                    if List.isEmpty plan.RemoveTracks then
                        return 0, []
                    else
                        let ids = plan.RemoveTracks |> List.map trackIdValue |> String.concat ","
                        let path = playlistTracksPath playlistId

                        let request: AC.ApiRequest =
                            { Service = AC.AppleMusic
                              Method = "DELETE"
                              Path = path
                              Query = [ "ids", ids ]
                              Headers = appleHeaders config path
                              Body = None }

                        let! response = runtime.Execute request

                        if response.StatusCode >= 200 && response.StatusCode < 300 then
                            return plan.RemoveTracks.Length, []
                        else
                            return
                                0,
                                [ mkLog
                                      AC.Error
                                      "PlaylistTrackRemoveFailure"
                                      $"Failed to remove tracks from playlist '{plan.PlaylistName}'."
                                      (Map
                                          [ "playlist", plan.PlaylistName
                                            "status", string response.StatusCode
                                            "trackIds", ids ]) ]
                }

            let result: AC.ReconcileResult =
                { Plans = [ plan ]
                  AddedCount = addCount
                  RemovedCount = removeCount
                  HadPlaylistFailures = not (List.isEmpty addLogs) || not (List.isEmpty removeLogs) }

            return result, addLogs @ removeLogs
        }

    // Findings 1 / 2 / 8: reconcilePlaylists is now fully async (no
    // Async.RunSynchronously anywhere) and uses ResizeArray accumulators to
    // avoid the O(n²) list-append pattern in the fold.
    let reconcilePlaylists
        (config: Config.ValidSyncConfig)
        (discovery: AC.DiscoveryResult)
        (runtime: AC.ApiRuntime)
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
            let! playlistMap =
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

            for playlist in config.Playlists do
                // Resolve the playlist ID: look up by name, or create if missing.
                let! resolvedId, existingTracks, createFailure =
                    async {
                        match playlistMap |> Map.tryFind playlist.Name with
                        | Some existingId ->
                            // Playlist exists — fetch its current tracks.
                            let tracksPath = playlistTracksPath existingId

                            let getRequest: AC.ApiRequest =
                                { Service = AC.AppleMusic
                                  Method = "GET"
                                  Path = tracksPath
                                  Query = []
                                  Headers = appleHeaders config tracksPath
                                  Body = None }

                            let! tracksResponse = runtime.Execute getRequest

                            if tracksResponse.StatusCode >= 200 && tracksResponse.StatusCode < 300 then
                                return Some existingId, parseExistingTracks tracksResponse.Body, false
                            else
                                logAcc.Add(
                                    mkLog
                                        AC.Error
                                        "PlaylistTrackListFailure"
                                        $"Failed to read playlist '{playlist.Name}'."
                                        (Map [ "playlist", playlist.Name; "status", string tracksResponse.StatusCode ])
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

                let plan = computePlan today rollingWindowDays playlist discovery.Releases existingTracks
                planAcc.Add plan

                match resolvedId with
                | Some pid ->
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
                  HadPlaylistFailures = hadFailures }

            return finalResult, logAcc |> Seq.toList
        }
