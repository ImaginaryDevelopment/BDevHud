namespace BDevHud

open System
open IO.Adapter
open SqlLite.Adapter

/// Text search operations using trigram matching
module SearchOperations =

    /// Display git file search results
    let private displayGitResults (searchTerm: string) (results: IndexedFile list) =
        if results.Length > 0 then
            printfn "\nFound %d matching file(s) in Git repositories:" results.Length
            
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
                    printfn "        � Size: %d bytes" file.FileSize))
        else
            printfn "\nNo git files found containing '%s'" searchTerm

    /// Display Octopus search results
    let private displayOctopusResults (searchTerm: string) (results: OctopusStepResult list) =
        if results.Length > 0 then
            printfn "\nFound %d matching Octopus deployment step(s):" results.Length
            
            // Group results by project
            let groupedResults = 
                results
                |> List.groupBy (fun step -> step.ProjectName)
                |> List.sortBy (fun (projectName, _) -> projectName)
            
            // Display results grouped by project
            groupedResults
            |> List.iteri (fun projectIndex (projectName, stepsInProject) ->
                printfn "\n🐙 Project: %s (%d step(s))" projectName stepsInProject.Length
                
                // Display steps in this project
                stepsInProject
                |> List.iteri (fun stepIndex step ->
                    printfn "\n    [%d.%d] Step: %s" (projectIndex + 1) (stepIndex + 1) step.StepName
                    printfn "        � Action Type: %s" step.ActionType
                    printfn "        🆔 Step ID: %s" step.StepId
                    printfn "        🕐 Indexed: %s" (step.IndexedAt.ToString("yyyy-MM-dd HH:mm:ss"))
                    
                    if step.Properties.Count > 0 then
                        printfn "        📋 Properties (%d):" step.Properties.Count
                        step.Properties
                        |> Map.toList
                        |> List.sortBy fst
                        |> List.iter (fun (key, value) ->
                            let displayValue = 
                                if value.Length > 100 then
                                    value.Substring(0, 97) + "..."
                                else
                                    value
                            printfn "           • %s: %s" key displayValue)))
        else
            printfn "\nNo Octopus deployment steps found containing '%s'" searchTerm

    /// Search only git files
    let private searchGitOnly (searchTerm: string) =
        printfn "\n%s" (String.replicate 50 "=")
        printfn "Git File Search"
        printfn "%s" (String.replicate 50 "=")
        printfn "Searching for: '%s'" searchTerm
        
        if FileIndex.hasIndexedData() then
            let (fileCount, trigramCount, repos) = FileIndex.getIndexStats()
            printfn "Searching %d indexed files with %d trigrams across %d repositories..." fileCount trigramCount repos.Length
            
            let results = FileIndex.searchText searchTerm
            displayGitResults searchTerm results
        else
            printfn "❌ No git indexed data found. Use --index-files flag first to index repository files."
            printfn "\n💡 Use --index-files flag to index Terraform and PowerShell files for search"

    /// Search only Octopus steps
    let private searchOctopusOnly (searchTerm: string) =
        printfn "\n%s" (String.replicate 50 "=")
        printfn "Octopus Deployment Step Search"
        printfn "%s" (String.replicate 50 "=")
        printfn "Searching for: '%s'" searchTerm
        
        let (stepCount, trigramCount, projects) = FileIndex.getOctopusIndexStats(FileIndex.dbPath)
        if stepCount > 0 then
            printfn "Searching %d indexed steps with %d trigrams across %d projects..." stepCount trigramCount projects.Length
            
            let results = FileIndex.searchOctopusSteps searchTerm
            displayOctopusResults searchTerm results
        else
            printfn "❌ No Octopus indexed data found. Use --index-octopus flag first to index deployment steps."
            printfn "\n💡 Use --index-octopus flag to index Octopus Deploy deployment steps for search"

    /// Search both git files and Octopus steps
    let private searchBoth (searchTerm: string) =
        printfn "\n%s" (String.replicate 50 "=")
        printfn "Combined Search (Git + Octopus)"
        printfn "%s" (String.replicate 50 "=")
        printfn "Searching for: '%s'" searchTerm
        
        // Search git files
        if FileIndex.hasIndexedData() then
            let (fileCount, trigramCount, repos) = FileIndex.getIndexStats()
            printfn "\n📁 Git Files: Searching %d indexed files with %d trigrams across %d repositories..." fileCount trigramCount repos.Length
            
            let gitResults = FileIndex.searchText searchTerm
            displayGitResults searchTerm gitResults
        else
            printfn "\n📁 Git Files: No indexed data found."
        
        // Search Octopus steps
        let (stepCount, trigramCount, projects) = FileIndex.getOctopusIndexStats(FileIndex.dbPath)
        if stepCount > 0 then
            printfn "\n🐙 Octopus Steps: Searching %d indexed steps with %d trigrams across %d projects..." stepCount trigramCount projects.Length
            
            let octoResults = FileIndex.searchOctopusSteps searchTerm
            displayOctopusResults searchTerm octoResults
        else
            printfn "\n🐙 Octopus Steps: No indexed data found."

    /// Perform text search using trigram matching if requested
    let performTextSearch () =
        // Check for specific search types first
        match CommandLineArgs.getSearchGitTerm() with
        | Some searchTerm ->
            searchGitOnly searchTerm
        | None ->
            match CommandLineArgs.getSearchOctoTerm() with
            | Some searchTerm ->
                searchOctopusOnly searchTerm
            | None ->
                // Check for general search (both databases)
                match CommandLineArgs.getSearchTerm() with
                | Some searchTerm ->
                    searchBoth searchTerm
                | None ->
                    printfn "\n💡 Search options:"
                    printfn "   --search=<term>      : Search both Git files and Octopus steps"
                    printfn "   --search-git=<term>  : Search only Git files"
                    printfn "   --search-octo=<term> : Search only Octopus deployment steps"