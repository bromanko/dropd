# EARS Requirements Specification: dropd

**Source:** Conversational requirements elicitation and Apple Music API documentation research
**Date:** March 2, 2026
**System:** dropd

## 1. Artist Seeding

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-001 | When dropd performs a sync, dropd shall retrieve the list of artists from the user's Apple Music library. | Test: Trigger a sync and verify that all library artists are retrieved. |
| DD-002 | When dropd performs a sync, dropd shall retrieve the list of favorited artists from the user's Apple Music library. | Test: Favorite an artist in Apple Music, trigger a sync, and verify the artist appears in the seed list. |
| DD-003 | When dropd identifies library artists and favorited artists, dropd shall merge them into a deduplicated seed artist list. | Test: Add an artist to both the library and favorites, trigger a sync, and verify the artist appears only once in the seed list. |

## 2. Label-Based Discovery

### Ubiquitous Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-004 | dropd shall store a user-configured list of record label names. | Inspection: Review configuration to confirm label list is present and editable. |

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-005 | When dropd performs a sync, dropd shall resolve each configured label name to an Apple Music catalog record label identifier. | Test: Configure a label (e.g., "Ninja Tune"), trigger a sync, and verify the label ID is resolved. |
| DD-006 | When dropd has resolved a record label identifier, dropd shall retrieve the latest releases for that label from the Apple Music catalog. | Test: Trigger a sync with a configured label and verify that recent releases are returned. |

### Unwanted Behavior Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-007 | If a configured label name cannot be resolved to an Apple Music catalog identifier, then dropd shall log a warning identifying the unresolved label. | Test: Configure a nonexistent label name, trigger a sync, and verify a warning is logged. |
| DD-008 | If a configured label name cannot be resolved to an Apple Music catalog identifier, then dropd shall continue processing remaining configured labels. | Test: Configure one nonexistent and one valid label, trigger a sync, and verify processing continues to the valid label. |

## 3. Similar Artist Discovery

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-009 | When dropd has a seed artist list, dropd shall query a similar-artist data source for artists similar to each seed artist. | Test: Trigger a sync with seed artists and verify that similar artist queries are made for each. |
| DD-010 | When the similar-artist data source returns similar artists, dropd shall filter out artists that are already in the seed artist list. | Test: Verify that a similar artist who is already in the user's library does not appear as a discovery candidate. |
| DD-011 | When dropd identifies similar artists, dropd shall resolve each similar artist to an Apple Music catalog artist identifier, preferring shared identifiers (for example, MBID) and falling back to normalized name matching. | Test: Verify that a similar artist returned by Last.fm is matched to an Apple Music catalog entry. |
| DD-012 | When dropd performs fallback name matching for artist resolution, dropd shall normalize names by lowercasing text, trimming leading and trailing whitespace, and collapsing repeated internal whitespace to a single space before comparison. | Test: Provide equivalent artist names with different casing and spacing and verify they resolve to the same artist. |
| DD-013 | When dropd resolves similar artists to Apple Music catalog artist identifiers, dropd shall deduplicate the resolved artist set by catalog artist identifier before release retrieval. | Test: Cause two seed artists to return the same similar artist and verify the resolved artist is queried only once for releases. |

### Unwanted Behavior Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-014 | If a similar artist cannot be resolved to an Apple Music catalog artist identifier, then dropd shall skip that artist. | Test: Introduce an artist name that has no Apple Music match (note: Last.fm returns HTTP 200 with `{"error":6,...}` for nonexistent artists), trigger a sync, and verify it is skipped. |
| DD-015 | If a similar artist cannot be resolved to an Apple Music catalog artist identifier, then dropd shall continue processing remaining similar artists. | Test: Include one unresolved and one resolvable similar artist and verify processing continues to the resolvable artist. |
| DD-016 | If the similar-artist data source is unavailable, then dropd shall log an error. | Test: Simulate a Last.fm outage and verify an error is logged. |
| DD-017 | If the similar-artist data source is unavailable, then dropd shall continue the sync using only seed artists and label-based discovery. | Test: Simulate a Last.fm outage, trigger a sync, and verify playlists are still updated from seed artists and labels. |

