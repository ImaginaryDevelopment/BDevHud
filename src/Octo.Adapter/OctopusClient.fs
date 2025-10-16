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
        { Id: string
          Name: string
          Description: string
          IsDisabled: bool
          SpaceName: string
          OctopusUrl: string
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

                    { Id = project.Id
                      Name = project.Name
                      Description = project.Description
                      IsDisabled = project.IsDisabled
                      SpaceName = project.SpaceName
                      OctopusUrl = config.ServerUrl
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

    /// Type for representing deployment process steps
    type DeploymentStep =
        { Name: string
          StepNumber: int
          StepId: string  // The actual Octopus step ID
          ActionType: string
          PowerShellScript: string option
          StepTemplate: string option
          StepTemplateId: string option
          Variables: OctopusVariable list
          Properties: Map<string, string> }

    /// Type for step template information
    type StepTemplateInfo =
        { Id: string
          Name: string
          Description: string
          PowerShellScript: string option
          GitRepositoryUrl: string option
          GitPath: string option }

    /// Get deployment process for a project by name
    let getProjectDeploymentProcess (config: OctopusConfig) (projectName: string) : Task<Result<DeploymentStep list, string>> =
        task {
            try
                let repository = createRepository config
                let project = repository.Projects.FindByName projectName
                
                if isNull project then
                    return Error $"Project '{projectName}' not found"
                else
                    let deploymentProcess = repository.DeploymentProcesses.Get(project.DeploymentProcessId)
                    let projectVariables = repository.VariableSets.Get(project.VariableSetId)
                    
                    // DIAGNOSTIC: Explore deployment process properties
                    printfn "\n🔍 DIAGNOSTIC: Deployment Process for '%s'" projectName
                    printfn "  📋 Process ID: %s" deploymentProcess.Id
                    printfn "  📊 Process Version: %d" deploymentProcess.Version
                    printfn "  📝 Steps Count: %d" deploymentProcess.Steps.Count
                    
                    // DIAGNOSTIC: Explore project properties for git links
                    printfn "\n🔍 DIAGNOSTIC: Project Properties"
                    printfn "  🏷️  Project ID: %s" project.Id
                    printfn "  📂 Project Slug: %s" project.Slug
                    printfn "  🔗 Project Connectivity Policy: %s" (if isNull project.ProjectConnectivityPolicy then "null" else project.ProjectConnectivityPolicy.ToString())
                    if not (isNull project.PersistenceSettings) then
                        printfn "  💾 Persistence Settings Type: %s" (project.PersistenceSettings.GetType().Name)
                        printfn "  💾 Persistence Settings: %s" (project.PersistenceSettings.ToString())
                    
                    let steps = 
                        deploymentProcess.Steps
                        |> Seq.mapi (fun index step ->
                            let actions = step.Actions |> List.ofSeq
                            
                            // DIAGNOSTIC: Explore step properties
                            printfn "\n  🔍 DIAGNOSTIC: Step %d - '%s'" (index + 1) step.Name
                            printfn "    📋 Step ID: %s" step.Id
                            printfn "    🎬 Actions Count: %d" actions.Length
                            printfn "    ⚙️  Step Properties Count: %d" step.Properties.Count
                            
                            // DIAGNOSTIC: Show all step-level properties
                            if step.Properties.Count > 0 then
                                printfn "    📝 Step Properties:"
                                for kvp in step.Properties do
                                    printfn "      - %s: %s" kvp.Key (if kvp.Value = null then "null" else kvp.Value.ToString())
                            
                            // Get the primary action (usually the first one)
                            match actions with
                            | action :: _ ->
                                // DIAGNOSTIC: Explore action properties for git links
                                printfn "    🔍 DIAGNOSTIC: Action Properties"
                                printfn "      🎯 Action Type: %s" action.ActionType
                                printfn "      🏷️  Action Name: %s" action.Name
                                printfn "      📊 Properties Count: %d" action.Properties.Count
                                
                                // DIAGNOSTIC: Look for git-related properties
                                let gitRelatedProperties = 
                                    action.Properties 
                                    |> Seq.filter (fun kvp -> 
                                        let key = kvp.Key.ToLower()
                                        key.Contains("git") || key.Contains("repository") || key.Contains("source") || key.Contains("scm"))
                                    |> List.ofSeq
                                
                                if gitRelatedProperties.Length > 0 then
                                    printfn "      🔗 Git-Related Properties Found:"
                                    for kvp in gitRelatedProperties do
                                        printfn "        - %s: %s" kvp.Key kvp.Value.Value
                                
                                // DIAGNOSTIC: Show ALL action properties for comprehensive analysis
                                printfn "      📝 All Action Properties:"
                                for kvp in action.Properties do
                                    let valueStr = if String.IsNullOrEmpty(kvp.Value.Value) then "(empty)" else kvp.Value.Value
                                    printfn "        - %s: %s" kvp.Key valueStr
                                
                                let powerShellScript = 
                                    if action.Properties.ContainsKey("Octopus.Action.Script.ScriptBody") then
                                        Some (action.Properties.["Octopus.Action.Script.ScriptBody"].Value)
                                    else None
                                
                                let stepTemplate = 
                                    if action.Properties.ContainsKey("Octopus.Action.Template.Id") then
                                        Some (action.Properties.["Octopus.Action.Template.Id"].Value)
                                    else None
                                
                                let stepTemplateName = 
                                    if action.Properties.ContainsKey("Octopus.Action.Template.Name") then
                                        Some (action.Properties.["Octopus.Action.Template.Name"].Value)
                                    else None
                                
                                let properties = 
                                    action.Properties 
                                    |> Seq.map (fun kvp -> kvp.Key, kvp.Value.Value)
                                    |> Map.ofSeq
                                
                                // Get variables relevant to this step
                                let stepVariables = 
                                    projectVariables.Variables
                                    |> Seq.map (fun variable -> 
                                        { Name = variable.Name
                                          Value = if variable.IsSensitive then None else Some variable.Value
                                          IsSensitive = variable.IsSensitive  
                                          Scope = "Project"
                                          ProjectName = Some projectName
                                          LibrarySetName = None })
                                    |> List.ofSeq
                                
                                { Name = step.Name
                                  StepNumber = index + 1
                                  StepId = step.Id
                                  ActionType = action.ActionType
                                  PowerShellScript = powerShellScript
                                  StepTemplate = stepTemplateName
                                  StepTemplateId = stepTemplate
                                  Variables = stepVariables
                                  Properties = properties }
                            | [] ->
                                printfn "    ⚠️  No actions found for step '%s'" step.Name
                                { Name = step.Name
                                  StepNumber = index + 1
                                  StepId = step.Id
                                  ActionType = "Unknown"
                                  PowerShellScript = None
                                  StepTemplate = None
                                  StepTemplateId = None
                                  Variables = []
                                  Properties = Map.empty })
                        |> List.ofSeq
                    
                    return Ok steps
            with
            | ex -> return Error ex.Message
        }

    /// Get step template information by ID
    let getStepTemplate (config: OctopusConfig) (templateId: string) : Task<Result<StepTemplateInfo, string>> =
        task {
            try
                let repository = createRepository config
                let template = repository.ActionTemplates.Get(templateId)
                
                if isNull template then
                    return Error $"Step template '{templateId}' not found"
                else
                    // DIAGNOSTIC: Explore step template properties
                    printfn "\n🔍 DIAGNOSTIC: Step Template '%s' (ID: %s)" template.Name templateId
                    printfn "  📊 Properties Count: %d" template.Properties.Count
                    printfn "  🏷️  Template Version: %d" template.Version
                    printfn "  👤 Last Modified By: %s" (if isNull template.LastModifiedBy then "null" else template.LastModifiedBy)
                    
                    // DIAGNOSTIC: Look for ALL git-related properties in step template
                    let gitRelatedProperties = 
                        template.Properties 
                        |> Seq.filter (fun kvp -> 
                            let key = kvp.Key.ToLower()
                            key.Contains("git") || key.Contains("repository") || key.Contains("source") || key.Contains("scm") || key.Contains("vcs"))
                        |> List.ofSeq
                    
                    if gitRelatedProperties.Length > 0 then
                        printfn "  🔗 Git-Related Properties Found:"
                        for kvp in gitRelatedProperties do
                            printfn "    - %s: %s" kvp.Key kvp.Value.Value
                    else
                        printfn "  ❌ No git-related properties found"
                    
                    // DIAGNOSTIC: Show ALL step template properties for comprehensive analysis
                    printfn "  📝 All Step Template Properties:"
                    for kvp in template.Properties do
                        let valueStr = if String.IsNullOrEmpty(kvp.Value.Value) then "(empty)" else kvp.Value.Value
                        printfn "    - %s: %s" kvp.Key valueStr
                    
                    let powerShellScript = 
                        if template.Properties.ContainsKey("Octopus.Action.Script.ScriptBody") then
                            Some (template.Properties.["Octopus.Action.Script.ScriptBody"].Value)
                        else None
                    
                    // Check if template references a Git repository
                    let gitRepoUrl = 
                        if template.Properties.ContainsKey("Octopus.Action.Script.ScriptSource") && 
                           template.Properties.["Octopus.Action.Script.ScriptSource"].Value = "GitRepository" then
                            template.Properties.TryGetValue("Octopus.Action.GitRepository.Source") |> function
                            | (true, value) -> Some (value.Value)
                            | _ -> None
                        else None
                    
                    let gitPath = 
                        if template.Properties.ContainsKey("Octopus.Action.Script.ScriptFileName") then
                            Some (template.Properties.["Octopus.Action.Script.ScriptFileName"].Value)
                        else None
                    
                    let stepTemplateInfo = 
                        { Id = template.Id
                          Name = template.Name
                          Description = if isNull template.Description then "" else template.Description
                          PowerShellScript = powerShellScript
                          GitRepositoryUrl = gitRepoUrl
                          GitPath = gitPath }
                    
                    return Ok stepTemplateInfo
            with
            | ex -> return Error ex.Message
        }
