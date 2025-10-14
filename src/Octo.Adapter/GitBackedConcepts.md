# Octopus Deploy Git-Backed Concepts

Octopus Deploy supports "Config as Code" functionality that allows certain configuration elements to be stored in Git repositories and version controlled alongside your application code.

## Git-Backed Octopus Concepts

### Deployment Process
- **Step templates and deployment steps** - Custom and built-in deployment actions
- **Variable definitions and scoping** - Project variables with environment/role scoping
- **Process configuration and logic** - Conditional deployment logic and branching
- **Custom scripts and deployment actions** - PowerShell, Bash, and other script steps

### Project Configuration
- **Project settings and metadata** - Project name, description, and basic settings
- **Deployment process definitions** - The complete deployment workflow
- **Variable sets and values** - Project-specific variable definitions
- **Channel configurations** - Release channel rules and settings
- **Lifecycle assignments** - Which lifecycle phases apply to the project

### Variable Sets
- **Shared variable libraries** - Reusable variable collections across projects
- **Environment-specific variables** - Variables scoped to specific environments
- **Project-specific variables** - Variables unique to individual projects
- **Sensitive variable references** - References to secure values (actual values stored securely in Octopus)

### Step Templates
- **Custom step template definitions** - Reusable deployment step definitions
- **Community step templates** - When customized or forked
- **Script modules and shared code** - Reusable code libraries and functions

### Runbooks
- **Runbook processes and steps** - Maintenance and operations procedures
- **Runbook variables** - Variables specific to runbook execution
- **Execution settings and schedules** - When and how runbooks should run

### Channels and Lifecycles
- **Channel rules and version ranges** - Rules for which releases go to which channels
- **Lifecycle phase definitions** - Environment progression rules and gates
- **Deployment targets and environments** - Configuration for where deployments go

## What is NOT Git-Backed

These concepts remain stored and managed within Octopus Deploy itself:

### Infrastructure
- **Deployment targets** - Physical servers, cloud instances, Kubernetes clusters
- **Environments** - Development, Staging, Production environment definitions
- **Worker pools** - Execution infrastructure for deployments

### Security
- **User accounts** - Individual user credentials and profiles
- **Teams** - User groups and organizational structure
- **Permissions** - Access control and authorization rules
- **Certificates** - SSL/TLS certificates and security credentials

### System Settings
- **Server configuration** - Octopus Server settings and preferences
- **Authentication providers** - LDAP, Active Directory, SSO configurations
- **License information** - Octopus licensing and subscription details

### Operational Data
- **Tenants** - Multi-tenant deployment configurations
- **Tenant-specific variables** - Variables scoped to individual tenants
- **Releases and Deployments** - Historical deployment records and artifacts
- **Deployment logs** - Execution history and audit trails

### Sensitive Values
- **API keys** - Actual API key values (only references stored in Git)
- **Passwords** - Actual password values (only references stored in Git)
- **Connection strings** - Sensitive connection details (only references stored in Git)

## Benefits of Git-Backed Configuration

1. **Version Control** - Track changes to deployment processes over time
2. **Code Reviews** - Review infrastructure changes through pull requests
3. **Branching Strategies** - Align deployment configuration with code branching
4. **Rollback Capability** - Easily revert to previous configuration versions
5. **Collaboration** - Multiple team members can contribute to deployment processes
6. **Audit Trail** - Git history provides complete change tracking
7. **Infrastructure as Code** - Treat deployment configuration as code artifacts

## Implementation Notes

- Git-backed projects store their configuration in `.octopus/` directories within the repository
- Sensitive values are never stored in Git - only variable names and references
- The Octopus Server maintains the actual sensitive values securely
- Git-backed configuration can be mixed with traditional Octopus-managed configuration
- Projects can be migrated between Git-backed and traditional modes