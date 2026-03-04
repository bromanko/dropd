namespace Dropd.Core

open System
open System.Text.Json
open Dropd.Core.Types

module SyncEngine =

    module AC = ApiContracts

    // Re-export shared helpers under short aliases for local use (Finding 10).
    let private mkLog = JsonHelpers.mkLog
    let private tryGetProperty = JsonHelpers.tryGetProperty
    let private tryGetString = JsonHelpers.tryGetString
    let private tryParseDateOnly = JsonHelpers.tryParseDateOnly
    let private truncateBody = JsonHelpers.truncateBody

    let private artistIdValue (CatalogArtistId value) = value
    let private albumIdValue (CatalogAlbumId value) = value
    let private withArtistId value = CatalogArtistId value
    let private withAlbumId value = CatalogAlbumId value
    let private withTrackId value = CatalogTrackId value

    let private tryGetStringArrayLocal (name: string) (element: JsonElement) =
        match tryGetProperty name element with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    item.GetString() |> Option.ofObj
                else
                    None)
            |> Seq.toList
        | _ -> []

    /// Parse the ``data`` array from a JSON response body, applying a chooser
    /// to each element.  Collects results in a single pass without intermediate
    /// list allocations (Findings 7 & 9).
    let private parseDataArray (body: string) (chooser: JsonElement -> 'a option) : 'a list =
        use document = JsonDocument.Parse(body)

        match tryGetProperty "data" document.RootElement with
        | Some data when data.ValueKind = JsonValueKind.Array ->
            let results = ResizeArray()
            let mutable enumerator = data.EnumerateArray()

            while enumerator.MoveNext() do
                match chooser enumerator.Current with
                | Some x -> results.Add(x)
                | None -> ()

            Seq.toList results
        | _ -> []

    let private parseArtistList (body: string) : Artist list =
        parseDataArray body (fun item ->
            match tryGetString "id" item with
            | None -> None
            | Some id ->
                let name =
                    item
                    |> tryGetProperty "attributes"
                    |> Option.bind (tryGetString "name")
                    |> Option.defaultValue id

                Some { Id = withArtistId id; Name = name })

    /// Parse the library artist response (with `include=catalog`) and extract
    /// catalog artist IDs and names from the `relationships.catalog.data` array.
    /// Library artists whose catalog relationship is missing or empty are skipped
    /// because their IDs cannot be used for catalog or ratings API calls.
    let private parseLibraryArtistsWithCatalog (body: string) : Artist list =
        parseDataArray body (fun item ->
            item
            |> tryGetProperty "relationships"
            |> Option.bind (tryGetProperty "catalog")
            |> Option.bind (tryGetProperty "data")
            |> Option.bind (fun data ->
                if data.ValueKind = JsonValueKind.Array then
                    data.EnumerateArray() |> Seq.tryHead
                else
                    None)
            |> Option.bind (fun catalogItem ->
                match tryGetString "id" catalogItem with
                | None -> None
                | Some catalogId ->
                    let name =
                        catalogItem
                        |> tryGetProperty "attributes"
                        |> Option.bind (tryGetString "name")
                        |> Option.defaultValue catalogId

                    Some { Id = withArtistId catalogId; Name = name }))

    /// Parse the `/v1/me/ratings/artists` response and return artist IDs that
    /// have a positive rating (value = 1). The response contains rating objects
    /// with `id` (the catalog artist ID) and `attributes.value` (1 or -1).
    let private parseRatedArtistIds (body: string) : string list =
        parseDataArray body (fun item ->
            match tryGetString "id" item with
            | None -> None
            | Some id ->
                let value =
                    item
                    |> tryGetProperty "attributes"
                    |> Option.bind (fun attrs ->
                        match tryGetProperty "value" attrs with
                        | Some v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
                        | _ -> None)
                    |> Option.defaultValue 0

                if value = 1 then Some id else None)

    let private parseSearchLabelId (body: string) =
        use document = JsonDocument.Parse(body)

        document.RootElement
        |> tryGetProperty "results"
        |> Option.bind (tryGetProperty "record-labels")
        |> Option.bind (tryGetProperty "data")
        |> Option.bind (fun data ->
            if data.ValueKind = JsonValueKind.Array then
                data.EnumerateArray() |> Seq.tryHead
            else
                None)
        |> Option.bind (tryGetString "id")

    let private parseRelease (item: JsonElement) : AC.DiscoveredRelease option =
        match tryGetString "id" item, item |> tryGetProperty "attributes" with
        | Some id, Some attributes ->
            let artistId = attributes |> tryGetString "artistId" |> Option.defaultValue "" |> withArtistId
            let artistName = attributes |> tryGetString "artistName" |> Option.defaultValue ""
            let releaseName = attributes |> tryGetString "name" |> Option.defaultValue id

            let releaseDate =
                attributes
                |> tryGetString "releaseDate"
                |> Option.bind tryParseDateOnly

            let genreNames = attributes |> tryGetStringArrayLocal "genreNames"

            let trackIds =
                item
                |> tryGetProperty "relationships"
                |> Option.bind (tryGetProperty "tracks")
                |> Option.bind (tryGetProperty "data")
                |> Option.map (fun trackData ->
                    if trackData.ValueKind = JsonValueKind.Array then
                        trackData.EnumerateArray()
                        |> Seq.choose (tryGetString "id")
                        |> Seq.map withTrackId
                        |> Seq.toList
                    else
                        [])
                |> Option.defaultValue []

            Some
                { Id = withAlbumId id
                  ArtistId = artistId
                  ArtistName = artistName
                  Name = releaseName
                  ReleaseDate = releaseDate
                  GenreNames = genreNames
                  TrackIds = trackIds }
        | _ -> None

    let private parseReleaseList (body: string) : AC.DiscoveredRelease list =
        parseDataArray body parseRelease

    let private execute (runtime: AC.ApiRuntime) (request: AC.ApiRequest) = runtime.Execute request |> Async.RunSynchronously

    // Finding 12: the single authoritative appleHeaders lives in JsonHelpers;
    // SyncEngine delegates to it rather than maintaining its own copy.
    let private appleHeaders (config: Config.ValidSyncConfig) (path: string) =
        JsonHelpers.appleHeaders config path

    let private executeApple
        (config: Config.ValidSyncConfig)
        (runtime: AC.ApiRuntime)
        (methodName: string)
        (path: string)
        (query: (string * string) list)
        (body: string option)
        =
        let request: AC.ApiRequest =
            { Service = AC.AppleMusic
              Method = methodName
              Path = path
              Query = query
              Headers = appleHeaders config path
              Body = body }

        execute runtime request

    let private toResult (parse: string -> 'a) (response: AC.ApiResponse) =
        if response.StatusCode >= 200 && response.StatusCode < 300 then
            Ok(parse response.Body)
        else
            Error response

    let fetchLibraryArtists (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) =
        executeApple config runtime "GET" "/v1/me/library/artists" [ "include", "catalog" ] None
        |> toResult parseLibraryArtistsWithCatalog

    let fetchFavoritedArtists (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (libraryArtists: Artist list) =
        if List.isEmpty libraryArtists then
            Ok []
        else
            let nameMap =
                libraryArtists
                |> List.map (fun a -> artistIdValue a.Id, a.Name)
                |> Map.ofList

            let uniqueArtists = libraryArtists |> Normalization.dedupByArtistId
            let batches = uniqueArtists |> List.map (fun a -> a.Id) |> List.chunkBySize 25

            let rec processBatches accBatches remaining =
                match remaining with
                | [] ->
                    accBatches
                    |> List.rev
                    |> List.collect id
                    |> List.distinct
                    |> List.map (fun id ->
                        { Id = CatalogArtistId id
                          Name = nameMap |> Map.tryFind id |> Option.defaultValue id })
                    |> Ok
                | batch :: rest ->
                    let ids = batch |> List.map artistIdValue |> String.concat ","
                    let response = executeApple config runtime "GET" "/v1/me/ratings/artists" [ "ids", ids ] None

                    match toResult parseRatedArtistIds response with
                    | Ok ratedIds -> processBatches (ratedIds :: accBatches) rest
                    | Error err -> Error err

            processBatches [] batches

    /// Parse ratings response and extract artist names for items with value = -1 (dislike).
    let private parseDislikedArtistNames (body: string) : string list =
        parseDataArray body (fun item ->
            let value =
                item
                |> tryGetProperty "attributes"
                |> Option.bind (fun attrs ->
                    match tryGetProperty "value" attrs with
                    | Some v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
                    | _ -> None)
                |> Option.defaultValue 0

            if value = -1 then
                item
                |> tryGetProperty "attributes"
                |> Option.bind (tryGetString "artistName")
            else
                None)

    let fetchSongRatings (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) =
        executeApple config runtime "GET" "/v1/me/ratings/songs" [] None
        |> toResult id

    let fetchAlbumRatings (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) =
        executeApple config runtime "GET" "/v1/me/ratings/albums" [] None
        |> toResult id

    /// Collect excluded artist names (normalized) from song and album ratings.
    let collectExcludedArtists (songRatingsBody: string) (albumRatingsBody: string) : Set<string> =
        let songDislikes = parseDislikedArtistNames songRatingsBody
        let albumDislikes = parseDislikedArtistNames albumRatingsBody
        (songDislikes @ albumDislikes)
        |> List.map Normalization.normalizeText
        |> Set.ofList

    /// Filter out releases whose artist name (normalized) is in the excluded set.
    let filterByExcludedArtists (excludedNormalized: Set<string>) (releases: AC.DiscoveredRelease list) : AC.DiscoveredRelease list =
        releases
        |> List.filter (fun release ->
            not (excludedNormalized.Contains(Normalization.normalizeText release.ArtistName)))

    let resolveLabelId (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (labelName: string) =
        executeApple
            config
            runtime
            "GET"
            "/v1/catalog/us/search"
            [ "term", labelName; "types", "record-labels"; "limit", "1" ]
            None
        |> toResult parseSearchLabelId

    let private parseLabelViewReleases (body: string) : AC.DiscoveredRelease list =
        use document = JsonDocument.Parse(body)

        document.RootElement
        |> tryGetProperty "views"
        |> Option.bind (tryGetProperty "latest-releases")
        |> Option.bind (tryGetProperty "data")
        |> Option.map (fun data ->
            if data.ValueKind = JsonValueKind.Array then
                data.EnumerateArray() |> Seq.toList |> List.choose parseRelease
            else
                [])
        |> Option.defaultValue []

    let private fetchLabelReleases (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (labelId: string) =
        let path = $"/v1/catalog/us/record-labels/{labelId}"
        executeApple config runtime "GET" path [ "views", "latest-releases" ] None
        |> toResult parseLabelViewReleases

    // Finding 3 / 9: use cons-and-reverse (prepend then List.rev at the end) to
    // eliminate the O(n²) @ appends from the original folder.
    let resolveLabels (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) : AC.DiscoveredArtist list * AC.DiscoveredRelease list * AC.LogEntry list =
        let folder
            (artistsAcc: AC.DiscoveredArtist list, releasesAcc: AC.DiscoveredRelease list, logsAcc: AC.LogEntry list)
            labelName
            =
            match resolveLabelId config runtime labelName with
            | Error response ->
                let logEntry =
                    mkLog
                        AC.Error
                        "ApiFailure"
                        "Failed to resolve label ID."
                        (Map
                            [ "endpoint", "/v1/catalog/us/search"
                              "status", string response.StatusCode
                              // Finding 15: truncate response body before logging.
                              "message", truncateBody response.Body ])

                artistsAcc, releasesAcc, logEntry :: logsAcc

            | Ok None ->
                let warning =
                    mkLog
                        AC.Warning
                        "UnknownLabel"
                        $"Label '{labelName}' could not be resolved."
                        (Map [ "label", labelName ])

                artistsAcc, releasesAcc, warning :: logsAcc

            | Ok(Some labelId) ->
                match fetchLabelReleases config runtime labelId with
                | Error response ->
                    let logEntry =
                        mkLog
                            AC.Error
                            "ApiFailure"
                            "Failed to fetch latest releases for label."
                            (Map
                                [ "endpoint", $"/v1/catalog/us/record-labels/{labelId}?views=latest-releases"
                                  "status", string response.StatusCode
                                  // Finding 15: truncate response body before logging.
                                  "message", truncateBody response.Body ])

                    artistsAcc, releasesAcc, logEntry :: logsAcc

                | Ok releases ->
                    let labelArtists: AC.DiscoveredArtist list =
                        releases |> List.map (fun release -> { Id = release.ArtistId; Name = release.ArtistName })

                    // Prepend rather than append — O(1) per iteration.
                    labelArtists @ artistsAcc, releases @ releasesAcc, logsAcc

        let artists, releases, logsRev = config.LabelNames |> List.fold folder ([], [], [])
        // Reverse the log list once to restore original order.
        let logs = List.rev logsRev
        artists |> List.distinctBy (fun artist -> artist.Id), releases, logs

    let fetchArtistReleases (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (artistId: CatalogArtistId) =
        let path = $"/v1/catalog/us/artists/{artistIdValue artistId}/albums"
        executeApple config runtime "GET" path [ "sort", "-releaseDate"; "limit", "25" ] None
        |> toResult parseReleaseList

    let filterByLookback (today: DateOnly) (lookbackDays: int) (releases: AC.DiscoveredRelease list) =
        releases |> List.filter (fun release -> Normalization.isWithinLookback today lookbackDays release.ReleaseDate)

    let dedupReleases (releases: AC.DiscoveredRelease list) = releases |> List.distinctBy (fun release -> release.Id)

    let private fetchAlbumDetails (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (albumId: CatalogAlbumId) =
        let path = $"/v1/catalog/us/albums/{albumIdValue albumId}"
        executeApple config runtime "GET" path [] None
        |> toResult (parseReleaseList >> List.tryHead)

    // Finding 9: use cons-and-reverse to eliminate the O(n²) @ appends from
    // classifyByGenres.
    let classifyByGenres (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (releases: AC.DiscoveredRelease list) =
        let folder (acc: AC.DiscoveredRelease list, logs: AC.LogEntry list) (release: AC.DiscoveredRelease) =
            let hydratedRelease, hydrationLogs =
                if List.isEmpty release.GenreNames || List.isEmpty release.TrackIds then
                    match fetchAlbumDetails config runtime release.Id with
                    | Ok(Some details) ->
                        let genreNames = if List.isEmpty release.GenreNames then details.GenreNames else release.GenreNames
                        let trackIds = if List.isEmpty release.TrackIds then details.TrackIds else release.TrackIds
                        { release with GenreNames = genreNames; TrackIds = trackIds }, []
                    | Ok None -> release, []
                    | Error response ->
                        let entry =
                            mkLog
                                AC.Error
                                "ApiFailure"
                                "Failed to fetch album details."
                                (Map
                                    [ "endpoint", $"/v1/catalog/us/albums/{albumIdValue release.Id}"
                                      "status", string response.StatusCode
                                      // Finding 15: truncate response body before logging.
                                      "message", truncateBody response.Body ])

                        release, [ entry ]
                else
                    release, []

            if List.isEmpty hydratedRelease.GenreNames then
                let warning =
                    mkLog
                        AC.Warning
                        "MissingGenres"
                        $"Release '{hydratedRelease.Name}' has no genre metadata."
                        (Map [ "releaseId", albumIdValue hydratedRelease.Id; "release", hydratedRelease.Name ])

                // Prepend to both accumulators — O(1) per iteration.
                acc, warning :: (List.rev hydrationLogs @ logs)
            else
                hydratedRelease :: acc, (List.rev hydrationLogs) @ logs

        let releasesRev, logsRev = releases |> List.fold folder ([], [])
        // Reverse once at the end to restore original order.
        List.rev releasesRev, List.rev logsRev

    let private buildSeedArtists (libraryArtists: Artist list) (favoritedArtists: Artist list) : AC.DiscoveredArtist list =
        (libraryArtists @ favoritedArtists)
        |> Normalization.dedupByArtistId
        |> List.map (fun artist -> { Id = artist.Id; Name = artist.Name })

    /// Parse an Apple Music artist search response into a list of (id, name) pairs.
    let private parseSearchArtists (body: string) : (string * string) list =
        use document = JsonDocument.Parse(body)

        document.RootElement
        |> tryGetProperty "results"
        |> Option.bind (tryGetProperty "artists")
        |> Option.bind (tryGetProperty "data")
        |> Option.map (fun data ->
            if data.ValueKind = JsonValueKind.Array then
                data.EnumerateArray()
                |> Seq.choose (fun item ->
                    match tryGetString "id" item with
                    | None -> None
                    | Some id ->
                        let name =
                            item
                            |> tryGetProperty "attributes"
                            |> Option.bind (tryGetString "name")
                            |> Option.defaultValue id
                        Some(id, name))
                |> Seq.toList
            else
                [])
        |> Option.defaultValue []

    /// Resolve a similar artist name to an Apple Music catalog artist by
    /// searching the catalog and comparing normalized names.
    let private resolveArtistByName
        (config: Config.ValidSyncConfig)
        (runtime: AC.ApiRuntime)
        (artistName: string)
        : AC.DiscoveredArtist option =
        let response =
            executeApple config runtime "GET" $"/v1/catalog/{config.Storefront}/search" [ "term", artistName; "types", "artists"; "limit", "5" ] None

        match toResult parseSearchArtists response with
        | Error _ -> None
        | Ok candidates ->
            let normalizedQuery = Normalization.normalizeText artistName

            candidates
            |> List.tryFind (fun (_, name) -> Normalization.normalizeText name = normalizedQuery)
            |> Option.map (fun (id, name) -> { Id = withArtistId id; Name = name }: AC.DiscoveredArtist)

    /// Public wrapper for resolveArtistByName, exposed for unit testing.
    let resolveArtistByNamePublic (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (artistName: string) =
        resolveArtistByName config runtime artistName

    /// Discover similar artists for all seed artists via the provider, resolve
    /// them to Apple Music catalog IDs, and return a deduplicated list.
    /// Provider calls are fanned out concurrently via Async.Parallel; an auth
    /// failure from any call short-circuits further processing.
    /// Errors from the provider are logged and treated as non-fatal.
    let private discoverSimilarArtists
        (provider: AC.SimilarArtistProvider)
        (config: Config.ValidSyncConfig)
        (runtime: AC.ApiRuntime)
        (seedArtists: AC.DiscoveredArtist list)
        (appendLog: AC.LogLevel -> string -> string -> Map<string, string> -> unit)
        : AC.DiscoveredArtist list =
        let seedNormalized =
            seedArtists
            |> List.map (fun a -> Normalization.normalizeText a.Name)
            |> Set.ofList

        // Fan out all provider calls concurrently and collect results.
        let providerResults =
            seedArtists
            |> List.map (fun seed -> async {
                let! result = provider.GetSimilar(seed.Name, None)
                return seed, result
            })
            |> Async.Parallel
            |> Async.RunSynchronously

        // Process results with a fold, short-circuiting on auth failure.
        let rawSimilar =
            providerResults
            |> Array.fold
                (fun (authFailed, acc) (seed, result) ->
                    if authFailed then (true, acc)
                    else
                        match result with
                        | Ok artists ->
                            let filtered =
                                artists
                                |> List.filter (fun a -> not (seedNormalized.Contains(Normalization.normalizeText a.Name)))
                            (false, acc @ filtered)
                        | Error(AC.AuthFailure message) ->
                            appendLog
                                AC.Error
                                "LastFmAuthFailure"
                                "Last.fm authentication failed."
                                (Map [ "message", message ])
                            (true, acc)
                        | Error(AC.Unavailable(statusCode, message)) ->
                            appendLog
                                AC.Error
                                "SimilarArtistServiceUnavailable"
                                "Similar artist data source is unavailable."
                                (Map [ "status", string statusCode; "message", message ])
                            (false, acc)
                        | Error(AC.MalformedResponse message) ->
                            appendLog
                                AC.Warning
                                "SimilarArtistMalformedResponse"
                                "Malformed response from similar artist provider."
                                (Map [ "message", message ])
                            (false, acc))
                (false, [])
            |> snd

        // Deduplicate by normalized name before resolving.
        let uniqueSimilar =
            rawSimilar
            |> List.fold
                (fun (seen: Set<string>, acc: Types.SimilarArtist list) artist ->
                    let key = Normalization.normalizeText artist.Name
                    if seen.Contains key then (seen, acc)
                    else (seen.Add key, artist :: acc))
                (Set.empty, [])
            |> snd
            |> List.rev

        // Resolve each similar artist to an Apple Music catalog artist.
        let resolved =
            uniqueSimilar
            |> List.choose (fun similar -> resolveArtistByName config runtime similar.Name)

        // Deduplicate resolved artists by catalog ID.
        resolved |> List.distinctBy (fun artist -> artist.Id)

    let private outcomeToText = function
        | Success -> "success"
        | PartialFailure -> "partial_failure"
        | Aborted _ -> "aborted"

    // Finding 16: redact sensitive header values before storing requests in the
    // observable log so tokens are not persisted verbatim.
    let private isSensitiveHeader (name: string) =
        String.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
        || String.Equals(name, "Music-User-Token", StringComparison.OrdinalIgnoreCase)

    let private redactHeaders (headers: (string * string) list) =
        headers
        |> List.map (fun (name, value) ->
            if isSensitiveHeader name then name, "[redacted]"
            else name, value)

    // Finding 15: redact sensitive query string parameters (e.g. Last.fm api_key)
    // so API keys are not persisted in the observable log.
    let private isSensitiveQueryParam (name: string) =
        String.Equals(name, "api_key", StringComparison.OrdinalIgnoreCase)

    let private redactQueryParams (query: (string * string) list) =
        query
        |> List.map (fun (name, value) ->
            if isSensitiveQueryParam name then name, "[redacted]"
            else name, value)

    let private runSyncInternal (providerOpt: AC.SimilarArtistProvider option) (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (knownPlaylistIds: Map<string, string>) : Types.SyncOutcome * AC.ObservedSync =
        let recordedRequests = ResizeArray<AC.ApiRequest>()
        let recordedLogs = ResizeArray<AC.LogEntry>()

        let runtimeWithRecording: AC.ApiRuntime =
            { runtime with
                Execute =
                    (fun request ->
                        async {
                            // Finding 16: store a copy with redacted auth header values
                            // so tokens are not retained in the observable log.
                            let safeRequest = { request with Headers = redactHeaders request.Headers }
                            recordedRequests.Add safeRequest
                            return! runtime.Execute request
                        }) }

        // Create the provider using the recording runtime so Last.fm requests are captured.
        let provider =
            match providerOpt with
            | Some p -> p
            | None -> SimilarArtists.createLastFmProvider config runtimeWithRecording

        let appendLog level code message data = recordedLogs.Add(mkLog level code message data)
        let appendLogs logs = logs |> List.iter recordedLogs.Add
        let mutable resolvedPlaylistIds = Map.empty

        let snapshot outcome =
            appendLog AC.Info "SyncOutcome" "Sync finished." (Map [ "outcome", outcomeToText outcome ])

            let observed: AC.ObservedSync =
                { Requests = recordedRequests |> Seq.toList
                  Logs = recordedLogs |> Seq.toList
                  ResolvedPlaylistIds = resolvedPlaylistIds }

            outcome, observed

        let abort reason = snapshot (Aborted reason)

        let maybeAuthAbort path (response: AC.ApiResponse) =
            if response.StatusCode = 401 then
                appendLog
                    AC.Error
                    "AppleMusicAuthFailure"
                    "Apple Music authentication failed."
                    (Map
                        [ "endpoint", path
                          "status", string response.StatusCode
                          // Finding 15: truncate body before logging.
                          "message", truncateBody response.Body ])

                Some "AuthFailure"
            elif path.StartsWith("/v1/me", StringComparison.OrdinalIgnoreCase) && response.StatusCode = 403 then
                appendLog
                    AC.Error
                    "AppleMusicAuthFailure"
                    "Music User Token re-authorization required."
                    (Map
                        [ "endpoint", path
                          "status", string response.StatusCode
                          // Finding 15: truncate body before logging.
                          "message", truncateBody response.Body ])

                Some "AuthFailure"
            else
                None

        appendLog AC.Info "SyncStarted" "Sync started." Map.empty

        match fetchLibraryArtists config runtimeWithRecording with
        | Error response ->
            match maybeAuthAbort "/v1/me/library/artists" response with
            | Some reason -> abort reason
            | None ->
                appendLog
                    AC.Error
                    "ApiFailure"
                    "Failed to retrieve library artists."
                    (Map
                        [ "endpoint", "/v1/me/library/artists"
                          "status", string response.StatusCode
                          "message", truncateBody response.Body ])

                abort "LibraryArtistsFailed"
        | Ok libraryArtists ->
            match fetchFavoritedArtists config runtimeWithRecording libraryArtists with
            | Error response ->
                match maybeAuthAbort "/v1/me/ratings/artists" response with
                | Some reason -> abort reason
                | None ->
                    appendLog
                        AC.Error
                        "ApiFailure"
                        "Failed to retrieve favorited artists."
                        (Map
                            [ "endpoint", "/v1/me/ratings/artists"
                              "status", string response.StatusCode
                              "message", truncateBody response.Body ])

                    abort "FavoritedArtistsFailed"
            | Ok favoritedArtists ->
                let seedArtists = buildSeedArtists libraryArtists favoritedArtists
                let labelArtists, labelReleases, labelLogs = resolveLabels config runtimeWithRecording
                appendLogs labelLogs

                // -- Similar-artist discovery (Phase 2) --
                let similarArtists = discoverSimilarArtists provider config runtimeWithRecording seedArtists appendLog

                // Single-pass dedup avoids intermediate @ concatenations and extra HashSet allocation.
                let allArtists =
                    [seedArtists; similarArtists; labelArtists]
                    |> List.concat
                    |> List.fold (fun (seen, acc) artist ->
                        if Set.contains artist.Id seen then (seen, acc)
                        else (Set.add artist.Id seen, artist :: acc))
                        (Set.empty, [])
                    |> snd
                    |> List.rev

                let artistReleaseResults =
                    allArtists
                    |> List.map (fun artist -> artist, fetchArtistReleases config runtimeWithRecording artist.Id)

                let fatalCatalogError =
                    artistReleaseResults
                    |> List.tryPick (fun (artist, result) ->
                        match result with
                        | Ok _ -> None
                        | Error response ->
                            appendLog
                                AC.Error
                                "ApiFailure"
                                "Failed to query Apple Music catalog releases."
                                (Map
                                    [ "artist", artist.Name
                                      "endpoint", $"/v1/catalog/us/artists/{artistIdValue artist.Id}/albums"
                                      "status", string response.StatusCode
                                      "message", truncateBody response.Body ])

                            if response.StatusCode >= 500 then Some response else None)

                match fatalCatalogError with
                | Some _ -> abort "CatalogUnavailable"
                | None ->
                    let artistReleases =
                        artistReleaseResults
                        |> List.choose (fun (_, result) -> match result with | Ok releases -> Some releases | Error _ -> None)
                        |> List.collect id

                    let today = runtimeWithRecording.UtcNow() |> fun now -> DateOnly.FromDateTime(now.UtcDateTime)
                    let lookbackDays = config.LookbackDays |> Config.PositiveInt.value

                    let candidateReleases =
                        labelReleases @ artistReleases
                        |> filterByLookback today lookbackDays
                        |> dedupReleases

                    let classifiedReleases, classifyLogs = classifyByGenres config runtimeWithRecording candidateReleases
                    appendLogs classifyLogs

                    // -- Artist dislike filtering (Phase 2) --
                    // Fetch song and album ratings concurrently since they are independent.
                    let excludedArtists =
                        let songResultAsync = async { return fetchSongRatings config runtimeWithRecording }
                        let albumResultAsync = async { return fetchAlbumRatings config runtimeWithRecording }
                        let results = Async.Parallel [| songResultAsync; albumResultAsync |] |> Async.RunSynchronously
                        let songResult, albumResult = results.[0], results.[1]
                        match songResult, albumResult with
                        | Ok songBody, Ok albumBody ->
                            let excluded = collectExcludedArtists songBody albumBody
                            excluded |> Set.iter (fun artist ->
                                appendLog
                                    AC.Info
                                    "ExcludedDislikedArtist"
                                    $"Excluded disliked artist from playlist population."
                                    (Map [ "artist", artist ]))
                            excluded
                        | Error _, _ | _, Error _ ->
                            // Non-fatal: if ratings retrieval fails, skip filtering.
                            Set.empty

                    let filteredReleases = filterByExcludedArtists excludedArtists classifiedReleases

                    let discovery: AC.DiscoveryResult =
                        { SeedArtists = seedArtists
                          SimilarArtists = similarArtists
                          LabelArtists = labelArtists
                          Releases = filteredReleases }

                    // reconcilePlaylists now returns Async; run it synchronously
                    // at this single top-level call site (Finding 1 / 8).
                    let reconcileResult, reconcileLogs =
                        PlaylistReconcile.reconcilePlaylists config discovery runtimeWithRecording knownPlaylistIds
                        |> Async.RunSynchronously

                    appendLogs reconcileLogs
                    resolvedPlaylistIds <- reconcileResult.ResolvedPlaylistIds

                    let outcome = if reconcileResult.HadPlaylistFailures then PartialFailure else Success

                    appendLog
                        AC.Info
                        "SyncCompleted"
                        "Sync completed."
                        (Map
                            [ "tracks_added", string reconcileResult.AddedCount
                              "tracks_removed", string reconcileResult.RemovedCount ])

                    snapshot outcome

    let runSyncWithProvider (provider: AC.SimilarArtistProvider) (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (knownPlaylistIds: Map<string, string>) : Types.SyncOutcome * AC.ObservedSync =
        runSyncInternal (Some provider) config runtime knownPlaylistIds

    let runSync (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) (knownPlaylistIds: Map<string, string>) : Types.SyncOutcome * AC.ObservedSync =
        runSyncInternal None config runtime knownPlaylistIds
