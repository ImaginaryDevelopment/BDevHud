namespace GitHub.Adapter

open System
open System.Threading.Tasks
open Octokit

/// GitHub repository information
type GitHubRepository =
    { Id: int64
      Name: string
      FullName: string
      Description: string
      HtmlUrl: string
      CloneUrl: string
      SshUrl: string
      IsPrivate: bool
      IsFork: bool
      CreatedAt: DateTime
      UpdatedAt: DateTime
      PushedAt: DateTime option
      Language: string option
      Owner: string }

/// GitHub repository secret information (metadata only, values are not accessible)
type GitHubRepositorySecret =
    { Name: string
      CreatedAt: DateTime
      UpdatedAt: DateTime }

/// GitHub Actions runner information
type GitHubRunner =
    { Id: int64
      Name: string
      Os: string
      Status: string
      Busy: bool
      Labels: string list }

/// GitHub configuration
type GitHubConfig =
    { Token: string option
      UserAgent: string }

module GitHubAdapter =

    /// Create GitHub client with optional authentication
    let private createGitHubClient (config: GitHubConfig) =
        let client = GitHubClient(ProductHeaderValue(config.UserAgent))

        match config.Token with
        | Some token ->
            client.Credentials <- Credentials(token)
            client
        | None -> client

    /// Convert Octokit Repository to our GitHubRepository type
    let private repositoryToRecord (repo: Repository) : GitHubRepository =
        { Id = repo.Id
          Name = repo.Name
          FullName = repo.FullName
          Description = if isNull repo.Description then "" else repo.Description
          HtmlUrl = repo.HtmlUrl
          CloneUrl = repo.CloneUrl
          SshUrl = repo.SshUrl
          IsPrivate = repo.Private
          IsFork = repo.Fork
          CreatedAt = repo.CreatedAt.DateTime
          UpdatedAt = repo.UpdatedAt.DateTime
          PushedAt =
            if repo.PushedAt.HasValue then
                Some repo.PushedAt.Value.DateTime
            else
                None
          Language = if isNull repo.Language then None else Some repo.Language
          Owner = repo.Owner.Login }

    /// Check if a repository name matches a wildcard pattern
    let private matchesWildcard (pattern: string) (name: string) : bool =
        if String.IsNullOrEmpty(pattern) then
            true
        else
            // Simple wildcard matching - supports * and ?
            let rec matchPattern (patternChars: char list) (nameChars: char list) : bool =
                match (patternChars, nameChars) with
                | ([], []) -> true
                | ([], _) -> false
                | ([ '*' ], []) -> true
                | ([ '*' ], _) ->
                    // * matches zero or more characters
                    matchPattern patternChars (List.tail nameChars)
                    || matchPattern (List.tail patternChars) nameChars
                | ([ '?' ], []) -> false
                | ([ '?' ], _) ->
                    // ? matches exactly one character
                    matchPattern (List.tail patternChars) (List.tail nameChars)
                | (p :: ps, n :: ns) when p = n ->
                    // Exact character match
                    matchPattern ps ns
                | _ -> false

            let patternList = pattern.ToCharArray() |> Array.toList
            let nameList = name.ToCharArray() |> Array.toList
            matchPattern patternList nameList

    /// Get all repositories accessible to the authenticated user with optional wildcard filtering
    let getRepositories (config: GitHubConfig) (namePattern: string option) : Task<GitHubRepository list> =
        task {
            let client = createGitHubClient config

            try
                // Get all repositories accessible to the authenticated user
                let! repositories = client.Repository.GetAllForCurrent()

                let filteredRepos =
                    match namePattern with
                    | Some pattern ->
                        repositories
                        |> Seq.filter (fun repo -> matchesWildcard pattern repo.Name)
                        |> Seq.toList
                    | None -> repositories |> Seq.toList

                return filteredRepos |> List.map repositoryToRecord
            with ex ->
                printfn $"Error fetching repositories: {ex.Message}"
                return []
        }

    /// Get repositories with additional filtering options
    let getRepositoriesWithFilter
        (config: GitHubConfig)
        (namePattern: string option)
        (includePrivate: bool)
        (includeForks: bool)
        : Task<GitHubRepository list> =
        task {
            let! allRepos = getRepositories config namePattern

            let filteredRepos =
                allRepos
                |> List.filter (fun repo -> (includePrivate || not repo.IsPrivate) && (includeForks || not repo.IsFork))

            return filteredRepos
        }

    /// Get repositories for a specific organization with optional wildcard filtering
    let getOrganizationRepositories
        (config: GitHubConfig)
        (organization: string)
        (namePattern: string option)
        : Task<GitHubRepository list> =
        task {
            let client = createGitHubClient config

            try
                // Get all repositories for the organization
                let! repositories = client.Repository.GetAllForOrg(organization)

                let filteredRepos =
                    match namePattern with
                    | Some pattern ->
                        repositories
                        |> Seq.filter (fun repo -> matchesWildcard pattern repo.Name)
                        |> Seq.toList
                    | None -> repositories |> Seq.toList

                return filteredRepos |> List.map repositoryToRecord
            with ex ->
                printfn $"Error fetching repositories for organization {organization}: {ex.Message}"
                return []
        }

    /// Search repositories by name pattern across all accessible repositories
    let searchRepositories
        (config: GitHubConfig)
        (namePattern: string)
        (includePrivate: bool)
        : Task<GitHubRepository list> =
        task {
            let client = createGitHubClient config

            try
                let searchRequest = SearchRepositoriesRequest(namePattern)

                let! searchResult = client.Search.SearchRepo(searchRequest)

                return searchResult.Items |> Seq.map repositoryToRecord |> List.ofSeq
            with ex ->
                printfn $"Error searching repositories: {ex.Message}"
                return []
        }

    /// Get repository by full name (owner/repo)
    let getRepository (config: GitHubConfig) (owner: string) (repoName: string) : Task<GitHubRepository option> =
        task {
            let client = createGitHubClient config

            try
                let! repository = client.Repository.Get(owner, repoName)
                return Some(repositoryToRecord repository)
            with ex ->
                printfn $"Error fetching repository {owner}/{repoName}: {ex.Message}"
                return None
        }

    /// Get repository secrets count (requires admin access)
    /// Note: Secret values are never returned, only names and metadata
    let getRepositorySecretsCount (config: GitHubConfig) (owner: string) (repoName: string) : Task<int> =
        task {
            try
                use httpClient = new System.Net.Http.HttpClient()
                httpClient.DefaultRequestHeaders.Add("User-Agent", config.UserAgent)
                
                match config.Token with
                | Some token -> 
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}")
                | None -> ()
                
                let url = $"https://api.github.com/repos/{owner}/{repoName}/actions/secrets"
                let! response = httpClient.GetAsync(url)
                
                if response.IsSuccessStatusCode then
                    let! content = response.Content.ReadAsStringAsync()
                    // Parse JSON to count secrets
                    let json = System.Text.Json.JsonDocument.Parse(content)
                    let secrets = json.RootElement.GetProperty("total_count")
                    return secrets.GetInt32()
                else
                    // Likely permission issue or repo doesn't have Actions enabled
                    return 0
            with ex ->
                // Silent fail for permission issues
                return 0
        }

    /// Get self-hosted runners count for a repository (requires admin access)
    let getRepositoryRunnersCount (config: GitHubConfig) (owner: string) (repoName: string) : Task<int> =
        task {
            try
                use httpClient = new System.Net.Http.HttpClient()
                httpClient.DefaultRequestHeaders.Add("User-Agent", config.UserAgent)
                
                match config.Token with
                | Some token -> 
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}")
                | None -> ()
                
                let url = $"https://api.github.com/repos/{owner}/{repoName}/actions/runners"
                let! response = httpClient.GetAsync(url)
                
                if response.IsSuccessStatusCode then
                    let! content = response.Content.ReadAsStringAsync()
                    // Parse JSON to count runners
                    let json = System.Text.Json.JsonDocument.Parse(content)
                    let runners = json.RootElement.GetProperty("total_count")
                    return runners.GetInt32()
                else
                    return 0
            with ex ->
                return 0
        }
