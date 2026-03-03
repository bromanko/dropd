namespace Dropd.Core

open System
open System.Text.Json
open Dropd.Core.Types

/// Shared JSON-parsing, logging, and auth-header utilities used by both
/// PlaylistReconcile and SyncEngine. Placed before both modules in the
/// project's compilation order.
module JsonHelpers =

    module AC = ApiContracts

    // ── Logging ──────────────────────────────────────────────────────────────

    let mkLog level code message data : AC.LogEntry =
        { Level = level
          Code = code
          Message = message
          Data = data }

    /// Truncate a response body to 200 characters before embedding it in a log
    /// entry, preventing sensitive data from being stored verbatim in logs.
    let truncateBody (body: string) =
        if body.Length > 200 then body.[..199] + "…" else body

    // ── JSON helpers ─────────────────────────────────────────────────────────

    let tryGetProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let tryGetString (name: string) (element: JsonElement) =
        tryGetProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj
            else
                None)

    let tryParseDateOnly (value: string) =
        match DateOnly.TryParse(value) with
        | true, parsed -> Some parsed
        | _ -> None

    // ── Apple Music auth headers ──────────────────────────────────────────────

    /// Return the Apple Music headers appropriate for the given path.
    /// `Music-User-Token` is included only for personalised `/v1/me` paths.
    let appleHeaders (config: Config.ValidSyncConfig) (path: string) =
        let developerToken = config.AppleMusicDeveloperToken |> AppleMusicDeveloperToken.value
        let userToken = config.AppleMusicUserToken |> AppleMusicUserToken.value

        let common = [ "Authorization", $"Bearer {developerToken}" ]

        if path.StartsWith("/v1/me", StringComparison.OrdinalIgnoreCase) then
            common @ [ "Music-User-Token", userToken ]
        else
            common
