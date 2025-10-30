namespace SqlLite.Adapter

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading.Tasks
open System.Collections.Concurrent
open Microsoft.Data.Sqlite

// Repository information for caching
type CachedRepoInfo = {
    Path: string
    RepoName: string
    RepoUrl: string
    CurrentBranch: string option
    LastPullAttempt: DateTime option
    LastSuccessfulPull: DateTime option
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

// GitHub repository entry
type GitHubRepo = {
    Id: int64
    FullName: string
    Name: string
    Owner: string
    Description: string
    CloneUrl: string
    SshUrl: string
    IsPrivate: bool
    IsFork: bool
    Language: string option
    CreatedAt: DateTime
    UpdatedAt: DateTime
    PushedAt: DateTime option
    IndexedAt: DateTime
    SecretsCount: int option
    RunnersCount: int option
}

// Octopus deployment step entry
type OctopusStep = {
    Id: int
    ProjectName: string
    StepName: string
    StepId: string
    ActionType: string
    PropertiesJson: string
    StepTemplateId: string option
    IndexedAt: DateTime
}

// Octopus step template
type OctopusStepTemplate = {
    Id: int
    TemplateId: string
    TemplateName: string
    Description: string
    ScriptBody: string option
    PropertiesJson: string
    IndexedAt: DateTime
}

// Octopus trigram entry
type OctopusTrigram = {
    Trigram: string
    StepId: int
    Position: int
    PropertyKey: string
}

// Octopus template trigram entry
type OctopusTemplateTrigram = {
    Trigram: string
    TemplateId: int
    Position: int
    PropertyKey: string
}

// Octopus step search result with deserialized properties
type OctopusStepResult = {
    Id: int
    ProjectName: string
    StepName: string
    StepId: string
    ActionType: string
    Properties: Map<string, string>
    StepTemplateName: string option
    IndexedAt: DateTime
}

// Octopus step template search result with deserialized properties
type OctopusStepTemplateResult = {
    Id: int
    TemplateId: string
    TemplateName: string
    Description: string
    ScriptBody: string option
    Properties: Map<string, string>
    IndexedAt: DateTime
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
                current_branch TEXT,
                last_pull_attempt TEXT,
                last_successful_pull TEXT,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
        """
        
        use command = new SqliteCommand(createTableCommand, connection)
        command.ExecuteNonQuery() |> ignore
        
        // Add the last_successful_pull column if it doesn't exist (migration)
        let addColumnCommand = """
            ALTER TABLE repo_cache ADD COLUMN last_successful_pull TEXT;
        """
        try
            use addColumnCmd = new SqliteCommand(addColumnCommand, connection)
            addColumnCmd.ExecuteNonQuery() |> ignore
        with
        | :? SqliteException as ex when ex.Message.Contains("duplicate column name") -> 
            // Column already exists, ignore
            ()
        | _ -> reraise()
        
        // Add the current_branch column if it doesn't exist (migration)
        let addBranchColumnCommand = """
            ALTER TABLE repo_cache ADD COLUMN current_branch TEXT;
        """
        try
            use addBranchCmd = new SqliteCommand(addBranchColumnCommand, connection)
            addBranchCmd.ExecuteNonQuery() |> ignore
        with
        | :? SqliteException as ex when ex.Message.Contains("duplicate column name") -> 
            // Column already exists, ignore
            ()
        | _ -> reraise()
    
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
                SELECT path, repo_name, repo_url, current_branch, last_pull_attempt, last_successful_pull
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
                
                let lastSuccessfulPullObj = reader.["last_successful_pull"]
                let lastSuccessfulPull = 
                    if lastSuccessfulPullObj = DBNull.Value then
                        None
                    else
                        let lastSuccessfulPullStr = lastSuccessfulPullObj :?> string
                        if String.IsNullOrEmpty(lastSuccessfulPullStr) then
                            None
                        else
                            Some (DateTime.Parse(lastSuccessfulPullStr, null, System.Globalization.DateTimeStyles.RoundtripKind))
                
                let currentBranchObj = reader.["current_branch"]
                let currentBranch = 
                    if currentBranchObj = DBNull.Value then
                        None
                    else
                        let branchStr = currentBranchObj :?> string
                        if String.IsNullOrEmpty(branchStr) then
                            None
                        else
                            Some branchStr
                
                Some {
                    Path = reader.["path"] :?> string
                    RepoName = reader.["repo_name"] :?> string
                    RepoUrl = reader.["repo_url"] :?> string
                    CurrentBranch = currentBranch
                    LastPullAttempt = lastPullAttempt
                    LastSuccessfulPull = lastSuccessfulPull
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
                INSERT INTO repo_cache (path, repo_name, repo_url, current_branch, last_pull_attempt, last_successful_pull, updated_at)
                VALUES (@path, @repo_name, @repo_url, @current_branch, @last_pull_attempt, @last_successful_pull, @updated_at)
                ON CONFLICT(path) DO UPDATE SET
                    repo_name = excluded.repo_name,
                    repo_url = excluded.repo_url,
                    current_branch = excluded.current_branch,
                    last_pull_attempt = excluded.last_pull_attempt,
                    last_successful_pull = excluded.last_successful_pull,
                    updated_at = excluded.updated_at
            """
            
            use command = new SqliteCommand(upsertCommand, connection)
            command.Parameters.AddWithValue("@path", repoInfo.Path) |> ignore
            command.Parameters.AddWithValue("@repo_name", repoInfo.RepoName) |> ignore
            command.Parameters.AddWithValue("@repo_url", repoInfo.RepoUrl) |> ignore
            command.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("O")) |> ignore
            
            match repoInfo.CurrentBranch with
            | Some branch -> command.Parameters.AddWithValue("@current_branch", branch) |> ignore
            | None -> command.Parameters.AddWithValue("@current_branch", DBNull.Value) |> ignore
            
            match repoInfo.LastPullAttempt with
            | Some dateTime -> command.Parameters.AddWithValue("@last_pull_attempt", dateTime.ToString("O")) |> ignore
            | None -> command.Parameters.AddWithValue("@last_pull_attempt", DBNull.Value) |> ignore
            
            match repoInfo.LastSuccessfulPull with
            | Some dateTime -> command.Parameters.AddWithValue("@last_successful_pull", dateTime.ToString("O")) |> ignore
            | None -> command.Parameters.AddWithValue("@last_successful_pull", DBNull.Value) |> ignore
            
            command.ExecuteNonQuery() |> ignore
        with
        | ex ->
            printfn $"Error writing to cache for {repoInfo.Path}: {ex.Message}"
    
    // Update just the last pull attempt timestamp
    let updateLastSuccessfulPull (repoPath: string) : unit =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let updateCommand = """
                UPDATE repo_cache 
                SET last_successful_pull = @last_successful_pull, updated_at = @updated_at
                WHERE path = @path
            """
            
            use command = new SqliteCommand(updateCommand, connection)
            command.Parameters.AddWithValue("@path", repoPath) |> ignore
            command.Parameters.AddWithValue("@last_successful_pull", DateTime.UtcNow.ToString("O")) |> ignore
            command.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("O")) |> ignore
            
            command.ExecuteNonQuery() |> ignore
        with
        | ex ->
            printfn $"Error updating last successful pull for {repoPath}: {ex.Message}"

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
                SELECT path, repo_name, repo_url, current_branch, last_pull_attempt, last_successful_pull
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
                
                let lastSuccessfulPullObj = reader.["last_successful_pull"]
                let lastSuccessfulPull = 
                    if lastSuccessfulPullObj = DBNull.Value then
                        None
                    else
                        let lastSuccessfulPullStr = lastSuccessfulPullObj :?> string
                        if String.IsNullOrEmpty(lastSuccessfulPullStr) then
                            None
                        else
                            Some (DateTime.Parse(lastSuccessfulPullStr, null, System.Globalization.DateTimeStyles.RoundtripKind))
                
                let currentBranchObj = reader.["current_branch"]
                let currentBranch = 
                    if currentBranchObj = DBNull.Value then
                        None
                    else
                        let branchStr = currentBranchObj :?> string
                        if String.IsNullOrEmpty(branchStr) then
                            None
                        else
                            Some branchStr
                
                let repo = {
                    Path = reader.["path"] :?> string
                    RepoName = reader.["repo_name"] :?> string
                    RepoUrl = reader.["repo_url"] :?> string
                    CurrentBranch = currentBranch
                    LastPullAttempt = lastPullAttempt
                    LastSuccessfulPull = lastSuccessfulPull
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
    
    let dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BDevHud", "file_index.db")
    
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

        // Create Octopus deployment steps table
        let createOctopusStepsTableCommand = """
            CREATE TABLE IF NOT EXISTS octopus_steps (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_name TEXT NOT NULL COLLATE NOCASE,
                step_name TEXT NOT NULL COLLATE NOCASE,
                step_id TEXT NOT NULL COLLATE NOCASE,
                action_type TEXT NOT NULL COLLATE NOCASE,
                properties_json TEXT NOT NULL COLLATE NOCASE,
                step_template_id TEXT COLLATE NOCASE,
                indexed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(project_name, step_id)
            )
        """

        use command5 = new SqliteCommand(createOctopusStepsTableCommand, connection)
        command5.ExecuteNonQuery() |> ignore

        // Migration: Add step_template_id column if it doesn't exist
        let checkColumnQuery = "PRAGMA table_info(octopus_steps)"
        use checkCmd = new SqliteCommand(checkColumnQuery, connection)
        use reader = checkCmd.ExecuteReader()
        let mutable hasStepTemplateIdColumn = false
        while reader.Read() do
            let columnName = reader.GetString(1) // Column name is at index 1
            if columnName = "step_template_id" then
                hasStepTemplateIdColumn <- true
        reader.Close()
        
        if not hasStepTemplateIdColumn then
            printfn "🔧 Migrating database: Adding step_template_id column to octopus_steps table..."
            let alterTableCommand = "ALTER TABLE octopus_steps ADD COLUMN step_template_id TEXT COLLATE NOCASE"
            use alterCmd = new SqliteCommand(alterTableCommand, connection)
            alterCmd.ExecuteNonQuery() |> ignore
            printfn "✅ Migration complete!"

        // Create Octopus step templates table
        let createOctopusStepTemplatesTableCommand = """
            CREATE TABLE IF NOT EXISTS octopus_step_templates (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                template_id TEXT NOT NULL COLLATE NOCASE UNIQUE,
                template_name TEXT NOT NULL COLLATE NOCASE,
                description TEXT COLLATE NOCASE,
                script_body TEXT COLLATE NOCASE,
                properties_json TEXT NOT NULL COLLATE NOCASE,
                indexed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
        """

        // Create Octopus trigrams table
        let createOctopusTrigramsTableCommand = """
            CREATE TABLE IF NOT EXISTS octopus_trigrams (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trigram TEXT NOT NULL COLLATE NOCASE,
                step_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                property_key TEXT NOT NULL COLLATE NOCASE,
                FOREIGN KEY (step_id) REFERENCES octopus_steps (id) ON DELETE CASCADE
            )
        """

        // Create Octopus step template trigrams table
        let createOctopusTemplateTrigramsTableCommand = """
            CREATE TABLE IF NOT EXISTS octopus_template_trigrams (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trigram TEXT NOT NULL COLLATE NOCASE,
                template_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                property_key TEXT NOT NULL COLLATE NOCASE,
                FOREIGN KEY (template_id) REFERENCES octopus_step_templates (id) ON DELETE CASCADE
            )
        """

        // Create indexes for Octopus tables
        let createOctopusTrigramIndexCommand = """
            CREATE INDEX IF NOT EXISTS idx_octopus_trigrams_trigram ON octopus_trigrams(trigram)
        """

        let createOctopusStepsIndexCommand = """
            CREATE INDEX IF NOT EXISTS idx_octopus_steps_project ON octopus_steps(project_name)
        """

        let createOctopusTemplateTrigramIndexCommand = """
            CREATE INDEX IF NOT EXISTS idx_octopus_template_trigrams_trigram ON octopus_template_trigrams(trigram)
        """

        let createOctopusTemplatesIndexCommand = """
            CREATE INDEX IF NOT EXISTS idx_octopus_templates_template_id ON octopus_step_templates(template_id)
        """

        use command5 = new SqliteCommand(createOctopusStepsTableCommand, connection)
        command5.ExecuteNonQuery() |> ignore

        use command6 = new SqliteCommand(createOctopusStepTemplatesTableCommand, connection)
        command6.ExecuteNonQuery() |> ignore

        use command7 = new SqliteCommand(createOctopusTrigramsTableCommand, connection)
        command7.ExecuteNonQuery() |> ignore

        use command8 = new SqliteCommand(createOctopusTemplateTrigramsTableCommand, connection)
        command8.ExecuteNonQuery() |> ignore

        use command9 = new SqliteCommand(createOctopusTrigramIndexCommand, connection)
        command9.ExecuteNonQuery() |> ignore

        use command10 = new SqliteCommand(createOctopusStepsIndexCommand, connection)
        command10.ExecuteNonQuery() |> ignore

        use command11 = new SqliteCommand(createOctopusTemplateTrigramIndexCommand, connection)
        command11.ExecuteNonQuery() |> ignore

        use command12 = new SqliteCommand(createOctopusTemplatesIndexCommand, connection)
        command12.ExecuteNonQuery() |> ignore
    
    // Generate trigrams from text with parallel processing for large texts
    let private generateTrigrams (text: string) : (string * int) list =
        if text.Length < 3 then []
        else
            let normalizedText = text.ToLowerInvariant()
            let length = normalizedText.Length
            
            if length > 10000 then
                // Use parallel processing for large texts
                [0..length-3]
                |> Array.ofList
                |> Array.Parallel.map (fun i -> normalizedText.Substring(i, 3), i)
                |> Array.toList
                |> List.distinct
            else
                // Use sequential processing for smaller texts
                [0..length-3]
                |> List.map (fun i -> normalizedText.Substring(i, 3), i)
                |> List.distinct
    
    // Check if file should be indexed (terraform or powershell)
    let private shouldIndexFile (filePath: string) : string option =
        let extension = Path.GetExtension(filePath).ToLowerInvariant()
        match extension with
        | ".tf" | ".tfvars" | ".hcl" -> Some "terraform"
        | ".ps1" | ".psm1" | ".psd1" -> Some "powershell"
        | _ -> None
    
    /// Directory patterns to exclude when scanning for files within repositories
    let private fileSearchBlacklist = [
        "node_modules";
        "bin";
        "obj";
        ".git";
        ".vs";
        ".vscode";
        "packages";
        ".nuget";
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

    /// Check if a file path contains any blacklisted directory segments
    let private isFileInBlacklistedDirectory (filePath: string) : bool =
        let pathSegments = filePath.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |], StringSplitOptions.RemoveEmptyEntries)
        pathSegments
        |> Array.exists (fun segment -> 
            fileSearchBlacklist
            |> List.exists (fun blacklisted -> 
                segment.Equals(blacklisted, StringComparison.OrdinalIgnoreCase)))

    // Get all terraform and powershell files in a repository, excluding blacklisted directories
    let getIndexableFiles (repoPath: string) : (string * string) list =
        try
            if not (Directory.Exists(repoPath)) then []
            else
                Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
                |> Seq.choose (fun filePath ->
                    // Skip files in blacklisted directories
                    if isFileInBlacklistedDirectory filePath then
                        None
                    else
                        match shouldIndexFile filePath with
                        | Some fileType -> Some (filePath, fileType)
                        | None -> None)
                |> Seq.toList
        with
        | ex ->
            printfn $"Error scanning files in {repoPath}: {ex.Message}"
            []
    
    // Check if a file needs to be reindexed based on last modified timestamp
    let private needsReindexing (repoPath: string) (filePath: string) (currentLastModified: DateTime) : bool =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let checkQuery = """
                SELECT last_modified FROM indexed_files 
                WHERE repo_path = @repo_path AND file_path = @file_path
            """
            
            use command = new SqliteCommand(checkQuery, connection)
            command.Parameters.AddWithValue("@repo_path", repoPath) |> ignore
            command.Parameters.AddWithValue("@file_path", filePath) |> ignore
            
            use reader = command.ExecuteReader()
            
            if reader.Read() then
                let storedLastModified = DateTime.Parse(reader.["last_modified"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                // File needs reindexing if current timestamp is newer than stored timestamp
                currentLastModified > storedLastModified
            else
                // File not in database, needs indexing
                true
        with
        | ex ->
            printfn $"Error checking if file needs reindexing {filePath}: {ex.Message}"
            // If there's an error, assume it needs reindexing to be safe
            true

    // Index a single file
    let indexFile (repoPath: string) (filePath: string) (fileType: string) : unit =
        try
            if not (File.Exists(filePath)) then
                printfn $"File not found: {filePath}"
            else
                let fileInfo = FileInfo(filePath)
                let lastModified = fileInfo.LastWriteTimeUtc
                let fileSize = fileInfo.Length
                
                // Skip 0KB files
                if fileSize = 0L then
                    printfn $"Skipping 0KB file: {Path.GetFileName(filePath)}"
                // Check if file needs reindexing based on timestamp
                elif not (needsReindexing repoPath filePath lastModified) then
                    printfn $"Skipping unchanged file: {Path.GetFileName(filePath)}"
                else
                    let content = File.ReadAllText(filePath)
                    
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
                    
                    // Generate trigrams in parallel and queue SQL inserts
                    let trigrams = generateTrigrams content
                    
                    // Use batch insert for better performance with large trigram counts
                    if trigrams.Length > 1000 then
                        // For large files, use batch processing to avoid memory issues
                        let batchSize = 1000
                        let batches = 
                            trigrams 
                            |> List.chunkBySize batchSize
                        
                        for batch in batches do
                            use transaction = connection.BeginTransaction()
                            try
                                for (trigram, position) in batch do
                                    let insertTrigramCommand = """
                                        INSERT INTO trigrams (trigram, file_id, position)
                                        VALUES (@trigram, @file_id, @position)
                                    """
                                    
                                    use trigramCommand = new SqliteCommand(insertTrigramCommand, connection, transaction)
                                    trigramCommand.Parameters.AddWithValue("@trigram", trigram) |> ignore
                                    trigramCommand.Parameters.AddWithValue("@file_id", fileId) |> ignore
                                    trigramCommand.Parameters.AddWithValue("@position", position) |> ignore
                                    trigramCommand.ExecuteNonQuery() |> ignore
                                
                                transaction.Commit()
                            with
                            | ex -> 
                                transaction.Rollback()
                                raise ex
                    else
                        // For smaller files, process normally
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
                    
                    // Check for large trigram count and display warning
                    if trigrams.Length > 5000 then
                        let relativePath = Path.GetRelativePath(repoPath, filePath)
                        printfn $"⚠️  WARNING: Large file with {trigrams.Length:N0} trigrams in {Path.GetFileName(repoPath)}/{relativePath}"
                    
                    printfn $"Indexed: {Path.GetFileName(filePath)}"
        with
        | ex ->
            printfn $"Error indexing file {filePath}: {ex.Message}"
    
    // Index all files in a repository with performance tracking
    let indexRepository (repoPath: string) : unit =
        try
            printfn $"Indexing repository: {repoPath}"
            let files = getIndexableFiles repoPath
            printfn $"Found {files.Length} indexable files"
            
            let mutable indexedCount = 0
            let mutable skippedCount = 0
            
            for (filePath, fileType) in files do
                let fileInfo = FileInfo(filePath)
                let lastModified = fileInfo.LastWriteTimeUtc
                
                if needsReindexing repoPath filePath lastModified then
                    indexFile repoPath filePath fileType
                    indexedCount <- indexedCount + 1
                else
                    skippedCount <- skippedCount + 1
            
            printfn $"Completed indexing repository: {repoPath}"
            printfn $"  📊 Statistics: {indexedCount} indexed, {skippedCount} skipped (unchanged)"
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
            printfn $"Error searching text: {ex.Message}"
            []

    // Search Octopus deployment steps using trigram matching
    let searchOctopusSteps (searchTerm: string) : OctopusStepResult list =
        try
            if searchTerm.Length < 3 then
                printfn "Search term must be at least 3 characters long"
                []
            else
                initializeDatabase()
                
                let searchTrigrams = generateTrigrams searchTerm |> List.map fst
                
                use connection = new SqliteConnection($"Data Source={dbPath}")
                connection.Open()
                
                // Find Octopus steps that contain all trigrams from the search term
                // Join with step templates to get template name if available
                let searchQuery = """
                    SELECT DISTINCT s.id, s.project_name, s.step_name, s.step_id, s.action_type, s.properties_json, s.indexed_at, t.template_name
                    FROM octopus_steps s
                    LEFT JOIN octopus_step_templates t ON s.step_template_id = t.template_id
                    WHERE s.id IN (
                        SELECT step_id
                        FROM octopus_trigrams
                        WHERE trigram IN (""" + String.Join(",", searchTrigrams |> List.mapi (fun i _ -> $"@trigram{i}")) + """)
                        GROUP BY step_id
                        HAVING COUNT(DISTINCT trigram) = @trigram_count
                    )
                    ORDER BY s.project_name, s.step_name
                """
                
                use command = new SqliteCommand(searchQuery, connection)
                
                // Add parameters for each trigram
                searchTrigrams |> List.iteri (fun i trigram ->
                    command.Parameters.AddWithValue($"@trigram{i}", trigram) |> ignore)
                
                command.Parameters.AddWithValue("@trigram_count", searchTrigrams.Length) |> ignore
                
                use reader = command.ExecuteReader()
                
                let mutable results = []
                
                while reader.Read() do
                    let propertiesJson = reader.["properties_json"] :?> string
                    let properties = 
                        if String.IsNullOrWhiteSpace(propertiesJson) then
                            Map.empty
                        else
                            try
                                System.Text.Json.JsonSerializer.Deserialize<Map<string, string>>(propertiesJson)
                            with
                            | _ -> Map.empty
                    
                    let templateName = 
                        if reader.["template_name"] = box DBNull.Value then
                            None
                        else
                            Some (reader.["template_name"] :?> string)
                    
                    let step = {
                        OctopusStepResult.Id = reader.["id"] :?> int64 |> int
                        ProjectName = reader.["project_name"] :?> string
                        StepName = reader.["step_name"] :?> string
                        StepId = reader.["step_id"] :?> string
                        ActionType = reader.["action_type"] :?> string
                        Properties = properties
                        StepTemplateName = templateName
                        IndexedAt = DateTime.Parse(reader.["indexed_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    }
                    results <- step :: results
                
                List.rev results
        with
        | ex ->
            printfn $"Error searching Octopus steps: {ex.Message}"
            []

    // Get indexing statistics
    let getIndexingStats () : unit =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            // Get file counts by type
            let fileStatsQuery = """
                SELECT 
                    file_type,
                    COUNT(*) as file_count,
                    SUM(file_size) as total_size,
                    MIN(last_modified) as oldest_file,
                    MAX(last_modified) as newest_file
                FROM indexed_files 
                GROUP BY file_type
                ORDER BY file_count DESC
            """
            
            use fileStatsCommand = new SqliteCommand(fileStatsQuery, connection)
            use reader = fileStatsCommand.ExecuteReader()
            
            printfn "\n📊 File Indexing Statistics:"
            printfn "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
            
            let mutable totalFiles = 0
            let mutable totalSize = 0L
            
            while reader.Read() do
                let fileType = reader.["file_type"] :?> string
                let fileCount = reader.["file_count"] :?> int64 |> int
                let totalSizeBytes = reader.["total_size"] :?> int64
                let oldestFile = reader.["oldest_file"] :?> string
                let newestFile = reader.["newest_file"] :?> string
                
                let sizeInMB = float totalSizeBytes / (1024.0 * 1024.0)
                
                printfn "📄 %s Files: %s files, %.2f MB" (fileType.ToUpper()) (fileCount.ToString("N0")) sizeInMB
                let oldestDateTime = DateTime.Parse(oldestFile, null, System.Globalization.DateTimeStyles.RoundtripKind)
                let newestDateTime = DateTime.Parse(newestFile, null, System.Globalization.DateTimeStyles.RoundtripKind)
                printfn "   Oldest: %s" (oldestDateTime.ToString("yyyy-MM-dd HH:mm"))
                printfn "   Newest: %s" (newestDateTime.ToString("yyyy-MM-dd HH:mm"))
                
                totalFiles <- totalFiles + fileCount
                totalSize <- totalSize + totalSizeBytes
            
            reader.Close()
            
            // Get trigram statistics
            let trigramStatsQuery = """
                SELECT COUNT(*) as trigram_count FROM trigrams
            """
            
            use trigramStatsCommand = new SqliteCommand(trigramStatsQuery, connection)
            let trigramCount = trigramStatsCommand.ExecuteScalar() :?> int64 |> int
            
            // Get repository statistics
            let repoStatsQuery = """
                SELECT COUNT(DISTINCT repo_path) as repo_count FROM indexed_files
            """
            
            use repoStatsCommand = new SqliteCommand(repoStatsQuery, connection)
            let repoCount = repoStatsCommand.ExecuteScalar() :?> int64 |> int
            
            let totalSizeInMB = float totalSize / (1024.0 * 1024.0)
            
            printfn "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
            printfn "📈 Total: %s files from %s repositories (%.2f MB)" (totalFiles.ToString("N0")) (repoCount.ToString("N0")) totalSizeInMB
            printfn "🔍 Search Index: %s trigrams" (trigramCount.ToString("N0"))
            printfn "💾 Database: %s" dbPath
            
        with
        | ex ->
            printfn $"Error getting indexing statistics: {ex.Message}"

    // Clean up orphaned trigrams and show cleanup statistics
    let cleanupDatabase () : unit =
        try
            initializeDatabase()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            // Count orphaned trigrams before cleanup
            let countOrphanedQuery = """
                SELECT COUNT(*) FROM trigrams t 
                LEFT JOIN indexed_files f ON t.file_id = f.id 
                WHERE f.id IS NULL
            """
            
            use countCommand = new SqliteCommand(countOrphanedQuery, connection)
            let orphanedBefore = countCommand.ExecuteScalar() :?> int64 |> int
            
            // Remove orphaned trigrams
            let cleanupQuery = """
                DELETE FROM trigrams 
                WHERE file_id NOT IN (SELECT id FROM indexed_files)
            """
            
            use cleanupCommand = new SqliteCommand(cleanupQuery, connection)
            let deletedRows = cleanupCommand.ExecuteNonQuery()
            
            // Vacuum the database to reclaim space
            let vacuumCommand = new SqliteCommand("VACUUM", connection)
            vacuumCommand.ExecuteNonQuery() |> ignore
            
            printfn "🧹 Database cleanup completed:"
            printfn "   Removed %d orphaned trigram entries" deletedRows
            printfn "   Database compacted and optimized"
            
        with
        | ex ->
            printfn $"Error cleaning up database: {ex.Message}"
    
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

    // Remove files from blacklisted directories from the database
    let cleanupBlacklistedFiles () : unit =
        try
            ensureDirectoryExists()
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            // Get all indexed files to check
            let selectCommand = "SELECT id, file_path FROM indexed_files"
            use selectCmd = new SqliteCommand(selectCommand, connection)
            use reader = selectCmd.ExecuteReader()
            
            let mutable filesToDelete = []
            while reader.Read() do
                let fileId = reader.["id"] :?> int64 |> int
                let filePath = reader.["file_path"] :?> string
                if isFileInBlacklistedDirectory filePath then
                    filesToDelete <- fileId :: filesToDelete
            
            reader.Close()
            
            if filesToDelete.IsEmpty then
                printfn "No blacklisted files found in database"
            else
                printfn $"Removing {filesToDelete.Length} files from blacklisted directories..."
                
                // Start transaction for cleanup
                use transaction = connection.BeginTransaction()
                
                try
                    for fileId in filesToDelete do
                        // Delete trigrams for this file
                        let deleteTrigramsCommand = "DELETE FROM trigrams WHERE file_id = @fileId"
                        use deleteTrigramsCmd = new SqliteCommand(deleteTrigramsCommand, connection, transaction)
                        deleteTrigramsCmd.Parameters.AddWithValue("@fileId", fileId) |> ignore
                        deleteTrigramsCmd.ExecuteNonQuery() |> ignore
                        
                        // Delete the file record
                        let deleteFileCommand = "DELETE FROM indexed_files WHERE id = @fileId"
                        use deleteFileCmd = new SqliteCommand(deleteFileCommand, connection, transaction)
                        deleteFileCmd.Parameters.AddWithValue("@fileId", fileId) |> ignore
                        deleteFileCmd.ExecuteNonQuery() |> ignore
                    
                    transaction.Commit()
                    printfn $"Successfully removed {filesToDelete.Length} blacklisted files and their trigrams"
                with
                | ex ->
                    transaction.Rollback()
                    printfn $"Error during cleanup, transaction rolled back: {ex.Message}"
                    
        with
        | ex ->
            printfn $"Error cleaning up blacklisted files: {ex.Message}"

    /// Index an Octopus step template with its properties for trigram search
    let indexStepTemplate (templateId: string) (templateName: string) (description: string) (scriptBody: string option) (properties: Map<string, string>) : unit =
        initializeDatabase()
        
        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        
        try
            // Serialize properties to JSON
            let propertiesJson = 
                properties 
                |> Map.toSeq 
                |> Seq.map (fun (k, v) -> sprintf "\"%s\":\"%s\"" (k.Replace("\"", "\\\"")) (v.Replace("\"", "\\\"")))
                |> String.concat ","
                |> sprintf "{%s}"
            
            use transaction = connection.BeginTransaction()
            try
                // Insert or update the step template
                let upsertTemplateCommand = """
                    INSERT OR REPLACE INTO octopus_step_templates (template_id, template_name, description, script_body, properties_json, indexed_at)
                    VALUES (@template_id, @template_name, @description, @script_body, @properties_json, @indexed_at)
                """
                
                use templateCommand = new SqliteCommand(upsertTemplateCommand, connection, transaction)
                templateCommand.Parameters.AddWithValue("@template_id", templateId) |> ignore
                templateCommand.Parameters.AddWithValue("@template_name", templateName) |> ignore
                templateCommand.Parameters.AddWithValue("@description", description) |> ignore
                templateCommand.Parameters.AddWithValue("@script_body", scriptBody |> Option.defaultValue "") |> ignore
                templateCommand.Parameters.AddWithValue("@properties_json", propertiesJson) |> ignore
                templateCommand.Parameters.AddWithValue("@indexed_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")) |> ignore
                templateCommand.ExecuteNonQuery() |> ignore
                
                // Get the template's database ID
                let getIdCommand = """
                    SELECT id FROM octopus_step_templates WHERE template_id = @template_id
                """
                use getIdCmd = new SqliteCommand(getIdCommand, connection, transaction)
                getIdCmd.Parameters.AddWithValue("@template_id", templateId) |> ignore
                let dbTemplateId = getIdCmd.ExecuteScalar() :?> int64 |> int
                
                // Delete existing trigrams for this template
                let deleteTrigramsCommand = "DELETE FROM octopus_template_trigrams WHERE template_id = @template_id"
                use deleteCmd = new SqliteCommand(deleteTrigramsCommand, connection, transaction)
                deleteCmd.Parameters.AddWithValue("@template_id", dbTemplateId) |> ignore
                deleteCmd.ExecuteNonQuery() |> ignore
                
                // Generate and insert trigrams for each property
                for (key, value) in properties |> Map.toSeq do
                    let combinedText = sprintf "%s:%s" key value
                    let trigrams = generateTrigrams combinedText
                    
                    for (trigram, position) in trigrams do
                        let insertTrigramCommand = """
                            INSERT INTO octopus_template_trigrams (trigram, template_id, position, property_key)
                            VALUES (@trigram, @template_id, @position, @property_key)
                        """
                        
                        use trigramCmd = new SqliteCommand(insertTrigramCommand, connection, transaction)
                        trigramCmd.Parameters.AddWithValue("@trigram", trigram) |> ignore
                        trigramCmd.Parameters.AddWithValue("@template_id", dbTemplateId) |> ignore
                        trigramCmd.Parameters.AddWithValue("@position", position) |> ignore
                        trigramCmd.Parameters.AddWithValue("@property_key", key) |> ignore
                        trigramCmd.ExecuteNonQuery() |> ignore
                
                // Also generate trigrams for script body if present
                match scriptBody with
                | Some script when not (String.IsNullOrWhiteSpace(script)) ->
                    let trigrams = generateTrigrams script
                    for (trigram, position) in trigrams do
                        let insertTrigramCommand = """
                            INSERT INTO octopus_template_trigrams (trigram, template_id, position, property_key)
                            VALUES (@trigram, @template_id, @position, @property_key)
                        """
                        
                        use trigramCmd = new SqliteCommand(insertTrigramCommand, connection, transaction)
                        trigramCmd.Parameters.AddWithValue("@trigram", trigram) |> ignore
                        trigramCmd.Parameters.AddWithValue("@template_id", dbTemplateId) |> ignore
                        trigramCmd.Parameters.AddWithValue("@position", position) |> ignore
                        trigramCmd.Parameters.AddWithValue("@property_key", "ScriptBody") |> ignore
                        trigramCmd.ExecuteNonQuery() |> ignore
                | _ -> ()
                
                transaction.Commit()
            with
            | ex ->
                transaction.Rollback()
                printfn $"Transaction failed for step template '{templateName}': {ex.Message}"
        with
        | ex ->
            printfn $"❌ Error indexing step template '{templateName}': {ex.Message}"

    /// Index an Octopus deployment step with its properties for trigram search
    let indexOctopusStep (projectName: string) (stepName: string) (stepId: string) (actionType: string) (stepTemplateId: string option) (properties: Map<string, string>) : unit =
        initializeDatabase()
        
        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        
        try
            // Serialize properties to JSON
            let propertiesJson = 
                properties 
                |> Map.toSeq 
                |> Seq.map (fun (k, v) -> sprintf "\"%s\":\"%s\"" (k.Replace("\"", "\\\"")) (v.Replace("\"", "\\\"")))
                |> String.concat ","
                |> sprintf "{%s}"
            
            use transaction = connection.BeginTransaction()
            try
                // Insert or update the step
                let upsertStepCommand = """
                    INSERT OR REPLACE INTO octopus_steps (project_name, step_name, step_id, action_type, properties_json, step_template_id, indexed_at)
                    VALUES (@project_name, @step_name, @step_id, @action_type, @properties_json, @step_template_id, @indexed_at)
                """
                
                use stepCommand = new SqliteCommand(upsertStepCommand, connection, transaction)
                stepCommand.Parameters.AddWithValue("@project_name", projectName) |> ignore
                stepCommand.Parameters.AddWithValue("@step_name", stepName) |> ignore
                stepCommand.Parameters.AddWithValue("@step_id", stepId) |> ignore
                stepCommand.Parameters.AddWithValue("@action_type", actionType) |> ignore
                stepCommand.Parameters.AddWithValue("@properties_json", propertiesJson) |> ignore
                stepCommand.Parameters.AddWithValue("@step_template_id", match stepTemplateId with | Some tid -> box tid | None -> box DBNull.Value) |> ignore
                stepCommand.Parameters.AddWithValue("@indexed_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")) |> ignore
                stepCommand.ExecuteNonQuery() |> ignore
                
                // Get the step's database ID
                let getIdCommand = """
                    SELECT id FROM octopus_steps WHERE project_name = @project_name AND step_id = @step_id
                """
                use getIdCmd = new SqliteCommand(getIdCommand, connection, transaction)
                getIdCmd.Parameters.AddWithValue("@project_name", projectName) |> ignore
                getIdCmd.Parameters.AddWithValue("@step_id", stepId) |> ignore
                let dbStepId = getIdCmd.ExecuteScalar() :?> int64 |> int
                
                // Delete existing trigrams for this step
                let deleteTrigramsCommand = "DELETE FROM octopus_trigrams WHERE step_id = @step_id"
                use deleteCmd = new SqliteCommand(deleteTrigramsCommand, connection, transaction)
                deleteCmd.Parameters.AddWithValue("@step_id", dbStepId) |> ignore
                deleteCmd.ExecuteNonQuery() |> ignore
                
                // Generate and insert trigrams for each property
                for (key, value) in properties |> Map.toSeq do
                    let combinedText = sprintf "%s:%s" key value
                    let trigrams = generateTrigrams combinedText
                    
                    for (trigram, position) in trigrams do
                        let insertTrigramCommand = """
                            INSERT INTO octopus_trigrams (trigram, step_id, position, property_key)
                            VALUES (@trigram, @step_id, @position, @property_key)
                        """
                        
                        use trigramCmd = new SqliteCommand(insertTrigramCommand, connection, transaction)
                        trigramCmd.Parameters.AddWithValue("@trigram", trigram) |> ignore
                        trigramCmd.Parameters.AddWithValue("@step_id", dbStepId) |> ignore
                        trigramCmd.Parameters.AddWithValue("@position", position) |> ignore
                        trigramCmd.Parameters.AddWithValue("@property_key", key) |> ignore
                        trigramCmd.ExecuteNonQuery() |> ignore
                
                transaction.Commit()
                let trigramCount = properties |> Map.toSeq |> Seq.sumBy (fun (k,v) -> generateTrigrams (sprintf "%s:%s" k v) |> List.length)
                printfn $"✅ Indexed step '{stepName}' with {properties.Count} properties and {trigramCount} trigrams"
            with
            | ex -> 
                transaction.Rollback()
                raise ex
        with
        | ex ->
            printfn $"❌ Error indexing Octopus step '{stepName}' in project '{projectName}': {ex.Message}"

    /// Get count of indexed Octopus data
    let getOctopusIndexStats (dbPath: string) : (int * int * string list) =
        initializeDatabase()
        
        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        
        try
            // Get step count
            let stepCountQuery = "SELECT COUNT(*) FROM octopus_steps"
            use stepCountCmd = new SqliteCommand(stepCountQuery, connection)
            let stepCount = stepCountCmd.ExecuteScalar() :?> int64 |> int
            
            // Get trigram count  
            let trigramCountQuery = "SELECT COUNT(*) FROM octopus_trigrams"
            use trigramCountCmd = new SqliteCommand(trigramCountQuery, connection)
            let trigramCount = trigramCountCmd.ExecuteScalar() :?> int64 |> int
            
            // Get project list
            let projectQuery = "SELECT DISTINCT project_name FROM octopus_steps ORDER BY project_name"
            use projectCmd = new SqliteCommand(projectQuery, connection)
            use reader = projectCmd.ExecuteReader()
            
            let projects = ResizeArray<string>()
            while reader.Read() do
                projects.Add(reader.GetString(0))
            
            let projectList = projects |> List.ofSeq
            (stepCount, trigramCount, projectList)
        with
        | ex ->
            printfn $"Error getting Octopus index stats: {ex.Message}"
            (0, 0, [])

/// GitHub repository indexing and storage functionality
module GitHubRepoIndex =
    
    let private dbPath = FileIndex.dbPath
    
    // Ensure the directory exists
    let private ensureDirectoryExists () =
        let dir = Path.GetDirectoryName(dbPath)
        if not (Directory.Exists(dir)) then
            Directory.CreateDirectory(dir) |> ignore
    
    // Ensure GitHub repos table exists
    let private initializeGitHubTable () =
        ensureDirectoryExists()
        
        use connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        
        let createTableCommand = """
            CREATE TABLE IF NOT EXISTS github_repos (
                id INTEGER PRIMARY KEY,
                github_id INTEGER NOT NULL UNIQUE,
                full_name TEXT NOT NULL COLLATE NOCASE,
                name TEXT NOT NULL COLLATE NOCASE,
                owner TEXT NOT NULL COLLATE NOCASE,
                description TEXT COLLATE NOCASE,
                clone_url TEXT NOT NULL,
                ssh_url TEXT NOT NULL,
                is_private INTEGER NOT NULL,
                is_fork INTEGER NOT NULL,
                language TEXT COLLATE NOCASE,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                pushed_at TEXT,
                indexed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                secrets_count INTEGER,
                runners_count INTEGER
            )
        """
        
        use command = new SqliteCommand(createTableCommand, connection)
        command.ExecuteNonQuery() |> ignore
        
        // Migration: Add secrets_count and runners_count columns if they don't exist
        try
            let alterTableCommand1 = "ALTER TABLE github_repos ADD COLUMN secrets_count INTEGER"
            use alterCmd1 = new SqliteCommand(alterTableCommand1, connection)
            alterCmd1.ExecuteNonQuery() |> ignore
        with
        | :? SqliteException as ex when ex.Message.Contains("duplicate column name") -> ()
        | _ -> ()
        
        try
            let alterTableCommand2 = "ALTER TABLE github_repos ADD COLUMN runners_count INTEGER"
            use alterCmd2 = new SqliteCommand(alterTableCommand2, connection)
            alterCmd2.ExecuteNonQuery() |> ignore
        with
        | :? SqliteException as ex when ex.Message.Contains("duplicate column name") -> ()
        | _ -> ()

        
        // Create index for searching
        let createIndexCommand = """
            CREATE INDEX IF NOT EXISTS idx_github_repos_owner ON github_repos(owner)
        """
        use indexCommand = new SqliteCommand(createIndexCommand, connection)
        indexCommand.ExecuteNonQuery() |> ignore
    
    /// Insert or update a GitHub repository
    let upsertRepository (repo: GitHubRepo) : unit =
        try
            initializeGitHubTable()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let upsertCommand = """
                INSERT OR REPLACE INTO github_repos 
                (github_id, full_name, name, owner, description, clone_url, ssh_url, 
                 is_private, is_fork, language, created_at, updated_at, pushed_at, indexed_at,
                 secrets_count, runners_count)
                VALUES 
                (@github_id, @full_name, @name, @owner, @description, @clone_url, @ssh_url,
                 @is_private, @is_fork, @language, @created_at, @updated_at, @pushed_at, @indexed_at,
                 @secrets_count, @runners_count)
            """
            
            use command = new SqliteCommand(upsertCommand, connection)
            command.Parameters.AddWithValue("@github_id", repo.Id) |> ignore
            command.Parameters.AddWithValue("@full_name", repo.FullName) |> ignore
            command.Parameters.AddWithValue("@name", repo.Name) |> ignore
            command.Parameters.AddWithValue("@owner", repo.Owner) |> ignore
            command.Parameters.AddWithValue("@description", repo.Description) |> ignore
            command.Parameters.AddWithValue("@clone_url", repo.CloneUrl) |> ignore
            command.Parameters.AddWithValue("@ssh_url", repo.SshUrl) |> ignore
            command.Parameters.AddWithValue("@is_private", if repo.IsPrivate then 1 else 0) |> ignore
            command.Parameters.AddWithValue("@is_fork", if repo.IsFork then 1 else 0) |> ignore
            command.Parameters.AddWithValue("@language", repo.Language |> Option.defaultValue "") |> ignore
            command.Parameters.AddWithValue("@created_at", repo.CreatedAt.ToString("O")) |> ignore
            command.Parameters.AddWithValue("@updated_at", repo.UpdatedAt.ToString("O")) |> ignore
            command.Parameters.AddWithValue("@pushed_at", 
                match repo.PushedAt with 
                | Some dt -> box (dt.ToString("O"))
                | None -> box DBNull.Value) |> ignore
            command.Parameters.AddWithValue("@indexed_at", repo.IndexedAt.ToString("O")) |> ignore
            command.Parameters.AddWithValue("@secrets_count", 
                match repo.SecretsCount with 
                | Some count -> box count
                | None -> box DBNull.Value) |> ignore
            command.Parameters.AddWithValue("@runners_count", 
                match repo.RunnersCount with 
                | Some count -> box count
                | None -> box DBNull.Value) |> ignore
            
            command.ExecuteNonQuery() |> ignore
        with
        | ex ->
            printfn $"Error upserting repository {repo.FullName}: {ex.Message}"
    
    /// Get all stored GitHub repositories
    let getAllRepositories () : GitHubRepo list =
        try
            initializeGitHubTable()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let selectCommand = """
                SELECT github_id, full_name, name, owner, description, clone_url, ssh_url,
                       is_private, is_fork, language, created_at, updated_at, pushed_at, indexed_at,
                       secrets_count, runners_count
                FROM github_repos
                ORDER BY full_name
            """
            
            use command = new SqliteCommand(selectCommand, connection)
            use reader = command.ExecuteReader()
            
            let mutable repos = []
            
            while reader.Read() do
                let language = 
                    let langValue = reader.["language"]
                    if langValue = box DBNull.Value || String.IsNullOrEmpty(langValue :?> string) then
                        None
                    else
                        Some (langValue :?> string)
                
                let pushedAt =
                    let pushedValue = reader.["pushed_at"]
                    if pushedValue = box DBNull.Value then
                        None
                    else
                        Some (DateTime.Parse(pushedValue :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind))
                
                let secretsCount =
                    let secretsValue = reader.["secrets_count"]
                    if secretsValue = box DBNull.Value then
                        None
                    else
                        Some (secretsValue :?> int64 |> int)
                
                let runnersCount =
                    let runnersValue = reader.["runners_count"]
                    if runnersValue = box DBNull.Value then
                        None
                    else
                        Some (runnersValue :?> int64 |> int)
                
                let repo = {
                    Id = reader.["github_id"] :?> int64
                    FullName = reader.["full_name"] :?> string
                    Name = reader.["name"] :?> string
                    Owner = reader.["owner"] :?> string
                    Description = reader.["description"] :?> string
                    CloneUrl = reader.["clone_url"] :?> string
                    SshUrl = reader.["ssh_url"] :?> string
                    IsPrivate = (reader.["is_private"] :?> int64) = 1L
                    IsFork = (reader.["is_fork"] :?> int64) = 1L
                    Language = language
                    CreatedAt = DateTime.Parse(reader.["created_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    UpdatedAt = DateTime.Parse(reader.["updated_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    PushedAt = pushedAt
                    IndexedAt = DateTime.Parse(reader.["indexed_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    SecretsCount = secretsCount
                    RunnersCount = runnersCount
                }
                repos <- repo :: repos
            
            List.rev repos
        with
        | ex ->
            printfn $"Error getting all repositories: {ex.Message}"
            []
    
    /// Get repositories by owner/organization
    let getRepositoriesByOwner (owner: string) : GitHubRepo list =
        try
            initializeGitHubTable()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let selectCommand = """
                SELECT github_id, full_name, name, owner, description, clone_url, ssh_url,
                       is_private, is_fork, language, created_at, updated_at, pushed_at, indexed_at,
                       secrets_count, runners_count
                FROM github_repos
                WHERE owner = @owner COLLATE NOCASE
                ORDER BY name
            """
            
            use command = new SqliteCommand(selectCommand, connection)
            command.Parameters.AddWithValue("@owner", owner) |> ignore
            
            use reader = command.ExecuteReader()
            
            let mutable repos = []
            
            while reader.Read() do
                let language = 
                    let langValue = reader.["language"]
                    if langValue = box DBNull.Value || String.IsNullOrEmpty(langValue :?> string) then
                        None
                    else
                        Some (langValue :?> string)
                
                let pushedAt =
                    let pushedValue = reader.["pushed_at"]
                    if pushedValue = box DBNull.Value then
                        None
                    else
                        Some (DateTime.Parse(pushedValue :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind))
                
                let secretsCount =
                    let secretsValue = reader.["secrets_count"]
                    if secretsValue = box DBNull.Value then
                        None
                    else
                        Some (secretsValue :?> int64 |> int)
                
                let runnersCount =
                    let runnersValue = reader.["runners_count"]
                    if runnersValue = box DBNull.Value then
                        None
                    else
                        Some (runnersValue :?> int64 |> int)
                
                let repo = {
                    Id = reader.["github_id"] :?> int64
                    FullName = reader.["full_name"] :?> string
                    Name = reader.["name"] :?> string
                    Owner = reader.["owner"] :?> string
                    Description = reader.["description"] :?> string
                    CloneUrl = reader.["clone_url"] :?> string
                    SshUrl = reader.["ssh_url"] :?> string
                    IsPrivate = (reader.["is_private"] :?> int64) = 1L
                    IsFork = (reader.["is_fork"] :?> int64) = 1L
                    Language = language
                    CreatedAt = DateTime.Parse(reader.["created_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    UpdatedAt = DateTime.Parse(reader.["updated_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    PushedAt = pushedAt
                    IndexedAt = DateTime.Parse(reader.["indexed_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    SecretsCount = secretsCount
                    RunnersCount = runnersCount
                }
                repos <- repo :: repos
            
            List.rev repos
        with
        | ex ->
            printfn $"Error getting repositories for owner {owner}: {ex.Message}"
            []
    
    /// Get repository count
    let getRepositoryCount () : int =
        try
            initializeGitHubTable()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let countCommand = "SELECT COUNT(*) FROM github_repos"
            use command = new SqliteCommand(countCommand, connection)
            let count = command.ExecuteScalar() :?> int64
            int count
        with
        | ex ->
            printfn $"Error getting repository count: {ex.Message}"
            0
    
    /// Search repositories by name or description (case-insensitive)
    let searchRepositories (searchTerm: string) : GitHubRepo list =
        try
            initializeGitHubTable()
            
            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            
            let selectCommand = """
                SELECT github_id, full_name, name, owner, description, clone_url, ssh_url,
                       is_private, is_fork, language, created_at, updated_at, pushed_at, indexed_at,
                       secrets_count, runners_count
                FROM github_repos
                WHERE full_name LIKE @search COLLATE NOCASE
                   OR name LIKE @search COLLATE NOCASE
                   OR description LIKE @search COLLATE NOCASE
                ORDER BY full_name
            """
            
            use command = new SqliteCommand(selectCommand, connection)
            command.Parameters.AddWithValue("@search", sprintf "%%%s%%" searchTerm) |> ignore
            
            use reader = command.ExecuteReader()
            
            let mutable repos = []
            
            while reader.Read() do
                let language = 
                    let langValue = reader.["language"]
                    if langValue = box DBNull.Value || String.IsNullOrEmpty(langValue :?> string) then
                        None
                    else
                        Some (langValue :?> string)
                
                let pushedAt =
                    let pushedValue = reader.["pushed_at"]
                    if pushedValue = box DBNull.Value then
                        None
                    else
                        Some (DateTime.Parse(pushedValue :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind))
                
                let secretsCount =
                    let secretsValue = reader.["secrets_count"]
                    if secretsValue = box DBNull.Value then
                        None
                    else
                        Some (secretsValue :?> int64 |> int)
                
                let runnersCount =
                    let runnersValue = reader.["runners_count"]
                    if runnersValue = box DBNull.Value then
                        None
                    else
                        Some (runnersValue :?> int64 |> int)
                
                let repo = {
                    Id = reader.["github_id"] :?> int64
                    FullName = reader.["full_name"] :?> string
                    Name = reader.["name"] :?> string
                    Owner = reader.["owner"] :?> string
                    Description = reader.["description"] :?> string
                    CloneUrl = reader.["clone_url"] :?> string
                    SshUrl = reader.["ssh_url"] :?> string
                    IsPrivate = (reader.["is_private"] :?> int64) = 1L
                    IsFork = (reader.["is_fork"] :?> int64) = 1L
                    Language = language
                    CreatedAt = DateTime.Parse(reader.["created_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    UpdatedAt = DateTime.Parse(reader.["updated_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    PushedAt = pushedAt
                    IndexedAt = DateTime.Parse(reader.["indexed_at"] :?> string, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    SecretsCount = secretsCount
                    RunnersCount = runnersCount
                }
                repos <- repo :: repos
            
            List.rev repos
        with
        | ex ->
            printfn $"Error searching repositories: {ex.Message}"
            []
