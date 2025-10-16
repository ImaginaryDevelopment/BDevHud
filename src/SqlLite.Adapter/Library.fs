namespace SqlLite.Adapter

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.Data.Sqlite

// Repository information for caching
type CachedRepoInfo = {
    Path: string
    RepoName: string
    RepoUrl: string
    LastPullAttempt: DateTime option
}

// File information for indexing
type IndexedFile = {
    Id: int
    RepoPath: string
    FilePath: string
    FileType: string // "terraform" or "powershell"
    Content: string
    LastModified: DateTime
    FileSize: int64
}

// Trigram search entry
type TrigramEntry = {
    Trigram: string
    FileId: int
    Position: int
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

/// File indexing and trigram search functionality
module FileIndex =
    
    let private dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BDevHud", "file_index.db")
    
    // Ensure the directory exists
    let private ensureDirectoryExists () =
        let dir = Path.GetDirectoryName(dbPath)
        if not (Directory.Exists(dir)) then
            Directory.CreateDirectory(dir) |> ignore
    
    // Initialize the database and create tables
    let private initializeDatabase () =
        ensureDirectoryExists()
        
        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        
        // Set case insensitive collation for the database
        let pragmaCommand = "PRAGMA case_sensitive_like = OFF"
        use pragmaCmd = new SqliteCommand(pragmaCommand, connection)
        pragmaCmd.ExecuteNonQuery() |> ignore
        
        // Create indexed_files table
        let createFilesTableCommand = """
            CREATE TABLE IF NOT EXISTS indexed_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                repo_path TEXT NOT NULL COLLATE NOCASE,
                file_path TEXT NOT NULL COLLATE NOCASE,
                file_type TEXT NOT NULL COLLATE NOCASE,
                content TEXT NOT NULL COLLATE NOCASE,
                last_modified TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(repo_path, file_path)
            )
        """
        
        // Create trigrams table for search with case insensitive trigrams
        let createTrigramsTableCommand = """
            CREATE TABLE IF NOT EXISTS trigrams (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trigram TEXT NOT NULL COLLATE NOCASE,
                file_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                FOREIGN KEY (file_id) REFERENCES indexed_files (id) ON DELETE CASCADE
            )
        """
        
        // Create index on trigrams for fast search
        let createTrigramIndexCommand = """
            CREATE INDEX IF NOT EXISTS idx_trigrams_trigram ON trigrams(trigram)
        """
        
        // Create index on file paths for fast lookups
        let createFilePathIndexCommand = """
            CREATE INDEX IF NOT EXISTS idx_files_repo_path ON indexed_files(repo_path)
        """
        
        use command1 = new SqliteCommand(createFilesTableCommand, connection)
        command1.ExecuteNonQuery() |> ignore
        
        use command2 = new SqliteCommand(createTrigramsTableCommand, connection)
        command2.ExecuteNonQuery() |> ignore
        
        use command3 = new SqliteCommand(createTrigramIndexCommand, connection)
        command3.ExecuteNonQuery() |> ignore
        
        use command4 = new SqliteCommand(createFilePathIndexCommand, connection)
        command4.ExecuteNonQuery() |> ignore
    
    // Generate trigrams from text
    let private generateTrigrams (text: string) : (string * int) list =
        if text.Length < 3 then []
        else
            let normalizedText = text.ToLowerInvariant()
            [0..normalizedText.Length-3]
            |> List.map (fun i -> normalizedText.Substring(i, 3), i)
            |> List.distinct
    
    // Check if file should be indexed (terraform or powershell)
    let private shouldIndexFile (filePath: string) : string option =
        let extension = Path.GetExtension(filePath).ToLowerInvariant()
        match extension with
        | ".tf" | ".tfvars" | ".hcl" -> Some "terraform"
        | ".ps1" | ".psm1" | ".psd1" -> Some "powershell"
        | _ -> None
    
    // Get all terraform and powershell files in a repository
    let getIndexableFiles (repoPath: string) : (string * string) list =
        try
            if not (Directory.Exists(repoPath)) then []
            else
                Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
                |> Seq.choose (fun filePath ->
                    match shouldIndexFile filePath with
                    | Some fileType -> Some (filePath, fileType)
                    | None -> None)
                |> Seq.toList
        with
        | ex ->
            printfn $"Error scanning files in {repoPath}: {ex.Message}"
            []
    
    // Index a single file
    let indexFile (repoPath: string) (filePath: string) (fileType: string) : unit =
        try
            if not (File.Exists(filePath)) then
                printfn $"File not found: {filePath}"
            else
                let fileInfo = FileInfo(filePath)
                let content = File.ReadAllText(filePath)
                let lastModified = fileInfo.LastWriteTimeUtc
                let fileSize = fileInfo.Length
                
                initializeDatabase()
                
                use connection = new SqliteConnection($"Data Source={dbPath}")
                connection.Open()
                
                // Insert or update file
                let upsertFileCommand = """
                    INSERT INTO indexed_files (repo_path, file_path, file_type, content, last_modified, file_size, updated_at)
                    VALUES (@repo_path, @file_path, @file_type, @content, @last_modified, @file_size, @updated_at)
                    ON CONFLICT(repo_path, file_path) DO UPDATE SET
                        content = excluded.content,
                        last_modified = excluded.last_modified,
                        file_size = excluded.file_size,
                        updated_at = excluded.updated_at
                """
                
                use fileCommand = new SqliteCommand(upsertFileCommand, connection)
                fileCommand.Parameters.AddWithValue("@repo_path", repoPath) |> ignore
                fileCommand.Parameters.AddWithValue("@file_path", filePath) |> ignore
                fileCommand.Parameters.AddWithValue("@file_type", fileType) |> ignore
                fileCommand.Parameters.AddWithValue("@content", content) |> ignore
                fileCommand.Parameters.AddWithValue("@last_modified", lastModified.ToString("O")) |> ignore
                fileCommand.Parameters.AddWithValue("@file_size", fileSize) |> ignore
                fileCommand.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("O")) |> ignore
                
                fileCommand.ExecuteNonQuery() |> ignore
                
                // Get the file ID
                let getFileIdCommand = """
                    SELECT id FROM indexed_files WHERE repo_path = @repo_path AND file_path = @file_path
                """
                
                use getIdCommand = new SqliteCommand(getFileIdCommand, connection)
                getIdCommand.Parameters.AddWithValue("@repo_path", repoPath) |> ignore
                getIdCommand.Parameters.AddWithValue("@file_path", filePath) |> ignore
                
                let fileId = getIdCommand.ExecuteScalar() :?> int64 |> int
                
                // Delete existing trigrams for this file
                let deleteTrigramsCommand = """
                    DELETE FROM trigrams WHERE file_id = @file_id
                """
                
                use deleteCommand = new SqliteCommand(deleteTrigramsCommand, connection)
                deleteCommand.Parameters.AddWithValue("@file_id", fileId) |> ignore
                deleteCommand.ExecuteNonQuery() |> ignore
                
                // Generate and insert trigrams
                let trigrams = generateTrigrams content
                
                for (trigram, position) in trigrams do
                    let insertTrigramCommand = """
                        INSERT INTO trigrams (trigram, file_id, position)
                        VALUES (@trigram, @file_id, @position)
                    """
                    
                    use trigramCommand = new SqliteCommand(insertTrigramCommand, connection)
                    trigramCommand.Parameters.AddWithValue("@trigram", trigram) |> ignore
                    trigramCommand.Parameters.AddWithValue("@file_id", fileId) |> ignore
                    trigramCommand.Parameters.AddWithValue("@position", position) |> ignore
                    trigramCommand.ExecuteNonQuery() |> ignore
                
                printfn $"Indexed {fileType} file: {Path.GetFileName(filePath)} ({trigrams.Length} trigrams)"
        with
        | ex ->
            printfn $"Error indexing file {filePath}: {ex.Message}"
    
    // Index all files in a repository
    let indexRepository (repoPath: string) : unit =
        try
            printfn $"Indexing repository: {repoPath}"
            let files = getIndexableFiles repoPath
            printfn $"Found {files.Length} indexable files"
            
            for (filePath, fileType) in files do
                indexFile repoPath filePath fileType
            
            printfn $"Completed indexing repository: {repoPath}"
        with
        | ex ->
            printfn $"Error indexing repository {repoPath}: {ex.Message}"
    
    // Search for text using trigram matching
    let searchText (searchTerm: string) : IndexedFile list =
        try
            if searchTerm.Length < 3 then
                printfn "Search term must be at least 3 characters long"
                []
            else
                initializeDatabase()
                
                let searchTrigrams = generateTrigrams searchTerm |> List.map fst
                
                use connection = new SqliteConnection($"Data Source={dbPath}")
                connection.Open()
                
                // Find files that contain all trigrams from the search term
                let searchQuery = """
                    SELECT f.id, f.repo_path, f.file_path, f.file_type, f.content, f.last_modified, f.file_size
                    FROM indexed_files f
                    WHERE f.id IN (
                        SELECT file_id
                        FROM trigrams
                        WHERE trigram IN (""" + String.Join(",", searchTrigrams |> List.mapi (fun i _ -> $"@trigram{i}")) + """)
                        GROUP BY file_id
                        HAVING COUNT(DISTINCT trigram) = @trigram_count
                    )
                    ORDER BY f.repo_path, f.file_path
                """
                
                use command = new SqliteCommand(searchQuery, connection)
                
                // Add parameters for each trigram
                searchTrigrams |> List.iteri (fun i trigram ->
                    command.Parameters.AddWithValue($"@trigram{i}", trigram) |> ignore)
                
                command.Parameters.AddWithValue("@trigram_count", searchTrigrams.Length) |> ignore
                
                use reader = command.ExecuteReader()
                
                let mutable results = []
                
                while reader.Read() do
                    let file = {
                        Id = reader.["id"] :?> int64 |> int
                        RepoPath = reader.["repo_path"] :?> string
                        FilePath = reader.["file_path"] :?> string
                        FileType = reader.["file_type"] :?> string
                        Content = reader.["content"] :?> string
                        LastModified = DateTime.Parse(reader.["last_modified"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                        FileSize = reader.["file_size"] :?> int64
                    }
                    results <- file :: results
                
                List.rev results
        with
        | ex ->
            printfn $"Error searching text '{searchTerm}': {ex.Message}"
            []
    
    // Get all indexed files for a repository
    let getIndexedFilesForRepo (repoPath: string) : IndexedFile list =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let selectCommand = """
                SELECT id, repo_path, file_path, file_type, content, last_modified, file_size
                FROM indexed_files
                WHERE repo_path = @repo_path
                ORDER BY file_path
            """
            
            use command = new SqliteCommand(selectCommand, connection)
            command.Parameters.AddWithValue("@repo_path", repoPath) |> ignore
            
            use reader = command.ExecuteReader()
            
            let mutable files = []
            
            while reader.Read() do
                let file = {
                    Id = reader.["id"] :?> int64 |> int
                    RepoPath = reader.["repo_path"] :?> string
                    FilePath = reader.["file_path"] :?> string
                    FileType = reader.["file_type"] :?> string
                    Content = reader.["content"] :?> string
                    LastModified = DateTime.Parse(reader.["last_modified"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    FileSize = reader.["file_size"] :?> int64
                }
                files <- file :: files
            
            List.rev files
        with
        | ex ->
            printfn $"Error getting indexed files for {repoPath}: {ex.Message}"
            []
    
    // Check if the database has any indexed files
    let hasIndexedData () : bool =
        try
            if not (File.Exists(dbPath)) then
                false
            else
                initializeDatabase()
                
                use connection = new SqliteConnection($"Data Source={dbPath}")
                connection.Open()
                
                let countCommand = "SELECT COUNT(*) FROM indexed_files"
                use command = new SqliteCommand(countCommand, connection)
                let count = command.ExecuteScalar() :?> int64
                count > 0L
        with
        | ex ->
            printfn $"Error checking indexed data: {ex.Message}"
            false

    // Get statistics about indexed data
    let getIndexStats () : (int * int * string list) =
        try
            if not (File.Exists(dbPath)) then
                (0, 0, [])
            else
                initializeDatabase()
                
                use connection = new SqliteConnection($"Data Source={dbPath}")
                connection.Open()
                
                // Get file count
                let fileCountCommand = "SELECT COUNT(*) FROM indexed_files"
                use fileCountCmd = new SqliteCommand(fileCountCommand, connection)
                let fileCount = fileCountCmd.ExecuteScalar() :?> int64 |> int
                
                // Get trigram count
                let trigramCountCommand = "SELECT COUNT(*) FROM trigrams"
                use trigramCountCmd = new SqliteCommand(trigramCountCommand, connection)
                let trigramCount = trigramCountCmd.ExecuteScalar() :?> int64 |> int
                
                // Get unique repositories
                let repoCommand = "SELECT DISTINCT repo_path FROM indexed_files ORDER BY repo_path"
                use repoCmd = new SqliteCommand(repoCommand, connection)
                use reader = repoCmd.ExecuteReader()
                
                let mutable repos = []
                while reader.Read() do
                    let repoPath = reader.["repo_path"] :?> string
                    repos <- Path.GetFileName(repoPath) :: repos
                
                (fileCount, trigramCount, List.rev repos)
        with
        | ex ->
            printfn $"Error getting index stats: {ex.Message}"
            (0, 0, [])

    // Clear all indexed data
    let clearIndex () : unit =
        try
            if File.Exists(dbPath) then
                File.Delete(dbPath)
                printfn "File index cleared successfully"
            else
                printfn "No index file found to clear"
        with
        | ex ->
            printfn $"Error clearing file index: {ex.Message}"