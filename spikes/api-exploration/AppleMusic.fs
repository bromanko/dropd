module AppleMusic

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

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

/// Run Apple Music catalog API calls (public data, no user auth needed).
let runCatalog () = task {
    let token = Environment.GetEnvironmentVariable("DROPD_APPLE_MUSIC_TOKEN")

    if String.IsNullOrEmpty(token) then
        printfn "  Apple Music credentials not configured."
        printfn "  Missing: DROPD_APPLE_MUSIC_TOKEN"
        printfn "  See docs/research/credential-setup.md for setup instructions."
        printfn ""
        return 0
    else

    use client = new HttpClient()
    client.BaseAddress <- Uri("https://api.music.apple.com")
    client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

    // 1. Catalog artist albums sorted by release date (Radiohead, ID 657515)
    let! resp = client.GetAsync("/v1/catalog/us/artists/657515/albums?sort=-releaseDate&limit=3")
    do! printResponseSummary "GET /v1/catalog/us/artists/657515/albums?sort=-releaseDate&limit=3" resp

    // 2. Search for record label "Ninja Tune"
    let! resp = client.GetAsync("/v1/catalog/us/search?term=Ninja+Tune&types=record-labels&limit=1")
    do! printResponseSummary "GET /v1/catalog/us/search?term=Ninja+Tune&types=record-labels&limit=1" resp

    // 3. Record label latest releases — parse the label ID from the search result above.
    let! searchBody = resp.Content.ReadAsStringAsync()
    let mutable labelId = "1543411840" // fallback known Ninja Tune label ID
    try
        let doc = JsonDocument.Parse(searchBody)
        let results = doc.RootElement.GetProperty("results").GetProperty("record-labels").GetProperty("data")
        if results.GetArrayLength() > 0 then
            labelId <- results.[0].GetProperty("id").GetString()
    with _ -> ()

    let! resp = client.GetAsync($"/v1/catalog/us/record-labels/{labelId}?views=latest-releases")
    do! printResponseSummary $"GET /v1/catalog/us/record-labels/{labelId}?views=latest-releases" resp

    return 0
}

/// Run Apple Music personal library API calls (requires Music User Token).
let runPersonal () = task {
    let token = Environment.GetEnvironmentVariable("DROPD_APPLE_MUSIC_TOKEN")
    let userToken = Environment.GetEnvironmentVariable("DROPD_APPLE_USER_TOKEN")

    let missing =
        [ if String.IsNullOrEmpty(token) then "DROPD_APPLE_MUSIC_TOKEN"
          if String.IsNullOrEmpty(userToken) then "DROPD_APPLE_USER_TOKEN" ]

    if not (List.isEmpty missing) then
        printfn "  Apple Music personal endpoints not configured."
        printfn "  Missing: %s" (String.Join(", ", missing))
        printfn "  See docs/research/credential-setup.md for setup instructions."
        printfn ""
        return 0
    else

    use client = new HttpClient()
    client.BaseAddress <- Uri("https://api.music.apple.com")
    client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)
    client.DefaultRequestHeaders.Add("Music-User-Token", userToken)

    // 1. Library artists
    let! resp = client.GetAsync("/v1/me/library/artists?limit=5")
    do! printResponseSummary "GET /v1/me/library/artists?limit=5" resp

    // 2. Song ratings (requires explicit song IDs)
    let! resp = client.GetAsync("/v1/me/ratings/songs?ids=203709340")
    do! printResponseSummary "GET /v1/me/ratings/songs?ids=203709340" resp

    return 0
}
