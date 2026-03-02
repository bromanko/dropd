# Apple Music API Research

**Date:** March 2, 2026

## Overview

The Apple Music API (part of MusicKit) provides REST endpoints for accessing catalog data, user library data, and managing playlists. It is free to use with an Apple Developer account. Authentication requires a developer token (JWT signed with a private key from Apple's developer portal) and a user token for personal library access.

## Relevant Endpoints

### User Library

| Endpoint | Description |
|---|---|
| `GET /v1/me/library/artists` | Fetch all library artists in alphabetical order |
| `GET /v1/me/library/songs` | Fetch all library songs in alphabetical order |
| `GET /v1/me/library/albums` | Fetch all library albums in alphabetical order |

MusicKit also supports filtering **favorited** artists in a user's library and checking if an artist resource is already favorited. This can be used to identify "starred" or favorited artists as seed artists for Drop D.

### Catalog — Artists & New Releases

| Endpoint | Description |
|---|---|
| `GET /v1/catalog/{storefront}/artists/{id}` | Fetch a catalog artist by ID |
| `GET /v1/catalog/{storefront}/artists/{id}/albums` | Fetch an artist's albums |
| `GET /v1/catalog/{storefront}/artists/{id}/albums?sort=-releaseDate` | Fetch albums sorted by newest first (undocumented but confirmed working) |

The `sort=-releaseDate` parameter on the artist albums endpoint is key — it allows fetching the most recent releases without paginating through the entire discography.

### Catalog — Record Labels

| Endpoint | Description |
|---|---|
| `GET /v1/catalog/{storefront}/record-labels/{id}` | Fetch a record label |
| `GET /v1/catalog/{storefront}/record-labels/{id}?views=top-releases,latest-releases` | Fetch a label's latest and top releases |
| Search for labels via catalog search with `types=record-labels` | Find label IDs by name |

The `views=latest-releases` parameter on record labels is well-documented and returns recent releases from that label. This directly supports the label-based discovery feature.

### Catalog — Genre Metadata

Albums and songs include a `genreNames` attribute — an array of genre strings (e.g., `["Electronic", "Music"]`). This is returned by default on album/song resources without needing a special relationship fetch.

| Endpoint | Description |
|---|---|
| `GET /v1/catalog/{storefront}/genres` | List all available genre categories |
| `genreNames` attribute on Albums/Songs | Array of genre strings included by default |

### Recommendations

| Endpoint | Description |
|---|---|
| `GET /v1/me/recommendations` | Personalized recommendations based on library and purchase history |
| `GET /v1/me/recommendations/{id}` | Fetch a specific recommendation by ID |

Returns `PersonalRecommendation` objects containing recommended playlists, albums, and stations. These are content-level recommendations (not artist-level), but could supplement the similar-artist discovery from Last.fm.

### Playlist Management

| Endpoint | Description |
|---|---|
| `POST /v1/me/library/playlists` | Create a new library playlist |
| `POST /v1/me/library/playlists/{id}/tracks` | Add tracks to an existing library playlist |

Full CRUD support for playlists in the user's library. Tracks can be added by referencing catalog song IDs.

## Limitations & Notes

- **No similar artists endpoint.** The Apple Music API does not expose a dedicated "similar artists" relationship. Discovery of adjacent artists requires a third-party source.
- **No play count via REST API.** The web/REST API doesn't expose play counts or "top played" data. The Swift MusicKit framework can access play counts, but the REST API cannot.
- **The `sort=-releaseDate` parameter is undocumented** but has been confirmed working by multiple developers since at least 2022.
- **Rate limits are not publicly documented** for the Apple Music API, but standard Apple API practices apply.
- **Storefront** parameter (e.g., `us`) is required for catalog endpoints and determines regional availability.
