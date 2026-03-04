namespace Dropd.Core

open System.Text.Json
open Dropd.Core.Types

module SimilarArtists =

    module AC = ApiContracts

    let private tryGetProperty = JsonHelpers.tryGetProperty
    let private tryGetString = JsonHelpers.tryGetString

    /// Build the Last.fm API request for artist.getSimilar.
    let buildRequest (config: Config.ValidSyncConfig) (artistName: string) : AC.ApiRequest =
        let apiKey = LastFmApiKey.value config.LastFmApiKey

        { Service = AC.LastFm
          Method = "GET"
          Path = "/2.0"
          Query =
            [ "method", "artist.getSimilar"
              "artist", artistName
              "api_key", apiKey
              "format", "json"
              "limit", "10" ]
          Headers = []
          Body = None }

    /// Parse a Last.fm JSON response body into either a list of similar artists
    /// or a provider error.
    let parseResponse (statusCode: int) (body: string) : Result<SimilarArtist list, AC.SimilarArtistProviderError> =
        if statusCode < 200 || statusCode >= 300 then
            Error(AC.Unavailable(statusCode, $"Last.fm returned HTTP {statusCode}"))
        else
            try
                use document = JsonDocument.Parse(body)
                let root = document.RootElement

                // Check for Last.fm error payload (arrives with HTTP 200).
                match tryGetProperty "error" root with
                | Some errorEl when errorEl.ValueKind = JsonValueKind.Number ->
                    let errorCode = errorEl.GetInt32()
                    let message =
                        tryGetString "message" root
                        |> Option.defaultValue "Unknown Last.fm error"

                    if errorCode = 10 then
                        Error(AC.AuthFailure message)
                    else
                        Error(AC.Unavailable(200, message))
                | _ ->
                    // Parse success payload.
                    let artists =
                        root
                        |> tryGetProperty "similarartists"
                        |> Option.bind (tryGetProperty "artist")
                        |> Option.map (fun arr ->
                            if arr.ValueKind = JsonValueKind.Array then
                                arr.EnumerateArray()
                                |> Seq.choose (fun item ->
                                    let name = tryGetString "name" item
                                    match name with
                                    | None -> None
                                    | Some n ->
                                        let mbid =
                                            tryGetString "mbid" item
                                            |> Option.bind (fun m -> if System.String.IsNullOrWhiteSpace m then None else Some m)
                                        Some { Name = n; Mbid = mbid })
                                |> Seq.toList
                            else
                                [])
                        |> Option.defaultValue []

                    Ok artists
            with ex ->
                Error(AC.MalformedResponse ex.Message)

    /// Create a Last.fm-backed similar artist provider.
    let createLastFmProvider (config: Config.ValidSyncConfig) (runtime: AC.ApiRuntime) : AC.SimilarArtistProvider =
        { Name = "Last.fm"
          GetSimilar =
            fun (artistName, _mbid) ->
                async {
                    let request = buildRequest config artistName
                    let! response = runtime.Execute request
                    return parseResponse response.StatusCode response.Body
                } }
