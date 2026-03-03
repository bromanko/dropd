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

    let private playlistPath (playlistName: string) =
        let escaped = Uri.EscapeDataString playlistName
        $"/v1/me/library/playlists/{escaped}/tracks"

    // Finding 12: delegate to the single, path-aware implementation in JsonHelpers
    // so Music-User-Token is included only for /v1/me paths, consistently with
    // SyncEngine.
    let private appleHeaders (config: Config.ValidSyncConfig) (path: string) =
        JsonHelpers.appleHeaders config path

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

                        let path = playlistPath plan.PlaylistName

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
                        let path = playlistPath plan.PlaylistName

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

            for playlist in config.Playlists do
                let getPath = playlistPath playlist.Name

                let getRequest: AC.ApiRequest =
                    { Service = AC.AppleMusic
                      Method = "GET"
                      Path = getPath
                      Query = []
                      Headers = appleHeaders config getPath
                      Body = None }

                let! existingResponse = runtime.Execute getRequest

                // Resolve existing tracks; if 404, attempt to create the playlist.
                // A nested async { } keeps all awaits properly structured.
                let! existingTracks, createFailure =
                    async {
                        if existingResponse.StatusCode = 404 then
                            // Finding 4 / 11: JsonObject prevents JSON injection from
                            // playlist names containing quotes or backslashes.
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
                                return [], false
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

                                return [], true

                        elif existingResponse.StatusCode >= 200 && existingResponse.StatusCode < 300 then
                            return parseExistingTracks existingResponse.Body, false

                        else
                            logAcc.Add(
                                mkLog
                                    AC.Error
                                    "PlaylistTrackListFailure"
                                    $"Failed to read playlist '{playlist.Name}'."
                                    (Map [ "playlist", playlist.Name; "status", string existingResponse.StatusCode ])
                            )

                            return [], true
                    }

                let plan = computePlan today rollingWindowDays playlist discovery.Releases existingTracks
                planAcc.Add plan

                let! applyResult, applyLogs = applyPlan config runtime plan
                logAcc.AddRange applyLogs
                addedTotal <- addedTotal + applyResult.AddedCount
                removedTotal <- removedTotal + applyResult.RemovedCount
                hadFailures <- hadFailures || createFailure || applyResult.HadPlaylistFailures

            let finalResult: AC.ReconcileResult =
                { Plans = planAcc |> Seq.toList
                  AddedCount = addedTotal
                  RemovedCount = removedTotal
                  HadPlaylistFailures = hadFailures }

            return finalResult, logAcc |> Seq.toList
        }
