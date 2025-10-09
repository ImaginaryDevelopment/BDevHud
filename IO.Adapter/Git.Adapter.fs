namespace IO.Adapter

open System
open System.Diagnostics
open System.IO

/// Git operations adapter for running git commands
module GitAdapter =

    /// Represents the result of a git remote command
    type GitRemoteResult =
        { Success: bool
          Output: string
          Error: string }

    /// Gets git remote information for a repository folder
    /// Returns the output of "git remote -v" command
    let getRemote (folderPath: string) : GitRemoteResult =
        try
            if not (Directory.Exists(folderPath)) then
                { Success = false
                  Output = ""
                  Error = $"Directory does not exist: {folderPath}" }
            else
                let gitDir = Path.Combine(folderPath, ".git")

                if not (Directory.Exists(gitDir)) then
                    { Success = false
                      Output = ""
                      Error = $"Not a git repository: {folderPath}" }
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

                            if gitProcess.ExitCode = 0 then
                                { Success = true
                                  Output = outputStr
                                  Error = errorStr }
                            else
                                { Success = false
                                  Output = outputStr
                                  Error = errorStr }
                        else
                            gitProcess.Kill()

                            { Success = false
                              Output = ""
                              Error = "Git command timed out" }
                    else
                        { Success = false
                          Output = ""
                          Error = "Failed to start git process" }
        with ex ->
            { Success = false
              Output = ""
              Error = $"Exception: {ex.Message}" }

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
