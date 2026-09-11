# Software Design Document (SDD)
# AWS SSM Port Forwarding Launcher for Windows

**Document version:** 2.1  
**Status:** Implementation-ready  
**Language:** en-US  
**Target:** Windows 10/11 x64  
**Framework:** .NET 10 Windows Forms  
**Primary goal:** Let a user establish managed-node or remote-host AWS Systems Manager port-forwarding sessions through a shared, table-based UI that asks only for the parameters required by each connection type.

---

## 1. Revision Summary

Version 2.1 retains the simple design and adds AWS-supported remote-host forwarding.

The normal user journey must require only:

1. selecting the folder that contains the AWS `config` and/or `credentials` files;
2. choosing a discovered AWS profile;
3. adding one or more connection rows and providing only their applicable destination/ports;
4. clicking **Connect**.

The application must automatically:

- discover profiles from the selected AWS files;
- determine whether a profile uses AWS IAM Identity Center / SSO or credentials already available to the AWS CLI;
- request browser approval only when an SSO login is actually required;
- validate the selected identity;
- resolve the AWS Region from the profile;
- find the running EC2 instance whose `Name` tag is `bastion-host`;
- verify that the instance is online in Systems Manager;
- choose the correct AWS SSM document and parameter schema for each connection row;
- start one SSM session per connection row;
- monitor and stop those sessions.

The application is connection-oriented and service-agnostic. It must not contain a hardcoded reference to Garnet, MuleSoft, Redis, PostgreSQL, or any other application/service. Port `6106`, host `tap-papi-uat.mulesoft.pip.internal`, and ports `443`/`9100` are examples only.

---

## 2. Product Objective

Replace a manual workflow similar to this:

```bash
aws sso login --profile JI8-HOM

INSTANCE_ID=$(aws ec2 describe-instances \
  --profile JI8-HOM \
  --filters \
    "Name=tag:Name,Values=bastion-host" \
    "Name=instance-state-name,Values=running" \
  --query "Reservations[].Instances[].InstanceId" \
  --output text)

aws ssm start-session \
  --profile JI8-HOM \
  --target "$INSTANCE_ID" \
  --document-name AWS-StartPortForwardingSession \
  --parameters '{"portNumber":["6106"],"localPortNumber":["6106"]}'

aws ssm start-session \
  --profile JI8-HOM \
  --target "$INSTANCE_ID" \
  --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters '{"host":["tap-papi-uat.mulesoft.pip.internal"],"portNumber":["443"],"localPortNumber":["9100"]}'
```

with a safe desktop workflow that does not require the user to know or compose AWS CLI commands.

The literal values `JI8-HOM`, `bastion-host`, the EC2 instance ID, example host, and example ports must not be embedded in production source code.

- Profile comes from the selected AWS files.
- Target instance ID comes from EC2 discovery.
- Target tag policy comes from application configuration and defaults to `Name=bastion-host`.
- Forwarding type, remote host when applicable, remote port, and local port come from each connection row.

---

## 3. Product Principles

1. **Simple normal path:** folder, profile, connection table, Connect.
2. **AWS CLI is the credential authority:** the app never implements its own credential store.
3. **No secret entry in the app:** credentials must already exist in the selected AWS credentials file or be supplied by an AWS-supported profile mechanism.
4. **SSO only when needed:** validate the identity first; do not force a new login for a valid cached session.
5. **No shell composition:** invoke `aws.exe` directly using structured process arguments.
6. **No service coupling:** a port is a port; the app does not need to know whether it belongs to Garnet or another service.
7. **Exceptional complexity stays exceptional:** Region or target selection appears only if automatic resolution cannot produce exactly one safe result.
8. **Progressive disclosure:** a remote-host field is enabled only for remote-host forwarding; managed-node rows never ask for it.
9. **Known schemas, not arbitrary commands:** users select a supported forwarding type; they never type document names, CLI flags, or raw parameter JSON.

---

## 4. Scope

### 4.1 MVP

The MVP must include:

