open System
open System.IO
open IO.Adapter

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

        gitFolders
        |> Array.iteri (fun i folder ->
            let parentDir = Directory.GetParent(folder).FullName
            printfn $"{i + 1}. {parentDir}"

            // Get and display remote information
            let remoteResult = GitAdapter.getRemote parentDir

            if remoteResult.Success && remoteResult.Remotes.Length > 0 then
                remoteResult.Remotes
                |> List.iter (fun remote -> printfn $"    {remote.Name}      {remote.Url} ({remote.Type})")

                // Run git pull for this repository
                let (pullSuccess, pullError) = GitAdapter.gitPull parentDir

                if not pullSuccess then
                    printfn $"    ⚠️  Git pull failed: {pullError}"
            else
                printfn "    (no remote configured)")

// Get root directory from command line args or environment variable
let getRootDirectory () =
    let args = Environment.GetCommandLineArgs()

    // Check for command line argument (skip the first arg which is the program name)
    if args.Length > 1 then
        let rootPath = args.[1]
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

// Main program entry point
let main () =
    printfn "Git Folder Spider Search"
    printfn "======================="

    let rootDirectory = getRootDirectory ()
    let gitFolders = findGitFolders rootDirectory
    displayGitFolders gitFolders

    printfn "\nSearch completed."

// Run the main function
main ()
