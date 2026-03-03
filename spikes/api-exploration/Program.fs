open System

[<EntryPoint>]
let main _argv =
    printfn ""
    printfn "========================================"
    printfn "  dropd — API Exploration Spike"
    printfn "========================================"
    printfn ""

    printfn "--- Apple Music API (catalog) ---"
    printfn ""
    AppleMusic.runCatalog().GetAwaiter().GetResult() |> ignore

    printfn "--- Apple Music API (personal library) ---"
    printfn ""
    AppleMusic.runPersonal().GetAwaiter().GetResult() |> ignore

    printfn "--- Last.fm API ---"
    printfn ""
    LastFm.run().GetAwaiter().GetResult() |> ignore

    printfn "========================================"
    printfn "  Exploration complete."
    printfn "========================================"
    printfn ""

    // Return 0 regardless — missing credentials is an expected, successful outcome.
    0
