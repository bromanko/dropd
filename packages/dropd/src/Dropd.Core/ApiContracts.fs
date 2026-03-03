namespace Dropd.Core

open System

module ApiContracts =

    type ApiService =
        | AppleMusic
        | LastFm

    type ApiRequest =
        { Service: ApiService
          Method: string
          Path: string
          Query: (string * string) list
          Headers: (string * string) list
          Body: string option }

    type ApiResponse =
        { StatusCode: int
          Body: string
          Headers: (string * string) list }

    type LogLevel =
        | Debug
        | Info
        | Warning
        | Error

    type LogEntry =
        { Level: LogLevel
          Code: string
          Message: string
          Data: Map<string, string> }

    type ObservedSync =
        { Requests: ApiRequest list
          Logs: LogEntry list
          /// Playlist name → Apple Music ID mappings resolved during this sync.
          /// Callers can persist this to work around listing endpoint inconsistency.
          ResolvedPlaylistIds: Map<string, string> }

    type ApiRuntime =
        { Execute: ApiRequest -> Async<ApiResponse>
          UtcNow: unit -> DateTimeOffset }

    type DiscoveredArtist =
        { Id: Types.CatalogArtistId
          Name: string }

    type DiscoveredRelease =
        { Id: Types.CatalogAlbumId
          ArtistId: Types.CatalogArtistId
          ArtistName: string
          Name: string
          ReleaseDate: DateOnly option
          GenreNames: string list
          TrackIds: Types.CatalogTrackId list }

    type DiscoveryResult =
        { SeedArtists: DiscoveredArtist list
          LabelArtists: DiscoveredArtist list
          Releases: DiscoveredRelease list }

    type PlaylistPlan =
        { PlaylistName: string
          AddTracks: Types.CatalogTrackId list
          RemoveTracks: Types.CatalogTrackId list }

    type ReconcileResult =
        { Plans: PlaylistPlan list
          AddedCount: int
          RemovedCount: int
          HadPlaylistFailures: bool
          /// Final name → ID mapping for all resolved playlists (created or found).
          /// Callers can persist this to avoid relying on the listing endpoint.
          ResolvedPlaylistIds: Map<string, string> }
