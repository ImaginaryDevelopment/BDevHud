namespace BDevHud

open System
open System.IO

/// Configuration for repository blacklisting to exclude certain repos from operations
module BlacklistConfig =

    /// Repository patterns to exclude from git pull operations
    /// Add repository names or path patterns that should not be pulled
    let private pullBlacklist = [
        // Archived or legacy repositories
        "archived";
        "legacy";
        "old";
        "deprecated";
        "backup";
        
        // Known problematic repositories
        "largefiles";
        "binaries";
        "media";
        
        // Template or example repositories
        "template";
        "example";
        "demo";
        "poc";
        
        // Add specific repository names here if needed
        // "specific-repo-name";
    ]

    /// Repository patterns to exclude from search indexing
    /// These are broader patterns that might be useful for search but not pulls
    let private searchBlacklist = [
        "node_modules";
        "bin";
        "obj";
        ".git";
        ".vs";
        ".vscode";
    ]

    /// Checks if a repository should be excluded from pull operations
    /// Based on repository name or path containing blacklisted patterns
    let shouldExcludeFromPull (repoName: string) (repoPath: string) : bool =
        let lowerRepoName = repoName.ToLowerInvariant()
        let lowerRepoPath = repoPath.ToLowerInvariant()
        
        pullBlacklist
        |> List.exists (fun pattern ->
            lowerRepoName.Contains(pattern.ToLowerInvariant()) ||
            lowerRepoPath.Contains(pattern.ToLowerInvariant()))

    /// Checks if a repository should be excluded from search indexing
    let shouldExcludeFromSearch (repoName: string) (repoPath: string) : bool =
        let lowerRepoName = repoName.ToLowerInvariant()
        let lowerRepoPath = repoPath.ToLowerInvariant()
        
        searchBlacklist
        |> List.exists (fun pattern ->
            lowerRepoName.Contains(pattern.ToLowerInvariant()) ||
            lowerRepoPath.Contains(pattern.ToLowerInvariant()))

    /// Gets the reason why a repository was blacklisted for pulls
    let getPullBlacklistReason (repoName: string) (repoPath: string) : string option =
        let lowerRepoName = repoName.ToLowerInvariant()
        let lowerRepoPath = repoPath.ToLowerInvariant()
        
        pullBlacklist
        |> List.tryFind (fun pattern ->
            lowerRepoName.Contains(pattern.ToLowerInvariant()) ||
            lowerRepoPath.Contains(pattern.ToLowerInvariant()))
        |> Option.map (fun pattern -> $"blacklisted pattern: '{pattern}'")

    /// Gets all pull blacklist patterns (for debugging/configuration display)
    let getPullBlacklistPatterns () : string list =
        pullBlacklist

    /// Gets all search blacklist patterns (for debugging/configuration display)
    let getSearchBlacklistPatterns () : string list =
        searchBlacklist