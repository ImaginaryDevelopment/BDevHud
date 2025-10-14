# BDevHud

A development heads-up display (HUD) solution built in F# that provides visibility and management capabilities for development workflows and deployment processes.

## Overview

BDevHud is designed to be a centralized dashboard and automation tool for development teams, providing adapters and utilities to interact with various development and deployment systems.

## Solution Structure

### Core Application
- **BDevHud** - Main console application and entry point
- **Program.fs** - Application startup and coordination logic

### Adapters

#### IO.Adapter
*Note: Currently split across two locations - consolidation needed*

- **IO.Adapter/** (root level):
  - **DirectoryTraversal.fs** - File system navigation and directory operations
  - **Git.Adapter.fs** - Git repository interaction and version control operations
- **src/Io.Adapter/**: 
  - **Library.fs** - Core I/O functionality and helper methods

#### Octo.Adapter
Located in `src/Octo.Adapter/`, this adapter provides Octopus Deploy integration:
- **OctopusClient.fs** - Client library for interacting with Octopus Deploy API
  - Query projects from Octopus spaces
  - Parse space URLs and extract space identifiers
  - Retrieve project information and metadata
- **[GitBackedConcepts.md](src/Octo.Adapter/GitBackedConcepts.md)** - Documentation on Octopus Deploy's Config as Code capabilities

## Features

### Current Capabilities
- **File System Operations** - Directory traversal and file management
- **Git Integration** - Version control operations and repository management  
- **Octopus Deploy Integration** - Project querying and space management
  - Support for space URL parsing (e.g., `https://octopus.company.com/app#/Spaces-123`)
  - Project listing and metadata retrieval
  - Async/Task-based operations with proper error handling

### Architecture
- **F# First** - Built entirely in F# leveraging functional programming paradigms
- **Adapter Pattern** - Modular adapters for different systems and services
- **Async Operations** - Task-based asynchronous operations throughout
- **Type Safety** - Strong typing with F# records and discriminated unions
- **Error Handling** - Result types for predictable error management

## Technology Stack
- **.NET 9.0** - Target framework for all projects
- **F#** - Primary programming language
- **Octopus.Client** - Official Octopus Deploy .NET client library

## Getting Started

### Prerequisites
- .NET 9.0 SDK
- Access to target systems (Git repositories, Octopus Deploy instances)

### Configuration
The Octo.Adapter requires configuration for Octopus Deploy connectivity:
```fsharp
let config = {
    ServerUrl = "https://your-octopus-server.com"
    ApiKey = "API-YOURKEY" 
    SpaceId = None // Optional: specify a default space
}
```

### Usage Examples

#### Querying Octopus Projects
```fsharp
open Octo.Adapter

let spaceUrl = "https://octopus.company.com/app#/Spaces-123"
let! result = OctopusClient.getProjectsFromSpaceUrl config spaceUrl

match result with
| Ok projects -> 
    projects |> List.iter (fun p -> printfn $"Project: {p.Name} ({p.Id})")
| Error errorMsg -> 
    printfn $"Error: {errorMsg}"
```

## Documentation
- **[Octopus Deploy Git-Backed Concepts](src/Octo.Adapter/GitBackedConcepts.md)** - Comprehensive guide to Octopus Deploy's Config as Code functionality

## Project Status
🚧 **In Development** - Core adapters and functionality are being built out

### Roadmap
- Enhanced Git operations and repository analysis
- Extended Octopus Deploy management capabilities
- Web-based dashboard interface
- Configuration management and persistence
- Additional adapter integrations (CI/CD systems, cloud providers)

## Contributing
This is a development tool project. Contributions and suggestions are welcome as the solution evolves.

## License
[License information to be determined]