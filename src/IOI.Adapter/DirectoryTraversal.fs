namespace IO.Adapter

open System
open System.IO

/// Generic directory traversal functionality with robust error handling
module DirectoryTraversal =

    /// Traverses directories recursively, applying a function to each directory
    /// and accumulating results. Handles errors gracefully without stopping the traversal.
    ///
    /// Parameters:
    /// - rootDir: The root directory to start traversal from
    /// - folderProcessor: Function that processes each directory and returns results
    /// - initialResults: Initial accumulator value
    /// - resultCombiner: Function to combine results from different directories
    let rec traverseDirectories<'T>
        (rootDir: string)
        (folderProcessor: string -> 'T list)
        (initialResults: 'T list)
        (resultCombiner: 'T list -> 'T list -> 'T list)
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
                    currentResults <- traverseDirectories subdir folderProcessor currentResults resultCombiner
                with
                | :? UnauthorizedAccessException -> printfn $"Access denied: {subdir}"
                | :? DirectoryNotFoundException -> printfn $"Directory not found: {subdir}"
                | :? PathTooLongException -> printfn $"Path too long: {subdir}"
                | ex -> printfn $"Error traversing {subdir}: {ex.Message}"
        with
        | :? UnauthorizedAccessException -> printfn $"Access denied to directory: {rootDir}"
        | :? DirectoryNotFoundException -> printfn $"Directory not found: {rootDir}"
        | :? PathTooLongException -> printfn $"Path too long: {rootDir}"
        | ex -> printfn $"Error accessing directory {rootDir}: {ex.Message}"

        currentResults

    /// Traverses multiple root directories and combines results
    let traverseMultipleRoots<'T>
        (rootDirs: string list)
        (folderProcessor: string -> 'T list)
        (initialResults: 'T list)
        (resultCombiner: 'T list -> 'T list -> 'T list)
        =

        let allResults = System.Collections.Generic.List<'T>()
        allResults.AddRange(initialResults)

        for rootDir in rootDirs do
            printfn $"Traversing directory: {rootDir}"

            try
                let dirResults = traverseDirectories rootDir folderProcessor [] resultCombiner
                allResults.AddRange(dirResults)
            with ex ->
                printfn $"Error traversing root directory {rootDir}: {ex.Message}"

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
        =

        printfn "Traversing all local drives..."
        let localDrives = getLocalDrives ()
        traverseMultipleRoots localDrives folderProcessor initialResults resultCombiner