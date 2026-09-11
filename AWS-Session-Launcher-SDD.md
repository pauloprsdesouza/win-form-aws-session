# Software Design Document (SDD)
# AWS Session Launcher for Windows

**Document version:** 1.0  
**Target platform:** Windows 10/11 x64  
**Framework:** .NET 8 Windows Forms  
**Primary purpose:** Provide a friendly desktop UI for authenticating with AWS profiles and starting/stopping AWS Systems Manager Session Manager port-forwarding sessions without requiring users to manually type AWS CLI commands.

---

## 1. Executive Summary

Build a Windows desktop application distributed as a self-contained `.exe` that:

1. Automatically discovers the user's AWS configuration.
2. Reads available AWS profile names without exposing or displaying secret values.
3. Lets the user select a profile.
4. Validates authentication and, when appropriate, performs `aws sso login --profile <profile>`.
5. Discovers eligible EC2/SSM managed instances, including a bastion host.
6. Lets the user define one or more port-forwarding rules.
7. Starts AWS Systems Manager Session Manager tunnels.
8. Displays tunnel health/status in the UI.
9. Allows the user to stop individual tunnels or all tunnels.
10. Persists only non-sensitive preferences such as the last profile, last target, and forwarding presets.

The application is intended to replace a workflow similar to:

```text
aws sso login --profile JI8-HOM

aws ssm start-session \
  --profile JI8-HOM \
  --target <instance-id> \
  ...
```

The original concept is valid, but the implementation should **not** simply concatenate shell commands from user input. The application must resolve profiles, targets, and arguments structurally and invoke `aws.exe` using safe process arguments.

---

## 2. Product Rationale

### 2.1 Does the application make sense?

Yes.

This application is useful when users repeatedly need to:

- authenticate against AWS IAM Identity Center / SSO;
- find a bastion or managed EC2 instance;
- create SSM port-forwarding tunnels;
- access databases or internal services through `localhost`;
- avoid remembering long AWS CLI commands;
- manage multiple tunnels at once.

It is particularly useful in enterprise environments where developers or support personnel need temporary access to resources without inbound SSH/RDP exposure.

### 2.2 Improvements over the initial idea

The initial requirement says the application should ask for the `.aws` directory and parse `config` or `credentials`. The improved design changes that behavior:

- The application should **auto-detect** AWS configuration first.
- Only ask the user to browse for files if auto-detection fails.
- Respect `AWS_CONFIG_FILE` and `AWS_SHARED_CREDENTIALS_FILE`.
- Never display `aws_secret_access_key`, session tokens, or cached SSO tokens.
- Do not assume all profiles are SSO profiles.
- Validate credentials with `aws sts get-caller-identity`.
- Only run `aws sso login` when the selected profile uses SSO and authentication is missing/expired.
- Do not use nested shell expressions such as `$(aws ec2 describe-instances ...)`.
- Resolve EC2 instances explicitly and safely.
- Do not assume a single matching bastion instance.
- Verify that the selected target is actually available through Systems Manager.
- Support both forwarding to the managed node and forwarding through the node to a remote host.
- Manage multiple tunnels as independent child processes.
- Detect local-port conflicts before opening a tunnel.
- Provide Start/Stop controls, status, logs, and actionable errors.
- Avoid using `cmd.exe`, PowerShell, or shell string concatenation when invoking AWS commands.

---

## 3. Scope

### 3.1 MVP

The MVP must support:

- Windows 10 and Windows 11.
- .NET 8 WinForms.
- AWS CLI v2 prerequisite detection.
- AWS Session Manager plugin prerequisite detection.
- Automatic AWS config discovery.
- Profile enumeration.
- SSO and non-SSO profile validation.
- EC2 target discovery by:
  - instance ID;
  - instance Name tag;
  - configurable tag filters.
- SSM managed-node validation.
- `AWS-StartPortForwardingSession`.
- `AWS-StartPortForwardingSessionToRemoteHost`.
- Multiple concurrent tunnels.
- Connection presets.
- Session status and log output.
- Graceful stop and cleanup.
- Self-contained Windows publication.

