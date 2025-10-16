namespace BDevHud

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open IO.Adapter
open SqlLite.Adapter

/// Git operations including repository discovery and pull operations
module GitOperations =

    /// Find git folders using the IO.Adapter
    let findGitFolders (rootDirectory: string option) (debugMode: bool) : string array =
        RepositoryDiscovery.findGitFolders rootDirectory debugMode

    /// Get cached repositories from git cache
    let getCachedRepositories () : (string * string) list =
        let cachedRepos = GitCache.getAllCachedRepos ()
        cachedRepos |> List.map (fun repo -> (repo.RepoName, repo.RepoUrl))

    /// Display results of git folder search with remote information (no git pull by default)
    let displayGitFolders (gitFolders: string[]) : GitRepoInfo list =
        if gitFolders.Length = 0 then
            printfn "No .git folders found."
            []
        else
            printfn "\nFound %d .git folder(s):" gitFolders.Length

            let mutable repoInfos = []

            gitFolders
            |> Array.iteri (fun i folder ->
                let parentDir = RepositoryDiscovery.getParentDirectory folder
                printfn "%d. %s" (i + 1) parentDir

                // Get and display remote information
                let remoteResult = GitAdapter.getRemote parentDir

                if remoteResult.Success then
                    match remoteResult.Remotes with
                    | remote :: _ ->
                        // Use the first remote found
                        let decodedUrl = System.Web.HttpUtility.UrlDecode(remote.Url)
                        
                        let operationType =
                            if remote.Url.Contains("git@") then "SSH"
                            elif remote.Url.Contains("https://") then "HTTPS" 
                            else "Other"
                        
                        printfn "    Remote: %s (%s)" decodedUrl operationType
                        
                        // Add to repo info list
                        let repoInfo = GitRepoInfo.create folder (Some remote.Url)
                        repoInfos <- repoInfo :: repoInfos
                    | [] ->
                        printfn "    Remote: (none configured)"
                        let repoInfo = GitRepoInfo.create folder None
                        repoInfos <- repoInfo :: repoInfos
                else
                    printfn "    Remote: (error: %s)" remoteResult.Error
                    let repoInfo = GitRepoInfo.create folder None
                    repoInfos <- repoInfo :: repoInfos)

            List.rev repoInfos

    /// Message types for parallel pull operations
    type PullMessage =
        | PullStarted of repoName: string * path: string
        | PullCompleted of repoName: string * success: bool * message: string
        | PullSkipped of repoName: string * reason: string

    /// Perform parallel git pull operations with concurrent messaging
    let performParallelGitPulls (repoInfos: GitRepoInfo list) =
        if CommandLineArgs.shouldPullRepos() then
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Parallel Git Pull Operations"
            printfn "%s" (String.replicate 50 "=")
            printfn "Checking %d repositories for pull eligibility..." repoInfos.Length

            let messageQueue = ConcurrentQueue<PullMessage>()
            let mutable successCount = 0
            let mutable failureCount = 0
            let mutable skippedCount = 0

            // Filter repos that need pulling
            let reposToProcess =
                repoInfos
                |> List.choose (fun repoInfo ->
                    match repoInfo.RemoteUrl with
                    | Some url ->
                        // Check blacklist first
                        if BlacklistConfig.shouldExcludeFromPull repoInfo.RepoName repoInfo.Path then
                            let reason = BlacklistConfig.getPullBlacklistReason repoInfo.RepoName repoInfo.Path
                            messageQueue.Enqueue(PullSkipped(repoInfo.RepoName, reason |> Option.defaultValue "blacklisted"))
                            None
                        else
                            let cachedRepo = GitCache.getCachedRepo repoInfo.Path
                            let shouldPull = 
                                match cachedRepo with
                                | Some cached -> GitCache.shouldAttemptPull cached.LastPullAttempt
                                | None -> true

                            if shouldPull then
                                Some (repoInfo, url, cachedRepo)
                            else
                                let timeSinceLastAttempt = 
                                    match cachedRepo with
                                    | Some cached ->
                                        match cached.LastPullAttempt with
                                        | Some lastAttempt ->
                                            let elapsed = DateTime.UtcNow - lastAttempt
                                            sprintf "%.1f minutes ago" elapsed.TotalMinutes
                                        | None -> "never"
                                    | None -> "never"
                                
                                messageQueue.Enqueue(PullSkipped(repoInfo.RepoName, $"30-minute cooldown: {timeSinceLastAttempt}"))
                                None
                    | None ->
                        messageQueue.Enqueue(PullSkipped(repoInfo.RepoName, "no remote URL"))
                        None)

            // Limit concurrency to prevent overwhelming the system - process in batches of 5
            let batchSize = 5
            let batches = 
                reposToProcess
                |> List.chunkBySize batchSize

            // Process batches sequentially, but repos within each batch in parallel
            for batch in batches do
                let pullTasks =
                    batch
                    |> List.map (fun (repoInfo, url, cachedRepo) ->
                        Task.Run(fun () ->
                            try
                                messageQueue.Enqueue(PullStarted(repoInfo.RepoName, repoInfo.Path))

                                // Update cache with attempt timestamp BEFORE trying pull
                                let cacheEntry = {
                                    Path = repoInfo.Path
                                    RepoName = repoInfo.RepoName
                                    RepoUrl = url
                                    LastPullAttempt = Some DateTime.UtcNow
                                    LastSuccessfulPull = cachedRepo |> Option.bind (fun c -> c.LastSuccessfulPull)
                                }
                                GitCache.upsertRepo cacheEntry

                                // Run git pull for this repository
                                let (pullSuccess, pullError) = GitAdapter.gitPull repoInfo.Path

                                if pullSuccess then
                                    GitCache.updateLastSuccessfulPull repoInfo.Path
                                    messageQueue.Enqueue(PullCompleted(repoInfo.RepoName, true, "Pull successful"))
                                else
                                    messageQueue.Enqueue(PullCompleted(repoInfo.RepoName, false, $"Pull failed: {pullError}"))
                            with
                            | ex ->
                                messageQueue.Enqueue(PullCompleted(repoInfo.RepoName, false, $"Exception: {ex.Message}"))
                        ))

                // Wait for current batch to complete before starting next batch
                let batchTask = Task.WhenAll(pullTasks)
                
                // Monitor messages for this batch
                while not batchTask.IsCompleted || not messageQueue.IsEmpty do
                    let mutable message = Unchecked.defaultof<PullMessage>
                    if messageQueue.TryDequeue(&message) then
                        match message with
                        | PullStarted(repoName, path) ->
                            printfn $"\n🔄 Starting pull for: {repoName}"
                            printfn $"    Path: {path}"
                        | PullCompleted(repoName, success, msg) -> 
                            if success then
                                printfn $"    ✅ {repoName}: {msg}"
                                successCount <- successCount + 1
                            else
                                printfn $"    ❌ {repoName}: {msg}"
                                failureCount <- failureCount + 1
                        | PullSkipped(repoName, reason) ->
                            printfn $"⏭️  Skipping {repoName} ({reason})"
                            skippedCount <- skippedCount + 1
                    
                    if not batchTask.IsCompleted then
                        System.Threading.Thread.Sleep(100)

                // Wait for batch to complete
                batchTask.Wait()

            // Process any remaining messages from initial filtering
            while not messageQueue.IsEmpty do
                let mutable message = Unchecked.defaultof<PullMessage>
                if messageQueue.TryDequeue(&message) then
                    match message with
                    | PullSkipped(repoName, reason) ->
                        printfn $"⏭️  Skipping {repoName} ({reason})"
                        skippedCount <- skippedCount + 1
                    | _ -> () // Should not happen at this stage

            let totalAttempted = successCount + failureCount
            printfn $"\nParallel git pull operations completed:"
            printfn $"  ✅ Successful: {successCount}"
            printfn $"  ❌ Failed: {failureCount}"  
            printfn $"  ⏭️  Skipped: {skippedCount}"
            printfn $"  📊 Total Attempted: {totalAttempted} of {repoInfos.Length} repositories"
        else
            printfn "\n💡 Use --pull-repos flag to perform git pull operations on all repositories"

    /// Perform git pull operations on repositories if requested (legacy sequential version)
    let performGitPulls (repoInfos: GitRepoInfo list) =
        if CommandLineArgs.shouldPullRepos() then
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Git Pull Operations"
            printfn "%s" (String.replicate 50 "=")
            printfn "Checking %d repositories for pull eligibility..." repoInfos.Length

            let mutable pullCount = 0
            let mutable skippedCount = 0

            repoInfos
            |> List.iteri (fun i repoInfo ->
                match repoInfo.RemoteUrl with
                | Some url ->
                    // Check cache to see if we should attempt a pull (30-minute cooldown)
                    let cachedRepo = GitCache.getCachedRepo repoInfo.Path
                    let shouldPull = 
                        match cachedRepo with
                        | Some cached -> GitCache.shouldAttemptPull cached.LastPullAttempt
                        | None -> true // No cache entry, should attempt

                    if shouldPull then
                        printfn "\n[%d/%d] Pulling: %s" (i + 1) repoInfos.Length repoInfo.RepoName
                        printfn "    Path: %s" repoInfo.Path

                        // Update cache with attempt timestamp BEFORE trying pull
                        let cacheEntry = {
                            Path = repoInfo.Path
                            RepoName = repoInfo.RepoName
                            RepoUrl = url
                            LastPullAttempt = Some DateTime.UtcNow
                            LastSuccessfulPull = cachedRepo |> Option.bind (fun c -> c.LastSuccessfulPull)
                        }
                        GitCache.upsertRepo cacheEntry

                        // Run git pull for this repository
                        let (pullSuccess, pullError) = GitAdapter.gitPull repoInfo.Path

                        if pullSuccess then
                            printfn "    ✅ Pull successful"
                            GitCache.updateLastSuccessfulPull repoInfo.Path
                            pullCount <- pullCount + 1
                        else
                            printfn "    ❌ Pull failed: %s" pullError
                            pullCount <- pullCount + 1 // Still count as attempted
                    else
                        let timeSinceLastAttempt = 
                            match cachedRepo with
                            | Some cached ->
                                match cached.LastPullAttempt with
                                | Some lastAttempt ->
                                    let elapsed = DateTime.UtcNow - lastAttempt
                                    sprintf "%.1f minutes ago" elapsed.TotalMinutes
                                | None -> "never"
                            | None -> "never"
                        
                        printfn "[%d/%d] Skipping %s (last attempt: %s)" (i + 1) repoInfos.Length repoInfo.RepoName timeSinceLastAttempt
                        skippedCount <- skippedCount + 1
                | None ->
                    printfn "[%d/%d] Skipping %s (no remote URL)" (i + 1) repoInfos.Length repoInfo.RepoName)

            printfn "\nGit pull operations completed: %d attempted, %d skipped (30-minute cooldown)" pullCount skippedCount
        else
            printfn "\n💡 Use --pull-repos flag to perform git pull operations on all repositories"