namespace IO.Adapter

open System
open System.IO

/// Repository discovery functionality using directory traversal
module RepositoryDiscovery =

    /// Find .git folders in a specific directory (non-recursive)
    let private findGitFoldersInDirectory (directory: string) (debugMode: bool) : string list =
        try
            if Directory.Exists(directory) then
                Directory.GetDirectories(directory)
                |> Array.filter (fun dir ->
                    let gitPath = Path.Combine(dir, ".git")
                    if Directory.Exists(gitPath) then
                        if debugMode then
                            printfn "Found .git folder: %s" dir
                        true
                    else
                        false)
                |> Array.map (fun dir -> Path.Combine(dir, ".git"))
                |> Array.toList
            else
                []
        with
        | ex ->
            if debugMode then
                printfn "Error scanning directory %s: %s" directory ex.Message
            []

    /// Spider-search for .git folders under a root directory
    /// If no root directory is provided, searches all local drives
    /// If the root directory itself has a .git folder, use it directly
    let findGitFolders (rootDirectory: string option) (debugMode: bool) : string array =
        match rootDirectory with
        | Some root ->
            if Directory.Exists(root) then
                // Check if the root directory itself has a .git folder
                let gitPath = Path.Combine(root, ".git")
                if Directory.Exists(gitPath) then
                    if debugMode then
                        printfn "Found .git folder in root directory: %s" root
                    [| gitPath |]
                else
                    if debugMode then
                        printfn "Searching for .git folders under: %s" root
                    DirectoryTraversal.traverseDirectories
                        root
                        (fun dir -> findGitFoldersInDirectory dir debugMode)
                        []
                        (@)
                        debugMode
                    |> List.toArray
            else
                printfn "Directory does not exist: %s" root
                [||]
        | None ->
            printfn "Searching all local drives for .git folders..."
            
            DriveInfo.GetDrives()
            |> Array.filter (fun drive -> drive.DriveType = DriveType.Fixed && drive.IsReady)
            |> Array.collect (fun drive ->
                let drivePath = drive.RootDirectory.FullName
                DirectoryTraversal.traverseDirectories
                    drivePath
                    (fun dir -> findGitFoldersInDirectory dir debugMode)
                    []
                    (@)
                    debugMode
                |> List.toArray)

    /// Get repository name from a directory path
    let getRepositoryName (directoryPath: string) : string =
        Path.GetFileName(directoryPath)

    /// Get parent directory from a git folder path
    let getParentDirectory (gitFolderPath: string) : string =
        Directory.GetParent(gitFolderPath).FullName

    /// Get file name from a file path
    let getFileName (filePath: string) : string =
        Path.GetFileName(filePath)

    /// Get file size information
    let getFileInfo (filePath: string) : int64 =
        let fileInfo = FileInfo(filePath)
        fileInfo.Length