### 3.2 Out of scope for MVP

Do not implement:

- direct SSH private-key management;
- storing AWS access keys inside the application;
- a custom implementation of the Session Manager WebSocket protocol;
- macOS/Linux UI;
- automatic IAM policy creation;
- remote desktop protocol client;
- database GUI/client;
- privileged OS service;
- cloud-side infrastructure provisioning.

---

## 4. AWS Prerequisites

The workstation must have:

1. AWS CLI v2.
2. AWS Systems Manager Session Manager plugin.
3. At least one configured AWS profile.
4. IAM permissions required for the chosen operation.
5. A target EC2/managed node configured for AWS Systems Manager.
6. Network connectivity from the managed node to any remote host used by remote-host port forwarding.

For `AWS-StartPortForwardingSessionToRemoteHost`, the managed node must have a sufficiently recent SSM Agent version.

The application must detect prerequisites and show an actionable status rather than failing later.

---

## 5. AWS Configuration Discovery

### 5.1 Resolution order

Resolve paths independently.

#### Config file

1. Environment variable `AWS_CONFIG_FILE`, if defined.
2. `%USERPROFILE%\.aws\config`.

#### Credentials file

1. Environment variable `AWS_SHARED_CREDENTIALS_FILE`, if defined.
2. `%USERPROFILE%\.aws\credentials`.

If neither file exists, show a setup screen allowing the user to:

- browse for the config file;
- browse for the credentials file;
- open a terminal instruction showing `aws configure sso` or `aws configure`.

Do not force the user to manually choose the `.aws` folder when the standard location is already valid.

### 5.2 Parsing

The parser must recognize at minimum:

```ini
[default]

[profile dev]

[profile prod]

[sso-session company]
```

and credentials sections such as:

```ini
[default]

[dev]
```

Normalize profile names:

- `[profile dev]` -> `dev`
- `[dev]` in credentials -> `dev`
- `[default]` -> `default`

Do not expose secret values in memory longer than needed. Prefer not to parse secret fields at all.

### 5.3 Profile classification

Classify profiles when possible:

```text
Sso
StaticCredentials
AssumeRole
CredentialProcess
Unknown
```

A profile is considered SSO when it contains either modern SSO-session configuration or legacy SSO profile fields.

The classification is informative only. AWS CLI remains the source of truth for authentication behavior.

---

## 6. Authentication Flow

### 6.1 Profile selection

For each profile show:

- profile name;
- region, if available;
- profile type;
- authentication status:
  - Unknown
  - Valid
  - Login required
  - Invalid
- caller identity when validated:
  - AWS account ID;
  - principal ARN.

### 6.2 Validation algorithm

When the user selects a profile:

1. Run:

```text
aws sts get-caller-identity --profile <profile> --output json --no-cli-pager
```

2. If the command succeeds:
   - mark the profile `Valid`;
   - parse and display account ID and ARN;
   - do not perform an unnecessary SSO login.

3. If it fails with an authentication/SSO-expiration condition and the profile is SSO:
   - show `Login required`;
   - enable **Sign in**.

4. On **Sign in**, run:

```text
aws sso login --profile <profile> --no-cli-pager
```

5. The AWS CLI may open the system browser.
6. After completion, rerun `sts get-caller-identity`.
7. Do not consider login successful until caller identity validation succeeds.

### 6.3 Non-SSO profiles

Do not run `aws sso login` for static credential, assume-role, or credential-process profiles.

Simply validate them through the AWS CLI.

---

## 7. Target Discovery

### 7.1 Default target strategy

The original example appears to search for an instance using a `Name` tag such as a bastion host.

The application must support a default configurable filter:

```text
tag:Name = bastion-host
instance-state-name = running
```

The actual tag value must be configurable.

### 7.2 Discovery command

Use AWS CLI JSON output, not text shell substitution.

Example conceptual command:

