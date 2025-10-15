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

// Format API key for display (show first 8 chars like Octopus UI, but ensure security)
let formatApiKeyForDisplay (apiKey: string) =
    if isNull apiKey then
        "[NULL]"
    elif String.IsNullOrWhiteSpace(apiKey) then
        "[EMPTY OR WHITESPACE]"
    elif apiKey.Length > 10 then
        // Show first 8 chars, mask the rest (ensuring at least 3 chars are hidden)
        let visible = apiKey.Substring(0, 8)
        let masked = String.replicate (apiKey.Length - 8) "*"
        $"{visible}{masked} (length {apiKey.Length})"
    else
        // For short keys, don't show any characters for security
        $"[REDACTED - length {apiKey.Length}]"

// Get Octopus variable search pattern from command line
let getVariableSearchPattern () =
    let args = Environment.GetCommandLineArgs()
    // Check for command line argument (look for --search-variable)
    let searchVarArgIndex =
        args |> Array.tryFindIndex (fun arg -> arg = "--search-variable" || arg = "--search-var")

    match searchVarArgIndex with
    | Some index when index + 1 < args.Length ->
        let pattern = args.[index + 1]
        printfn $"Searching for variables matching: {pattern}"
        Some pattern
    | _ -> None

// Get project step analysis parameter from command line (format: projectName/stepNumber)
let getProjectStepAnalysis () =
    let args = Environment.GetCommandLineArgs()
    // Check for command line argument (look for --analyze-step)
    let analyzeStepArgIndex =
        args |> Array.tryFindIndex (fun arg -> arg = "--analyze-step" || arg = "--step-analysis")

    match analyzeStepArgIndex with
    | Some index when index + 1 < args.Length ->
        let stepSpec = args.[index + 1]
        printfn $"Analyzing deployment step: {stepSpec}"
        
        // Parse projectName/stepIdentifier format (stepIdentifier can be number or decimal like 10.1)
        let parts = stepSpec.Split('/')
        if parts.Length = 2 && not (String.IsNullOrWhiteSpace(parts.[0])) && not (String.IsNullOrWhiteSpace(parts.[1])) then
            Some (parts.[0].Trim(), parts.[1].Trim())
        else
            printfn $"❌ Invalid format. Expected: projectName/stepIdentifier (e.g., 'MyProject/1' or 'MyProject/10.1')"
            None
    | _ -> None