- Windows Forms desktop UI;
- self-contained Windows publication;
- first-run selection of an `.aws` folder;
- optional auto-suggestion of `%USERPROFILE%\.aws`;
- persistence of the last valid folder path and last selected profile;
- profile discovery through the AWS CLI;
- support for IAM Identity Center / SSO, credentials-file, AssumeRole, `credential_process`, and other AWS CLI profiles when already configured;
- validation with `sts get-caller-identity`;
- SSO sign-in when required and approved by the user;
- Region resolution from the selected profile;
- automatic running-bastion discovery and SSM online validation;
- managed-node forwarding with `AWS-StartPortForwardingSession`;
- remote-host forwarding with `AWS-StartPortForwardingSessionToRemoteHost`;
- one or more heterogeneous connection rows in the same table;
- start, monitor, and stop sessions;
- local port conflict detection;
- concise, sanitized activity messages;
- automated unit and integration tests using a fake AWS CLI process adapter.

### 4.2 Explicitly out of scope for MVP

Do not implement:

- AWS access-key or secret-key input fields;
- writing or editing AWS credentials;
- a credential vault;
- a custom SSO/OAuth or Session Manager WebSocket implementation;
- arbitrary SSM document names, arbitrary CLI arguments, or raw parameter JSON;
- service-specific built-in presets such as Garnet, MuleSoft, Redis, or databases;
- SSH/RDP clients;
- IAM policy creation or AWS infrastructure provisioning;
- a Windows service;
- macOS/Linux UI.

The product supports only the two AWS-managed port-forwarding document schemas defined in this SDD. Additional documents require a future typed extension mechanism and must not be exposed as free-form commands.

---

## 5. AWS-Supported Behavior and Design Decisions

### 5.1 Shared AWS files

AWS uses shared `config` and `credentials` files containing named profiles. On Windows, their default location is `%USERPROFILE%\.aws`. AWS supports non-default paths through `AWS_CONFIG_FILE` and `AWS_SHARED_CREDENTIALS_FILE`.

When the user selects a folder, set these variables only on AWS CLI child processes:

```text
AWS_CONFIG_FILE=<selected-folder>\config
AWS_SHARED_CREDENTIALS_FILE=<selected-folder>\credentials
```

Do not modify machine-level or user-level environment variables. Set both child-process overrides to paths inside the selected folder even when one file is absent. This prevents the AWS CLI from accidentally combining the selected folder with a different default file from `%USERPROFILE%\.aws`. A folder is valid when it contains `config`, `credentials`, or both; AWS CLI handles the absent counterpart as a missing file.

### 5.2 Profile discovery

Use AWS CLI as the source of truth:

```text
aws configure list-profiles
```

Run it with the selected file paths in the child environment. Trim output, remove blanks, and deduplicate names. Do not parse secret values merely to enumerate profiles.

### 5.3 Authentication behavior

Always validate first:

```text
aws sts get-caller-identity \
  --profile <profile> \
  --output json \
  --no-cli-pager
```

If successful, authentication is valid regardless of the AWS-supported provider. Do not perform SSO again.

If validation fails:

- classify the profile as SSO when its config contains `sso_session` or legacy `sso_start_url` settings;
- for SSO, ask the user to approve sign-in and run `aws sso login --profile <profile> --no-cli-pager`;
- after successful login, rerun STS and accept authentication only if STS succeeds;
- for non-SSO, do not run SSO; explain that credentials are missing, invalid, or expired and ask the user to update the selected AWS files outside the app, then click **Reload**.

The AWS CLI v2 normally opens the system browser for SSO. If browser launch fails, show sanitized CLI guidance and offer **Use device code**, invoking `aws sso login --use-device-code --no-browser`.

### 5.4 Credentials-file profiles

Support credentials already defined like:

```ini
[JI8-HOM]
aws_access_key_id = <value>
aws_secret_access_key = <value>
aws_session_token = <optional-temporary-token>
```

Never display, copy into settings, log, transmit, validate by inspection, or rewrite these values. Pass only the profile name and selected file locations to AWS CLI.

Prefer IAM Identity Center/SSO or another temporary-credential mechanism over long-lived IAM user keys, but do not prevent configured credentials-file profiles from working.

---

## 6. User Experience

### 6.1 Information architecture

Use two visually distinct areas:

1. **AWS context card** — values shared by every forwarding session: AWS folder, profile, authentication, Region, and resolved bastion.
2. **Connections table** — values that vary per forwarding session: type, destination host when applicable, remote port, local port, and status.

This separation prevents duplicated profile/target fields in every row and makes the common-versus-specific model visible to the user.

### 6.2 Main window

