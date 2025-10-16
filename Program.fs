open System
open System.IO
open System.Web
open System.Text
open System.Threading.Tasks
open IO.Adapter
open Octo.Adapter
open GitHub.Adapter
open SqlLite.Adapter
open BDevHud

// =============================================================================
// OCTOPUS ADAPTER FUNCTIONS MODULE  
// =============================================================================

module OctopusOperations =
    /// Get Octopus URL from command line args or environment variable
    let getOctopusUrl () =
        let args = Environment.GetCommandLineArgs()
        
        // Check for command line argument first
        let octopusUrlArg =
            args
            |> Array.tryFind (fun arg -> arg.StartsWith("--octopus-url="))
            |> Option.map (fun arg -> arg.Substring(14)) // Remove "--octopus-url=" prefix
        
        match octopusUrlArg with
        | Some url ->
            printfn "Using command line Octopus URL: %s" url
            Some url
        | None ->
            // Check environment variable
            let envUrl = Environment.GetEnvironmentVariable("OCTOPUS_URL")
            if not (String.IsNullOrEmpty(envUrl)) then
                printfn "Using OCTOPUS_URL environment variable: %s" envUrl
                Some envUrl
            else
                None

    // Format API key for display (hide most characters)
    let formatApiKeyForDisplay (apiKey: string) : string =
        if apiKey.Length <= 8 then
            "****"
        else
            let prefix = apiKey.Substring(0, 4)
            let suffix = apiKey.Substring(apiKey.Length - 4)
            sprintf "%s****%s" prefix suffix

    /// Get Octopus API key from command line args or environment variable
    let getOctopusApiKey () =
        let args = Environment.GetCommandLineArgs()
        
        // Check for command line argument first
        let octopusApiKeyArg =
            args
            |> Array.tryFind (fun arg -> arg.StartsWith("--octopus-api-key="))
            |> Option.map (fun arg -> arg.Substring(18)) // Remove "--octopus-api-key=" prefix
        
        match octopusApiKeyArg with
        | Some apiKey ->
            printfn "Using command line Octopus API key: %s" (formatApiKeyForDisplay apiKey)
            Some apiKey
        | None ->
            // Check environment variable
            let envApiKey = Environment.GetEnvironmentVariable("OCTO_API_KEY")
            if not (String.IsNullOrEmpty(envApiKey)) then
                printfn "Using OCTO_API_KEY environment variable: %s" (formatApiKeyForDisplay envApiKey)
                Some envApiKey
            else
                None

    /// Get variable search pattern from command line args
    let getVariableSearchPattern () =
        let args = Environment.GetCommandLineArgs()
        
        // Look for --search-variable pattern
        let searchVariableArg =
            args
            |> Array.tryFind (fun arg -> arg.StartsWith("--search-variable="))
            |> Option.map (fun arg -> arg.Substring(18)) // Remove "--search-variable=" prefix
        
        match searchVariableArg with
        | Some pattern ->
            printfn "Searching for variables matching: %s" pattern
            Some pattern
        | None -> None

    /// Get project and step for deployment step analysis
    let getProjectStepAnalysis () =
        let args = Environment.GetCommandLineArgs()
        
        // Look for --analyze-step pattern
        let analyzeStepArg =
            args
            |> Array.tryFind (fun arg -> arg.StartsWith("--analyze-step="))
            |> Option.map (fun arg -> arg.Substring(15)) // Remove "--analyze-step=" prefix
        
        match analyzeStepArg with
        | Some stepSpec ->
            printfn "Analyzing deployment step: %s" stepSpec
            // Parse format: projectName/stepIdentifier
            if stepSpec.Contains("/") then
                let parts = stepSpec.Split('/')
                if parts.Length = 2 then
                    Some (parts.[0], parts.[1])
                else
                    printfn "❌ Invalid format. Expected: projectName/stepIdentifier (e.g., 'MyProject/1' or 'MyProject/10.1')"
                    None
            else
                printfn "❌ Invalid format. Expected: projectName/stepIdentifier (e.g., 'MyProject/1' or 'MyProject/10.1')"
                None
        | None -> None

    /// Finds GitHub repositories that match local git repositories
    let findMatchingGitHubRepos (octopusProjects: OctopusClient.OctopusProjectWithGit list) (localGitRepos: (string * string) list) =
        octopusProjects
        |> List.collect (fun project ->
            match project.GitRepoUrl with
            | Some gitUrl when gitUrl.Contains("github.com") ->
                // Find local repos that match this GitHub URL
                localGitRepos
                |> List.choose (fun (repoName, repoUrl) ->
                    if repoUrl.Contains(gitUrl.Replace("https://github.com/", ""), System.StringComparison.OrdinalIgnoreCase) then
                        Some (gitUrl, repoName, repoUrl)
                    else
                        None)
            | _ -> [])

    /// Display Octopus projects with git repository information
    let displayOctopusProjects (projects: OctopusClient.OctopusProjectWithGit list) =
        if projects.Length = 0 then
            printfn "No Octopus projects found."
        else
            printfn "\nFound %d Octopus project(s):" projects.Length

            projects
            |> List.iteri (fun i project ->
                printfn "%d. %s" (i + 1) project.Name
                printfn "    Octopus URL: %s" project.OctopusUrl

                match project.GitRepoUrl with
                | Some gitUrl -> printfn "    Git Repository: %s" gitUrl
                | None -> printfn "    Git Repository: (not found)")