### Ubiquitous Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-018 | dropd shall access similar-artist data through an abstracted interface that is independent of any specific third-party provider. | Inspection: Review code to verify similar-artist logic is behind an interface that can be swapped without changing calling code. |
| DD-019 | dropd shall limit tracks from similar artists to a configurable maximum percentage of each playlist's total tracks. | Test: Set the similar artist percentage to 20%, trigger a sync, and verify that no more than 20% of the playlist's tracks are from similar artists. |

## 4. Artist Filtering

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-020 | When dropd performs a sync, dropd shall retrieve the user's personal ratings for songs and albums from the Apple Music API. | Test: Dislike a song in Apple Music, trigger a sync, and verify the rating is retrieved. |
| DD-021 | When dropd finds a song or album with a dislike rating (value of -1), dropd shall exclude the primary artist of that song or album from all playlist population for the current sync. | Test: Dislike a song by an artist, trigger a sync, and verify no tracks from that artist appear in any playlist. |
| DD-022 | When dropd excludes an artist due to dislike ratings, dropd shall log the excluded artist name. | Test: Dislike a song, trigger a sync, and verify a log entry identifies the excluded artist. |

## 5. New Release Detection

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-023 | When dropd has a combined list of seed artists, similar artists, and label-sourced artists, dropd shall query the Apple Music catalog for new releases (albums, singles, and collaborations) from each artist. | Test: Trigger a sync and verify that new releases are fetched for artists from all three sources. |
| DD-024 | When dropd queries for new releases, dropd shall retrieve releases sorted by release date in descending order. | Test: Verify that releases returned for an artist are ordered newest-first. |
| DD-025 | When dropd retrieves new releases, dropd shall include only releases with a release date within a configurable lookback period. | Test: Set the lookback period to 30 days, trigger a sync, and verify that no releases older than 30 days are included. |
| DD-026 | When dropd queries for new releases, dropd shall include releases where the artist appears as a featured or collaborating artist. | Test: Verify that a collaboration featuring a seed artist is included in the new releases. |
| DD-027 | When dropd aggregates release candidates from multiple discovery sources, dropd shall deduplicate releases by Apple Music catalog release identifier before genre classification. | Test: Cause the same release to be discovered via seed, similar-artist, and label flows and verify it is processed once. |
| DD-028 | When dropd builds the track-add set for a playlist update, dropd shall deduplicate candidate tracks by Apple Music catalog track identifier before submission. | Test: Include duplicate track references in candidate input and verify each track is submitted at most once for addition. |

### Unwanted Behavior Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-029 | If the Apple Music catalog is unavailable when querying for new releases, then dropd shall log an error. | Test: Simulate an Apple Music API outage and verify an error is logged. |
| DD-030 | If the Apple Music catalog is unavailable when querying for new releases, then dropd shall abort the current sync. | Test: Simulate an Apple Music API outage, trigger a sync, and verify the sync terminates. |

## 6. Genre Classification

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-031 | When dropd retrieves a new release, dropd shall read the genre metadata from the Apple Music catalog entry for that release. | Test: Retrieve a release and verify that genre names are extracted from the `genreNames` attribute. |
| DD-032 | When dropd has genre metadata for a release, dropd shall match the release to one or more configured genre playlists using normalized exact matching against the playlist's genre criteria. | Test: Configure a playlist with genre criteria `electronic`, retrieve a release tagged ` Electronic `, and verify it is matched. |
| DD-033 | When a release matches genre criteria for multiple configured playlists, dropd shall include tracks from that release in each matching playlist. | Test: Configure two playlists that both match a release genre and verify the release's tracks are added to both playlists. |

