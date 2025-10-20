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

    /// Check if we should skip git operations (search-only mode or database-only operations)
    let shouldSkipGitOperations () =
        let hasAnySearch = getSearchTerm().IsSome || getSearchGitTerm().IsSome || getSearchOctoTerm().IsSome
        match hasAnySearch with
        | true -> 
            // Skip git ops if ONLY searching (no other operations that need repos)
            not (shouldPullRepos() || shouldIndexFiles())
        | false -> 
            // Skip git ops if ONLY doing database operations that don't need repos
            let databaseOnlyOps = shouldShowIndexStats() || shouldCleanupDatabase() || shouldCleanupBlacklistedFiles()
            let repoRequiredOps = shouldPullRepos() || shouldIndexFiles()
            databaseOnlyOps && not repoRequiredOps