```text
┌────────────────────────────────────────────────────────────────────────────┐
│ AWS Port Forwarding                                                       │
├────────────────────────────────────────────────────────────────────────────┤
│ AWS CONTEXT                                                               │
│ Folder  [ C:\Users\Paulo\.aws                         ] [Browse]          │
│ Profile [ JI8-HOM ▼ ] [Reload]       Authentication: ✓ Connected          │
│ Region: us-east-1                    Bastion: ✓ bastion-host / SSM Online  │
├────────────────────────────────────────────────────────────────────────────┤
│ CONNECTIONS                                            [+ Add connection] │
│                                                                            │
│ ✓ │ Type          │ Destination              │ Remote │ Local │ Status     │
│ ──┼───────────────┼──────────────────────────┼────────┼───────┼────────────│
│ ✓ │ Managed node▼ │ Bastion (same target)    │ 6106   │ 6106  │ Ready      │
│ ✓ │ Remote host ▼ │ tap-papi-uat...internal  │ 443    │ 9100  │ Ready      │
│                                                                            │
│ Validation messages appear directly below the affected row.               │
│                                                                            │
│ [Start enabled] [Stop all]                            0 active sessions     │
├────────────────────────────────────────────────────────────────────────────┤
│ Ready                                                                      │
└────────────────────────────────────────────────────────────────────────────┘
```

Recommended WinForms control: a styled `DataGridView` with an accessible row-editing panel or custom row template. Do not force horizontal scrolling at the minimum supported width.

### 6.3 Connections table behavior

| Column | Control | Managed node | Remote host |
| --- | --- | --- | --- |
| Enabled | Checkbox | Shown | Shown |
| Type | Combo box | `Managed node` | `Remote host` |
| Destination | Conditional text | Read-only `Bastion (same target)` | Required editable hostname/IP |
| Remote port | Numeric input | Required | Required |
| Local port | Numeric input | Required; defaults to remote | Required; defaults to remote |
| Status | Badge/text | Per-session status | Per-session status |
| Actions | Icon buttons with tooltips | Start/Stop/Delete | Start/Stop/Delete |

Rules:

- Selecting **Managed node** disables and clears `RemoteHost`; the Destination cell displays `Bastion (same target)`.
- Selecting **Remote host** enables Destination and places focus there.
- When the remote port is entered into a new row and local port is blank, copy it to local port once. Do not keep both fields permanently linked after the user edits local port.
- Changing any command-affecting field while its session is active is blocked; the user must stop it first.
- Delete requires confirmation only for an active row; otherwise delete immediately with Undo available through a temporary notification.
- Persist non-sensitive connection rows, including internal hostnames, only when the user enables **Remember connections**. Default is off.
- Never include service-specific dropdown values. An optional user-defined Label may be added later but must not affect command construction.

### 6.4 Add/edit interaction

Clicking **Add connection** appends one row with:

```text
Enabled = true
Type = Managed node
Destination = Bastion (same target)
RemotePort = blank
LocalPort = blank
Status = Incomplete
```

Keyboard behavior:

- `Ctrl+N`: add row;
- `Enter`: commit the current cell and move to the next required field;
- `Delete`: delete selected stopped rows after confirmation rules;
- `Ctrl+Enter`: start enabled valid rows;
- `Esc`: cancel cell editing.

### 6.5 Validation presentation

Validate inline, not through a sequence of modal dialogs.

- Invalid cells receive an error state plus icon and accessible message.
- Row status becomes `Incomplete` or `Invalid`.
- **Start enabled** ignores disabled rows but is disabled when any enabled row is invalid.
- A summary such as `2 ready • 1 incomplete • 0 active` appears beside the actions.
- Do not use color alone.

### 6.6 First-run and normal flow

1. Suggest `%USERPROFILE%\.aws` when valid; keep folder visible/editable.
2. Discover profiles and restore the last profile only when it still exists.
3. Validate identity; request SSO approval only when necessary.
4. Resolve Region and bastion automatically.
5. User adds rows, selects type, and enters applicable destination/ports.
6. User starts all enabled rows or one row.

If credentials are already valid, no authentication choice is shown.

### 6.7 Exceptional prompts

Do not ask for Region or target normally. Prompt only for missing Region, multiple eligible bastions, or no eligible bastion. Never silently select the first of multiple targets.

### 6.8 Visual and accessibility requirements

- Use 8-pixel spacing increments, clear section headings, restrained color, and sufficient contrast.
- Keep primary action visually dominant; destructive actions are secondary and require explicit icons/tooltips.
- Support keyboard navigation, screen-reader labels, visible focus, DPI scaling, and text-plus-color states.
- Keep UI responsive.
- Use concise statuses: `Incomplete`, `Ready`, `Connecting`, `Connected`, `Stopping`, `Stopped`, `Failed`.
- Put verbose sanitized details in a collapsible Diagnostics panel, not the table.