```text
aws ec2 describe-instances
  --profile <profile>
  --region <region>
  --filters
      Name=instance-state-name,Values=running
      Name=tag:Name,Values=<configured-name>
  --output json
  --no-cli-pager
```

Parse JSON in the application.

### 7.3 Multiple matches

Never arbitrarily select the first instance.

If:

- zero instances match -> show `No matching instance`.
- one instance matches -> preselect it.
- more than one instance matches -> show a selection list.

Display:

- instance ID;
- Name tag;
- private IP;
- availability zone;
- state;
- SSM status.

### 7.4 Systems Manager availability

After EC2 discovery, determine whether candidates are managed and online through Systems Manager.

Prefer targets whose SSM PingStatus is `Online`.

The UI must visually distinguish:

```text
Online
ConnectionLost
Inactive
NotManaged
Unknown
```

The Start button must be disabled for targets known not to be usable through SSM.

---

## 8. Port Forwarding Modes

Support two modes.

### 8.1 Mode A — Forward to managed node

Use when the target service runs on the selected managed instance.

SSM document:

```text
AWS-StartPortForwardingSession
```

Required values:

```text
target instance
remote port
local port
```

Conceptual command:

```text
aws ssm start-session
  --profile <profile>
  --target <instance-id>
  --document-name AWS-StartPortForwardingSession
  --parameters portNumber=<remote-port>,localPortNumber=<local-port>
  --no-cli-pager
```

### 8.2 Mode B — Forward through managed node to remote host

Use when the managed instance acts as a bastion to reach RDS, Redis, internal APIs, etc.

SSM document:

```text
AWS-StartPortForwardingSessionToRemoteHost
```

Required values:

```text
target instance
remote host
remote port
local port
```

Conceptual command:

```text
aws ssm start-session
  --profile <profile>
  --target <instance-id>
  --document-name AWS-StartPortForwardingSessionToRemoteHost
  --parameters host=<remote-host>,portNumber=<remote-port>,localPortNumber=<local-port>
  --no-cli-pager
```

### 8.3 Multiple ports

Treat each forwarding rule as an independent SSM session/process.

Example:

```text
Rule 1: localhost:5432 -> db.internal:5432
Rule 2: localhost:6379 -> redis.internal:6379
Rule 3: localhost:9200 -> search.internal:9200
```

The user can start or stop each rule independently, plus use **Start All** and **Stop All**.

---

## 9. Port Forward Rule Model

```csharp
public sealed record PortForwardRule
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required PortForwardMode Mode { get; init; }

    public string? RemoteHost { get; init; }

    public required int RemotePort { get; init; }
    public required int LocalPort { get; init; }

    public bool Enabled { get; init; } = true;
}
```

```csharp
public enum PortForwardMode
{
    ManagedNode,
    RemoteHost
}
```

Validation:

- ports must be between 1 and 65535;
- local port must be free before session startup;
- `RemoteHost` is mandatory for RemoteHost mode;
- reject CR/LF and invalid host characters;
- duplicate local ports are not allowed among enabled rules.

---

## 10. Session Lifecycle

### 10.1 State model

```text
Stopped
Starting
Running
Stopping
Failed
```

### 10.2 Start

For each rule:

1. validate profile;
2. validate target;
3. validate port rule;
4. verify local port is available;
5. create `ProcessStartInfo`;
6. execute `aws.exe` directly;
7. use `ArgumentList`, never concatenated command strings;
8. capture stdout/stderr;
9. associate the child process with the rule;
10. update state to `Running` only after the process remains alive and startup output indicates the session is established, or after an implementation-defined readiness timeout.

### 10.3 Stop

Attempt graceful termination first.

If the child process does not exit within a short timeout:

- terminate its process tree;
- clean up internal state;
- mark the session stopped.

The UI must never leave a rule shown as Running when the child process has exited.

### 10.4 Unexpected exit

When an AWS CLI process exits unexpectedly:

- capture exit code;
- preserve recent stderr;
- set rule status to `Failed`;
- display an actionable message;
- provide **Retry**.

### 10.5 Application close

If active sessions exist, prompt:

