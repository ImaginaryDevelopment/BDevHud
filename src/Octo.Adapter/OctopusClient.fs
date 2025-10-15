namespace Octo.Adapter

open System
open System.Threading.Tasks
open Octopus.Client
open Octopus.Client.Model

type SpaceType =
    | SpaceName of string
    | SpaceId of string

type OctopusConfig =
    { ServerUrl: string
      ApiKey: string
      Space: SpaceType option }

type OctopusProject =
    { Id: string
      Name: string
      Description: string
      IsDisabled: bool
      SpaceName: string }

module OctopusClient =

    let private createRepository (config: OctopusConfig) =
        let endpoint = OctopusServerEndpoint(config.ServerUrl, config.ApiKey)
        OctopusRepository(endpoint)

    let private resolveSpace (repository: OctopusRepository) (spaceType: SpaceType) : SpaceResource =
        match spaceType with
        | SpaceName name -> repository.Spaces.FindByName name
        | SpaceId id -> repository.Spaces.Get id

    let private projectToRecord (spaceName: string) (project: ProjectResource) : OctopusProject =
        { Id = project.Id
          Name = project.Name
          Description =
            if isNull project.Description then
                ""
            else
                project.Description
          IsDisabled = project.IsDisabled
          SpaceName = spaceName }

    /// Test connection to Octopus server by attempting to find the default space
    let testConnection (config: OctopusConfig) : Task<Result<string, string>> =
        task {
            try
                let repository = createRepository config
                // Try to find the default space - this should force a real network call
                let defaultSpace = repository.Spaces.FindByName "Default"
                if isNull defaultSpace then
                    return Ok "Connected successfully (Default space not found - might be older Octopus version)"
                else
                    return Ok $"Connected successfully (Found default space: {defaultSpace.Name})"
            with
            | ex -> return Error ex.Message
        }

    /// Get all spaces available in the Octopus instance
    let getSpaces (config: OctopusConfig) : Task<SpaceResource list> =
        task {
            let repository = createRepository config
            let spaces = repository.Spaces.Get()
            return spaces |> List.ofSeq
        }

    /// Get projects from a specific space
    let getProjectsFromSpace (config: OctopusConfig) (spaceType: SpaceType) : Task<OctopusProject list> =
        task {
            let repository = createRepository config
            use client = repository.Client
            let space = resolveSpace repository spaceType
            let repositoryForSpace = client.ForSpace space
            let projects = repositoryForSpace.Projects.GetAll()
            let spaceName = space.Name
            return projects |> Seq.map (projectToRecord spaceName) |> List.ofSeq
        }

    /// Parse space ID from a space URL (e.g., "https://octopus.company.com/app#/Spaces-123")
    let parseSpaceIdFromUrl (spaceUrl: string) : string option =
        try
            let uri = Uri(spaceUrl)
            let fragment = uri.Fragment.TrimStart('#')

            // Handle different URL formats
            if fragment.Contains("/Spaces-") then
                let spacePart =
                    fragment.Split([| "/Spaces-" |], StringSplitOptions.RemoveEmptyEntries)

                if spacePart.Length > 1 then
                    let spaceId = "Spaces-" + spacePart.[1].Split('/').[0]
                    Some spaceId
                else
                    None
            elif fragment.StartsWith("Spaces-") then
                let spaceId = fragment.Split('/').[0]
                Some spaceId
            else
                None
        with _ ->
            None

    /// Get projects from a space URL
    let getProjectsFromSpaceUrl (config: OctopusConfig) (spaceUrl: string) : Task<Result<OctopusProject list, string>> =
        task {
            match parseSpaceIdFromUrl spaceUrl with
            | Some spaceId ->
                try
                    let! projects = getProjectsFromSpace config (SpaceId spaceId)
                    return Ok projects
                with ex ->
                    return Error $"Failed to get projects from space {spaceId}: {ex.Message}"
            | None -> return Error $"Could not parse space ID from URL: {spaceUrl}"
        }

    /// Get all projects from the specified space (if no space is specified in config, use default)
    let getAllProjects (config: OctopusConfig) : Task<OctopusProject list> =
        task {
            match config.Space with
            | Some spaceType -> return! getProjectsFromSpace config spaceType
            | None ->
                return! getProjectsFromSpace config (SpaceName "default")
        }

    /// Result type for Octopus project with related git repository information
    type OctopusProjectWithGit =
        { OctopusUrl: string
          ProjectName: string
          GitRepoUrl: string option }

    /// Get all projects from Octopus and match them with git repositories
    let getProjectsWithGitInfo
        (config: OctopusConfig)
        (gitRepos: (string * string) list)
        : Task<OctopusProjectWithGit list> =
        task {
            let! projects = getAllProjects config

            return
                projects
                |> List.map (fun project ->
                    // Try to find matching git repository by project name
                    let matchingGitRepo =
                        gitRepos
                        |> List.tryFind (fun (repoName, repoUrl) ->
                            // Simple name matching - could be enhanced with more sophisticated logic
                            repoName.ToLower().Contains(project.Name.ToLower())
                            || project.Name.ToLower().Contains(repoName.ToLower()))

                    let gitRepoUrl = matchingGitRepo |> Option.map snd

                    { OctopusUrl = config.ServerUrl
                      ProjectName = project.Name
                      GitRepoUrl = gitRepoUrl })
        }

    /// Type for representing Octopus variables
    type OctopusVariable =
        { Name: string
          Value: string option  // None for sensitive variables that can't be retrieved
          IsSensitive: bool
          Scope: string
          ProjectName: string option
          LibrarySetName: string option }

    /// Get variables from a specific project
    let getProjectVariables (config: OctopusConfig) (projectId: string) : Task<OctopusVariable list> =
        task {
            let repository = createRepository config
            let project = repository.Projects.Get(projectId)
            let variableSet = repository.VariableSets.Get(project.VariableSetId)
            
            return
                variableSet.Variables
                |> Seq.map (fun variable -> 
                    { Name = variable.Name
                      Value = if variable.IsSensitive then None else Some variable.Value
                      IsSensitive = variable.IsSensitive  
                      Scope = 
                        let scopes = 
                            [ if variable.Scope.ContainsKey(ScopeField.Environment) then 
                                yield "Env: " + String.Join(", ", variable.Scope.[ScopeField.Environment])
                              if variable.Scope.ContainsKey(ScopeField.Machine) then 
                                yield "Machine: " + String.Join(", ", variable.Scope.[ScopeField.Machine])
                              if variable.Scope.ContainsKey(ScopeField.Role) then 
                                yield "Role: " + String.Join(", ", variable.Scope.[ScopeField.Role]) ]
                        if scopes.IsEmpty then "All" else String.Join("; ", scopes)
                      ProjectName = Some project.Name
                      LibrarySetName = None })
                |> List.ofSeq
        }

    /// Get variables from all projects
    let getAllProjectVariables (config: OctopusConfig) : Task<OctopusVariable list> =
        task {
            let! projects = getAllProjects config
            let! allVariables = 
                projects 
                |> List.map (fun project -> getProjectVariables config project.Id)
                |> Task.WhenAll
            
            return allVariables |> Array.toList |> List.concat
        }

    /// Search for variables by name pattern (case-insensitive)
    let searchVariables (config: OctopusConfig) (namePattern: string) : Task<OctopusVariable list> =
        task {
            let! allVariables = getAllProjectVariables config
            let pattern = namePattern.ToLower()
            
            return 
                allVariables
                |> List.filter (fun var -> var.Name.ToLower().Contains(pattern))
        }