---

## 7. Common and Conditional Forwarding Parameters

### 7.1 Canonical command model

Every connection is represented by one typed `PortForwardRule`. The user does not enter commands.

| Command element | Source | Common or conditional |
| --- | --- | --- |
| `aws ssm start-session` | Application | Common |
| `--profile` | Selected AWS context | Common |
| `--region` | Resolved AWS context | Common |
| `--target` | Resolved SSM-online bastion | Common |
| `--document-name` | Derived from row Type | Common argument, type-specific value |
| `--parameters` | Serialized from row fields | Common argument, type-specific schema |
| `--no-cli-pager` | Application | Common |

`winpty` is not part of AWS CLI or the command model. It is a Git Bash terminal compatibility wrapper and must not be invoked by the Windows application.

### 7.2 Supported typed schemas

| UI Type | SSM document | Required row parameters | Not applicable |
| --- | --- | --- | --- |
| Managed node | `AWS-StartPortForwardingSession` | `portNumber`, `localPortNumber` | `host` |
| Remote host | `AWS-StartPortForwardingSessionToRemoteHost` | `host`, `portNumber`, `localPortNumber` | — |

The document name is derived internally from the enum. Users must not type or override it.

### 7.3 Field semantics

- `RemoteHost`: DNS hostname or IP reachable and resolvable from the managed bastion. Required only for Remote host.
- `RemotePort`: destination port on the managed node or remote host.
- `LocalPort`: listening port on the user's Windows machine; local clients use `localhost:<LocalPort>`.

### 7.4 Validation

- Ports are integers from 1 through 65535.
- Enabled rows require both ports.
- Local ports must be unique among enabled rows.
- Remote host is required only for Remote host rows.
- Host input must not include a URL scheme, credentials, path, query, fragment, or port suffix.
- Accept a valid DNS name, IPv4 literal, or IPv6 literal; trim surrounding whitespace.
- Reject CR/LF and control characters in every string field.
- Maximum hostname length is 253 characters.
- More than 20 enabled rows requires confirmation.
- Do not allow raw JSON, arbitrary key/value parameters, environment variables, or CLI switches in the table.

### 7.5 Local port availability

Before starting, inspect TCP listeners and attempt a short-lived exclusive bind to `IPAddress.Loopback`. Release it and immediately start AWS. If a race still causes failure, map the CLI/process error clearly.

---

## 8. Region and Target Discovery

### 8.1 Region

Resolve using:

```text
aws configure get region --profile <profile>
```

If blank, do not guess. Prompt once, apply the Region to current-run commands with `--region`, and do not modify AWS files automatically.

### 8.2 Target policy

Keep policy in non-secret external configuration:

```json
{
  "AwsTarget": {
    "TagKey": "Name",
    "TagValue": "bastion-host",
    "RequireRunning": true,
    "RequireSsmOnline": true
  }
}
```

`bastion-host` is a configurable convention, not a compiled constant. Enterprise packaging may replace this file without recompilation.

### 8.3 EC2 discovery

Invoke and parse JSON:

```text
aws ec2 describe-instances \
  --profile <profile> \
  --region <region> \
  --filters \
    Name=tag:Name,Values=bastion-host \
    Name=instance-state-name,Values=running \
  --output json \
  --no-cli-pager
```

Real arguments use configured values. Do not use `$(...)`, `cmd.exe`, PowerShell, `--output text` as an application contract, or a query that ambiguously collapses multiple instances.

### 8.4 SSM availability

For discovered IDs, call `ssm describe-instance-information` and require `PingStatus=Online` when configured.

- zero matches: actionable failure;
- one match: automatic selection;
- multiple matches: explicit selection;
- never choose the first implicitly.

Cache target only for the current profile, Region, and run. Rediscover on folder/profile/Region change, Reload, or target-related reconnect failure.

---

## 9. Session Creation and Lifecycle

### 9.1 One session per connection row

For N enabled rows, create N independent processes/statuses. Rows may use different forwarding types while sharing the same authenticated AWS context and resolved target.

Managed-node example:

