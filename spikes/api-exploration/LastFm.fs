module LastFm

open System
open System.Net.Http

/// Print a one-line summary of an HTTP response: status code and a truncated body preview.
let private printResponseSummary (label: string) (response: HttpResponseMessage) = task {
    let! body = response.Content.ReadAsStringAsync()
    let preview =
        if body.Length > 200 then body.Substring(0, 200) + "..."
        else body
    printfn "  [%d %s] %s" (int response.StatusCode) (string response.StatusCode) label
    printfn "    %s" preview
    printfn ""
}

/// Run all Last.fm API exploration calls.
/// Returns 0 on success, 0 with a guidance message when credentials are missing.
let run () = task {
    let apiKey = Environment.GetEnvironmentVariable("DROPD_LASTFM_API_KEY")

    if String.IsNullOrEmpty(apiKey) then
        printfn "  Last.fm credentials not configured."
        printfn "  Missing: DROPD_LASTFM_API_KEY"
        printfn "  See docs/research/credential-setup.md for setup instructions."
        printfn ""
        return 0
    else

    let baseUrl = "http://ws.audioscrobbler.com/2.0/"

    use client = new HttpClient()

    // 1. Similar artists for Radiohead
    let url = $"{baseUrl}?method=artist.getSimilar&artist=Radiohead&api_key={apiKey}&format=json&limit=5"
    let! resp = client.GetAsync(url)
    do! printResponseSummary "artist.getSimilar (Radiohead)" resp

    // 2. Similar artists for a nonexistent artist
    let url = $"{baseUrl}?method=artist.getSimilar&artist=zzznonexistentartistzzz&api_key={apiKey}&format=json&limit=5"
    let! resp = client.GetAsync(url)
    do! printResponseSummary "artist.getSimilar (nonexistent artist)" resp

    // 3. Artist search for Bonobo
    let url = $"{baseUrl}?method=artist.search&artist=Bonobo&api_key={apiKey}&format=json&limit=5"
    let! resp = client.GetAsync(url)
    do! printResponseSummary "artist.search (Bonobo)" resp

    return 0
}
