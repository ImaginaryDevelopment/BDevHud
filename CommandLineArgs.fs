namespace BDevHud

open System
open System.IO

/// Command line argument parsing functionality
module CommandLineArgs =
    
    /// Get command line arguments
    let private getArgs () = Environment.GetCommandLineArgs()
    
    /// Check if debug logging should be enabled
    let shouldDebugLog () =
        let args = getArgs ()
        args |> Array.contains "--debug" || args |> Array.contains "-d"

    /// Check if git pull operations should be performed
    let shouldPullRepos () =
        let args = getArgs ()
        args |> Array.contains "--pull-repos" || args |> Array.contains "--pull" || args |> Array.contains "--git-pull"

    /// Check if file indexing should be performed
    let shouldIndexFiles () =
        let args = getArgs ()
        args |> Array.contains "--index-files" || args |> Array.contains "--index"

    /// Check if Octopus Deploy integration should be performed
    let shouldRunOctopus () =
        let args = getArgs ()
        args |> Array.contains "--octopus"

    /// Check if Octopus Deploy data should be indexed
    let shouldIndexOctopus () =
        let args = getArgs ()
        args |> Array.contains "--index-octopus" || args |> Array.contains "--octopus-index"

    /// Check if GitHub integration should be performed
    let shouldRunGitHub () =
        let args = getArgs ()
        args |> Array.contains "--github"

    /// Get search term from command line args (searches both git and octopus)
    let getSearchTerm () =
        let args = getArgs ()
        // Look for --search=term pattern
        args
        |> Array.tryFind (fun arg -> arg.StartsWith("--search="))
        |> Option.map (fun arg -> arg.Substring(9)) // Remove "--search=" prefix

    /// Get git-only search term from command line args
    let getSearchGitTerm () =
        let args = getArgs ()
        // Look for --search-git=term pattern
        args
        |> Array.tryFind (fun arg -> arg.StartsWith("--search-git="))
        |> Option.map (fun arg -> arg.Substring(13)) // Remove "--search-git=" prefix

    /// Get octopus-only search term from command line args
    let getSearchOctoTerm () =
        let args = getArgs ()
        // Look for --search-octo=term pattern
        args
        |> Array.tryFind (fun arg -> arg.StartsWith("--search-octo="))
        |> Option.map (fun arg -> arg.Substring(14)) // Remove "--search-octo=" prefix

    /// Get Octopus URL from command line args
    let getOctopusUrl () =
        let args = getArgs ()
        args
        |> Array.tryFind (fun arg -> arg.StartsWith("--octopus-url="))
        |> Option.map (fun arg -> arg.Substring(14)) // Remove "--octopus-url=" prefix

    /// Get Octopus API key from command line args  
    let getOctopusApiKey () =
        let args = getArgs ()
        args
        |> Array.tryFind (fun arg -> arg.StartsWith("--octopus-api-key="))
        |> Option.map (fun arg -> arg.Substring(18)) // Remove "--octopus-api-key=" prefix

    /// Get GitHub token from command line args
    let getGitHubToken () =
        let args = getArgs ()
        args
        |> Array.tryFind (fun arg -> arg.StartsWith("--github-token="))
        |> Option.map (fun arg -> arg.Substring(15)) // Remove "--github-token=" prefix

    /// Check if we should list GitHub repositories
    let shouldListGitHubRepos () =
        let args = getArgs ()
        args |> Array.contains "--list-github-repos" || args |> Array.contains "--github-repos"

    /// Get GitHub organization filter from command line args
    let getGitHubOrg () =
        let args = getArgs ()
        args
        |> Array.tryFind (fun arg -> arg.StartsWith("--github-org="))
        |> Option.map (fun arg -> arg.Substring(13)) // Remove "--github-org=" prefix

    /// Check if we should display GitHub repositories from database
    let shouldDisplayGitHubRepos () =
        let args = getArgs ()
        args |> Array.contains "--display-github-repos" || args |> Array.contains "--show-github-repos"

    /// Get root directory from command line args or environment variable
    let getRootDirectory () =
        let args = getArgs ()
        
        // Check for directory argument (first non-flag argument after program name)
        let directoryArg =
            args
            |> Array.skip 1 // Skip program name
            |> Array.tryFind (fun arg -> not (arg.StartsWith("--")) && not (arg.StartsWith("-")))
        
        match directoryArg with
        | Some rootPath ->
            printfn "Using command line root directory: %s" rootPath
            Some rootPath
        | None ->
            // Check DEVROOT environment variable first
            let devRoot = Environment.GetEnvironmentVariable("DEVROOT")
            if not (String.IsNullOrEmpty(devRoot)) then
                printfn "Using DEVROOT environment variable: %s" devRoot
                Some devRoot
            else
                // Check DEVDIR environment variable as fallback
                let devDir = Environment.GetEnvironmentVariable("DEVDIR")
                if not (String.IsNullOrEmpty(devDir)) then
                    printfn "Using DEVDIR environment variable: %s" devDir
                    Some devDir
                else
                    printfn "No root directory specified. Searching all local drives."
                    None

    /// Check if indexing statistics should be shown
    let shouldShowIndexStats () =
        let args = getArgs ()
        args |> Array.contains "--index-stats" || args |> Array.contains "--stats"

    /// Check if database cleanup should be performed
    let shouldCleanupDatabase () =
        let args = getArgs ()
        args |> Array.contains "--cleanup-db" || args |> Array.contains "--cleanup"

    /// Check if blacklisted files cleanup should be performed
    let shouldCleanupBlacklistedFiles () =
        let args = getArgs ()
        args |> Array.contains "--cleanup-blacklisted" || args |> Array.contains "--cleanup-bl"

    /// Get update remote parameters (search string and target string)
    let getUpdateRemoteParams () =
        let args = getArgs ()
        // Look for --update-remote=search,target pattern
        args
        |> Array.tryFind (fun arg -> arg.StartsWith("--update-remote="))
        |> Option.bind (fun arg -> 
            let value = arg.Substring(16) // Remove "--update-remote=" prefix
            let parts = value.Split(',')
            if parts.Length = 2 then
                Some (parts.[0], parts.[1])
            else
                None)

    /// Check if we should skip git operations (search-only mode or database-only operations)
    let shouldSkipGitOperations () =
        let hasAnySearch = getSearchTerm().IsSome || getSearchGitTerm().IsSome || getSearchOctoTerm().IsSome
        let hasGitHubRepoListing = shouldListGitHubRepos()
        let hasGitHubRepoDisplay = shouldDisplayGitHubRepos()
        
        if hasGitHubRepoListing || hasGitHubRepoDisplay then
            true
        elif hasAnySearch then
            not (shouldPullRepos() || shouldIndexFiles())
        else
            let databaseOnlyOps = shouldShowIndexStats() || shouldCleanupDatabase() || shouldCleanupBlacklistedFiles()
            let repoRequiredOps = shouldPullRepos() || shouldIndexFiles()
            databaseOnlyOps && not repoRequiredOps