### Unwanted Behavior Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-034 | If a release has no genre metadata in the Apple Music catalog, then dropd shall log a warning identifying the release. | Test: Encounter a release without `genreNames` and verify a warning is logged. |
| DD-035 | If a release has no genre metadata in the Apple Music catalog, then dropd shall exclude that release from genre-based playlist assignment. | Test: Encounter a release without `genreNames` and verify the release is not added to any playlist. |

## 7. Playlist Configuration

### Ubiquitous Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-036 | dropd shall store a user-configured list of playlist definitions, each containing a playlist name and a set of genre criteria. | Inspection: Review configuration to confirm playlist definitions are present with name and genre criteria. |
| DD-037 | dropd shall store a configurable rolling window duration that determines how long tracks remain in a playlist, defaulting to 30 days. | Inspection: Review configuration to confirm the rolling window duration is present, editable, and defaults to 30 days. |
| DD-038 | dropd shall store a configurable maximum percentage of similar-artist tracks allowed per playlist. | Inspection: Review configuration to confirm the similar-artist percentage setting is present and editable. |
| DD-039 | dropd shall store a configurable new-release lookback duration, defaulting to 30 days. | Inspection: Review configuration to confirm the lookback setting is present, editable, and defaults to 30 days. |
| DD-040 | dropd shall store a configurable API request timeout, defaulting to 10 seconds. | Inspection: Review configuration to confirm the timeout setting is present, editable, and defaults to 10 seconds. |
| DD-041 | dropd shall store a configurable maximum API retry count per request, defaulting to 3 retries. | Inspection: Review configuration to confirm the retry count setting is present, editable, and defaults to 3. |
| DD-042 | dropd shall store a configurable pagination policy with defaults of 100 items per page and a maximum of 20 pages per endpoint call-chain per sync. | Inspection: Review configuration to confirm page size and max-page settings are present and defaulted correctly. |
| DD-043 | dropd shall store a configurable maximum sync runtime, defaulting to 15 minutes. | Inspection: Review configuration to confirm the max sync runtime setting is present, editable, and defaults to 15 minutes. |
| DD-044 | dropd shall store a configurable API error-rate abort threshold, defaulting to 30 percent failed API requests within a sync. | Inspection: Review configuration to confirm the API error-rate threshold is present, editable, and defaults to 30 percent. |

## 8. Playlist Management

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-045 | When dropd runs for the first time for a given playlist definition, dropd shall create a new playlist in the user's Apple Music library with the configured playlist name and shall persist the returned playlist identifier for use in subsequent syncs. | Test: Configure a new playlist definition, trigger a sync, and verify the playlist is created in Apple Music and its identifier is persisted. |
| DD-046 | When dropd has matched new releases to a playlist, dropd shall add the tracks from those releases to the corresponding Apple Music library playlist. | Test: Trigger a sync with new matching releases and verify the tracks appear in the Apple Music playlist. |
| DD-047 | When dropd updates a playlist, dropd shall not add tracks that are already present in that playlist, comparing by Apple Music catalog track identifier regardless of whether existing tracks are stored under library-scoped identifiers. | Test: Trigger two consecutive syncs with the same new releases and verify no duplicate tracks are added. |
| DD-048 | When dropd updates a playlist, dropd shall identify tracks whose release date is older than the configured rolling window duration and remove them if the Apple Music API supports track removal; otherwise dropd shall log the stale tracks at informational level. | Test: Set the rolling window to 14 days, add a track released 15 days ago, trigger a sync, and verify the stale track is either removed or logged as skipped. **Note:** The Apple Music REST API currently returns HTTP 401 on `DELETE /v1/me/library/playlists/{id}/tracks`. Track removal is not functional until Apple provides a supported mechanism. |
| DD-049 | When a sync starts, dropd shall compute desired playlist contents from current source data and configured rules before applying playlist mutations. | Test: Change source data between two syncs and verify the second sync recomputes and applies the new desired state. |
| DD-050 | When dropd starts a new sync after a prior partial-failure sync, dropd shall reconcile playlists by recalculating desired additions and removals instead of relying on rollback state from the failed sync. | Test: Force a partial-failure sync, run a subsequent sync, and verify playlists converge to the expected state. |