// Get Octopus API key from command line args or environment variable
let getOctopusApiKey () =
    let args = Environment.GetCommandLineArgs()

    // Check for command line argument (look for --octopus-api-key or --api-key)
    let apiKeyArgIndex =
        args |> Array.tryFindIndex (fun arg -> arg = "--octopus-api-key" || arg = "--api-key")

    match apiKeyArgIndex with
    | Some index when index + 1 < args.Length ->
        let apiKey = args.[index + 1]
        printfn $"Using command line Octopus API key: {formatApiKeyForDisplay apiKey}"
        Some apiKey
    | _ ->
        // Check for OCTO_API_KEY environment variable
        let apiKey = Environment.GetEnvironmentVariable("OCTO_API_KEY")

        if not (String.IsNullOrEmpty(apiKey)) then
            printfn $"Using OCTO_API_KEY environment variable: {formatApiKeyForDisplay apiKey}"
            Some apiKey
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

    // Check for Octopus URL and API key, then display Octopus projects
    let octopusUrl = getOctopusUrl ()
    let octopusApiKey = getOctopusApiKey ()

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
                    let portPart = if uri.Port <> -1 then $":{uri.Port}" else ""
                    
                    // Preserve the path part (like /api) but remove fragment
                    let pathPart = 
                        if String.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath = "/" then 
                            ""
                        else 
                            uri.AbsolutePath.TrimEnd('/')
                    
                    let baseServerUrl = $"{uri.Scheme}://{uri.Host}{portPart}{pathPart}"
                    
                    // Check if URL contains space identifier in fragment
                    let extractedSpaceId = 
                        if not (String.IsNullOrEmpty(uri.Fragment)) then
                            let fragment = uri.Fragment.TrimStart('#')
                            if fragment.Contains("/Spaces-") then
                                let spacePart = fragment.Split([|"/Spaces-"|], StringSplitOptions.RemoveEmptyEntries)
                                if spacePart.Length > 1 then
                                    Some ("Spaces-" + spacePart.[1].Split('/').[0])
                                else None
                            elif fragment.StartsWith("Spaces-") then
                                Some (fragment.Split('/').[0])
                            else None
                        else None
                    
                    let spaceDisplay = extractedSpaceId |> Option.defaultValue "(default space)"
                    printfn $"🔍 Parsed URL:"
                    printfn $"    Base Server URL: {baseServerUrl}"
                    printfn $"    Extracted Space ID: {spaceDisplay}"
                    
                    (baseServerUrl, extractedSpaceId)
                with 
                | ex -> 
                    printfn $"⚠️  URL parsing warning: {ex.Message}"
                    printfn $"    Using URL as-is: {url}"
                    (url, None)

            let spaceType = 
                match spaceId with 
                | Some id -> Some (Octo.Adapter.SpaceId id)
                | None -> None

            let config = 
                { ServerUrl = baseUrl
                  ApiKey = apiKey
                  Space = spaceType }

            let targetSpace = spaceId |> Option.defaultValue "Default"
            printfn $"🔗 Connecting to Octopus Deploy..."
            printfn $"    Server: {baseUrl}"
            printfn $"    API Key: {formatApiKeyForDisplay apiKey}"
            printfn $"    Target Space: {targetSpace}"
            
            // First, test connectivity with a proper connection test
            printfn $"🔍 Testing connection to Octopus server..."
            let connectionResult = OctopusClient.testConnection config |> Async.AwaitTask |> Async.RunSynchronously
            
            match connectionResult with
            | Ok serverInfo ->
                printfn $"✅ Connection successful! {serverInfo}"
                
                // Now get spaces
                printfn $"🔍 Listing available spaces..."
                printfn $"    Making API call to: {baseUrl}/api/spaces"
                let spaces = OctopusClient.getSpaces config |> Async.AwaitTask |> Async.RunSynchronously
                printfn $"📋 Found {spaces.Length} space(s):"
                spaces |> List.iteri (fun i space -> 
                    printfn $"    {i + 1}. {space.Name} (ID: {space.Id})")
                
                // If no spaces found but connection worked, might need to use default space
                if spaces.Length = 0 then
                    printfn $"⚠️  No spaces returned - this might indicate:"
                    printfn $"    - Using an older Octopus version without spaces"
                    printfn $"    - API key lacks permission to list spaces"
                    printfn $"    - Need to query default space directly"
            | Error errorMsg ->
                printfn $"❌ Connection failed: {errorMsg}"
                printfn $"💡 This confirms you need to be on the VPN or check network connectivity"
                raise (System.Exception($"Connection test failed: {errorMsg}"))
            
            // Get all projects from the specified space (or default)
            printfn $"🔍 Querying projects..."
            let projectApiUrl = 
                match spaceId with
                | Some space -> $"{baseUrl}/api/{space}/projects"
                | None -> $"{baseUrl}/api/projects"
            printfn $"    Making API call to: {projectApiUrl}"
            let projects = OctopusClient.getAllProjects config |> Async.AwaitTask |> Async.RunSynchronously
            
            printfn $"📋 Found {projects.Length} Octopus project(s):"
            if projects.Length = 0 then
                printfn $"    No projects found in the target space."
                if spaceId.IsSome then
                    printfn $"    💡 Try using just the base URL without space ID for default space"
                else
                    printfn $"    💡 This might be an older Octopus version or permission issue"
            else
                projects |> List.iteri (fun i project ->
                    let description = if String.IsNullOrEmpty(project.Description) then "(no description)" else project.Description
                    printfn $"{i + 1}. {project.Name}"
                    printfn $"    ID: {project.Id}"
                    printfn $"    Space: {project.SpaceName}"
                    printfn $"    Description: {description}"
                    printfn $"    Disabled: {project.IsDisabled}")

            // Check if user wants to search for variables
            match getVariableSearchPattern () with
            | Some pattern ->
                printfn $"\n🔍 Searching for variables matching '{pattern}'..."
                let variables = OctopusClient.searchVariables config pattern |> Async.AwaitTask |> Async.RunSynchronously
                
                if variables.Length > 0 then
                    printfn $"📋 Found {variables.Length} variable(s):"
                    variables |> List.iteri (fun i var ->
                        let valueDisplay = if var.IsSensitive then "[SENSITIVE - Cannot retrieve]" else (var.Value |> Option.defaultValue "[Empty]")
                        printfn $"{i + 1}. {var.Name}"
                        printfn $"    Value: {valueDisplay}"
                        printfn $"    Sensitive: {var.IsSensitive}"
                        printfn $"    Scope: {var.Scope}"
                        match var.ProjectName with
                        | Some projectName -> printfn $"    Project: {projectName}"
                        | None -> ()
                        match var.LibrarySetName with
                        | Some libName -> printfn $"    Library Set: {libName}"
                        | None -> ()
                        printfn "")
                else
                    printfn "❌ No variables found matching the pattern."
            | None -> ()

            // Check if user wants to analyze a specific deployment step
            match getProjectStepAnalysis () with
            | Some (projectName, stepNumber) ->
                printfn $"\n🔍 Analyzing deployment step {stepNumber} in project '{projectName}'..."
                let stepsResult = OctopusClient.getProjectDeploymentProcess config projectName |> Async.AwaitTask |> Async.RunSynchronously
                
                match stepsResult with
                | Ok steps ->
                    // Try to match by step number (if it's an integer) or by step name (if it contains the identifier)
                    let matchingStep = 
                        steps |> List.tryFind (fun step -> 
                            // First try exact step number match (if stepNumber is parseable as int)
                            match System.Int32.TryParse(stepNumber) with
                            | (true, stepNum) when step.StepNumber = stepNum -> true
                            | _ -> 
                                // Otherwise try matching by step name or position identifier
                                step.Name.Contains(stepNumber, System.StringComparison.OrdinalIgnoreCase) ||
                                stepNumber.Contains(step.StepNumber.ToString()))
                    
                    match matchingStep with
                    | Some step ->
                        printfn $"📋 Step Analysis: {step.Name}"
                        printfn $"    Step Number: {step.StepNumber}"
                        printfn $"    Action Type: {step.ActionType}"
                        
                        // Show PowerShell script if available
                        match step.PowerShellScript with
                        | Some script ->
                            printfn $"    PowerShell Script:"
                            printfn $"    ┌─ Script Content ─────────────────────────────────────"
                            script.Split('\n') |> Array.iteri (fun i line -> 
                                printfn $"    │ {i + 1:D3}: {line}")
                            printfn $"    └─────────────────────────────────────────────────────"
                        | None -> printfn $"    PowerShell Script: None"
                        
                        // Show step template information if available
                        match step.StepTemplateId with
                        | Some templateId ->
                            printfn $"    Step Template: {step.StepTemplate |> Option.defaultValue templateId}"
                            printfn $"    🔍 Fetching template details..."
                            
                            let templateResult = OctopusClient.getStepTemplate config templateId |> Async.AwaitTask |> Async.RunSynchronously
                            match templateResult with
                            | Ok templateInfo ->
                                printfn $"    Template Name: {templateInfo.Name}"
                                printfn $"    Template Description: {templateInfo.Description}"
                                
                                match templateInfo.GitRepositoryUrl with
                                | Some gitUrl ->
                                    printfn $"    🔗 Git Repository: {gitUrl}"
                                    match templateInfo.GitPath with
                                    | Some gitPath -> printfn $"    📁 Git Path: {gitPath}"
                                    | None -> ()
                                | None -> ()
                                
                                match templateInfo.PowerShellScript with
                                | Some templateScript ->
                                    printfn $"    Template PowerShell Script:"
                                    printfn $"    ┌─ Template Script Content ───────────────────────────"
                                    templateScript.Split('\n') |> Array.iteri (fun i line -> 
                                        printfn $"    │ {i + 1:D3}: {line}")
                                    printfn $"    └─────────────────────────────────────────────────────"
                                | None -> printfn $"    Template PowerShell Script: None"
                            | Error err ->
                                printfn $"    ❌ Error fetching template: {err}"
                        | None -> printfn $"    Step Template: None (inline step)"
                        
                        // Show variables count
                        printfn $"    Variables Available: {step.Variables.Length}"
                        if step.Variables.Length > 0 then
                            printfn $"    🔧 Project Variables:"
                            step.Variables |> List.take (min 5 step.Variables.Length) |> List.iter (fun var ->
                                let valueDisplay = if var.IsSensitive then "[SENSITIVE]" else (var.Value |> Option.defaultValue "[Empty]")
                                printfn $"      - {var.Name}: {valueDisplay}")
                            if step.Variables.Length > 5 then
                                printfn $"      ... and {step.Variables.Length - 5} more variables"
                        
                        // Show key properties
                        printfn $"    Properties: {step.Properties.Count}"
                        if step.Properties.Count > 0 then
                            printfn $"    🔧 Key Properties:"
                            step.Properties 
                            |> Map.toList 
                            |> List.filter (fun (key, _) -> 
                                key.Contains("Script") || key.Contains("Template") || key.Contains("Package"))
                            |> List.take 5
                            |> List.iter (fun (key, value) ->
                                let displayValue = if value.Length > 100 then value.Substring(0, 100) + "..." else value
                                printfn $"      - {key}: {displayValue}")
                    | None ->
                        printfn $"❌ Step '{stepNumber}' not found in project '{projectName}'"
                        printfn $"Available steps:"
                        steps |> List.iter (fun step ->
                            printfn $"  {step.StepNumber}. {step.Name}")
                | Error err ->
                    printfn $"❌ Error analyzing project: {err}"
            | None -> ()

            // Show cached git repos for potential matching
            let gitRepos = getGitReposFromCache ()
            if gitRepos.Length > 0 then
                printfn $"\n📁 Found {gitRepos.Length} git repositories in cache for potential matching:"
                gitRepos |> List.iter (fun (name, url) -> printfn $"  - {name}: {url}")
            else
                printfn "\n📁 No git repositories found in cache. Run the git scan first to populate the cache."

        with
        | ex -> 
            printfn $"❌ Error connecting to Octopus Deploy:"
            printfn $"    {ex.Message}"
            if ex.InnerException <> null then
                printfn $"    Inner exception: {ex.InnerException.Message}"
            printfn $"💡 Troubleshooting tips:"
            printfn $"    - Ensure the server URL is correct (just base URL, not browser URL)"
            printfn $"    - Verify your API key has sufficient permissions"
            printfn $"    - Check if the server is accessible from your network"
            printfn $"    - Try using just the base URL: https://octopus.rbxd.ds/"
    
    | Some url, None ->
        printfn "\n%s" (String.replicate 50 "=")
        printfn "Octopus Deploy Integration"
        printfn "%s" (String.replicate 50 "=")
        printfn $"Octopus URL found: {url}"
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