```text
There are 3 active AWS tunnels.
Stop all tunnels and exit?
```

Default action: stop all and exit.

---

## 11. Safe Process Execution

Implement a reusable runner.

```csharp
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    Task<ManagedProcess> StartLongRunningAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}
```

Rules:

- no `cmd.exe /c`;
- no PowerShell wrapper;
- no shell execution;
- `UseShellExecute = false`;
- use `ProcessStartInfo.ArgumentList`;
- redirect stdout/stderr where compatible;
- never log secret values;
- sanitize displayed command previews;
- use cancellation tokens;
- apply timeouts to short-running AWS CLI operations.

---

## 12. AWS CLI Detection

At startup:

1. resolve `aws.exe` using:
   - PATH;
   - standard Windows AWS CLI installation locations if necessary.
2. execute:

```text
aws --version
```

3. show version.

Status example:

```text
AWS CLI       2.x.x      Ready
Session Mgr   installed  Ready
```

If AWS CLI is missing, disable AWS-dependent features.

---

## 13. Session Manager Plugin Detection

Run the Session Manager plugin executable with a safe version/check mechanism supported by the installed version, or locate it through standard installation paths.

If absent:

- show a blocking prerequisite error for tunnel startup;
- do not crash;
- provide concise installation guidance in the UI.

Do not attempt to implement the Session Manager transport protocol in MVP.

---

## 14. User Experience

### 14.1 Main window

Suggested layout:

```text
+--------------------------------------------------------------+
| AWS Session Launcher                 AWS CLI ✓  SSM Plugin ✓ |
+--------------------------------------------------------------+
| Profile                                                    |
| [ JI8-HOM v ] [Sign in] [Validate]                         |
| Account: 123456789012                                      |
| Identity: arn:aws:sts::...                                 |
+--------------------------------------------------------------+
| Target / Bastion                                           |
| Filter: Name = [bastion-host         ] [Refresh]            |
| [instance-id | name | private-ip | SSM Online]              |
+--------------------------------------------------------------+
| Port Forwarding                                             |
| ✓ Name      Mode         Host          Remote   Local Status |
| ✓ Postgres  RemoteHost   db.internal   5432     5432  ●      |
| ✓ Redis     RemoteHost   redis...      6379     6379  ●      |
|                                                              |
| [+ Add] [Edit] [Remove]       [Start All] [Stop All]         |
+--------------------------------------------------------------+
| Activity / Logs                                              |
| 11:34 Authenticated profile JI8-HOM                          |
| 11:35 Selected i-0123456789                                  |
| 11:35 Postgres tunnel listening on localhost:5432            |
+--------------------------------------------------------------+
```

### 14.2 First-run experience

On first startup:

1. prerequisite check;
2. auto-discover AWS files;
3. load profiles;
4. preselect a previous profile if valid;
5. otherwise let the user select;
6. validate profile;
7. discover target;
8. load saved forwarding presets.

Do not show a folder picker unless discovery fails.

### 14.3 Visual design

Use a clean native Windows desktop design:

- Segoe UI;
- clear spacing;
- minimal modal dialogs;
- status badges;
- green/yellow/red state indicators;
- primary Start buttons;
- disabled controls for invalid states;
- tooltips for advanced concepts.

No third-party UI framework is required for MVP.

---

## 15. Settings and Persistence

Store application preferences in:

```text
%LOCALAPPDATA%\AwsSessionLauncher\settings.json
```

Allowed:

- config-file override path;
- credentials-file override path;
- last selected profile name;
- last region;
- target filter;
- last selected instance ID;
- connection presets;
- window dimensions.

Never store:

- AWS secret access keys;
- AWS session token;
- IAM Identity Center access tokens;
- passwords;
- database credentials.

Suggested model:

```csharp
public sealed class AppSettings
{
    public string? ConfigFilePath { get; set; }
    public string? CredentialsFilePath { get; set; }
    public string? LastProfile { get; set; }
    public string? LastRegion { get; set; }
    public TargetFilterSettings TargetFilter { get; set; } = new();
    public List<PortForwardRuleSettings> Rules { get; set; } = [];
}
```

