namespace Dropd.Core

open System
open Dropd.Core.Types

module Normalization =

    let normalizeText (value: string) =
        if String.IsNullOrWhiteSpace value then
            ""
        else
            value.Trim().ToLowerInvariant().Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> String.concat " "

    let private dedupBy (keySelector: 'a -> 'k) (items: 'a list) =
        let folder (seen: Set<'k>, acc: 'a list) item =
            let key = keySelector item

            if seen.Contains key then
                seen, acc
            else
                seen.Add key, item :: acc

        items |> List.fold folder (Set.empty, []) |> snd |> List.rev

    let dedupByArtistId (artists: Artist list) = artists |> dedupBy (fun artist -> artist.Id)

    let dedupByReleaseId (albums: Album list) = albums |> dedupBy (fun album -> album.Id)

    let dedupByTrackId (trackIds: CatalogTrackId list) = trackIds |> dedupBy id

    let isWithinLookback (today: DateOnly) (lookbackDays: int) (releaseDate: DateOnly option) =
        match releaseDate with
        | None -> false
        | Some date ->
            let cutoff = today.AddDays(-lookbackDays)
            date >= cutoff && date <= today
