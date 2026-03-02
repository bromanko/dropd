# Last.fm API Research

**Date:** March 2, 2026

## Overview

Last.fm provides a free REST API for accessing music metadata, including artist similarity data. The API has been operational for ~20 years and remains actively used. It is the primary candidate for powering the "similar artist discovery" feature in Drop D.

## Relevant Endpoints

### Similar Artists

| Endpoint | Description |
|---|---|
| `artist.getSimilar` | Returns a list of artists similar to a given artist, with a match score (0.0–1.0) |
| `artist.getInfo` | Artist metadata including biography, tags, and similar artists |
| `artist.getTopTags` | Genre/style tags for an artist (useful for supplemental genre classification) |

The `artist.getSimilar` endpoint accepts either an artist name or a MusicBrainz ID (MBID). Using MBID is more reliable for avoiding name-matching issues.

### Additional Useful Endpoints

| Endpoint | Description |
|---|---|
| `artist.search` | Search for an artist by name |
| `artist.getCorrection` | Check if a supplied artist name has a correction to a canonical name |
| `tag.getTopArtists` | Get top artists for a given tag/genre |
| `track.getSimilar` | Similar tracks based on listening data |

## Pricing

**Free** for non-commercial use. Requires a free Last.fm account and API key.

Commercial use requires a separate agreement — contact `partners@last.fm`.

## Rate Limits

- **5 requests per second** per originating IP address, averaged over a 5-minute window
- No published daily request cap, but accounts may be suspended for "excessive" usage
- **100 MB cap** on total Last.fm data stored at any given time

### Impact on Drop D

Rate limits are a non-issue for Drop D's use case. A daily run processing ~500 artists at 5 requests/second would complete in under 2 minutes. Even with additional calls for tags or corrections, total daily usage would be well within acceptable bounds.

## Terms of Service — Key Points

- **Non-commercial use only** without a separate commercial agreement. Drop D as a personal service qualifies as non-commercial.
- **Attribution required:** Must display a "powered by AudioScrobbler/Last.fm" badge with a link back to Last.fm. For a headless personal service, this is largely a formality but should be included in any UI or logs.
- **No sub-licensing** of Last.fm data to third parties.
- **Data must be cached** in accordance with HTTP headers sent with API responses.
- **Last.fm may terminate access** at any time at their sole discretion (standard boilerplate).
- **Governed by English law.**

## Risks

- **Service longevity:** Last.fm is an aging service. While it's been running for ~20 years, there is a non-zero risk of API deprecation or shutdown. Mitigation: abstract the similar-artist data source behind an interface so it can be swapped for an alternative (e.g., MusicBrainz, Spotify API) if needed.
- **Data quality:** `artist.getSimilar` may return sparse results for niche or very new artists. Using MBID instead of artist name improves reliability.
- **Name matching:** Artist names may not match exactly between Last.fm and Apple Music. The `artist.getCorrection` endpoint can help, but cross-service reconciliation may require fuzzy matching or MBID/ISRC lookups.

## API Details

- **Root URL:** `http://ws.audioscrobbler.com/2.0/`
- **Response format:** XML by default; JSON available via `format=json` parameter
- **Authentication:** API key passed as a query parameter for read-only methods