Write settings atomically.

---

## 16. Region Resolution

Resolve region in this order:

1. explicit user override in the application;
2. selected profile's configured region;
3. ask the user to choose a region.

Do not silently invent a region.

Remember the override per profile if useful.

---

## 17. Error Handling

Create typed error categories:

```text
PrerequisiteMissing
ProfileNotFound
AuthenticationRequired
AuthenticationFailed
RegionMissing
AwsPermissionDenied
TargetNotFound
MultipleTargetsFound
TargetNotManaged
TargetOffline
LocalPortInUse
SessionStartFailed
SessionUnexpectedExit
ConfigurationParseFailed
Unknown
```

Every user-facing error must contain:

- short summary;
- likely cause;
- suggested action;
- optional expandable technical details.

Example:

```text
Could not start Postgres tunnel.

Local port 5432 is already in use.

Choose another local port or stop the process currently using 5432.
```

Avoid dumping raw stack traces into modal dialogs.

---

## 18. Logging

Use `Microsoft.Extensions.Logging`.

Write rotating application logs to:

```text
%LOCALAPPDATA%\AwsSessionLauncher\logs\
```

Logs may include:

- operation name;
- profile name;
- account ID;
- region;
- instance ID;
- rule name;
- local/remote ports;
- AWS CLI exit code.

Logs must not include:

- secret access key;
- secret tokens;
- authorization headers;
- cached SSO token contents.

Include a **Copy diagnostics** action that emits sanitized diagnostic text.

---

## 19. Architecture

Use a lightweight layered architecture rather than full enterprise DDD.

```text
src/
  AwsSessionLauncher.WinForms/
    Program.cs
    Presentation/
      MainForm.cs
      Controls/
      Dialogs/
      Presenters/
    Application/
      AwsEnvironmentService.cs
      AwsProfileService.cs
      AwsAuthenticationService.cs
      AwsInstanceDiscoveryService.cs
      SsmSessionService.cs
      PortValidationService.cs
      SettingsService.cs
    Domain/
      AwsProfile.cs
      AwsIdentity.cs
      Ec2Target.cs
      PortForwardRule.cs
      SsmSession.cs
      Enums/
    Infrastructure/
      AwsCliProcessRunner.cs
      AwsConfigParser.cs
      FileSettingsRepository.cs
      WindowsPortInspector.cs
      PrerequisiteDetector.cs

tests/
  AwsSessionLauncher.UnitTests/
  AwsSessionLauncher.IntegrationTests/
```

### 19.1 Dependency injection

Use:

```text
Microsoft.Extensions.Hosting
Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Logging
Microsoft.Extensions.Options
```

`Program.cs` should configure the Generic Host and resolve `MainForm`.

### 19.2 Avoid unnecessary dependencies

Do not add:

- MediatR;
- EF Core;
- database;
- AutoMapper;
- message bus;
- web server.

This is a local desktop utility. Keep the architecture testable but small.

---

## 20. Important Domain Models

### 20.1 AWS Profile

```csharp
public sealed record AwsProfile
{
    public required string Name { get; init; }
    public string? Region { get; init; }
    public AwsProfileType Type { get; init; }
    public string? SsoSessionName { get; init; }
}
```

### 20.2 EC2 target

```csharp
public sealed record Ec2Target
{
    public required string InstanceId { get; init; }
    public string? Name { get; init; }
    public string? PrivateIpAddress { get; init; }
    public string? AvailabilityZone { get; init; }
    public required string State { get; init; }
    public SsmTargetStatus SsmStatus { get; init; }
}
```

### 20.3 Session

```csharp
public sealed class SsmSession
{
    public required Guid Id { get; init; }
    public required PortForwardRule Rule { get; init; }
    public required string InstanceId { get; init; }
    public SessionState State { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? LastError { get; set; }
}
```

---

## 21. AWS Command Adapter

Centralize AWS commands in one component.

