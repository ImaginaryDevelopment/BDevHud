namespace IO.Adapter

open System
open System.IO

/// Generic directory traversal functionality with robust error handling
module DirectoryTraversal =

    /// Directory patterns to exclude during traversal (commonly ignored folders)
    let private directoryBlacklist = [
        "node_modules";
        "bin";
        "obj";
        ".git";
        ".vs";
        ".vscode";
        "packages";
        ".nuget";
        "node_modules";
        "target";
        "build";
        ".gradle";
        ".mvn";
        "__pycache__";
        ".pytest_cache";
        "venv";
        ".venv";
        "env";
        ".env";
    ]

    /// Check if a directory should be skipped during traversal
    let private shouldSkipDirectory (directoryPath: string) : bool =
        let dirName = Path.GetFileName(directoryPath).ToLowerInvariant()
        directoryBlacklist
        |> List.exists (fun pattern -> 
            dirName.Contains(pattern.ToLowerInvariant(), System.StringComparison.OrdinalIgnoreCase))

    /// Traverses directories recursively, applying a function to each directory
    /// and accumulating results. Handles errors gracefully without stopping the traversal.
    ///
    /// Parameters:
    /// - rootDir: The root directory to start traversal from
    /// - folderProcessor: Function that processes each directory and returns results
    /// - initialResults: Initial accumulator value
    /// - resultCombiner: Function to combine results from different directories
    /// - debugMode: Whether to show detailed error messages including access denied
    let rec traverseDirectories<'T>
        (rootDir: string)
        (folderProcessor: string -> 'T list)
        (initialResults: 'T list)
        (resultCombiner: 'T list -> 'T list -> 'T list)
        (debugMode: bool)
        =

        let mutable currentResults = initialResults

        try
            // Process the current directory
            let dirResults = folderProcessor rootDir
            currentResults <- resultCombiner currentResults dirResults
        with ex ->
            printfn $"Error processing directory {rootDir}: {ex.Message}"

        try
            // Get subdirectories and traverse them recursively
            let subdirs = Directory.GetDirectories(rootDir)

            for subdir in subdirs do
                try
                    // Skip blacklisted directories to improve performance and avoid unnecessary traversal
                    if shouldSkipDirectory subdir then
                        if debugMode then printfn $"Skipping blacklisted directory: {subdir}"
                    else
                        currentResults <- traverseDirectories subdir folderProcessor currentResults resultCombiner debugMode
                with
                | :? UnauthorizedAccessException -> if debugMode then printfn $"Access denied: {subdir}"
                | :? DirectoryNotFoundException -> if debugMode then printfn $"Directory not found: {subdir}"
                | :? PathTooLongException -> if debugMode then printfn $"Path too long: {subdir}"
                | ex -> if debugMode then printfn $"Error traversing {subdir}: {ex.Message}"
        with
        | :? UnauthorizedAccessException -> if debugMode then printfn $"Access denied to directory: {rootDir}"
        | :? DirectoryNotFoundException -> if debugMode then printfn $"Directory not found: {rootDir}"
        | :? PathTooLongException -> if debugMode then printfn $"Path too long: {rootDir}"
        | ex -> if debugMode then printfn $"Error accessing directory {rootDir}: {ex.Message}"

        currentResults

    /// Traverses multiple root directories and combines results
    let traverseMultipleRoots<'T>
        (rootDirs: string list)
        (folderProcessor: string -> 'T list)
        (initialResults: 'T list)
        (resultCombiner: 'T list -> 'T list -> 'T list)
        (debugMode: bool)
        =

        let allResults = System.Collections.Generic.List<'T>()
        allResults.AddRange(initialResults)

        for rootDir in rootDirs do
            printfn $"Traversing directory: {rootDir}"

            try
                let dirResults = traverseDirectories rootDir folderProcessor [] resultCombiner debugMode
                allResults.AddRange(dirResults)
            with ex ->
                if debugMode then printfn $"Error traversing root directory {rootDir}: {ex.Message}"

        allResults |> Seq.toList

    /// Gets all local fixed drives that are ready for traversal
    let getLocalDrives () =
        let drives = DriveInfo.GetDrives()

        drives
        |> Array.filter (fun d -> d.DriveType = DriveType.Fixed && d.IsReady)
        |> Array.map (fun d -> d.RootDirectory.FullName)
        |> Array.toList

    /// Traverses all local drives with the given processor function
    let traverseAllLocalDrives<'T>
        (folderProcessor: string -> 'T list)
        (initialResults: 'T list)
        (resultCombiner: 'T list -> 'T list -> 'T list)
        (debugMode: bool)
        =

        printfn "Traversing all local drives..."
        let localDrives = getLocalDrives ()
        traverseMultipleRoots localDrives folderProcessor initialResults resultCombiner debugMode