```text
aws ssm start-session \
  --profile <profile> \
  --region <region> \
  --target <resolved-instance-id> \
  --document-name AWS-StartPortForwardingSession \
  --parameters '{"portNumber":["6106"],"localPortNumber":["6106"]}' \
  --no-cli-pager
```

Parameters come from input. `6106` is never inserted when input is empty.

Remote-host example:

```text
aws ssm start-session \
  --profile <profile> \
  --region <region> \
  --target <resolved-instance-id> \
  --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters '{"host":["tap-papi-uat.mulesoft.pip.internal"],"portNumber":["443"],"localPortNumber":["9100"]}' \
  --no-cli-pager
```

The example host and ports are never production defaults.

### 9.2 Safe process invocation

Use `ProcessStartInfo`:

```csharp
UseShellExecute = false;
CreateNoWindow = true;
RedirectStandardOutput = true;
RedirectStandardError = true;
```

Populate `ArgumentList`; never concatenate shell command text.

```csharp
startInfo.ArgumentList.Add("ssm");
startInfo.ArgumentList.Add("start-session");
startInfo.ArgumentList.Add("--profile");
startInfo.ArgumentList.Add(request.ProfileName);
startInfo.ArgumentList.Add("--region");
startInfo.ArgumentList.Add(request.Region);
startInfo.ArgumentList.Add("--target");
startInfo.ArgumentList.Add(request.InstanceId);
startInfo.ArgumentList.Add("--document-name");
startInfo.ArgumentList.Add(documentNameResolver.Resolve(request.Rule.Type));
startInfo.ArgumentList.Add("--parameters");
startInfo.ArgumentList.Add(parameterSerializer.Serialize(request.Rule));
startInfo.ArgumentList.Add("--no-cli-pager");
```

Add selected AWS file overrides to `startInfo.Environment`.

The typed serializer must generate exactly one of these shapes:

```json
{"portNumber":["6106"],"localPortNumber":["6106"]}
```

```json
{"host":["tap-papi-uat.mulesoft.pip.internal"],"portNumber":["443"],"localPortNumber":["9100"]}
```

Never serialize `host` for Managed node. Never accept a pre-serialized parameters string from UI or settings.

### 9.3 Readiness

Mark `Connected` only when the process remains alive, no terminal error was parsed, and the local TCP port listens within 20 seconds. Do not rely only on one English output phrase. On timeout, stop the process tree and mark failed.

### 9.4 Stop

For each owned session:

1. mark `Stopping`;
2. cancel stream readers;
3. terminate its owned process tree;
4. if Session ID was captured, make a best-effort `ssm terminate-session` call;
5. release resources and mark `Stopped`.

On app close, confirm only when sessions are active, stop owned sessions, and never terminate unrelated AWS/plugin processes.

### 9.5 Partial success

One failure must not stop successful mappings. Show each result and provide **Retry failed** and **Stop all**.

---

## 10. State Model

Application readiness:

```text
Initializing -> FolderRequired -> DiscoveringProfiles -> ProfileRequired
-> ValidatingAuthentication -> SignInRequired | ResolvingTarget -> Ready | Error
```

Per-port session:

```text
Stopped -> Connecting -> Connected -> Stopping -> Stopped
Connecting -> Failed
Connected -> Failed
Failed -> Connecting
```

Transitions must be explicit and testable. A mapping owns at most one active process.

---

## 11. Errors

| Condition | User message | Action |
| --- | --- | --- |
| AWS CLI missing | AWS CLI v2 was not found. | Official install help |
| Plugin missing | Session Manager plugin was not found. | Official install help |
| Invalid folder | Select a folder containing `config` or `credentials`. | Browse |
| No profiles | No profiles were found. | Reload / help |
| SSO expired | Sign-in approval is required. | Sign in |
| SSO cancelled | Sign-in was cancelled. | Retry |
| Credentials invalid | Credentials are missing, invalid, or expired. | Update AWS files / Reload |
| Region missing | This profile does not define a Region. | Select Region |
| EC2 denied | This profile cannot discover EC2 instances. | Show permission |
| No bastion | No running, SSM-online bastion was found. | Reload |
| Multiple bastions | Multiple eligible bastions were found. | Select target |
| SSM denied | This profile cannot start an SSM session. | Show permission |
| Remote host invalid | Enter a hostname or IP without protocol, path, or port. | Edit Destination |
| Remote host unreachable | The bastion could not resolve or reach the remote host and port. | Verify DNS, routes, security groups, NACLs, and service availability |
| SSM Agent incompatible | The bastion's SSM Agent does not support this forwarding type. | Ask the AWS administrator to update the agent |
| Local port occupied | Local port `<n>` is already in use. | Change / Retry |
| Session exited | The forwarding session ended unexpectedly. | Retry |

