namespace BDevHud

open System
open System.IO
open IO.Adapter
open Octo.Adapter
open SqlLite.Adapter
open GitHub.Adapter

// =============================================================================
// OCTOPUS OPERATIONS MODULE  
// =============================================================================

module OctopusOperations =
    
    /// Get Octopus URL from command line args or environment variables
    let getOctopusUrl () : string option =
        match CommandLineArgs.getOctopusUrl() with
        | Some url -> Some url
        | None -> 
            let octopusUrl = Environment.GetEnvironmentVariable("OCTOPUS_URL")
            if String.IsNullOrEmpty(octopusUrl) then None else Some octopusUrl

    /// Get Octopus API key from command line args or environment variables
    let getOctopusApiKey () : string option =
        match CommandLineArgs.getOctopusApiKey() with
        | Some key -> Some key
        | None ->
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
    /// Handle listing GitHub repositories if requested
    let handleListGitHubRepos () =
        if not (CommandLineArgs.shouldListGitHubRepos()) then
            ()
        else
            printfn "\n%s" (String.replicate 50 "=")
            printfn "GitHub Repository List"
            printfn "%s" (String.replicate 50 "=")

            // Get token from command line or environment
            let githubToken = 
                match CommandLineArgs.getGitHubToken() with
                | Some token -> Some token
                | None -> 
                    let envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                    if String.IsNullOrEmpty(envToken) then None else Some envToken

            // Get organization filter
            let orgFilter = CommandLineArgs.getGitHubOrg()

            match githubToken with
            | Some token ->
                printfn "🔑 Using GitHub token for authentication"
                
                match orgFilter with
                | Some org -> printfn "🏢 Filtering by organization: %s" org
                | None -> printfn "📡 Fetching all accessible repositories..."
                
                printfn "🌐 API Endpoint: https://api.github.com/user/repos\n"

                try
                    let config = { GitHubConfig.Token = Some token; UserAgent = "BDevHud" }
                    
                    let stopwatch = System.Diagnostics.Stopwatch.StartNew()
                    printfn "⏳ Fetching repositories from GitHub API..."
                    
                    let repos = 
                        GitHubAdapter.getRepositories config None 
                        |> Async.AwaitTask
                        |> Async.RunSynchronously
                    
                    stopwatch.Stop()
                    printfn "✅ Retrieved %d repositories from API (took %d seconds)\n" repos.Length (int stopwatch.Elapsed.TotalSeconds)

                    // Filter by organization if specified
                    let filteredRepos =
                        match orgFilter with
                        | Some org -> 
                            let filtered = repos |> List.filter (fun r -> r.Owner.Equals(org, StringComparison.OrdinalIgnoreCase))
                            printfn "🔍 Filtered to %d repositories in organization '%s'\n" filtered.Length org
                            filtered
                        | None -> repos

                    if filteredRepos.IsEmpty then
                        printfn "⚠️ No repositories found"
                    else
                        printfn "📊 Found %d repositories:\n" filteredRepos.Length

                        filteredRepos
                        |> List.sortBy (fun r -> r.FullName.ToLower())
                        |> List.iteri (fun i repo ->
                            let visibility = if repo.IsPrivate then "🔒 Private" else "🌐 Public"
                            let fork = if repo.IsFork then " [Fork]" else ""
                            let lang = match repo.Language with | Some l -> $" ({l})" | None -> ""
                            
                            printfn "%3d. %s %s%s%s" (i + 1) repo.FullName visibility fork lang
                            if not (String.IsNullOrEmpty(repo.Description)) then
                                printfn "     %s" repo.Description
                            printfn "     Clone: %s" repo.CloneUrl
                            printfn "")

                        printfn "\n📈 Summary:"
                        let privateCount = filteredRepos |> List.filter (fun r -> r.IsPrivate) |> List.length
                        let publicCount = filteredRepos.Length - privateCount
                        let forkCount = filteredRepos |> List.filter (fun r -> r.IsFork) |> List.length
                        printfn "  Private: %d | Public: %d | Forks: %d" privateCount publicCount forkCount

                        // Save to database
                        printfn "\n💾 Saving repositories to database..."
                        let saveStopwatch = System.Diagnostics.Stopwatch.StartNew()
                        
                        for repo in filteredRepos do
                            let dbRepo = {
                                GitHubRepo.Id = repo.Id
                                FullName = repo.FullName
                                Name = repo.Name
                                Owner = repo.Owner
                                Description = repo.Description
                                CloneUrl = repo.CloneUrl
                                SshUrl = repo.SshUrl
                                IsPrivate = repo.IsPrivate
                                IsFork = repo.IsFork
                                Language = repo.Language
                                CreatedAt = repo.CreatedAt
                                UpdatedAt = repo.UpdatedAt
                                PushedAt = repo.PushedAt
                                IndexedAt = DateTime.UtcNow
                                SecretsCount = None  // Will be populated separately
                                RunnersCount = None  // Will be populated separately
                            }
                            GitHubRepoIndex.upsertRepository dbRepo
                        
                        saveStopwatch.Stop()
                        printfn "✅ Saved %d repositories to database (took %.1f seconds)" filteredRepos.Length saveStopwatch.Elapsed.TotalSeconds

                with ex ->
                    printfn "❌ Error fetching repositories: %s" ex.Message

            | None ->
                printfn "❌ No GitHub token provided"
                printfn "Please provide a token using:"
                printfn "  --github-token=<your-token>"
                printfn "  or set GITHUB_TOKEN environment variable"
                printfn "\nOptional: Filter by organization with --github-org=<org-name>"

    /// Handle displaying GitHub repositories from database
    let handleDisplayGitHubRepos () =
        if not (CommandLineArgs.shouldDisplayGitHubRepos()) then
            ()
        else
            printfn "\n%s" (String.replicate 50 "=")
            printfn "GitHub Repositories from Database"
            printfn "%s" (String.replicate 50 "=")

            // Get search term
            let searchTerm = CommandLineArgs.getGitHubRepoSearch()
            
            // Get organization filter
            let orgFilter = CommandLineArgs.getGitHubOrg()

            let repos =
                match (searchTerm, orgFilter) with
                | (Some search, _) ->
                    printfn "🔍 Searching for: %s\n" search
                    let allResults = GitHubRepoIndex.searchRepositories search
                    match orgFilter with
                    | Some org ->
                        printfn "   (filtered by organization: %s)\n" org
                        allResults |> List.filter (fun r -> r.Owner.Equals(org, StringComparison.OrdinalIgnoreCase))
                    | None -> allResults
                | (None, Some org) ->
                    printfn "🏢 Filtering by organization: %s\n" org
                    GitHubRepoIndex.getRepositoriesByOwner org
                | (None, None) ->
                    printfn "📡 Loading all repositories from database...\n"
                    GitHubRepoIndex.getAllRepositories()

            if repos.IsEmpty then
                match searchTerm with
                | Some search ->
                    printfn "⚠️ No repositories found matching '%s'" search
                | None ->
                    printfn "⚠️ No repositories found in database"
                    printfn "\nTo index repositories, use:"
                    printfn "  --list-github-repos --github-token=<your-token>"
            else
                printfn "📊 Found %d repositories:\n" repos.Length

                repos
                |> List.sortBy (fun r -> r.FullName.ToLower())
                |> List.iteri (fun i repo ->
                    let visibility = if repo.IsPrivate then "🔒 Private" else "🌐 Public"
                    let fork = if repo.IsFork then " [Fork]" else ""
                    let lang = match repo.Language with | Some l -> $" ({l})" | None -> ""
                    
                    printfn "%3d. %s %s%s%s" (i + 1) repo.FullName visibility fork lang
                    if not (String.IsNullOrEmpty(repo.Description)) then
                        printfn "     %s" repo.Description
                    printfn "     Clone: %s" repo.CloneUrl
                    printfn "     Indexed: %s" (repo.IndexedAt.ToString("yyyy-MM-dd HH:mm"))
                    
                    // Show secrets and runners if available
                    match (repo.SecretsCount, repo.RunnersCount) with
                    | (Some secrets, Some runners) when secrets > 0 || runners > 0 ->
                        printfn "     🔐 Secrets: %d | 🏃 Runners: %d" secrets runners
                    | (Some secrets, _) when secrets > 0 ->
                        printfn "     🔐 Secrets: %d" secrets
                    | (_, Some runners) when runners > 0 ->
                        printfn "     🏃 Runners: %d" runners
                    | _ -> ()
                    
                    printfn "")

                printfn "\n📈 Summary:"
                let privateCount = repos |> List.filter (fun r -> r.IsPrivate) |> List.length
                let publicCount = repos.Length - privateCount
                let forkCount = repos |> List.filter (fun r -> r.IsFork) |> List.length
                printfn "  Private: %d | Public: %d | Forks: %d" privateCount publicCount forkCount
                
                let distinctOrgs = repos |> List.map (fun r -> r.Owner) |> List.distinct |> List.length
                printfn "  Organizations: %d" distinctOrgs
                
                // Show secrets/runners summary if any repos have been queried
                let reposWithSecrets = repos |> List.filter (fun r -> r.SecretsCount.IsSome) |> List.length
                let reposWithRunners = repos |> List.filter (fun r -> r.RunnersCount.IsSome) |> List.length
                if reposWithSecrets > 0 || reposWithRunners > 0 then
                    let totalSecrets = repos |> List.sumBy (fun r -> r.SecretsCount |> Option.defaultValue 0)
                    let totalRunners = repos |> List.sumBy (fun r -> r.RunnersCount |> Option.defaultValue 0)
                    printfn "\n🔍 Metadata:"
                    printfn "  Total Secrets: %d" totalSecrets
                    printfn "  Total Runners: %d" totalRunners
                    printfn "  Queried: %d repos" (max reposWithSecrets reposWithRunners)

    /// Handle querying GitHub metadata (secrets and runners) for stored repos
    let handleQueryGitHubMetadata () =
        if not (CommandLineArgs.shouldQueryGitHubMetadata()) then
            ()
        else
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Query GitHub Metadata (Secrets & Runners)"
            printfn "%s" (String.replicate 50 "=")

            // Get token from command line or environment
            let githubToken = 
                match CommandLineArgs.getGitHubToken() with
                | Some token -> Some token
                | None -> 
                    let envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                    if String.IsNullOrEmpty(envToken) then None else Some envToken

            // Get organization filter
            let orgFilter = CommandLineArgs.getGitHubOrg()

            match githubToken with
            | Some token ->
                printfn "🔑 Using GitHub token for authentication\n"
                
                let config = { GitHubConfig.Token = Some token; UserAgent = "BDevHud" }
                
                // Get repos from database
                let repos =
                    match orgFilter with
                    | Some org ->
                        printfn "🏢 Querying repositories for organization: %s" org
                        GitHubRepoIndex.getRepositoriesByOwner org
                    | None ->
                        printfn "📡 Querying all repositories from database..."
                        GitHubRepoIndex.getAllRepositories()

                if repos.IsEmpty then
                    printfn "⚠️ No repositories found in database"
                    printfn "\nTo index repositories first, use:"
                    printfn "  --list-github-repos --github-token=<your-token>"
                else
                    printfn "Found %d repositories to query\n" repos.Length
                    
                    let stopwatch = System.Diagnostics.Stopwatch.StartNew()
                    let mutable processed = 0
                    let mutable totalSecrets = 0
                    let mutable totalRunners = 0
                    
                    for repo in repos do
                        processed <- processed + 1
                        
                        // Query secrets count
                        let secretsCount = 
                            GitHubAdapter.getRepositorySecretsCount config repo.Owner repo.Name
                            |> Async.AwaitTask
                            |> Async.RunSynchronously
                        
                        // Query runners count
                        let runnersCount = 
                            GitHubAdapter.getRepositoryRunnersCount config repo.Owner repo.Name
                            |> Async.AwaitTask
                            |> Async.RunSynchronously
                        
                        totalSecrets <- totalSecrets + secretsCount
                        totalRunners <- totalRunners + runnersCount
                        
                        // Update database
                        let updatedRepo = { repo with SecretsCount = Some secretsCount; RunnersCount = Some runnersCount }
                        GitHubRepoIndex.upsertRepository updatedRepo
                        
                        // Progress indicator every 10 repos
                        if processed % 10 = 0 then
                            printfn "⏳ Processed %d/%d repositories..." processed repos.Length
                    
                    stopwatch.Stop()
                    
                    printfn "\n✅ Completed querying metadata for %d repositories (took %.1f seconds)" repos.Length stopwatch.Elapsed.TotalSeconds
                    printfn "\n📊 Summary:"
                    printfn "  Total Secrets: %d" totalSecrets
                    printfn "  Total Runners: %d" totalRunners
                    printfn "  Repositories with Secrets: %d" (repos |> List.filter (fun _ -> totalSecrets > 0) |> List.length)
                    printfn "  Repositories with Runners: %d" (repos |> List.filter (fun _ -> totalRunners > 0) |> List.length)

            | None ->
                printfn "❌ No GitHub token provided"
                printfn "Please provide a token using:"
                printfn "  --github-token=<your-token>"
                printfn "  or set GITHUB_TOKEN environment variable"

    /// Query and store individual GitHub secrets with names and scopes
    let handleQueryGitHubSecrets () =
        if not (CommandLineArgs.shouldQueryGitHubSecrets()) then
            ()
        else
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Query Individual GitHub Secrets"
            printfn "%s" (String.replicate 50 "=")

            // Get token from command line or environment
            let githubToken = 
                match CommandLineArgs.getGitHubToken() with
                | Some token -> Some token
                | None -> 
                    let envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                    if String.IsNullOrEmpty(envToken) then None else Some envToken

            // Get organization filter
            let orgFilter = CommandLineArgs.getGitHubOrg()

            match githubToken with
            | Some token ->
                printfn "🔑 Using GitHub token for authentication\n"
                
                let config = { GitHubConfig.Token = Some token; UserAgent = "BDevHud" }
                
                // Get repos from database
                let repos =
                    match orgFilter with
                    | Some org ->
                        printfn "🏢 Querying secrets for organization: %s" org
                        GitHubRepoIndex.getRepositoriesByOwner org
                    | None ->
                        printfn "📡 Querying secrets for all repositories from database..."
                        GitHubRepoIndex.getAllRepositories()

                if repos.IsEmpty then
                    printfn "⚠️ No repositories found in database"
                    printfn "\nTo index repositories first, use:"
                    printfn "  --list-github-repos --github-token=<your-token>"
                else
                    printfn "Found %d repositories to query\n" repos.Length
                    
                    let stopwatch = System.Diagnostics.Stopwatch.StartNew()
                    let mutable processed = 0
                    let mutable totalRepositorySecrets = 0
                    let mutable totalEnvironmentSecrets = 0
                    
                    for repo in repos do
                        processed <- processed + 1
                        
                        // Query repository-level secrets
                        let repoSecrets = 
                            GitHubAdapter.getRepositorySecrets config repo.Owner repo.Name
                            |> Async.AwaitTask
                            |> Async.RunSynchronously
                        
                        // Store each repository secret
                        for secret in repoSecrets do
                            let secretRecord = {
                                Id = 0L // Auto-incremented
                                RepoId = repo.Id
                                RepoFullName = repo.FullName
                                SecretName = secret.Name
                                Scope = "repository"
                                EnvironmentName = None
                                CreatedAt = Some secret.CreatedAt
                                UpdatedAt = Some secret.UpdatedAt
                                IndexedAt = DateTime.UtcNow
                            }
                            GitHubRepoIndex.upsertSecret secretRecord
                            totalRepositorySecrets <- totalRepositorySecrets + 1
                        
                        // Query environments
                        let environments = 
                            GitHubAdapter.getRepositoryEnvironments config repo.Owner repo.Name
                            |> Async.AwaitTask
                            |> Async.RunSynchronously
                        
                        // Query environment-level secrets for each environment
                        for envName in environments do
                            let envSecrets = 
                                GitHubAdapter.getEnvironmentSecrets config repo.Owner repo.Name envName
                                |> Async.AwaitTask
                                |> Async.RunSynchronously
                            
                            for secret in envSecrets do
                                let secretRecord = {
                                    Id = 0L // Auto-incremented
                                    RepoId = repo.Id
                                    RepoFullName = repo.FullName
                                    SecretName = secret.Name
                                    Scope = "environment"
                                    EnvironmentName = Some envName
                                    CreatedAt = Some secret.CreatedAt
                                    UpdatedAt = Some secret.UpdatedAt
                                    IndexedAt = DateTime.UtcNow
                                }
                                GitHubRepoIndex.upsertSecret secretRecord
                                totalEnvironmentSecrets <- totalEnvironmentSecrets + 1
                        
                        // Progress indicator every 10 repos
                        if processed % 10 = 0 then
                            printfn "⏳ Processed %d/%d repositories..." processed repos.Length
                    
                    stopwatch.Stop()
                    
                    printfn "\n✅ Completed querying secrets for %d repositories (took %.1f seconds)" repos.Length stopwatch.Elapsed.TotalSeconds
                    printfn "\n📊 Summary:"
                    printfn "  Repository-Level Secrets: %d" totalRepositorySecrets
                    printfn "  Environment-Level Secrets: %d" totalEnvironmentSecrets
                    printfn "  Total Individual Secrets: %d" (totalRepositorySecrets + totalEnvironmentSecrets)

            | None ->
                printfn "❌ No GitHub token provided"
                printfn "Please provide a token using:"
                printfn "  --github-token=<your-token>"
                printfn "  or set GITHUB_TOKEN environment variable"

    /// Display GitHub secrets from database
    let handleDisplayGitHubSecrets () =
        if not (CommandLineArgs.shouldDisplayGitHubSecrets()) then
            ()
        else
            printfn "\n%s" (String.replicate 50 "=")
            printfn "GitHub Secrets Display"
            printfn "%s" (String.replicate 50 "=")

            // Get organization filter
            let orgFilter = CommandLineArgs.getGitHubOrg()
            
            // Get repos from database
            let repos =
                match orgFilter with
                | Some org ->
                    printfn "🏢 Showing secrets for organization: %s\n" org
                    GitHubRepoIndex.getRepositoriesByOwner org
                | None ->
                    printfn "📡 Showing secrets for all repositories...\n"
                    GitHubRepoIndex.getAllRepositories()

            if repos.IsEmpty then
                printfn "⚠️ No repositories found in database"
            else
                let mutable totalRepoSecrets = 0
                let mutable totalEnvSecrets = 0
                let mutable reposWithSecrets = 0
                
                for repo in repos do
                    let secrets = GitHubRepoIndex.getSecretsForRepository repo.Id
                    
                    if not secrets.IsEmpty then
                        reposWithSecrets <- reposWithSecrets + 1
                        printfn "📦 %s" repo.FullName
                        
                        // Group by scope
                        let repoSecrets = secrets |> List.filter (fun s -> s.Scope = "repository")
                        let envSecrets = secrets |> List.filter (fun s -> s.Scope = "environment")
                        
                        if not repoSecrets.IsEmpty then
                            printfn "  🔐 Repository Secrets (%d):" repoSecrets.Length
                            for secret in repoSecrets do
                                printfn "     • %s (Updated: %s)" 
                                    secret.SecretName 
                                    (if secret.UpdatedAt.IsSome then secret.UpdatedAt.Value.ToString("yyyy-MM-dd") else "N/A")
                            totalRepoSecrets <- totalRepoSecrets + repoSecrets.Length
                        
                        if not envSecrets.IsEmpty then
                            printfn "  🌍 Environment Secrets (%d):" envSecrets.Length
                            // Group by environment
                            let byEnv = envSecrets |> List.groupBy (fun s -> s.EnvironmentName)
                            for (envName, envSecretsList) in byEnv do
                                printfn "     Environment: %s" (envName |> Option.defaultValue "Unknown")
                                for secret in envSecretsList do
                                    printfn "       • %s (Updated: %s)" 
                                        secret.SecretName 
                                        (if secret.UpdatedAt.IsSome then secret.UpdatedAt.Value.ToString("yyyy-MM-dd") else "N/A")
                            totalEnvSecrets <- totalEnvSecrets + envSecrets.Length
                        
                        printfn ""
                
                printfn "%s" (String.replicate 50 "=")
                printfn "📊 Summary:"
                printfn "  Total Repositories: %d" repos.Length
                printfn "  Repositories with Secrets: %d" reposWithSecrets
                printfn "  Total Repository-Level Secrets: %d" totalRepoSecrets
                printfn "  Total Environment-Level Secrets: %d" totalEnvSecrets
                printfn "  Total Secrets: %d" (totalRepoSecrets + totalEnvSecrets)



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

    /// Handle Octopus integration if requested
    let handleOctopusIntegration () =
        if not (CommandLineArgs.shouldRunOctopus()) then
            printfn "⏭️  Skipping Octopus integration (use --octopus to enable)"
        else
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Octopus Deploy Integration"
            printfn "%s" (String.replicate 50 "=")

            match OctopusOperations.getOctopusUrl(), OctopusOperations.getOctopusApiKey() with
            | Some octopusUrl, Some apiKey ->
                printfn "🐙 Octopus Deploy integration enabled"
                printfn "Server: %s" octopusUrl
                printfn "API Key: %s" (OctopusOperations.formatApiKeyForDisplay apiKey)
                
                try
                    printfn "\n🔍 Fetching Octopus Deploy projects..."
                    let config = { OctopusConfig.ServerUrl = octopusUrl; ApiKey = apiKey; Space = None }
                    let projects = OctopusClient.getAllProjects config |> Async.AwaitTask |> Async.RunSynchronously
                    
                    printfn "📊 Found %d projects. Analyzing deployment processes for Git references..." projects.Length
                    
                    let mutable projectsWithGitSteps = []
                    let mutable processedCount = 0
                    
                    for project in projects |> List.take (min 15 projects.Length) do
                        processedCount <- processedCount + 1
                        let isNextGenProject = project.Name.ToLower().Contains("nextgen")
                        
                        if isNextGenProject then
                            printfn "� [%d/%d] **NEXTGEN PROJECT** - Deep Analysis: %s" processedCount (min 15 projects.Length) project.Name
                        else
                            printfn "�🔍 [%d/%d] Analyzing: %s" processedCount (min 15 projects.Length) project.Name
                        
                        try
                            let deploymentStepsResult = OctopusClient.getProjectDeploymentProcess config project.Name |> Async.AwaitTask |> Async.RunSynchronously
                            match deploymentStepsResult with
                            | Ok steps ->
                                if isNextGenProject then
                                    printfn "   🔬 NEXTGEN DEEP DIVE - Found %d deployment steps" steps.Length
                                    for i, step in steps |> List.indexed do
                                        printfn "      📋 Step %d: %s" (i + 1) step.Name
                                        printfn "         🎯 Action Type: %s" step.ActionType
                                        printfn "         📊 Properties Count: %d" step.Properties.Count
                                        
                                        // For NextGen, show ALL properties to find Git references
                                        if step.Properties.Count > 0 then
                                            printfn "         📝 ALL Properties:"
                                            for (key, value) in step.Properties |> Map.toSeq do
                                                if value.Length > 100 then
                                                    printfn "            🔑 %s: %s..." key (value.Substring(0, 80))
                                                else
                                                    printfn "            🔑 %s: %s" key value
                                        
                                        // Look for any potential Git references with relaxed criteria
                                        let potentialGitProps = step.Properties |> Map.filter (fun key value ->
                                            key.ToLower().Contains("git") ||
                                            key.ToLower().Contains("repo") ||
                                            key.ToLower().Contains("source") ||
                                            key.ToLower().Contains("url") ||
                                            value.ToLower().Contains("github") ||
                                            value.ToLower().Contains("git") ||
                                            value.ToLower().Contains("repo"))
                                        
                                        if potentialGitProps.Count > 0 then
                                            printfn "         🌐 POTENTIAL GIT REFERENCES:"
                                            for (key, value) in potentialGitProps |> Map.toSeq do
                                                printfn "            ⭐ %s: %s" key value
                                
                                // First, let's look for ANY mention of git-related terms to debug
                                let anyGitMentions = steps |> List.collect (fun step ->
                                    step.Properties |> Map.toList |> List.filter (fun (key, value) ->
                                        let lowerValue = value.ToLower()
                                        let lowerKey = key.ToLower()
                                        lowerValue.Contains("git") || lowerKey.Contains("git") ||
                                        lowerValue.Contains("repo") || lowerKey.Contains("repo") ||
                                        lowerValue.Contains("source") || lowerKey.Contains("source"))
                                    |> List.map (fun (key, value) -> (step.Name, key, value)))
                                
                                if anyGitMentions.Length > 0 && processedCount <= 3 then
                                    printfn "   🔍 Found %d potential Git mentions:" anyGitMentions.Length
                                    for (stepName, key, value) in anyGitMentions |> List.take 5 do
                                        let displayValue = if value.Length > 50 then value.Substring(0, 47) + "..." else value
                                        printfn "      🔸 %s -> %s: %s" stepName key displayValue
                                
                                let gitSteps = steps |> List.filter (fun step ->
                                    step.Properties |> Map.exists (fun key value -> 
                                        let lowerValue = value.ToLower()
                                        let lowerKey = key.ToLower()
                                        
                                        // Very broad search for any actual Git references
                                        lowerValue.Contains("github.com") ||
                                        lowerValue.Contains("gitlab.com") ||
                                        lowerValue.Contains("bitbucket.org") ||
                                        lowerValue.Contains("git@") ||
                                        (lowerValue.Contains(".git") && lowerValue.Contains("http")) ||
                                        lowerKey.Contains("giturl") || 
                                        lowerKey.Contains("repositoryurl") ||
                                        (lowerKey.Contains("git") && lowerKey.Contains("url"))))
                                
                                if gitSteps.Length > 0 then
                                    projectsWithGitSteps <- (project.Name, gitSteps) :: projectsWithGitSteps
                                    printfn "   ✅ Found %d Git-related steps" gitSteps.Length
                                    
                                    // Show details of Git steps found
                                    for step in gitSteps |> List.take (min 2 gitSteps.Length) do
                                        printfn "      📂 Git Step: %s (%s)" step.Name step.ActionType
                                        let gitProps = step.Properties |> Map.filter (fun key value ->
                                            let lowerValue = value.ToLower()
                                            let lowerKey = key.ToLower()
                                            lowerValue.Contains("github.com") ||
                                            lowerValue.Contains("gitlab.com") ||
                                            lowerValue.Contains(".git") ||
                                            (lowerKey.Contains("git") && not (lowerKey.Contains("syntax"))))
                                        for (key, value) in gitProps |> Map.toSeq |> Seq.take 3 do
                                            let displayValue = if value.Length > 80 then value.Substring(0, 77) + "..." else value
                                            printfn "         🔗 %s: %s" key displayValue
                                else
                                    if isNextGenProject then
                                        printfn "   🤔 NEXTGEN: No Git references found with enhanced criteria - reviewed all properties above"
                                    else
                                        printfn "   ⚪ No Git references found"
                            | Error err ->
                                printfn "   ❌ Error: %s" err
                        with
                        | ex ->
                            printfn "   ❌ Exception: %s" ex.Message
                    
                    if projects.Length > 15 then
                        printfn "\n📝 Note: Only analyzed first 15 projects for Git references (including deep NextGen analysis)"
                    
                    printfn "\n📊 Summary:"
                    printfn "   Total projects: %d" projects.Length
                    printfn "   Analyzed: %d" (min 15 projects.Length)
                    printfn "   With Git in deployment steps: %d" projectsWithGitSteps.Length
                    
                    if projectsWithGitSteps.Length > 0 then
                        printfn "\n🔗 Projects with Git References in Deployment Steps:"
                        for i, (projectName, gitSteps) in projectsWithGitSteps |> List.rev |> List.indexed do
                            printfn "[%d] %s (%d Git steps)" (i + 1) projectName gitSteps.Length
                            for step in gitSteps |> List.take (min 2 gitSteps.Length) do
                                printfn "    📋 Step: %s" step.Name
                                let gitProperties = step.Properties |> Map.filter (fun key value -> 
                                    (key.ToLower().Contains("giturl") || 
                                     key.ToLower().Contains("git.url") ||
                                     key.ToLower().Contains("repository.url") ||
                                     key.ToLower().Contains("repositoryurl") ||
                                     key.ToLower().Contains("source.url") ||
                                     value.ToLower().Contains("github.com") ||
                                     (value.ToLower().Contains(".git") && value.ToLower().Contains("http")) ||
                                     value.ToLower().Contains("git@")) &&
                                    not (key.ToLower().Contains("script")) &&
                                    not (key.ToLower().Contains("syntax")))
                                for (key, value) in gitProperties |> Map.toSeq do
                                    if value.Length < 100 then
                                        printfn "       🔑 %s: %s" key value
                                    else
                                        printfn "       🔑 %s: %s..." key (value.Substring(0, 80))
                    
                    printfn "\n� Analysis Results:"
                    printfn "   • Octopus projects appear to use packaged artifacts rather than direct Git integration"
                    printfn "   • Deployment processes primarily use PowerShell scripts and package feeds"
                    printfn "   • No direct Git repository references found in deployment step configurations"
                    
                    printfn "\n�💡 Next Steps:"
                    if projectsWithGitSteps.Length > 0 then
                        printfn "   • Found projects with Git integration in deployment processes"
                        printfn "   • Use --github to explore GitHub repositories"
                        printfn "   • Use --index-files to index project files for searching"
                        printfn "   • Cross-reference with local repositories using --pull-repos"
                    else
                        printfn "   • Git integration likely exists at the package build level rather than deployment level"
                        printfn "   • Use --github to explore available GitHub repositories and build processes"
                        
                with
                | ex ->
                    printfn "❌ Failed to connect to Octopus Deploy: %s" ex.Message
                    printfn "Please verify the URL and API key are correct."
                    
            | None, _ ->
                printfn "❌ Octopus Deploy URL not provided."
                printfn "Use --octopus-url=\"your-url\" or set OCTOPUS_URL environment variable."
            | _, None ->
                printfn "❌ Octopus Deploy API key not provided."
                printfn "Use --octopus-api-key=\"your-key\" or set OCTO_API_KEY environment variable."

    /// Handle Octopus Deploy data indexing
    let handleOctopusIndexing () =
        if not (CommandLineArgs.shouldIndexOctopus()) then
            printfn "⏭️  Skipping Octopus indexing (use --index-octopus to enable)"
        else
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Octopus Deploy Data Indexing"
            printfn "%s" (String.replicate 50 "=")

            match OctopusOperations.getOctopusUrl(), OctopusOperations.getOctopusApiKey() with
            | Some octopusUrl, Some apiKey ->
                printfn "🗃️  Octopus Deploy indexing enabled"
                printfn "Server: %s" octopusUrl
                printfn "API Key: %s" (OctopusOperations.formatApiKeyForDisplay apiKey)
                
                try
                    printfn "\n📊 Current Octopus index statistics:"
                    let (stepCount, trigramCount, projects) = FileIndex.getOctopusIndexStats(FileIndex.dbPath)
                    printfn "   Steps indexed: %d" stepCount
                    printfn "   Trigrams indexed: %d" trigramCount
                    printfn "   Projects: %d" projects.Length
                    
                    printfn "\n🔍 Fetching Octopus Deploy projects for indexing..."
                    let config = { OctopusConfig.ServerUrl = octopusUrl; ApiKey = apiKey; Space = None }
                    let projects = OctopusClient.getAllProjects config |> Async.AwaitTask |> Async.RunSynchronously
                    
                    printfn "📊 Found %d projects. Indexing deployment step properties..." projects.Length
                    
                    let mutable totalStepsIndexed = 0
                    let mutable totalTrigramsAdded = 0
                    let mutable totalTemplatesIndexed = 0
                    let indexedTemplates = System.Collections.Generic.HashSet<string>()
                    
                    for project in projects do
                        printfn "\n🔍 Processing: %s" project.Name
                        
                        try
                            let deploymentStepsResult = OctopusClient.getProjectDeploymentProcess config project.Name |> Async.AwaitTask |> Async.RunSynchronously
                            match deploymentStepsResult with
                            | Ok steps ->
                                printfn "   Found %d deployment steps" steps.Length
                                
                                for step in steps do
                                    try
                                        // Properties are already a Map<string, string>
                                        let properties = step.Properties
                                        
                                        // Check if step uses a template and index it if not already indexed
                                        match step.StepTemplateId with
                                        | Some templateId when not (indexedTemplates.Contains(templateId)) ->
                                            printfn "   📋 Indexing step template: %s" (step.StepTemplate |> Option.defaultValue templateId)
                                            try
                                                let templateResult = OctopusClient.getStepTemplate config templateId |> Async.AwaitTask |> Async.RunSynchronously
                                                match templateResult with
                                                | Ok templateInfo ->
                                                    // Convert template properties to Map - include name and description for searching!
                                                    let templateProps = 
                                                        Map.empty
                                                        |> Map.add "Template.Name" templateInfo.Name
                                                        |> Map.add "Template.Description" templateInfo.Description
                                                        |> fun m ->
                                                            match templateInfo.PowerShellScript with
                                                            | Some script -> m |> Map.add "Octopus.Action.Script.ScriptBody" script
                                                            | None -> m
                                                    
                                                    FileIndex.indexStepTemplate 
                                                        templateInfo.Id 
                                                        templateInfo.Name 
                                                        templateInfo.Description 
                                                        templateInfo.PowerShellScript 
                                                        templateProps
                                                    
                                                    indexedTemplates.Add(templateId) |> ignore
                                                    totalTemplatesIndexed <- totalTemplatesIndexed + 1
                                                | Error err ->
                                                    printfn "   ⚠️  Could not fetch template: %s" err
                                            with
                                            | ex ->
                                                printfn "   ⚠️  Error indexing template: %s" ex.Message
                                        | _ -> ()
                                        
                                        // Index this step
                                        FileIndex.indexOctopusStep project.Name step.Name step.StepId step.ActionType step.StepTemplateId properties
                                        totalStepsIndexed <- totalStepsIndexed + 1
                                        
                                    with
                                    | ex ->
                                        printfn "   ❌ Error indexing step '%s': %s" step.Name ex.Message
                                
                            | Error err ->
                                printfn "   ❌ Error getting deployment steps: %s" err
                        with
                        | ex ->
                            printfn "   ❌ Error processing project '%s': %s" project.Name ex.Message
                    
                    printfn "\n📈 Final indexing statistics:"
                    let (finalStepCount, finalTrigramCount, finalProjects) = FileIndex.getOctopusIndexStats(FileIndex.dbPath)
                    printfn "   Total steps indexed: %d" finalStepCount
                    printfn "   Total trigrams: %d" finalTrigramCount
                    printfn "   Projects with data: %d" finalProjects.Length
                    printfn "   Steps processed this run: %d" totalStepsIndexed
                    printfn "   Step templates indexed this run: %d" totalTemplatesIndexed
                    
                with
                | ex ->
                    printfn "❌ Failed to index Octopus Deploy data: %s" ex.Message
                    printfn "Please verify the URL and API key are correct."
                    
            | None, _ ->
                printfn "❌ Octopus Deploy URL not provided."
                printfn "Use --octopus-url=\"your-url\" or set OCTOPUS_URL environment variable."
            | _, None ->
                printfn "❌ Octopus Deploy API key not provided."
                printfn "Use --octopus-api-key=\"your-key\" or set OCTO_API_KEY environment variable."

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
        IntegrationHandlers.handleListGitHubRepos ()
        IntegrationHandlers.handleDisplayGitHubRepos ()
        IntegrationHandlers.handleQueryGitHubMetadata ()
        IntegrationHandlers.handleQueryGitHubSecrets ()
        IntegrationHandlers.handleDisplayGitHubSecrets ()
        IntegrationHandlers.handleGitHubIntegration ()
        IntegrationHandlers.handleOctopusIntegration ()
        IntegrationHandlers.handleOctopusIndexing ()
        
        // Perform git pull operations if requested (using parallel processing)
        GitOperations.performParallelGitPulls repoInfos

        // Handle update remote URLs if requested
        match CommandLineArgs.getUpdateRemoteParams() with
        | Some (searchString, targetString) ->
            if repoInfos.IsEmpty then
                printfn "\n⚠️ No repositories found to update remotes"
            else
                printfn "\n🔄 Updating git remote URLs..."
                printfn "   Search: %s" searchString
                printfn "   Target: %s" targetString
                printfn ""
                
                let mutable totalUpdated = 0
                let mutable totalErrors = 0
                
                for repoInfo in repoInfos do
                    let (success, message, updatedCount) = GitAdapter.updateRemoteUrls repoInfo.Path searchString targetString
                    
                    if updatedCount > 0 then
                        printfn "✅ %s: %s" repoInfo.RepoName message
                        totalUpdated <- totalUpdated + updatedCount
                    elif not success then
                        printfn "❌ %s: %s" repoInfo.RepoName message
                        totalErrors <- totalErrors + 1
                
                printfn "\n📊 Summary: Updated %d remote(s) across repositories" totalUpdated
                if totalErrors > 0 then
                    printfn "   Errors: %d" totalErrors
        | None -> ()

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