```csharp
public interface IAwsCli
{
    Task<AwsCliVersion> GetVersionAsync(CancellationToken ct);

    Task<AwsIdentity> GetCallerIdentityAsync(
        string profile,
        string? region,
        CancellationToken ct);

    Task SsoLoginAsync(
        string profile,
        CancellationToken ct);

    Task<IReadOnlyList<Ec2Target>> DescribeInstancesAsync(
        string profile,
        string region,
        TargetFilter filter,
        CancellationToken ct);

    Task<IReadOnlyDictionary<string, SsmTargetStatus>> GetSsmStatusesAsync(
        string profile,
        string region,
        IReadOnlyCollection<string> instanceIds,
        CancellationToken ct);

    Task<ManagedProcess> StartPortForwardAsync(
        StartPortForwardRequest request,
        CancellationToken ct);
}
```

No UI code may directly build AWS command lines.

---

## 22. Target Filters

Default configuration:

```json
{
  "tagName": "Name",
  "tagValue": "bastion-host",
  "requireRunning": true,
  "requireSsmOnline": true
}
```

The UI should allow changing the filter.

Future-ready domain model:

```csharp
public sealed record Ec2TagFilter(string Name, IReadOnlyList<string> Values);
```

---

## 23. Connection Presets

Support presets such as:

```json
{
  "name": "Homolog - PostgreSQL",
  "mode": "RemoteHost",
  "remoteHost": "mydb.internal",
  "remotePort": 5432,
  "localPort": 5432
}
```

Do not place passwords in presets.

Provide:

```text
Save preset
Duplicate preset
Delete preset
```

---

## 24. Local Port Availability

Before starting a rule:

- inspect active TCP listeners;
- verify the requested local port is free.

Perform a second check immediately before creating the process because of race conditions.

If the port becomes occupied between validation and AWS startup, report the AWS/process failure normally.

---

## 25. Concurrency

The UI must remain responsive.

All AWS CLI calls must run asynchronously.

Rules:

- never call `.Result` or `.Wait()` on the UI thread;
- marshal UI state updates back onto the WinForms UI context;
- allow cancellation during discovery;
- serialize duplicate Start requests for the same rule;
- `Start All` may use bounded concurrency.

A rule must never own more than one active child session process.

---

## 26. Security Requirements

Mandatory:

- never log AWS secrets;
- never display credential-file secret values;
- never copy credential contents into app settings;
- use AWS CLI's credential provider/profile behavior;
- use temporary SSO credentials where configured;
- validate all user-controlled process arguments;
- use `ArgumentList`;
- no shell injection surface;
- sanitize logs;
- do not elevate to Administrator unnecessarily;
- do not open inbound Windows firewall ports;
- bind access through the Session Manager local listener behavior only;
- do not add SSH keys as a fallback.

Optional later enhancement:

- enterprise allow-list for profiles/accounts/regions.

---

## 27. IAM Permission Expectations

Exact IAM policies are environment-specific, but the application may require permissions such as:

```text
sts:GetCallerIdentity
ec2:DescribeInstances
ssm:DescribeInstanceInformation
ssm:StartSession
ssm:TerminateSession
```

Actual Session Manager permissions can be constrained by:

- managed instance ARN;
- Session Manager documents;
- tags;
- user/session policies.

Do not generate or modify IAM policies automatically.

---

## 28. Testing Strategy

### 28.1 Unit tests

Minimum coverage areas:

- config parsing;
- profile-name normalization;
- profile-type detection;
- region resolution;
- target-filter building;
- AWS JSON parsing;
- port validation;
- process argument construction;
- settings serialization;
- error mapping;
- session state transitions.

### 28.2 Process argument tests

Tests must prove that values containing spaces or shell metacharacters are passed as a single argument and never interpreted by a shell.

### 28.3 Integration tests

Integration tests must use a fake `aws.exe` test fixture or injectable process runner.

Do not require real AWS credentials in CI.

Simulate:

