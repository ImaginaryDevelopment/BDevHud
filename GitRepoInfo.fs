namespace BDevHud

open System
open IO.Adapter

/// Represents git repository information
type GitRepoInfo = {
    Path: string
    RemoteUrl: string option
    RepoName: string
}

/// Functions for working with git repository information
module GitRepoInfo =
    
    /// Create GitRepoInfo from a git folder path and remote URL
    let create (gitFolderPath: string) (remoteUrl: string option) : GitRepoInfo =
        let parentDir = RepositoryDiscovery.getParentDirectory gitFolderPath
        {
            Path = parentDir
            RemoteUrl = remoteUrl
            RepoName = RepositoryDiscovery.getRepositoryName parentDir
        }

    /// Display git repository information with remote details
    let displayRepoInfo (index: int) (repoInfo: GitRepoInfo) : unit =
        printfn "%d. %s" (index + 1) repoInfo.Path

        match repoInfo.RemoteUrl with
        | Some url ->
            // Decode URL-encoded components for better readability
            let decodedUrl = System.Web.HttpUtility.UrlDecode(url)
            
            let operationType =
                if url.Contains("git@") then "SSH"
                elif url.Contains("https://") then "HTTPS" 
                else "Other"
            
            printfn "    Remote: %s (%s)" decodedUrl operationType
        | None ->
            printfn "    Remote: (none)"