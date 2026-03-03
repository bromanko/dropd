namespace Dropd.Core

open System

module Types =

    /// Apple Music catalog artist identifier.
    type CatalogArtistId = CatalogArtistId of string

    /// Apple Music catalog album identifier.
    type CatalogAlbumId = CatalogAlbumId of string

    /// Apple Music catalog track identifier.
    type CatalogTrackId = CatalogTrackId of string

    /// Apple Music catalog record label identifier.
    type CatalogRecordLabelId = CatalogRecordLabelId of string

    /// Apple Music library playlist identifier.
    type LibraryPlaylistId = LibraryPlaylistId of string

    /// Apple Music developer token value.
    type AppleMusicDeveloperToken = AppleMusicDeveloperToken of string

    module AppleMusicDeveloperToken =
        let value (AppleMusicDeveloperToken token) = token
        let create token = AppleMusicDeveloperToken token

    /// Apple Music user token value.
    type AppleMusicUserToken = AppleMusicUserToken of string

    module AppleMusicUserToken =
        let value (AppleMusicUserToken token) = token
        let create token = AppleMusicUserToken token

    /// Last.fm API key value.
    type LastFmApiKey = LastFmApiKey of string

    module LastFmApiKey =
        let value (LastFmApiKey apiKey) = apiKey
        let create apiKey = LastFmApiKey apiKey

    /// A rating value from Apple Music (-1 = dislike, 1 = like).
    type RatingValue =
        | Dislike
        | Like

    /// Resource types that can be rated.
    type RatingResourceType =
        | Song
        | Album

    module RatingResourceType =
        let toApiValue = function
            | Song -> "songs"
            | Album -> "albums"

        let tryParse (value: string) =
            match value.Trim().ToLowerInvariant() with
            | "song"
            | "songs" -> Some Song
            | "album"
            | "albums" -> Some Album
            | _ -> None

    /// A user rating for a song or album.
    type Rating =
        { ResourceId: string
          ResourceType: RatingResourceType
          Value: RatingValue }

    /// An artist in the Apple Music catalog.
    type Artist =
        { Id: CatalogArtistId
          Name: string }

    /// A similar artist result from a discovery source.
    type SimilarArtist =
        { Name: string
          Mbid: string option }

    /// A track in the Apple Music catalog.
    type Track =
        { Id: CatalogTrackId
          Name: string
          ArtistName: string
          AlbumId: CatalogAlbumId option
          GenreNames: string list
          ReleaseDate: DateOnly option }

    /// An album/release in the Apple Music catalog.
    type Album =
        { Id: CatalogAlbumId
          Name: string
          ArtistName: string
          GenreNames: string list
          ReleaseDate: DateOnly option
          TrackIds: CatalogTrackId list }

    /// Reason a scheduled sync was skipped.
    type SyncSkipReason =
        | AlreadyRunning
        | MissedWhileUnavailable

    /// Overall outcome status for a sync run.
    type SyncOutcome =
        | Success
        | PartialFailure
        | Aborted of reason: string