### Unwanted Behavior Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-051 | If dropd fails to create a playlist in the user's Apple Music library, then dropd shall log an error identifying the playlist. | Test: Simulate a playlist creation failure and verify an error identifying the playlist is logged. |
| DD-052 | If dropd fails to create a playlist in the user's Apple Music library, then dropd shall continue processing remaining playlists. | Test: Simulate a playlist creation failure, trigger a sync, and verify other playlists are still processed. |
| DD-053 | If dropd fails to add tracks to a playlist, then dropd shall log an error identifying the playlist and affected tracks. | Test: Simulate a track addition failure and verify the error identifies the playlist and affected tracks. |
| DD-054 | If dropd fails to add tracks to a playlist, then dropd shall continue processing remaining playlists. | Test: Simulate a track addition failure, trigger a sync, and verify other playlists are still processed. |

## 9. Scheduling

### Ubiquitous Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-055 | dropd shall execute a sync automatically once per day. | Test: Observe the system over 48 hours and verify that exactly two syncs occur. |
| DD-056 | dropd shall store a configurable time of day at which the daily sync executes. | Inspection: Review configuration to confirm a sync time setting is present and editable. |

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-057 | When the configured sync time is reached, dropd shall initiate a sync. | Test: Set the sync time to a near-future time, wait, and verify a sync begins at the configured time. |
| DD-058 | When the configured sync time is reached while a sync is already in progress, dropd shall skip starting a second sync. | Test: Force a long-running sync across the scheduled boundary and verify no overlapping sync starts. |
| DD-059 | When dropd starts after being unavailable during a scheduled sync time, dropd shall wait until the next configured sync time instead of running a catch-up sync immediately. | Test: Stop service through a scheduled time, restart service, and verify no immediate catch-up sync runs. |

## 10. Authentication

### Ubiquitous Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-060 | dropd shall authenticate with the Apple Music API using a developer token and a user token. | Inspection: Review authentication flow to verify both tokens are used. |
| DD-061 | dropd shall authenticate with the Last.fm API using an API key. | Inspection: Review authentication flow to verify the API key is passed with requests. |
| DD-062 | dropd shall store API credentials outside of source code in a configuration or secrets store. | Inspection: Review the codebase and configuration to verify no credentials are hardcoded. |
| DD-063 | dropd shall generate Apple Music developer tokens as JWTs signed with ES256 and containing `kid`, `iss`, `iat`, and `exp` claims, with `exp` not greater than 6 months from issuance. | Inspection: Decode a generated token and verify algorithm, header values, claims, and expiration bound. |
| DD-064 | dropd shall include `Authorization: Bearer <developer-token>` in every Apple Music API request. | Test: Capture outgoing Apple Music requests and verify the Authorization header is present. |
| DD-065 | dropd shall include `Music-User-Token` in each Apple Music API request to personalized `/v1/me` endpoints. | Test: Capture outgoing `/v1/me` requests and verify the Music-User-Token header is present. |

### Unwanted Behavior Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-066 | If the Apple Music API authentication fails, then dropd shall log an error. | Test: Provide invalid Apple Music credentials and verify an authentication error is logged. |
| DD-067 | If the Apple Music API authentication fails, then dropd shall abort the current sync. | Test: Provide invalid Apple Music credentials, trigger a sync, and verify the sync does not proceed. |
| DD-068 | If the Last.fm API authentication fails, then dropd shall log an error. | Test: Provide an invalid Last.fm API key (Last.fm returns HTTP 200 with `{"error":10,"message":"Invalid API key ..."}`) and verify an authentication error is logged. |
| DD-069 | If the Last.fm API authentication fails, then dropd shall continue the sync without similar-artist discovery. | Test: Provide an invalid Last.fm API key (Last.fm returns HTTP 200 with `{"error":10,...}`), trigger a sync, and verify playlists are still updated from seed artists and labels. |
| DD-070 | If Apple Music returns HTTP 401 for a request, then dropd shall regenerate or reload the developer token. | Test: Force an expired developer token and verify token regeneration or reload occurs. |
| DD-071 | If Apple Music returns HTTP 401 for a request, then dropd shall retry that request once. | Test: Force an expired developer token, issue a request, and verify exactly one retry occurs. |
| DD-072 | If Apple Music returns HTTP 403 for a personalized endpoint request, then dropd shall log that Music User Token re-authorization is required. | Test: Force an invalid Music User Token on `/v1/me` requests and verify re-authorization requirement is logged. |
| DD-073 | If Apple Music returns HTTP 403 for a personalized endpoint request, then dropd shall abort the current sync. | Test: Force an invalid Music User Token on `/v1/me` requests and verify the sync aborts. |