Do not classify every failure as authentication. Retain exit code and sanitized stderr for diagnostics.

---

## 12. Prerequisites

At startup:

```text
aws --version
session-manager-plugin --version
```

Require AWS CLI v2 and the Session Manager plugin. Do not silently install them.

The target must be an online managed node, use a compatible SSM Agent, and allow the principal to start the selected document. AWS documents managed-node port forwarding as requiring SSM Agent 2.3.672.0 or later and remote-host forwarding as requiring SSM Agent 3.1.1374.0 or later. For remote-host rows, DNS resolution and network connectivity from the bastion to the remote host remain external prerequisites.

---

## 13. Security Requirements

- Never show/log/store AWS keys, session tokens, SSO tokens, or authorization codes.
- Never accept keys in UI fields.
- Use AWS CLI profile behavior.
- Pass config paths only to owned child processes.
- Validate folder paths, profile names, ports, Regions, instance IDs, and config values.
- Use `ArgumentList` and `UseShellExecute=false`; never invoke a shell.
- Do not request Administrator privileges or open firewall rules.
- Do not terminate unrelated processes.
- Sanitize UI errors, logs, diagnostics, and crash reports.
- Keep least-privilege IAM outside the app.

Exported diagnostics must redact account IDs, ARNs, instance IDs, local usernames, and absolute paths unless the user explicitly opts in.

---

## 14. IAM Expectations

The user's principal generally needs:

```text
ec2:DescribeInstances
ssm:DescribeInstanceInformation
ssm:StartSession
ssm:TerminateSession        # only for best-effort explicit cleanup
```

Scope `ssm:StartSession` to eligible managed instances (preferably by tags) and the two allowed documents: `AWS-StartPortForwardingSession` and `AWS-StartPortForwardingSessionToRemoteHost`. `sts:GetCallerIdentity` validates identity. The app must not generate or attach IAM policies.

---

## 15. Settings

Persist only non-sensitive preferences under Local Application Data:

```json
{
  "schemaVersion": 1,
  "lastAwsFolder": "C:\\Users\\Paulo\\.aws",
  "lastProfile": "JI8-HOM",
  "rememberConnections": false,
  "connections": []
}
```

When `rememberConnections=true`, each saved row may contain only `type`, `remoteHost`, `remotePort`, `localPort`, `enabled`, and an optional non-command label. Internal hostnames can be sensitive operational metadata, which is why remembering rows is explicit opt-in. Do not persist keys/tokens, target ID across runs, or raw CLI output. Write settings atomically and recover safely from corruption.

---

## 16. Lean Architecture

Do not add DDD, CQRS, MediatR, EF Core, or a database.

```text
AwsSsmPortForwarder.sln
src/
  AwsSsmPortForwarder.App/
    Program.cs
    MainForm.cs
    Dialogs/TargetSelectionDialog.cs
    Dialogs/RegionSelectionDialog.cs
  AwsSsmPortForwarder.Core/
    Models/
    Services/ConnectionOrchestrator.cs
    Services/ProfileService.cs
    Services/TargetResolver.cs
    Services/PortForwardRuleValidator.cs
    Services/LocalPortChecker.cs
    State/
  AwsSsmPortForwarder.Infrastructure/
    AwsCli/AwsCliClient.cs
    AwsCli/AwsCliArgumentFactory.cs
    AwsCli/SsmDocumentNameResolver.cs
    AwsCli/SsmParameterSerializer.cs
    AwsCli/AwsCliErrorMapper.cs
    Processes/ProcessRunner.cs
    Configuration/
tests/
  AwsSsmPortForwarder.UnitTests/
  AwsSsmPortForwarder.IntegrationTests/
  TestAssets/FakeAwsCli/
```

Main abstractions:

