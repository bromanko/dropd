module SmokeConfig

open System
open System.IO
open System.Text.Json
open Dropd.Core
open Dropd.Core.Types

// ── Local config file types ───────────────────────────────────────────────

[<CLIMutable>]
type JsonPlaylist =
    { name: string
      genreCriteria: string[] }

[<CLIMutable>]
type JsonConfig =
    { playlists: JsonPlaylist[]
      labelNames: string[] }

// ── Config loading ────────────────────────────────────────────────────────

let load () : Result<Config.SyncConfig, string> =
    let developerToken = Environment.GetEnvironmentVariable("DROPD_APPLE_MUSIC_TOKEN") |> Option.ofObj |> Option.defaultValue ""
    let userToken      = Environment.GetEnvironmentVariable("DROPD_APPLE_USER_TOKEN")  |> Option.ofObj |> Option.defaultValue ""
    // Phase 1 never calls Last.fm, but Config.validate requires a non-empty value.
    let lastFmKey      = Environment.GetEnvironmentVariable("DROPD_LASTFM_API_KEY")    |> Option.ofObj |> Option.defaultValue "phase1-unused"

    let missing =
        [ if String.IsNullOrWhiteSpace developerToken then "DROPD_APPLE_MUSIC_TOKEN"
          if String.IsNullOrWhiteSpace userToken      then "DROPD_APPLE_USER_TOKEN" ]

    if not (List.isEmpty missing) then
        let missingStr = String.concat ", " missing
        Error $"Missing required environment variables: {missingStr}\nSee docs/research/credential-setup.md or run: direnv allow"
    else

    // Config files are copied to the build output directory by the .fsproj.
    // Resolve them relative to the assembly location so this works regardless
    // of which directory `dotnet run` is invoked from.
    let assemblyDir = Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location)
    let localPath   = Path.Combine(assemblyDir, "config.local.json")
    let examplePath = Path.Combine(assemblyDir, "config.example.json")

    let configPath =
        if   File.Exists(localPath)   then Ok localPath
        elif File.Exists(examplePath) then Ok examplePath
        else
            Error (
                "No config file found. Copy config.example.json to config.local.json " +
                "in spikes/integration-smoke/ and rebuild:\n" +
                "  cp spikes/integration-smoke/config.example.json " +
                "spikes/integration-smoke/config.local.json")

    match configPath with
    | Error msg -> Error msg
    | Ok path ->

    try
        let json = File.ReadAllText(path)
        let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        let local = JsonSerializer.Deserialize<JsonConfig>(json, opts)

        let playlists =
            local.playlists
            |> Array.map (fun p ->
                { Config.Name = p.name
                  Config.GenreCriteria = p.genreCriteria |> Array.toList })
            |> Array.toList

        let labelNames = local.labelNames |> Array.toList

        Ok { Config.defaults with
                Playlists                = playlists
                LabelNames               = labelNames
                AppleMusicDeveloperToken = AppleMusicDeveloperToken developerToken
                AppleMusicUserToken      = AppleMusicUserToken userToken
                LastFmApiKey             = LastFmApiKey lastFmKey }
    with ex ->
        Error $"Could not parse config file at {path}: {ex.Message}"
