open System
open System.IO
open System.Web
open System.Text
open IO.Adapter

/// Repository cache entry
type RepoCacheEntry =
    { Name: string
      LastPullDate: string
      FileSystemPath: string
      RepoUrl: string }

/// Cache file path in user data directory
let getCacheFilePath () =
    let userData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
    Path.Combine(userData, "BDevHud.csv")

/// Extract repository name from URL
let extractRepoName (url: string) : string =
    try
        let decodedUrl = HttpUtility.UrlDecode(url)
        let uri = Uri(decodedUrl)
        let segments = uri.Segments

        if segments.Length > 0 then
            let lastSegment = segments.[segments.Length - 1]

            if lastSegment.EndsWith(".git") then
                lastSegment.Substring(0, lastSegment.Length - 4)
            else
                lastSegment
        else
            "unknown"
    with _ ->
        "unknown"

/// Load cache from CSV file
let loadCache () : Map<string, RepoCacheEntry> =
    try
        let cacheFile = getCacheFilePath ()

        if File.Exists(cacheFile) then
            File.ReadAllLines(cacheFile)
            |> Array.skip 1 // Skip header
            |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))
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
                |> Seq.map (fun entry -> $"{entry.Name},{entry.LastPullDate},{entry.FileSystemPath},{entry.RepoUrl}")
                |> Array.ofSeq
            )

        File.WriteAllLines(cacheFile, lines)
    with ex ->
        printfn $"Warning: Failed to save cache: {ex.Message}"

/// Spider-search for .git folders under a root directory
/// If no root directory is provided, searches all local drives
let findGitFolders (rootDirectory: string option) =
    /// Processes a directory to find .git folders
    let processDirectoryForGit (dir: string) =
        try
            let gitPath = Path.Combine(dir, ".git")

            if Directory.Exists(gitPath) then
                printfn $"Found .git folder: {dir}"
                [ gitPath ]
            else
                []
        with _ ->
            [] // Ignore errors when checking for .git in current directory

    /// Combines results from different directories
    let combineResults (existing: string list) (newResults: string list) = existing @ newResults

    match rootDirectory with
    | Some root ->
        if Directory.Exists(root) then
            printfn $"Searching for .git folders under: {root}"

            DirectoryTraversal.traverseDirectories root processDirectoryForGit [] combineResults
            |> List.toArray
        else
            printfn $"Directory does not exist: {root}"
            [||]
    | None ->
        printfn "Searching all local drives for .git folders..."

        DirectoryTraversal.traverseAllLocalDrives processDirectoryForGit [] combineResults
        |> List.toArray

/// Display results of git folder search with remote information and git pull
let displayGitFolders (gitFolders: string[]) =
    if gitFolders.Length = 0 then
        printfn "No .git folders found."
    else
        printfn $"\nFound {gitFolders.Length} .git folder(s):"

        // Load existing cache
        let mutable cache = loadCache ()

        gitFolders
        |> Array.iteri (fun i folder ->
            let parentDir = Directory.GetParent(folder).FullName
            printfn $"{i + 1}. {parentDir}"

            // Get and display remote information
            let remoteResult = GitAdapter.getRemote parentDir

            if remoteResult.Success && remoteResult.Remotes.Length > 0 then
                // Group remotes by name and URL to collapse fetch/push into single lines
                let groupedRemotes =
                    remoteResult.Remotes
                    |> List.groupBy (fun remote -> (remote.Name, remote.Url))
                    |> List.map (fun ((name, url), remotes) ->
                        let types = remotes |> List.map (fun r -> r.Type) |> List.distinct |> List.sort

                        let operationType =
                            match types with
                            | [ "fetch"; "push" ] -> "both"
                            | [ "fetch" ] -> "pull"
                            | [ "push" ] -> "push"
                            | _ -> String.concat ", " types

                        (name, url, operationType))

                groupedRemotes
                |> List.iter (fun (name, url, operationType) ->
                    let decodedUrl = HttpUtility.UrlDecode(url)
                    printfn $"    {name}      {decodedUrl} ({operationType})")

                // Run git pull for this repository
                let (pullSuccess, pullError) = GitAdapter.gitPull parentDir

                if not pullSuccess then
                    printfn $"    ⚠️  Git pull failed: {pullError}"

                // Update cache with repository information
                let primaryRemote = groupedRemotes |> List.head
                let (_, url, _) = primaryRemote
                let repoName = extractRepoName url
                let repoUrl = HttpUtility.UrlDecode(url)

                let cacheEntry =
                    { Name = repoName
                      LastPullDate =
                        if pullSuccess then
                            DateTime.Now.ToString("yyyyMMdd.HHmmss")
                        else
                            (cache.TryFind(parentDir)
                             |> Option.map (fun e -> e.LastPullDate)
                             |> Option.defaultValue "")
                      FileSystemPath = parentDir
                      RepoUrl = repoUrl }

                cache <- cache.Add(parentDir, cacheEntry)
            else
                printfn "    (no remote configured)")

        // Save updated cache
        saveCache cache

// Get root directory from command line args or environment variable
let getRootDirectory () =
    let args = Environment.GetCommandLineArgs()

    // Check for command line argument (skip the first arg which is the program name)
    if args.Length > 1 then
        let rootPath = args.[1]
        printfn $"Using command line root directory: {rootPath}"
        Some rootPath
    else
        // Check for DEVROOT environment variable
        let devRoot = Environment.GetEnvironmentVariable("DEVROOT")

        if not (String.IsNullOrEmpty(devRoot)) then
            printfn $"Using DEVROOT environment variable: {devRoot}"
            Some devRoot
        else
            printfn "No root directory specified. Searching all local drives."
            None

// Main program entry point
let main () =
    printfn "Git Folder Spider Search"
    printfn "======================="

    let rootDirectory = getRootDirectory ()
    let gitFolders = findGitFolders rootDirectory
    displayGitFolders gitFolders

    printfn "\nSearch completed."

// Run the main function
main ()