```csharp
public interface IAwsCliClient
{
    Task<IReadOnlyList<string>> ListProfilesAsync(AwsFolderContext folder, CancellationToken ct);
    Task<AwsIdentity> GetCallerIdentityAsync(AwsContext context, CancellationToken ct);
    Task SsoLoginAsync(AwsContext context, SsoLoginMode mode, CancellationToken ct);
    Task<string?> GetRegionAsync(AwsContext context, CancellationToken ct);
    Task<IReadOnlyList<AwsTarget>> DescribeTargetsAsync(AwsContext context, TargetOptions options, CancellationToken ct);
    Task<IReadOnlyDictionary<string, SsmNodeStatus>> DescribeSsmNodesAsync(AwsContext context, IReadOnlyCollection<string> ids, CancellationToken ct);
    Task<IManagedProcess> StartPortForwardAsync(StartPortForwardRequest request, CancellationToken ct);
    Task TerminateSessionAsync(AwsContext context, string sessionId, CancellationToken ct);
}
```

No Form/control may build AWS arguments directly.

---

## 17. Core Models

```csharp
public sealed record AwsFolderContext(string FolderPath, string? ConfigFilePath, string? CredentialsFilePath);
public sealed record AwsContext(AwsFolderContext Folder, string ProfileName, string? Region);
public enum PortForwardType { ManagedNode, RemoteHost }
public sealed record PortForwardRule(
    Guid Id,
    bool Enabled,
    PortForwardType Type,
    string? RemoteHost,
    int? RemotePort,
    int? LocalPort);
public sealed record AwsTarget(string InstanceId, string Name, string? PrivateIpAddress,
    string AvailabilityZone, string Ec2State, string SsmPingStatus);
public sealed record StartPortForwardRequest(AwsContext Context, string InstanceId, PortForwardRule Rule);
```

`RemoteHost` is a generic routing field and is meaningful only when `Type=RemoteHost`. Do not create `ServiceName`, `GarnetHost`, `MuleSoftHost`, or `DatabaseType`. Do not infer Type from the presence of a host; Type is explicit.

---

## 18. Orchestration

Folder change:

```text
Cancel discovery -> confirm before stopping active sessions -> validate folder
-> build child environment -> list profiles -> restore profile if valid
```

Profile change:

```text
Clear stale identity/Region/target -> call STS
-> success: resolve Region and target
-> SSO failure: SignInRequired
-> other failure: credentials error
```

Connect:

```text
Commit table edits -> validate enabled typed rules -> revalidate authentication
-> refresh target if stale -> validate local ports
-> derive document/parameters per rule -> start with bounded concurrency
-> wait for each listener -> publish independent results
```

Use bounded concurrency of 3 by default.

---

## 19. Concurrency and Logging

- All AWS work is asynchronous; never block the UI thread with `.Result`/`.Wait()`.
- Cancel obsolete discovery on folder/profile change.
- Serialize Connect requests.
- Registry is keyed by rule ID and enforces unique active local ports; one active process per rule.
- Drain stdout/stderr asynchronously.
- Keep a bounded sanitized ring buffer per session.
- Log versions, state transitions, target counts, port mappings, exit codes, and categorized errors.
- Never log full commands with unreviewed values or full environments.

---

## 20. Testing

Unit coverage must include folder/environment construction, conditional row validation, host validation, port boundaries, duplicate local-port conflicts, SSO classification without secret loading, Region resolution, AWS arguments, EC2 JSON, SSM correlation, target cardinality, document-name resolution, both parameter JSON schemas, error mapping, state transitions, and safe settings.

Argument-safety tests must prove no shell/`winpty` is used, every argument is separate, AWS file paths are environment entries, empty input emits no port, Managed node never emits `host`, Remote host always emits exactly one validated `host`, arbitrary documents/parameters cannot enter the builder, and example values exist only in examples/tests—not production defaults.

Fake-CLI integration tests must simulate CLI/plugin missing, profile discovery from either/both files, valid/expired SSO, SSO cancellation, valid/invalid credentials, missing Region, access denied, zero/one/multiple bastions, SSM states, both forwarding types, mixed-type tables, invalid/malicious host input, port conflicts, readiness, partial success, unexpected exit, Stop, and close cleanup.

CI must not require real AWS credentials. A real-AWS smoke test must be opt-in and use non-production resources.

---

## 21. Acceptance Criteria

