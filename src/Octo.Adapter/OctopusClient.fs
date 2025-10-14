namespace Octo.Adapter

open System
open System.Threading.Tasks
open Octopus.Client
open Octopus.Client.Model

type OctopusConfig = {
    ServerUrl: string
    ApiKey: string
    SpaceId: string option
}

type OctopusProject = {
    Id: string
    Name: string
    Description: string
    IsDisabled: bool
    SpaceId: string
}

module OctopusClient =
    
    let private createRepository (config: OctopusConfig) =
        let endpoint = OctopusServerEndpoint(config.ServerUrl, config.ApiKey)
        OctopusRepository(endpoint)
    
    let private createRepositoryForSpace (config: OctopusConfig) (spaceId: string) =
        let endpoint = OctopusServerEndpoint(config.ServerUrl, config.ApiKey)
        OctopusRepository(endpoint, RepositoryScope.ForSpace(SpaceResource(Id = spaceId)))
    
    let private projectToRecord (project: ProjectResource) : OctopusProject =
        {
            Id = project.Id
            Name = project.Name
            Description = project.Description ?? ""
            IsDisabled = project.IsDisabled
            SpaceId = project.SpaceId
        }
    
    /// Get all spaces available in the Octopus instance
    let getSpaces (config: OctopusConfig) : Task<SpaceResource list> =
        task {
            use repository = createRepository config
            let! spaces = repository.Spaces.GetAll() |> Async.AwaitTask
            return spaces |> List.ofSeq
        }
    
    /// Get projects from a specific space ID
    let getProjectsFromSpaceId (config: OctopusConfig) (spaceId: string) : Task<OctopusProject list> =
        task {
            use repository = createRepositoryForSpace config spaceId
            let! projects = repository.Projects.GetAll() |> Async.AwaitTask
            return projects |> Seq.map projectToRecord |> List.ofSeq
        }
    
    /// Parse space ID from a space URL (e.g., "https://octopus.company.com/app#/Spaces-123")
    let parseSpaceIdFromUrl (spaceUrl: string) : string option =
        try
            let uri = Uri(spaceUrl)
            let fragment = uri.Fragment.TrimStart('#')
            
            // Handle different URL formats
            if fragment.Contains("/Spaces-") then
                let spacePart = fragment.Split([|"/Spaces-"|], StringSplitOptions.RemoveEmptyEntries)
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
        with
        | _ -> None
    
    /// Get projects from a space URL
    let getProjectsFromSpaceUrl (config: OctopusConfig) (spaceUrl: string) : Task<Result<OctopusProject list, string>> =
        task {
            match parseSpaceIdFromUrl spaceUrl with
            | Some spaceId ->
                try
                    let! projects = getProjectsFromSpaceId config spaceId
                    return Ok projects
                with
                | ex -> return Error $"Failed to get projects from space {spaceId}: {ex.Message}"
            | None ->
                return Error $"Could not parse space ID from URL: {spaceUrl}"
        }
    
    /// Get all projects from the default space (if no space is specified in config)
    let getAllProjects (config: OctopusConfig) : Task<OctopusProject list> =
        task {
            match config.SpaceId with
            | Some spaceId -> 
                return! getProjectsFromSpaceId config spaceId
            | None ->
                use repository = createRepository config
                let! projects = repository.Projects.GetAll() |> Async.AwaitTask
                return projects |> Seq.map projectToRecord |> List.ofSeq
        }