## 11. Observability

### Ubiquitous Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-074 | dropd shall log the start and completion of each sync, including the number of new tracks added and tracks removed across all playlists. | Test: Trigger a sync and verify log output includes start time, completion time, tracks added, and tracks removed. |
| DD-075 | dropd shall log each API call failure with the endpoint, HTTP status code, and error message. | Test: Simulate an API failure and verify the log includes the endpoint, status code, and error details. |
| DD-076 | dropd shall retain application logs for 7 days. | Inspection: Review log retention configuration and stored log files to verify a 7-day retention window. |
| DD-077 | dropd shall automatically delete log entries older than the configured retention window. | Inspection: Review log pruning behavior and verify entries older than 7 days are removed. |
| DD-078 | dropd shall log each skipped sync occurrence, including whether the skip reason was overlap with an in-progress sync or missed schedule while service was unavailable. | Test: Trigger both skip scenarios and verify reason-coded log entries are written. |
| DD-079 | dropd shall log a sync outcome status of `success`, `partial_failure`, or `aborted` for each sync run. | Test: Execute one successful run, one run with playlist-level failures, and one aborted run, and verify outcome status logs. |

## 12. API Resilience and Execution Limits

### Event-Driven Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-080 | When an Apple Music API request returns HTTP 429 with a `Retry-After` header, dropd shall wait for the indicated duration before retrying the request. | Test: Mock a 429 response with `Retry-After` and verify the retry is delayed accordingly. |
| DD-081 | When an API request returns HTTP 429 without a `Retry-After` header, dropd shall wait 2 seconds before retrying the request. | Test: Mock a 429 without `Retry-After` and verify retry delay is 2 seconds. |
| DD-082 | When an API request fails due to timeout, transient network error, or HTTP 5xx response, dropd shall retry the request up to the configured retry limit using exponential backoff with jitter. | Test: Simulate transient failures and verify retry count and backoff progression. |
| DD-083 | When an Apple Music API paginated response contains a `next` link, dropd shall request subsequent pages until `next` is absent or the configured page-limit is reached. | Test: Mock paginated responses with `next` links and verify page traversal behavior. |

### Unwanted Behavior Requirements

| ID | Requirement | Verification |
|----|-------------|--------------|
| DD-084 | If the configured page-limit is reached while a `next` link is still present, then dropd shall log a warning. | Test: Force pagination to exceed max pages and verify a warning is logged. |
| DD-085 | If the configured page-limit is reached while a `next` link is still present, then dropd shall continue the sync with the fetched subset. | Test: Force pagination to exceed max pages and verify the sync continues with partial data. |
| DD-086 | If sync runtime exceeds the configured maximum sync runtime, then dropd shall abort the current sync. | Test: Simulate a long-running sync past 15 minutes and verify sync abort behavior. |
| DD-087 | If sync runtime exceeds the configured maximum sync runtime, then dropd shall log an `aborted` sync outcome. | Test: Simulate a long-running sync past 15 minutes and verify an `aborted` outcome is logged. |
| DD-088 | If the API error rate within a sync exceeds the configured threshold, then dropd shall abort the current sync. | Test: Force enough API failures to exceed 30 percent and verify sync abort. |
| DD-089 | If the API error rate within a sync exceeds the configured threshold, then dropd shall log an `aborted` sync outcome with error-rate details. | Test: Force enough API failures to exceed 30 percent and verify aborted outcome logging includes error-rate details. |

