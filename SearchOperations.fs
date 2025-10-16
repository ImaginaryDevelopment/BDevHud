namespace BDevHud

open System
open IO.Adapter
open SqlLite.Adapter

/// Text search operations using trigram matching
module SearchOperations =

    /// Perform text search using trigram matching if requested
    let performTextSearch () =
        match CommandLineArgs.getSearchTerm() with
        | Some searchTerm ->
            printfn "\n%s" (String.replicate 50 "=")
            printfn "Trigram Text Search"
            printfn "%s" (String.replicate 50 "=")
            printfn "Searching for: '%s'" searchTerm
            
            // Check if we have indexed data
            if FileIndex.hasIndexedData() then
                let (fileCount, trigramCount, repos) = FileIndex.getIndexStats()
                printfn "Searching %d indexed files with %d trigrams across %d repositories..." fileCount trigramCount repos.Length
                
                let results = FileIndex.searchText searchTerm
                
                if results.Length > 0 then
                    printfn "\nFound %d matching file(s):" results.Length
                    
                    results
                    |> List.iteri (fun i file ->
                        printfn "\n[%d] %s file:" (i + 1) (file.FileType.ToUpper())
                        printfn "    Repository: %s" (RepositoryDiscovery.getFileName file.RepoPath)
                        printfn "    File: %s" (RepositoryDiscovery.getFileName file.FilePath)
                        printfn "    Full path: %s" file.FilePath
                        printfn "    Size: %d bytes" file.FileSize)
                else
                    printfn "\nNo files found containing '%s'" searchTerm
            else
                printfn "❌ No indexed data found. Use --index-files flag first to index repository files."
                printfn "\n💡 Use --index-files flag to index Terraform and PowerShell files for search"
        | None ->
            printfn "\n💡 Use --search=<term> to search indexed files"