module HttpRuntime

open System
open System.Net.Http
open System.Text
open Dropd.Core.ApiContracts

let private baseUrlFor = function
    | AppleMusic -> "https://api.music.apple.com"
    | LastFm     -> "https://ws.audioscrobbler.com"

let create () : ApiRuntime =
    let client = new HttpClient()

    let execute (request: ApiRequest) = async {
        let baseUrl = baseUrlFor request.Service

        let queryString =
            if List.isEmpty request.Query then ""
            else
                let pairs =
                    request.Query
                    |> List.map (fun (k, v) ->
                        Uri.EscapeDataString(k) + "=" + Uri.EscapeDataString(v))
                "?" + String.concat "&" pairs

        let url = baseUrl + request.Path + queryString
        let message = new HttpRequestMessage(HttpMethod(request.Method), url)

        for (name, value) in request.Headers do
            message.Headers.TryAddWithoutValidation(name, value) |> ignore

        match request.Body with
        | Some body ->
            message.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        | None -> ()

        let! response = client.SendAsync(message) |> Async.AwaitTask
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        return {
            StatusCode = int response.StatusCode
            Body       = body
            Headers    =
                response.Headers
                |> Seq.map (fun h -> h.Key, String.concat "," h.Value)
                |> Seq.toList
        }
    }

    { Execute = execute
      UtcNow  = fun () -> DateTimeOffset.UtcNow }
