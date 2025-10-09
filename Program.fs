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

/// Display results of git folder search
let displayGitFolders (gitFolders: string[]) =
    if gitFolders.Length = 0 then
        printfn "No .git folders found."
    else
        printfn $"\nFound {gitFolders.Length} .git folder(s):"

        gitFolders
        |> Array.iteri (fun i folder ->
            let parentDir = Directory.GetParent(folder).FullName
            printfn $"{i + 1}. {parentDir}")

// Example usage
let main () =
    printfn "Git Folder Spider Search"
    printfn "======================="

    // Example 1: Search specific directory
    let specificDir = @"C:\dev" // Change this to your desired directory
    let gitFoldersInDev = findGitFolders (Some specificDir)
    displayGitFolders gitFoldersInDev

    printfn "\n" + String.replicate 50 "-" + "\n"

    // Example 2: Search all local drives
    let allGitFolders = findGitFolders None
    displayGitFolders allGitFolders

// Run the main function
main ()
