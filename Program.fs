namespace BDevHud

open System
open System.IO
open IO.Adapter
open Octo.Adapter
open SqlLite.Adapter

// =============================================================================
// OCTOPUS OPERATIONS MODULE  
// =============================================================================

module OctopusOperations =
    
    /// Get Octopus URL from environment variables
    let getOctopusUrl () : string option =
        let octopusUrl = Environment.GetEnvironmentVariable("OCTOPUS_URL")
        if String.IsNullOrEmpty(octopusUrl) then None else Some octopusUrl

    /// Get Octopus API key from environment variables
    let getOctopusApiKey () : string option =
        let apiKey = Environment.GetEnvironmentVariable("OCTO_API_KEY")
        if String.IsNullOrEmpty(apiKey) then None else Some apiKey

    /// Format API key for display (show first 4 and last 4 characters)
    let formatApiKeyForDisplay (apiKey: string) : string =
        if apiKey.Length <= 8 then
            "****"
        else
            let start = apiKey.Substring(0, 4)
            let ending = apiKey.Substring(apiKey.Length - 4)
            sprintf "%s****%s" start ending

    /// Find GitHub repositories that match Octopus project git URLs
    let findMatchingGitHubRepos (projects: OctopusClient.OctopusProjectWithGit list) (gitRepos: (string * string) list) : (string * string * string) list =
        projects
        |> List.choose (fun project ->
            match project.GitRepoUrl with
            | Some gitUrl when gitUrl.Contains("github.com") ->
                // Try to find a matching local repository
                let matchingRepo = 
                    gitRepos 
                    |> List.tryFind (fun (_, repoUrl) -> 
                        repoUrl.Contains(gitUrl.Split('/') |> Array.last |> fun s -> s.Replace(".git", "")))
                
                match matchingRepo with
                | Some (repoName, localUrl) -> Some (gitUrl, repoName, localUrl)
                | None -> None
            | _ -> None)
# BDevHud

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
// INTEGRATION HANDLERS
// =============================================================================

module IntegrationHandlers =
    /// Handle GitHub integration if requested
    let handleGitHubIntegration () =
        if not (CommandLineArgs.shouldRunGitHub()) then
            printfn "⏭️  Skipping GitHub integration (use --github to enable)"
        else
            printfn "\n%s" (String.replicate 50 "=")
            printfn "GitHub Integration"
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

// =============================================================================
// MAIN PROGRAM ENTRY POINT
// =============================================================================

module Program =
    /// Main program entry point
    let main () =
        printfn "Git Folder Spider Search"
        printfn "======================="

        // Check if we can skip git operations for search-only mode
        let skipGitOps = CommandLineArgs.shouldSkipGitOperations()
        
        let repoInfos = 
            if skipGitOps then
                printfn "Search-only mode: Skipping git repository discovery"
                []
            else
                // Get root directory and debug mode
                let rootDirectory = CommandLineArgs.getRootDirectory()
                let debugMode = CommandLineArgs.shouldDebugLog()

                // Find git folders and display them
                let gitFolders = GitOperations.findGitFolders rootDirectory debugMode
                GitOperations.displayGitFolders gitFolders

        // Handle integrations based on command line flags
        IntegrationHandlers.handleGitHubIntegration ()
        
        // Perform git pull operations if requested (using parallel processing)
        GitOperations.performParallelGitPulls repoInfos

        // Perform file indexing if requested
        if CommandLineArgs.shouldIndexFiles() then
            FileIndexingOperations.performFileIndexing repoInfos

        // Show indexing statistics if requested
        if CommandLineArgs.shouldShowIndexStats() then
            FileIndex.getIndexingStats()
        
        // Cleanup database if requested
        if CommandLineArgs.shouldCleanupDatabase() then
            printfn "\n🧹 Performing database cleanup..."
            FileIndex.cleanupDatabase()

        // Cleanup blacklisted files if requested
        if CommandLineArgs.shouldCleanupBlacklistedFiles() then
            printfn "\n🧹 Cleaning up blacklisted files from database..."
            FileIndex.cleanupBlacklistedFiles()

        // Perform text search if requested (can work without repoInfos)
        SearchOperations.performTextSearch ()

        printfn "\nOperations completed."

    // Run the main function
    main ()