## Traceability Notes

### Assumptions

- The Apple Music API `sort=-releaseDate` parameter on the artist albums endpoint is undocumented but has been confirmed working by multiple developers. If Apple removes this behavior, an alternative sorting strategy will be needed.
- The `genreNames` attribute on Apple Music albums/songs uses fine-grained sub-genre labels (e.g., "Dance", "House", "Techno", "Dubstep") rather than umbrella categories (e.g., "Electronic"). Only some albums include both the sub-genre and the parent genre. Users must list specific sub-genres in their playlist genre criteria for effective matching.
- The Last.fm `artist.getSimilar` endpoint provides adequate coverage for the genres the user listens to (Dance/House, Electronic, Hip Hop, Jazz).
- The Last.fm API returns HTTP 200 for all responses, including errors. Error conditions are indicated by an `"error"` field in the JSON response body (e.g., error code 10 for invalid API key, error code 6 for artist not found). Implementations must detect errors by parsing response bodies, not by checking HTTP status codes.

### Resolved Decisions

- **Rolling window duration default:** 30 days, globally configurable.
- **New-release lookback default:** 30 days, globally configurable.
- **Genre matching strategy:** Normalized exact matching (lowercase, trim, collapse internal whitespace).
- **Cross-service artist matching:** Prefer shared identifiers (for example, MBID), falling back to normalized name matching.
- **Collaboration handling:** Releases where a seed/discovered artist appears as a featured or collaborating artist are included.
- **Overlapping schedules:** If a sync is already running, the next scheduled sync is skipped.
- **Missed schedules during downtime:** No catch-up sync is run on restart; dropd waits for the next scheduled window.
- **Sync failure recovery model (MVP):** Best-effort writes are allowed; successful playlist updates persist, and later syncs reconcile toward desired state.
- **Similar artist volume control:** Similar artist tracks are capped at a configurable maximum percentage of each playlist to avoid overwhelming the playlist with unknown artists.
- **Artist deny list:** Derived from Apple Music dislike ratings (value of -1) on songs and albums. No separate deny list configuration is needed.
- **API defaults (MVP):** request timeout 10s, max retries 3, pagination 100 items/page with 20-page cap, max sync runtime 15 minutes, abort threshold at 30% API error rate.
- **Log retention (MVP):** 7 days.
- **Sensitive-data redaction in logs (MVP):** No additional redaction requirement beyond existing log content choices.
- **Playlist identification:** Apple Music library playlists are addressed by opaque IDs (e.g., `p.VKDUBYel0`), not by name. The playlist listing endpoint (`GET /v1/me/library/playlists`) has severe eventual consistency — newly created playlists may not appear for minutes or may lose their `name` attribute. dropd must persist a name→ID cache locally and merge it with the API listing to reliably find playlists across syncs.
- **Library vs. catalog identifiers:** Apple Music uses two ID namespaces. Library-scoped IDs (e.g., `r.xxx` for artists, `i.xxx` for songs) appear in `/v1/me/library` responses. Catalog IDs (e.g., `657515`, `1874397619`) appear in `/v1/catalog` responses. The library artists endpoint requires `include=catalog` to obtain catalog IDs. Existing playlist tracks expose catalog IDs at `attributes.playParams.catalogId`. All cross-reference comparisons (ratings, dedup, playlist membership) must use catalog IDs.
- **Track removal API limitation:** The Apple Music REST API returns HTTP 401 on `DELETE /v1/me/library/playlists/{id}/tracks` regardless of ID format (catalog or library). Rolling-window track removal is not currently functional. Stale tracks are logged at informational level but not removed.
- **Label availability in Apple Music catalog:** Not all record labels are searchable via the Apple Music record-labels search endpoint. For example, "Warp Records" returns empty results. This is a catalog data limitation, not a code issue.
