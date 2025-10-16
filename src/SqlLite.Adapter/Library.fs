namespace SqlLite.Adapter

open System
open System.IO
open Microsoft.Data.Sqlite

// Repository information for caching
type CachedRepoInfo = {
    Path: string
    RepoName: string
    RepoUrl: string
    LastPullAttempt: DateTime option
}

/// SQLite-based cache for git repository information with pull attempt tracking
module GitCache =
    
    let private dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BDevHud", "git_cache.db")
    
    // Ensure the directory exists
    let private ensureDirectoryExists () =
        let dir = Path.GetDirectoryName(dbPath)
        if not (Directory.Exists(dir)) then
            Directory.CreateDirectory(dir) |> ignore
    
    // Initialize the database and create tables if they don't exist
    let private initializeDatabase () =
        ensureDirectoryExists()
        
        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        
        let createTableCommand = """
            CREATE TABLE IF NOT EXISTS repo_cache (
                path TEXT PRIMARY KEY,
                repo_name TEXT NOT NULL,
                repo_url TEXT NOT NULL,
                last_pull_attempt TEXT,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
        """
        
        use command = new SqliteCommand(createTableCommand, connection)
        command.ExecuteNonQuery() |> ignore
    
    // Check if enough time has elapsed since last pull attempt (30 minutes)
    let shouldAttemptPull (lastPullAttempt: DateTime option) : bool =
        match lastPullAttempt with
        | None -> true // Never attempted, should try
        | Some lastAttempt ->
            let now = DateTime.UtcNow
            let timeSinceLastAttempt = now - lastAttempt
            timeSinceLastAttempt.TotalMinutes >= 30.0
    
    // Get cached repository information
    let getCachedRepo (repoPath: string) : CachedRepoInfo option =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let selectCommand = """
                SELECT path, repo_name, repo_url, last_pull_attempt
                FROM repo_cache 
                WHERE path = @path
            """
            
            use command = new SqliteCommand(selectCommand, connection)
            command.Parameters.AddWithValue("@path", repoPath) |> ignore
            
            use reader = command.ExecuteReader()
            
            if reader.Read() then
                let lastPullAttemptObj = reader.["last_pull_attempt"]
                let lastPullAttempt = 
                    if lastPullAttemptObj = DBNull.Value then
                        None
                    else
                        let lastPullAttemptStr = lastPullAttemptObj :?> string
                        if String.IsNullOrEmpty(lastPullAttemptStr) then
                            None
                        else
                            Some (DateTime.Parse(lastPullAttemptStr, null, System.Globalization.DateTimeStyles.RoundtripKind))
                
                Some {
                    Path = reader.["path"] :?> string
                    RepoName = reader.["repo_name"] :?> string
                    RepoUrl = reader.["repo_url"] :?> string
                    LastPullAttempt = lastPullAttempt
                }
            else
                None
        with
        | ex ->
            printfn $"Error reading from cache for {repoPath}: {ex.Message}"
            None
    
    // Update or insert repository information
    let upsertRepo (repoInfo: CachedRepoInfo) : unit =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let upsertCommand = """
                INSERT INTO repo_cache (path, repo_name, repo_url, last_pull_attempt, updated_at)
                VALUES (@path, @repo_name, @repo_url, @last_pull_attempt, @updated_at)
                ON CONFLICT(path) DO UPDATE SET
                    repo_name = excluded.repo_name,
                    repo_url = excluded.repo_url,
                    last_pull_attempt = excluded.last_pull_attempt,
                    updated_at = excluded.updated_at
            """
            
            use command = new SqliteCommand(upsertCommand, connection)
            command.Parameters.AddWithValue("@path", repoInfo.Path) |> ignore
            command.Parameters.AddWithValue("@repo_name", repoInfo.RepoName) |> ignore
            command.Parameters.AddWithValue("@repo_url", repoInfo.RepoUrl) |> ignore
            command.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("O")) |> ignore
            
            match repoInfo.LastPullAttempt with
            | Some dateTime -> command.Parameters.AddWithValue("@last_pull_attempt", dateTime.ToString("O")) |> ignore
            | None -> command.Parameters.AddWithValue("@last_pull_attempt", DBNull.Value) |> ignore
            
            command.ExecuteNonQuery() |> ignore
        with
        | ex ->
            printfn $"Error writing to cache for {repoInfo.Path}: {ex.Message}"
    
    // Update just the last pull attempt timestamp
    let updateLastPullAttempt (repoPath: string) : unit =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let updateCommand = """
                UPDATE repo_cache 
                SET last_pull_attempt = @last_pull_attempt, updated_at = @updated_at
                WHERE path = @path
            """
            
            use command = new SqliteCommand(updateCommand, connection)
            command.Parameters.AddWithValue("@path", repoPath) |> ignore
            command.Parameters.AddWithValue("@last_pull_attempt", DateTime.UtcNow.ToString("O")) |> ignore
            command.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("O")) |> ignore
            
            command.ExecuteNonQuery() |> ignore
        with
        | ex ->
            printfn $"Error updating pull timestamp for {repoPath}: {ex.Message}"
    
    // Get all cached repositories
    let getAllCachedRepos () : CachedRepoInfo list =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let selectAllCommand = """
                SELECT path, repo_name, repo_url, last_pull_attempt
                FROM repo_cache 
                ORDER BY repo_name
            """
            
            use command = new SqliteCommand(selectAllCommand, connection)
            use reader = command.ExecuteReader()
            
            let mutable repos = []
            
            while reader.Read() do
                let lastPullAttemptObj = reader.["last_pull_attempt"]
                let lastPullAttempt = 
                    if lastPullAttemptObj = DBNull.Value then
                        None
                    else
                        let lastPullAttemptStr = lastPullAttemptObj :?> string
                        if String.IsNullOrEmpty(lastPullAttemptStr) then
                            None
                        else
                            Some (DateTime.Parse(lastPullAttemptStr, null, System.Globalization.DateTimeStyles.RoundtripKind))
                
                let repo = {
                    Path = reader.["path"] :?> string
                    RepoName = reader.["repo_name"] :?> string
                    RepoUrl = reader.["repo_url"] :?> string
                    LastPullAttempt = lastPullAttempt
                }
                repos <- repo :: repos
            
            List.rev repos
        with
        | ex ->
            printfn $"Error reading all cached repos: {ex.Message}"
            []
    
    // Clear all cached data (for testing or cleanup)
    let clearCache () : unit =
        try
            if File.Exists(dbPath) then
                File.Delete(dbPath)
                printfn "Cache cleared successfully"
            else
                printfn "No cache file found to clear"
        with
        | ex ->
            printfn $"Error clearing cache: {ex.Message}"