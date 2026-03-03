namespace Dropd.Core

open System
open Dropd.Core.Types

module Config =

    /// A playlist definition containing a name and genre matching criteria.
    type PlaylistDefinition =
        { Name: string
          GenreCriteria: string list }

    /// Full sync configuration for dropd.
    type SyncConfig =
        { /// User-configured playlist definitions (DD-036).
          Playlists: PlaylistDefinition list

          /// Rolling window duration for track retention (DD-037).
          RollingWindowDays: int

          /// Maximum percentage of similar-artist tracks per playlist (DD-038).
          SimilarArtistMaxPercent: int

          /// New-release lookback duration in days (DD-039).
          LookbackDays: int

          /// API request timeout in seconds (DD-040).
          RequestTimeoutSeconds: int

          /// Maximum API retry count per request (DD-041).
          MaxRetries: int

          /// Items per page for paginated API requests (DD-042).
          PageSize: int

          /// Maximum pages per endpoint call-chain per sync (DD-042).
          MaxPages: int

          /// Maximum sync runtime in minutes (DD-043).
          MaxSyncRuntimeMinutes: int

          /// API error-rate abort threshold as a percentage (DD-044).
          ErrorRateAbortPercent: int

          /// User-configured list of record label names (DD-004).
          LabelNames: string list

          /// Time of day for daily sync in UTC (DD-056).
          SyncTimeUtc: TimeOnly

          /// Apple Music developer token.
          AppleMusicDeveloperToken: AppleMusicDeveloperToken

          /// Apple Music user token.
          AppleMusicUserToken: AppleMusicUserToken

          /// Last.fm API key.
          LastFmApiKey: LastFmApiKey }

    /// Percentage value constrained to the inclusive range 0..100.
    type Percent = private Percent of int

    module Percent =
        let value (Percent percent) = percent

    /// Positive integer constrained to values greater than 0.
    type PositiveInt = private PositiveInt of int

    module PositiveInt =
        let value (PositiveInt value) = value

    /// Non-negative integer constrained to values greater than or equal to 0.
    type NonNegativeInt = private NonNegativeInt of int

    module NonNegativeInt =
        let value (NonNegativeInt value) = value

    type ValidSyncConfig =
        { Playlists: PlaylistDefinition list
          RollingWindowDays: PositiveInt
          SimilarArtistMaxPercent: Percent
          LookbackDays: PositiveInt
          RequestTimeoutSeconds: PositiveInt
          MaxRetries: NonNegativeInt
          PageSize: PositiveInt
          MaxPages: PositiveInt
          MaxSyncRuntimeMinutes: PositiveInt
          ErrorRateAbortPercent: Percent
          LabelNames: string list
          SyncTimeUtc: TimeOnly
          AppleMusicDeveloperToken: AppleMusicDeveloperToken
          AppleMusicUserToken: AppleMusicUserToken
          LastFmApiKey: LastFmApiKey }

    type ConfigError =
        | MissingCredential of fieldName: string
        | NonPositiveValue of fieldName: string * actual: int
        | NegativeValue of fieldName: string * actual: int
        | PercentOutOfRange of fieldName: string * actual: int
        | EmptyPlaylistName of playlistIndex: int
        | EmptyGenreCriterion of playlistIndex: int * criterionIndex: int
        | EmptyLabelName of index: int

    /// Default configuration values per EARS requirements.
    let defaults : SyncConfig =
        { Playlists = []
          RollingWindowDays = 30
          SimilarArtistMaxPercent = 20
          LookbackDays = 30
          RequestTimeoutSeconds = 10
          MaxRetries = 3
          PageSize = 100
          MaxPages = 20
          MaxSyncRuntimeMinutes = 15
          ErrorRateAbortPercent = 30
          LabelNames = []
          SyncTimeUtc = TimeOnly(4, 0)
          AppleMusicDeveloperToken = AppleMusicDeveloperToken ""
          AppleMusicUserToken = AppleMusicUserToken ""
          LastFmApiKey = LastFmApiKey "" }

    let private nonPositive fieldName value =
        if value > 0 then
            None
        else
            Some(NonPositiveValue(fieldName, value))

    let private negative fieldName value =
        if value >= 0 then
            None
        else
            Some(NegativeValue(fieldName, value))

    let private percent fieldName value =
        if value >= 0 && value <= 100 then
            None
        else
            Some(PercentOutOfRange(fieldName, value))

    let private requiredCredential fieldName value =
        if String.IsNullOrWhiteSpace value then
            Some(MissingCredential fieldName)
        else
            None

    let private validatePlaylists (playlists: PlaylistDefinition list) =
        playlists
        |> List.mapi (fun playlistIndex playlist ->
            let nameErrors =
                if String.IsNullOrWhiteSpace playlist.Name then
                    [ EmptyPlaylistName playlistIndex ]
                else
                    []

            let genreErrors =
                playlist.GenreCriteria
                |> List.mapi (fun criterionIndex criterion ->
                    if String.IsNullOrWhiteSpace criterion then
                        Some(EmptyGenreCriterion(playlistIndex, criterionIndex))
                    else
                        None)
                |> List.choose id

            nameErrors @ genreErrors)
        |> List.collect id

    let private validateLabelNames (labelNames: string list) =
        labelNames
        |> List.mapi (fun index labelName ->
            if String.IsNullOrWhiteSpace labelName then
                Some(EmptyLabelName index)
            else
                None)
        |> List.choose id

    /// Validates a SyncConfig and returns constrained values if valid.
    let validate (config: SyncConfig) : Result<ValidSyncConfig, ConfigError list> =
        let (AppleMusicDeveloperToken developerToken) = config.AppleMusicDeveloperToken
        let (AppleMusicUserToken userToken) = config.AppleMusicUserToken
        let (LastFmApiKey lastFmApiKey) = config.LastFmApiKey

        let scalarErrors =
            [ nonPositive "RollingWindowDays" config.RollingWindowDays
              percent "SimilarArtistMaxPercent" config.SimilarArtistMaxPercent
              nonPositive "LookbackDays" config.LookbackDays
              nonPositive "RequestTimeoutSeconds" config.RequestTimeoutSeconds
              negative "MaxRetries" config.MaxRetries
              nonPositive "PageSize" config.PageSize
              nonPositive "MaxPages" config.MaxPages
              nonPositive "MaxSyncRuntimeMinutes" config.MaxSyncRuntimeMinutes
              percent "ErrorRateAbortPercent" config.ErrorRateAbortPercent
              requiredCredential "AppleMusicDeveloperToken" developerToken
              requiredCredential "AppleMusicUserToken" userToken
              requiredCredential "LastFmApiKey" lastFmApiKey ]
            |> List.choose id

        let errors =
            scalarErrors @ validatePlaylists config.Playlists @ validateLabelNames config.LabelNames

        if errors |> List.isEmpty then
            Ok
                { Playlists = config.Playlists
                  RollingWindowDays = PositiveInt config.RollingWindowDays
                  SimilarArtistMaxPercent = Percent config.SimilarArtistMaxPercent
                  LookbackDays = PositiveInt config.LookbackDays
                  RequestTimeoutSeconds = PositiveInt config.RequestTimeoutSeconds
                  MaxRetries = NonNegativeInt config.MaxRetries
                  PageSize = PositiveInt config.PageSize
                  MaxPages = PositiveInt config.MaxPages
                  MaxSyncRuntimeMinutes = PositiveInt config.MaxSyncRuntimeMinutes
                  ErrorRateAbortPercent = Percent config.ErrorRateAbortPercent
                  LabelNames = config.LabelNames
                  SyncTimeUtc = config.SyncTimeUtc
                  AppleMusicDeveloperToken = config.AppleMusicDeveloperToken
                  AppleMusicUserToken = config.AppleMusicUserToken
                  LastFmApiKey = config.LastFmApiKey }
        else
            Error errors
