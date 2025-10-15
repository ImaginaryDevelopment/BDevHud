namespace BDevHud

open System
open System.IO
open System.Web

/// Repository cache entry
type RepoCacheEntry =
    { Name: string
      LastPullDate: string
      FileSystemPath: string
      RepoUrl: string }

module HudCache =

    /// Cache file path in user data directory
    let getCacheFilePath () : string =
        let userDataDir =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)

        Path.Combine(userDataDir, "BDevHud.csv")

    /// Load cache from CSV file
    let loadCache () : Map<string, RepoCacheEntry> =
        try
            let cacheFile = getCacheFilePath ()

            if File.Exists(cacheFile) then
                File.ReadAllLines(cacheFile)
                |> Array.skip 1 // Skip header row
                |> Array.map (fun line ->
                    let parts = line.Split(',')

                    if parts.Length >= 4 then
                        let key = parts.[2] // Use FileSystemPath as key

                        let entry =
                            { Name = parts.[0]
                              LastPullDate = parts.[1]
                              FileSystemPath = parts.[2]
                              RepoUrl = parts.[3] }

                        (key, entry)
                    else
                        ("",
                         { Name = ""
                           LastPullDate = ""
                           FileSystemPath = ""
                           RepoUrl = "" }))
                |> Array.filter (fun (key, _) -> not (String.IsNullOrEmpty(key)))
                |> Map.ofArray
            else
                Map.empty
        with _ ->
            Map.empty

    /// Save cache to CSV file
    let saveCache (cache: Map<string, RepoCacheEntry>) =
        try
            let cacheFile = getCacheFilePath ()
            let directory = Path.GetDirectoryName(cacheFile)

            if not (Directory.Exists(directory)) then
                Directory.CreateDirectory(directory) |> ignore

            let lines =
                [| "Name,LastPullDate,FileSystemPath,RepoUrl" |]
                |> Array.append (
                    cache.Values
                    |> Seq.map (fun entry ->
                        $"{entry.Name},{entry.LastPullDate},{entry.FileSystemPath},{entry.RepoUrl}")
                    |> Array.ofSeq
                )

            File.WriteAllLines(cacheFile, lines)
        with ex ->
            printfn $"Warning: Failed to save cache: {ex.Message}"