- successful identity;
- expired SSO session;
- login success;
- access denied;
- zero target instances;
- multiple targets;
- SSM offline;
- start-session success;
- process early exit;
- local port occupied.

### 28.4 Manual AWS smoke test

A developer may run an opt-in real AWS smoke test locally using an environment variable such as:

```text
AWS_SESSION_LAUNCHER_SMOKE_PROFILE
```

Real-AWS tests must not run in normal CI.

---

## 29. Acceptance Criteria

The implementation is complete when all of the following pass:

1. Double-clicking the published `.exe` opens the UI.
2. The UI stays responsive during AWS operations.
3. Standard AWS config is found without asking the user for a folder.
4. Non-default `AWS_CONFIG_FILE` is respected.
5. Non-default `AWS_SHARED_CREDENTIALS_FILE` is respected.
6. Profiles from both config and credentials are deduplicated.
7. Secret values are never displayed.
8. The selected SSO profile can launch browser-based login.
9. Successful login is verified by `sts get-caller-identity`.
10. Non-SSO profiles do not incorrectly trigger `aws sso login`.
11. EC2 targets can be discovered by Name tag.
12. Multiple matching targets require user selection.
13. SSM-online state is visible.
14. The app can start a managed-node port-forwarding session.
15. The app can start a remote-host port-forwarding session.
16. Multiple port rules can run concurrently.
17. A local-port conflict is detected before startup.
18. Sessions can be stopped individually.
19. All sessions can be stopped together.
20. Closing the app cleans up active child processes.
21. Errors are actionable and sanitized.
22. Settings persist without storing credentials.
23. Unit/integration test suites pass.
24. Release publish produces a self-contained Windows executable.

---

## 30. Publishing

Primary release command:

```powershell
dotnet publish .\src\AwsSessionLauncher.WinForms\AwsSessionLauncher.WinForms.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true
```

The produced executable may be self-contained for .NET, but AWS CLI and the Session Manager plugin remain workstation prerequisites in MVP.

Optional future targets:

```text
win-arm64
MSIX installer
winget package
enterprise code signing
```

---

## 31. Recommended Implementation Sequence for Cursor

Implement in this order:

1. solution/projects and DI host;
2. process abstraction;
3. AWS CLI prerequisite detection;
4. AWS config discovery/parser;
5. profile list;
6. caller-identity validation;
7. SSO login;
8. target discovery;
9. SSM status discovery;
10. port rule domain/validation;
11. long-running SSM process management;
12. main WinForms UI;
13. presets/settings;
14. logs/diagnostics;
15. shutdown cleanup;
16. unit and integration tests;
17. self-contained publish profile;
18. README.

Do not begin with visual polish before the AWS/process lifecycle is reliable.

---

## 32. Cursor Implementation Instructions

Cursor should implement the complete solution, not only scaffold classes.

Requirements:

- compile after each major slice;
- keep warnings at zero where practical;
- add tests alongside each service;
- do not leave production methods as `TODO`;
- do not hard-code profile `JI8-HOM`;
- do not hard-code account IDs;
- do not hard-code instance IDs;
- do not hard-code secrets;
- do not hard-code `bastion-host` except as an editable default;
- do not use shell command concatenation;
- use cancellation tokens;
- use async I/O;
- maintain a responsive UI;
- centralize AWS CLI invocation;
- centralize error mapping;
- use strongly typed JSON deserialization;
- add XML/docs only where they improve maintainability;
- prefer simple code over unnecessary abstractions.

At the end, Cursor must provide:

```text
1. implementation summary
2. file tree
3. build command
4. test command
5. publish command
6. prerequisites
7. manual validation checklist
8. known limitations
```

---

## 33. Definition of Done

The project is Done when a non-technical user who already has authorized AWS access can:

1. double-click the app;
2. select an AWS profile;
3. sign in when required;
4. select the bastion/managed instance;
5. choose one or more connection presets;
6. click **Start**;
7. connect another local application to `localhost:<localPort>`;
8. see which tunnels are active;
9. stop them without opening a terminal.

That is the core user experience this product must optimize for.
