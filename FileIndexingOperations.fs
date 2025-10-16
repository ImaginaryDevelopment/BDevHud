namespace BDevHud

open System
open IO.Adapter
open SqlLite.Adapter

/// File indexing operations with blacklist support and detailed logging
module FileIndexingOperations =

    /// Repository blacklist - repositories to skip during indexing
    let private repositoryBlacklist = [
        "archivedOG"
        "SBS.Archived.og-src"
    ]

    /// Check if a repository should be skipped based on blacklist
    let private shouldSkipRepository (repoName: string) : bool =
        repositoryBlacklist
        |> List.exists (fun blacklisted -> 
            repoName.Contains(blacklisted, System.StringComparison.OrdinalIgnoreCase))

    /// Perform file indexing on repositories if requested
    let performFileIndexing (repoInfos: GitRepoInfo list) =
        if CommandLineArgs.shouldIndexFiles() then
            printfn "\n%s" (String.replicate 50 "=")
            printfn "File Indexing Operations"
            printfn "%s" (String.replicate 50 "=")
            printfn "Indexing Terraform and PowerShell files in %d repositories..." repoInfos.Length
            printfn """Repository blacklist: %s""" (String.concat ", " repositoryBlacklist)

            let mutable totalFilesIndexed = 0
            let mutable skippedRepos = 0

            repoInfos
            |> List.iteri (fun i repoInfo ->
                if shouldSkipRepository repoInfo.RepoName then
                    printfn "\n[%d/%d] ⚠️ Skipping blacklisted repository: %s" (i + 1) repoInfos.Length repoInfo.RepoName
                    skippedRepos <- skippedRepos + 1
                else
                    printfn "\n[%d/%d] Indexing repository: %s" (i + 1) repoInfos.Length repoInfo.RepoName
                    printfn "    Path: %s" repoInfo.Path

                    try
                        // Get indexable files (terraform and powershell)
                        let indexableFiles = FileIndex.getIndexableFiles repoInfo.Path
                        printfn "    Found %d indexable files" indexableFiles.Length

                        if indexableFiles.Length > 0 then
                            // Index each file with detailed logging
                            let mutable fileIndex = 0
                            for (filePath, fileType) in indexableFiles do
                                fileIndex <- fileIndex + 1
                                let fileName = RepositoryDiscovery.getFileName filePath
                                let fileSizeBytes = RepositoryDiscovery.getFileInfo filePath
                                let fileSizeKB = fileSizeBytes / 1024L
                                printfn "    [%d/%d] Starting file: %s (%s, %d KB)" fileIndex indexableFiles.Length fileName fileType fileSizeKB
                                
                            // Index the repository
                            FileIndex.indexRepository repoInfo.Path
                            totalFilesIndexed <- totalFilesIndexed + indexableFiles.Length
                            printfn "    ✅ Indexed %d files" indexableFiles.Length
                        else
                            printfn "    ⚠️ No Terraform or PowerShell files found"
                    with
                    | ex ->
                        printfn "    ❌ Error indexing repository: %s" ex.Message)

            printfn "\nFile indexing completed: %d total files indexed across %d repositories" totalFilesIndexed (repoInfos.Length - skippedRepos)
            printfn "Repositories processed: %d, skipped: %d" (repoInfos.Length - skippedRepos) skippedRepos
        else
            printfn "\n💡 Use --index-files flag to index Terraform and PowerShell files for search"