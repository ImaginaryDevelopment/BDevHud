namespace IO.Adapter

open System
open System.Diagnostics
open System.IO
open System.Web

/// Git operations adapter for running git commands
module GitAdapter =

    /// Represents a parsed git remote entry
    type GitRemoteEntry =
        { Name: string
          Url: string
          Type: string } // "fetch" or "push"

    /// Represents the result of a git remote command
    type GitRemoteResult =
        { Success: bool
          Output: string
          Error: string
          Remotes: GitRemoteEntry list }

    /// Parses git remote -v output into structured data
    let parseRemoteOutput (output: string) : GitRemoteEntry list =
        if String.IsNullOrWhiteSpace(output) then
            []
        else
            let lines = output.Split('\n')

            let nonEmptyLines =
                lines |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))

            let trimmedLines = nonEmptyLines |> Array.map (fun line -> line.Trim())

            let linesWithSpaces =
                trimmedLines
                |> Array.filter (fun line -> line.Contains(" ") || line.Contains("\t"))

            linesWithSpaces
            |> Array.map (fun line ->
                // Split on both spaces and tabs, then filter out empty entries
                let parts = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

                if parts.Length >= 3 then
                    let name = parts.[0]
                    let url = parts.[1]
                    let typePart = parts.[2].Trim('(', ')')

                    { Name = name
                      Url = url
                      Type = typePart }
                else
                    { Name = ""; Url = ""; Type = "" })
            |> Array.filter (fun entry -> not (String.IsNullOrEmpty(entry.Name)))
            |> Array.toList

    /// Gets git remote information for a repository folder
    /// Returns the output of "git remote -v" command
    let getRemote (folderPath: string) : GitRemoteResult =
        try
            if not (Directory.Exists(folderPath)) then
                { Success = false
                  Output = ""
                  Error = $"Directory does not exist: {folderPath}"
                  Remotes = [] }
            else
                let gitDir = Path.Combine(folderPath, ".git")

                if not (Directory.Exists(gitDir)) then
                    { Success = false
                      Output = ""
                      Error = $"Not a git repository: {folderPath}"
                      Remotes = [] }
                else
                    let startInfo = ProcessStartInfo()
                    startInfo.FileName <- "git"
                    startInfo.Arguments <- "remote -v"
                    startInfo.WorkingDirectory <- folderPath
                    startInfo.UseShellExecute <- false
                    startInfo.RedirectStandardOutput <- true
                    startInfo.RedirectStandardError <- true
                    startInfo.CreateNoWindow <- true

                    use gitProcess = new Process()
                    gitProcess.StartInfo <- startInfo

                    let output = System.Text.StringBuilder()
                    let error = System.Text.StringBuilder()

                    gitProcess.OutputDataReceived.Add(fun e ->
                        if not (String.IsNullOrEmpty(e.Data)) then
                            output.AppendLine(e.Data) |> ignore)

                    gitProcess.ErrorDataReceived.Add(fun e ->
                        if not (String.IsNullOrEmpty(e.Data)) then
                            error.AppendLine(e.Data) |> ignore)

                    let started = gitProcess.Start()

                    if started then
                        gitProcess.BeginOutputReadLine()
                        gitProcess.BeginErrorReadLine()

                        let completed = gitProcess.WaitForExit(5000) // 5 second timeout

                        if completed then
                            let outputStr = output.ToString().Trim()
                            let errorStr = error.ToString().Trim()
                            let parsedRemotes = parseRemoteOutput outputStr

                            if gitProcess.ExitCode = 0 then
                                { Success = true
                                  Output = outputStr
                                  Error = errorStr
                                  Remotes = parsedRemotes }
                            else
                                { Success = false
                                  Output = outputStr
                                  Error = errorStr
                                  Remotes = [] }
                        else
                            gitProcess.Kill()

                            { Success = false
                              Output = ""
                              Error = "Git command timed out"
                              Remotes = [] }
                    else
                        { Success = false
                          Output = ""
                          Error = "Failed to start git process"
                          Remotes = [] }
        with ex ->
            { Success = false
              Output = ""
              Error = $"Exception: {ex.Message}"
              Remotes = [] }

    /// Gets git remote information and returns just the output string
    /// Returns empty string if command fails
    let getRemoteOutput (folderPath: string) : string =
        let result = getRemote folderPath
        if result.Success then result.Output else ""

    /// Checks if a directory is a git repository
    let isGitRepository (folderPath: string) : bool =
        try
            Directory.Exists(folderPath)
            && Directory.Exists(Path.Combine(folderPath, ".git"))
        with _ ->
            false

    /// Runs git pull in the specified directory
    /// Returns success status and any error message
    let gitPull (folderPath: string) : (bool * string) =
        try
            if not (isGitRepository folderPath) then
                (false, "Not a git repository")
            else
                let startInfo = ProcessStartInfo()
                startInfo.FileName <- "git"
                startInfo.Arguments <- "pull"
                startInfo.WorkingDirectory <- folderPath
                startInfo.UseShellExecute <- false
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.CreateNoWindow <- true

                use gitProcess = new Process()
                gitProcess.StartInfo <- startInfo

                let output = System.Text.StringBuilder()
                let error = System.Text.StringBuilder()

                gitProcess.OutputDataReceived.Add(fun e ->
                    if not (String.IsNullOrEmpty(e.Data)) then
                        output.AppendLine(e.Data) |> ignore)

                gitProcess.ErrorDataReceived.Add(fun e ->
                    if not (String.IsNullOrEmpty(e.Data)) then
                        error.AppendLine(e.Data) |> ignore)

                let started = gitProcess.Start()

                if started then
                    gitProcess.BeginOutputReadLine()
                    gitProcess.BeginErrorReadLine()

                    let completed = gitProcess.WaitForExit(30000) // 30 second timeout

                    if completed then
                        let outputStr = output.ToString().Trim()
                        let errorStr = error.ToString().Trim()

                        if gitProcess.ExitCode = 0 then
                            (true, "")
                        else
                            (false,
                             if String.IsNullOrEmpty(errorStr) then
                                 outputStr
                             else
                                 errorStr)
                    else
                        gitProcess.Kill()
                        (false, "Git pull timed out")
                else
                    (false, "Failed to start git pull process")
        with ex ->
            (false, $"Exception: {ex.Message}")

    /// Gets the current branch name for a repository
    /// Returns the current branch name or error message
    let getCurrentBranch (folderPath: string) : (bool * string) =
        try
            if not (isGitRepository folderPath) then
                (false, "Not a git repository")
            else
                let startInfo = ProcessStartInfo()
                startInfo.FileName <- "git"
                startInfo.Arguments <- "branch --show-current"
                startInfo.WorkingDirectory <- folderPath
                startInfo.UseShellExecute <- false
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.CreateNoWindow <- true

                use gitProcess = new Process()
                gitProcess.StartInfo <- startInfo

                let output = System.Text.StringBuilder()
                let error = System.Text.StringBuilder()

                gitProcess.OutputDataReceived.Add(fun e ->
                    if not (String.IsNullOrEmpty(e.Data)) then
                        output.AppendLine(e.Data) |> ignore)

                gitProcess.ErrorDataReceived.Add(fun e ->
                    if not (String.IsNullOrEmpty(e.Data)) then
                        error.AppendLine(e.Data) |> ignore)

                let started = gitProcess.Start()

                if started then
                    gitProcess.BeginOutputReadLine()
                    gitProcess.BeginErrorReadLine()

                    let completed = gitProcess.WaitForExit(5000) // 5 second timeout

                    if completed then
                        let outputStr = output.ToString().Trim()
                        let errorStr = error.ToString().Trim()

                        if gitProcess.ExitCode = 0 && not (String.IsNullOrEmpty(outputStr)) then
                            (true, outputStr)
                        else
                            (false, if String.IsNullOrEmpty(errorStr) then "Unable to determine branch" else errorStr)
                    else
                        gitProcess.Kill()
                        (false, "Git branch command timed out")
                else
                    (false, "Failed to start git branch process")
        with ex ->
            (false, $"Exception: {ex.Message}")

    /// Checks if the current branch is master or main
    let isOnMainBranch (folderPath: string) : (bool * string * bool) =
        let (success, branchName) = getCurrentBranch folderPath
        if success then
            let isMainBranch = branchName = "master" || branchName = "main"
            (true, branchName, isMainBranch)
        else
            (false, branchName, false)

    /// Extract repository name from a git URL
    /// Parses the last segment of the URL path, removing .git extension if present
    let extractRepoName (url: string) : string =
        try
            let decodedUrl = HttpUtility.UrlDecode(url)
            let uri = Uri(decodedUrl)
            let segments = uri.Segments

            if segments.Length > 0 then
                let lastSegment = segments.[segments.Length - 1]

                if lastSegment.EndsWith(".git") then
                    lastSegment.Substring(0, lastSegment.Length - 4)
                else
                    lastSegment
            else
                "unknown"
        with _ ->
            "unknown"