1. The user selects an AWS folder/profile, adds connection rows, supplies only applicable fields, and connects.
2. The user never enters AWS secrets in the app.
3. Valid credentials profiles proceed without SSO.
4. Valid cached SSO proceeds without redundant login.
5. Expired SSO requests approval, runs login, and revalidates STS.
6. Folder/profile changes invalidate stale state.
7. Region and exactly one running, SSM-online bastion resolve automatically.
8. Multiple targets require selection; zero targets fail clearly.
9. No target ID or port is hardcoded or resolved through shell substitution.
10. A Managed node row maps `6106` to `6106:6106` without any Garnet knowledge.
11. A Remote host row maps the example remote host/port `443` to local port `9100` through the resolved bastion.
12. The table enables Destination only for Remote host and never asks for irrelevant parameters.
13. Document name and typed JSON parameters derive from the row type; users cannot enter raw commands/JSON.
14. Each row owns an independent session using the appropriate AWS-managed document.
15. Local conflicts and partial mixed-type success are represented accurately.
16. Stop one/all and app-close cleanup affect only owned processes.
17. Secrets never appear in UI, logs, settings, or snapshots.
18. Target policy is externally configurable.
19. Tests pass and the UI remains responsive.

---

## 22. Definition of Done

- Acceptance criteria pass.
- Manual smoke tests confirm SSO, credentials-file auth, managed-node forwarding, and remote-host forwarding.
- Self-contained `win-x64` executable is produced.
- CLI v2/plugin prerequisites and troubleshooting are documented.
- No secret/account-specific value is committed.
- Logs/diagnostics pass secret-leak review.
- README covers installation, first run, connection, and credential safety.

---

## 23. Publishing

Use .NET 10 LTS for this new application. Publish for Windows x64:

```powershell
dotnet publish .\src\AwsSsmPortForwarder.App\AwsSsmPortForwarder.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true
```

The release may include an external non-secret `appsettings.json` beside the executable so the target tag convention can be changed without recompilation. The end user still starts the product by double-clicking the executable. AWS CLI v2 and Session Manager plugin remain workstation prerequisites.

---

## 24. Cursor Implementation Directive

```text
Implement AWS SSM Port Forwarding Launcher exactly as specified in
AWS-Session-Launcher-SDD.md.

First inspect the repository and report conflicts. Then create a short plan
mapped to Sections 16, 18, 20, and 21. Implement vertical slices and run
build/tests after each slice.

Mandatory:
- separate shared AWS context from a table of typed connection rules;
- use progressive disclosure: Destination/host is editable only for Remote host;
- never accept AWS secrets in the app;
- use AWS CLI v2 as source of truth;
- support SSO approval and configured credential profiles;
- set AWS_CONFIG_FILE/AWS_SHARED_CREDENTIALS_FILE only on child processes;
- resolve target through JSON without shell substitution;
- support AWS-StartPortForwardingSession and AWS-StartPortForwardingSessionToRemoteHost;
- derive document names and parameters from an enum/schema; never accept raw command JSON;
- never invoke winpty, cmd.exe, PowerShell, or another shell;
- no Garnet/MuleSoft/service-specific model;
- no hardcoded profile, instance ID, Region, host, or port;
- keep bastion tag policy externally configurable;
- use ProcessStartInfo.ArgumentList and never invoke a shell;
- never log/persist credentials or tokens;
- add fake-CLI integration tests;
- do not add DDD, CQRS, MediatR, EF Core, or a database.

Stop and report if any mandatory security constraint is impossible. Never
silently weaken a requirement.
```

---

## 25. Official References

- Shared file format: https://docs.aws.amazon.com/sdkref/latest/guide/file-format.html
- Shared file locations/overrides: https://docs.aws.amazon.com/sdkref/latest/guide/file-location.html
- CLI setup/non-default paths: https://docs.aws.amazon.com/cli/latest/userguide/getting-started-quickstart.html
- `configure list-profiles`: https://docs.aws.amazon.com/cli/latest/reference/configure/list-profiles.html
- `sso login`: https://docs.aws.amazon.com/cli/latest/reference/sso/login.html
- IAM Identity Center concepts: https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-sso-concepts.html
- `sts get-caller-identity`: https://docs.aws.amazon.com/cli/latest/reference/sts/get-caller-identity.html
- EC2 `describe-instances`: https://docs.aws.amazon.com/cli/latest/reference/ec2/describe-instances.html
- SSM `describe-instance-information`: https://docs.aws.amazon.com/cli/latest/reference/ssm/describe-instance-information.html
- Starting port forwarding: https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-sessions-start.html
- `ssm start-session`: https://docs.aws.amazon.com/cli/latest/reference/ssm/start-session.html
- Session Manager plugin: https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html
- Sample IAM policies: https://docs.aws.amazon.com/systems-manager/latest/userguide/getting-started-restrict-access-quickstart.html
- .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- .NET single-file deployment: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
