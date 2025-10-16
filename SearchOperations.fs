namespace BDevHud

open System
open IO.Adapter
open SqlLite.Adapter

/// Text search operations using trigram matching
module SearchOperations =

    /// Perform text search using trigram matching if requested
    let performTextSearch () =
        match CommandLineArgs.getSearchTerm() with
        | Some searchTerm ->
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Trigram Text Search"
            printfn "%s" (String.replicate 50 "=")
            printfn "Searching for: '%s'" searchTerm
            
            // Check if we have indexed data
            if FileIndex.hasIndexedData() then
                let (fileCount, trigramCount, repos) = FileIndex.getIndexStats()
                printfn "Searching %d indexed files with %d trigrams across %d repositories..." fileCount trigramCount repos.Length
                
                let results = FileIndex.searchText searchTerm
                
                if results.Length > 0 then
                    printfn "\nFound %d matching file(s):" results.Length
                    
                    // Group results by repository
                    let groupedResults = 
                        results
                        |> List.groupBy (fun file -> file.RepoPath)
                        |> List.sortBy (fun (repoPath, _) -> RepositoryDiscovery.getFileName repoPath)
                    
                    // Display results grouped by repository with last pull info
                    groupedResults
                    |> List.iteri (fun repoIndex (repoPath, filesInRepo) ->
                        let repoName = RepositoryDiscovery.getFileName repoPath
                        printfn "\n📁 Repository: %s (%d file(s))" repoName filesInRepo.Length
                        
                        // Check current branch and display warning if not on master/main
                        let (branchSuccess, branchName, isMainBranch) = GitAdapter.isOnMainBranch repoPath
                        if branchSuccess && not isMainBranch then
                            printfn "    ⚠️  Branch warning: Currently on '%s' (not master/main)" branchName
                        
                        // Get last successful pull information
                        let cachedRepo = GitCache.getCachedRepo repoPath
                        match cachedRepo with
                        | Some cached ->
                            match cached.LastSuccessfulPull with
                            | Some lastPull ->
                                let elapsed = DateTime.UtcNow - lastPull
                                if elapsed.TotalDays >= 1.0 then
                                    printfn "    🔄 Last successful pull: %.1f days ago" elapsed.TotalDays
                                elif elapsed.TotalHours >= 1.0 then
                                    printfn "    🔄 Last successful pull: %.1f hours ago" elapsed.TotalHours
                                else
                                    printfn "    🔄 Last successful pull: %.1f minutes ago" elapsed.TotalMinutes
                            | None ->
                                match cached.LastPullAttempt with
                                | Some lastAttempt ->
                                    let elapsed = DateTime.UtcNow - lastAttempt
                                    printfn "    ⚠️  Last pull attempt: %.1f minutes ago (no successful pulls recorded)" elapsed.TotalMinutes
                                | None ->
                                    printfn "    ❓ No pull history recorded"
                        | None ->
                            printfn "    ❓ Repository not in cache"
                        
                        printfn "    📂 Path: %s" repoPath
                        
                        // Display files in this repository
                        filesInRepo
                        |> List.iteri (fun fileIndex file ->
                            printfn "\n    [%d.%d] %s file:" (repoIndex + 1) (fileIndex + 1) (file.FileType.ToUpper())
                            printfn "        📄 File: %s" (RepositoryDiscovery.getFileName file.FilePath)
                            printfn "        🔗 Full path: %s" file.FilePath
                            printfn "        📏 Size: %d bytes" file.FileSize))
                else
                    printfn "\nNo files found containing '%s'" searchTerm
            else
                printfn "❌ No indexed data found. Use --index-files flag first to index repository files."
                printfn "\n💡 Use --index-files flag to index Terraform and PowerShell files for search"
        | None ->
            printfn "\n💡 Use --search=<term> to search indexed files"