// =============================================================================
// GITHUB ADAPTER FUNCTIONS MODULE
// =============================================================================

module GitHubOperations =
    /// Display GitHub repositories (simplified for now)
    let displayGitHubRepositories (repos: obj list) =
        if repos.Length = 0 then
            printfn "No GitHub repositories found."
        else
            printfn "\nFound %d GitHub repository(ies) (details would be shown here)" repos.Length

// =============================================================================
// MAIN PROGRAM ENTRY POINT
// =============================================================================

/// Main program entry point
let main () =
    printfn "Git Folder Spider Search"
    printfn "======================="

    // Check if we can skip git operations for search-only mode
    let skipGitOps = CommandLineArgs.shouldSkipGitOperations()
    
    if skipGitOps then
        printfn "Search-only mode: Skipping git repository discovery"
    else
        // Get root directory and debug mode
        let rootDirectory = CommandLineArgs.getRootDirectory()
        let debugMode = CommandLineArgs.shouldDebugLog()

        // Find git folders and display them
        let gitFolders = GitOperations.findGitFolders rootDirectory debugMode
        let repoInfos = GitOperations.displayGitFolders gitFolders

        // Check for Octopus URL and API key, then display Octopus projects (skip if search-only mode)
        let octopusUrl = OctopusOperations.getOctopusUrl ()
        let octopusApiKey = OctopusOperations.getOctopusApiKey ()

        match octopusUrl, octopusApiKey with
        | Some url, Some apiKey ->
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Octopus Deploy Integration"
            printfn "%s" (String.replicate 50 "=")

            try
                // Parse the URL to extract base server URL and space ID
                let (baseUrl, spaceId) = 
                    try
                        let uri = Uri(url)
                        let portPart = if uri.Port <> -1 then sprintf ":%d" uri.Port else ""
                        
                        // Preserve the path part (like /api) but remove fragment
                        let pathPart = 
                            if String.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath = "/" then 
                                ""
                            else 
                                uri.AbsolutePath.TrimEnd('/')
                        
                        let baseServerUrl = sprintf "%s://%s%s%s" uri.Scheme uri.Host portPart pathPart
                        
                        // Check if URL contains space identifier in fragment
                        let spaceIdFromFragment = 
                            if not (String.IsNullOrEmpty(uri.Fragment)) && uri.Fragment.Contains("Spaces-") then
                                let fragment = uri.Fragment.TrimStart('#')
                                let spacePart = fragment.Split('/') |> Array.tryFind (fun part -> part.StartsWith("Spaces-"))
                                spacePart
                            else
                                None
                        
                        let spaceDisplay = spaceIdFromFragment |> Option.defaultValue "Default"
                        
                        printfn "🔍 Parsed URL:"
                        printfn "    Base Server URL: %s" baseServerUrl
                        printfn "    Extracted Space ID: %s" spaceDisplay
                        
                        (baseServerUrl, spaceIdFromFragment)
                    with ex ->
                        printfn "⚠️  URL parsing warning: %s" ex.Message
                        printfn "    Using URL as-is: %s" url
                        (url, None)

                // Create Octopus client configuration
                let space = spaceId |> Option.map SpaceId
                let config = {
                    ServerUrl = baseUrl
                    ApiKey = apiKey
                    Space = space
                }

                // Test connection and get server info
                printfn "🔗 Connecting to Octopus Deploy..."
                printfn "    Server: %s" baseUrl
                printfn "    API Key: %s" (OctopusOperations.formatApiKeyForDisplay apiKey)
                printfn "    Target Space: %s" (spaceId |> Option.defaultValue "Default")

                printfn "🔍 Testing connection to Octopus server..."
                let serverInfoResult = OctopusClient.testConnection config |> Async.AwaitTask |> Async.RunSynchronously
                match serverInfoResult with
                | Ok serverInfo ->
                    printfn "✅ Connection successful! %s" serverInfo
                    
                    printfn "🔍 Listing available spaces..."
                    printfn "    Making API call to: %s/api/spaces" baseUrl
                    let spacesResult = OctopusClient.getSpaces config |> Async.AwaitTask |> Async.RunSynchronously
                    printfn "📋 Found %d space(s):" spacesResult.Length
                    spacesResult |> List.iteri (fun i space ->
                        printfn "    %d. %s (ID: %s)" (i + 1) space.Name space.Id)

                    if spacesResult.Length = 0 then
                        printfn "⚠️  No spaces returned - this might indicate:"
                        printfn "    - Using an older Octopus version without spaces"
                        printfn "    - API key lacks permission to list spaces"
                        printfn "    - Need to query default space directly"

                    // Get projects (only if connection was successful)
                    printfn "🔍 Querying projects..."
                    let targetSpace = spaceId |> Option.defaultValue "Default"
                    let projectApiUrl = 
                        if spaceId.IsSome then 
                            sprintf "%s/api/%s/projects" baseUrl spaceId.Value
                        else 
                            sprintf "%s/api/projects" baseUrl
                    printfn "    Making API call to: %s" projectApiUrl

                    let gitRepos = GitOperations.getCachedRepositories()
                    let projects = OctopusClient.getProjectsWithGitInfo config gitRepos |> Async.AwaitTask |> Async.RunSynchronously
                    
                    printfn "📋 Found %d Octopus project(s):" projects.Length
                if projects.Length = 0 then
                    printfn "    No projects found in the target space."
                    if spaceId.IsSome then
                        printfn "    💡 Try using just the base URL without space ID for default space"
                    else
                        printfn "    💡 This might be an older Octopus version or permission issue"

                // Display projects with detailed information
                for i, project in projects |> List.indexed do
                    let description = if String.IsNullOrEmpty(project.Description) then "(no description)" else project.Description
                    if project.Name.Contains("nextgen", StringComparison.OrdinalIgnoreCase) then
                        printfn "%d. %s ⭐ NEXTGEN PROJECT" (i + 1) project.Name
                        printfn "    ID: %s" project.Id
                        printfn "    Space: %s" project.SpaceName
                        printfn "    Description: %s" description
                        printfn "    Disabled: %b" project.IsDisabled

                        // Show git repository information
                        match project.GitRepoUrl with
                        | Some gitUrl ->
                            // Find matching GitHub repositories
                            let matchingGitHubRepos = OctopusOperations.findMatchingGitHubRepos [project] (GitOperations.getCachedRepositories())
                            if matchingGitHubRepos.Length > 0 then
                                printfn "    🔗 GitHub Repositories:"
                                for (githubUrl, repoName, localRepoUrl) in matchingGitHubRepos do
                                    printfn "        📁 %s: %s" repoName githubUrl
                                    printfn "           Local: %s" localRepoUrl
                            else
                                printfn "    🔗 Git Repository: %s (no local match found)" gitUrl
                        | None ->
                            printfn "    🔗 Git Repository: (not configured)"

                // Check for git repositories and show matching information
                let totalGitRepos = GitOperations.getCachedRepositories()
                let matchedGitRepoResults = 
                    projects
                    |> List.collect (fun project ->
                        match project.GitRepoUrl with
                        | Some gitUrl when gitUrl.Contains("github.com") ->
                            OctopusOperations.findMatchingGitHubRepos [project] totalGitRepos
                        | _ -> [])
                
                let allMatchedGitRepos = 
                    matchedGitRepoResults
                    |> List.map (fun (_, repoName, repoUrl) -> (repoName, repoUrl))
                    |> Set.ofList
                
                // Find unmatched repos
                let unmatchedRepos = 
                    totalGitRepos 
                    |> List.filter (fun repo -> not (allMatchedGitRepos.Contains(repo)))
                
                if unmatchedRepos.Length > 0 then
                    printfn "\n📁 Local Git Repositories (%d total, %d matched with Octopus projects):" totalGitRepos.Length allMatchedGitRepos.Count
                    printfn "🔗 Matched repositories are shown above with their Octopus projects"
                    printfn "📂 Unmatched local repositories (%d):" unmatchedRepos.Length
                    for (name, url) in unmatchedRepos do
                        printfn "  - %s: %s" name url
                else
                    printfn "\n📁 All %d local git repositories are matched with Octopus projects! 🎉" totalGitRepos.Length

            with
            | ex -> 
                printfn "❌ Error connecting to Octopus Deploy:"
                printfn "    %s" ex.Message
                if ex.InnerException <> null then
                    printfn "    Inner exception: %s" ex.InnerException.Message
                printfn "💡 Troubleshooting tips:"
                printfn "    - Ensure the server URL is correct (just base URL, not browser URL)"
                printfn "    - Verify your API key has sufficient permissions"
                printfn "    - Check if the server is accessible from your network"
                printfn "    - Try using just the base URL: https://octopus.rbxd.ds/"
    
        | Some url, None ->
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Octopus Deploy Integration"
            printfn "%s" (String.replicate 50 "=")
            printfn "Octopus URL found: %s" url
            printfn "❌ No API key provided. Use --octopus-api-key <key> or set OCTO_API_KEY environment variable."
        
        | None, Some _ ->
            printfn "\n❌ Octopus API key provided but no URL. Use --octopus-url <url> or set OCTOPUS_URL environment variable."
        
        | None, None ->
            printfn "\nNo Octopus integration configured."
            printfn "Use --octopus-url <url> --octopus-api-key <key> or set OCTOPUS_URL and OCTO_API_KEY environment variables."
            printfn ""
            printfn "URL Examples:"
            printfn "  Base server: https://octopus.rbxd.ds/"
            printfn "  With space: https://octopus.rbxd.ds/app#/Spaces-1"
            printfn "  Environment variable: $env:OCTOPUS_URL=\"https://octopus.rbxd.ds/\""
            printfn "  Environment variable: $env:OCTO_API_KEY=\"API-YOURKEY\""
            printfn ""
            printfn "Additional Options:"
            printfn "  --pull-repos              Perform git pull on all repositories (moved to end for performance)"
            printfn "  --index-files             Index Terraform and PowerShell files for fast search"
            printfn "  --index-stats             Show file indexing statistics and database info"
            printfn "  --cleanup-db              Clean up orphaned database entries and compact database"
            printfn "  --skip-deployment-steps    Skip deployment step analysis (useful if API key lacks permissions)"
            printfn "  --debug, -d               Enable debug logging (shows access denied and other detailed errors)"
            printfn "  --search-variable <pattern> Search for Octopus variables matching pattern"
            printfn "  --analyze-step <project/step> Analyze specific deployment step"

        // GitHub Integration Demo
        printfn "\n%s" (String.replicate 50 "=")
        printfn "GitHub Integration Demo"
        printfn "%s" (String.replicate 50 "=")

        // Check for GitHub token
        let githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")

        if not (String.IsNullOrEmpty(githubToken)) then
            printfn "GitHub integration enabled with token authentication"
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

        // Perform git pull operations if requested (using parallel processing)
        GitOperations.performParallelGitPulls repoInfos

        // Perform file indexing if requested
        FileIndexingOperations.performFileIndexing repoInfos

        // Show indexing statistics if requested
        if CommandLineArgs.shouldShowIndexStats() then
            FileIndex.getIndexingStats()
        
        // Cleanup database if requested
        if CommandLineArgs.shouldCleanupDatabase() then
            printfn "\n🧹 Performing database cleanup..."
            FileIndex.cleanupDatabase()

    // Perform text search if requested (can work without repoInfos)
    SearchOperations.performTextSearch ()

    printfn "\nSearch completed."

// Run the main function
main ()