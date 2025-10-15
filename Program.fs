open System
open System.IO
open System.Web
open System.Text
open System.Threading.Tasks
open IO.Adapter
open Octo.Adapter
open GitHub.Adapter
open BDevHud

/// Extract repository name from a git URL (wrapper for GitAdapter function)
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
        let mutable cache = HudCache.loadCache ()

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
        HudCache.saveCache cache

// Get root directory from command line args or environment variable
let getRootDirectory () =
    let args = Environment.GetCommandLineArgs()

    // Check for command line argument (skip the first arg which is the program name)
    // Look for positional arguments that aren't Octopus-related
    let positionalArgs =
        args
        |> Array.skip 1
        |> Array.filter (fun arg ->
            not (arg.StartsWith("--") || arg.StartsWith("-"))
            && not (arg.StartsWith("http"))) // Filter out URLs

    if positionalArgs.Length > 0 then
        let rootPath = positionalArgs.[0]
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

// Get Octopus URL from command line args or environment variable
let getOctopusUrl () =
    let args = Environment.GetCommandLineArgs()

    // Check for command line argument (look for --octopus-url or -o)
    let octopusArgIndex =
        args |> Array.tryFindIndex (fun arg -> arg = "--octopus-url" || arg = "-o")

    match octopusArgIndex with
    | Some index when index + 1 < args.Length ->
        let octopusUrl = args.[index + 1]
        printfn $"Using command line Octopus URL: {octopusUrl}"
        Some octopusUrl
    | _ ->
        // Check for OCTOPUS_URL environment variable
        let octopusUrl = Environment.GetEnvironmentVariable("OCTOPUS_URL")

        if not (String.IsNullOrEmpty(octopusUrl)) then
            printfn $"Using OCTOPUS_URL environment variable: {octopusUrl}"
            Some octopusUrl
        else
            None

// Display Octopus projects with git repository information
let displayOctopusProjects (projects: OctopusClient.OctopusProjectWithGit list) =
    if projects.Length = 0 then
        printfn "No Octopus projects found."
    else
        printfn $"\nFound {projects.Length} Octopus project(s):"

        projects
        |> List.iteri (fun i project ->
            printfn $"{i + 1}. {project.ProjectName}"
            printfn $"    Octopus URL: {project.OctopusUrl}"

            match project.GitRepoUrl with
            | Some gitUrl -> printfn $"    Git Repository: {gitUrl}"
            | None -> printfn $"    Git Repository: (not found)")

// Get git repositories from cache for Octopus matching
let getGitReposFromCache () : (string * string) list =
    let cache = HudCache.loadCache ()
    cache.Values |> Seq.map (fun entry -> (entry.Name, entry.RepoUrl)) |> List.ofSeq

// Display GitHub repositories (simplified for now)
let displayGitHubRepositories (repos: obj list) =
    if repos.Length = 0 then
        printfn "No GitHub repositories found."
    else
        printfn $"\nFound {repos.Length} GitHub repository(ies) (details would be shown here)"

// Main program entry point
let main () =
    printfn "Git Folder Spider Search"
    printfn "======================="

    let rootDirectory = getRootDirectory ()
    let gitFolders = findGitFolders rootDirectory
    displayGitFolders gitFolders

    // Check for Octopus URL and display Octopus projects
    let octopusUrl = getOctopusUrl ()

    match octopusUrl with
    | Some url ->
        printfn "\n%s" (String.replicate 50 "=")
        printfn "Octopus Deploy Integration"
        printfn "%s" (String.replicate 50 "=")

        // Note: This is a simplified example - in a real implementation,
        // you would need to handle API keys, authentication, etc.
        printfn $"Octopus URL found: {url}"
        printfn "Note: Full Octopus integration requires API key configuration."
        printfn "This would connect to Octopus and match projects with git repositories."

        // For demonstration, show what would happen with cached git repos
        let gitRepos = getGitReposFromCache ()
        printfn $"\nFound {gitRepos.Length} git repositories in cache for potential matching:"
        gitRepos |> List.iter (fun (name, url) -> printfn $"  - {name}: {url}")
    | None -> printfn "\nNo Octopus URL specified. Use --octopus-url <url> or set OCTOPUS_URL environment variable."

    // GitHub Integration Demo
    printfn "\n%s" (String.replicate 50 "=")
    printfn "GitHub Integration Demo"
    printfn "%s" (String.replicate 50 "=")

    // Check for GitHub token
    let githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")

    if not (String.IsNullOrEmpty(githubToken)) then
        printfn $"GitHub integration enabled with token authentication"
        printfn "Note: This would query GitHub API for repositories with wildcard filtering."
        printfn "Example usage:"
        printfn "  - Get all accessible repos: GitHubAdapter.getRepositories config None"
        printfn "  - Filter by pattern: GitHubAdapter.getRepositories config (Some \"*test*\")"
        printfn "  - Get org repos: GitHubAdapter.getOrganizationRepositories config \"orgname\" None"
        printfn "  - Search across all repos: GitHubAdapter.searchRepositories config \"*api*\" false"
    else
        printfn "GitHub integration not configured."
        printfn "Set GITHUB_TOKEN environment variable to enable."
        printfn "Example:"
        printfn "  $env:GITHUB_TOKEN=\"your_token_here\""

    printfn "\nSearch completed."

// Run the main function
main ()
