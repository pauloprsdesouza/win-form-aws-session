# Software Design Document (SDD)
# AWS SSM Port Forwarding Launcher for Windows

**Document version:** 2.0  
**Status:** Implementation-ready  
**Language:** en-US  
**Target:** Windows 10/11 x64  
**Framework:** .NET 10 Windows Forms  
**Primary goal:** Let a user establish AWS Systems Manager port-forwarding sessions by selecting an AWS configuration folder and profile, entering one or more ports, and clicking **Connect**.

---

## 1. Revision Summary

Version 2.0 deliberately simplifies the previous design.

The normal user journey must require only:

1. selecting the folder that contains the AWS `config` and/or `credentials` files;
2. choosing a discovered AWS profile;
3. entering one or more ports;
4. clicking **Connect**.

The application must automatically:

- discover profiles from the selected AWS files;
- determine whether a profile uses AWS IAM Identity Center / SSO or credentials already available to the AWS CLI;
- request browser approval only when an SSO login is actually required;
- validate the selected identity;
- resolve the AWS Region from the profile;
- find the running EC2 instance whose `Name` tag is `bastion-host`;
- verify that the instance is online in Systems Manager;
- start one `AWS-StartPortForwardingSession` session per port mapping;
- monitor and stop those sessions.

The application is port-oriented and service-agnostic. It must not contain a hardcoded reference to Garnet, Redis, PostgreSQL, or any other application/service. Port `6106` is an example only.

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
```

with a safe desktop workflow that does not require the user to know or compose AWS CLI commands.

The literal values `JI8-HOM`, `bastion-host`, the EC2 instance ID, and `6106` must not be embedded in application source code.

- Profile comes from the selected AWS files.
- Target instance ID comes from EC2 discovery.
- Target tag policy comes from application configuration and defaults to `Name=bastion-host`.
- Ports come from user input.

---

## 3. Product Principles

1. **Simple normal path:** folder, profile, ports, Connect.
2. **AWS CLI is the credential authority:** the app never implements its own credential store.
3. **No secret entry in the app:** credentials must already exist in the selected AWS credentials file or be supplied by an AWS-supported profile mechanism.
4. **SSO only when needed:** validate the identity first; do not force a new login for a valid cached session.
5. **No shell composition:** invoke `aws.exe` directly using structured process arguments.
6. **No service coupling:** a port is a port; the app does not need to know whether it belongs to Garnet or another service.
7. **Exceptional complexity stays exceptional:** Region or target selection appears only if automatic resolution cannot produce exactly one safe result.

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
- one or more port mappings;
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
- `AWS-StartPortForwardingSessionToRemoteHost` or remote-host fields;
- service-specific presets such as Garnet, Redis, or databases;
- SSH/RDP clients;
- IAM policy creation or AWS infrastructure provisioning;
- a Windows service;
- macOS/Linux UI.

`AWS-StartPortForwardingSessionToRemoteHost` may be considered later only if the actual service is not listening on the managed bastion itself. It is excluded now because it requires a remote hostname and conflicts with the desired ports-only interface.

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

### 6.1 Main window

Keep the interface intentionally small.

```text
┌──────────────────────────────────────────────────────────────┐
│ AWS Port Forwarding                                         │
├──────────────────────────────────────────────────────────────┤
│ AWS folder                                                   │
│ [ C:\Users\Paulo\.aws                          ] [Browse]    │
│ ✓ config found   ✓ credentials found   AWS CLI ✓   SSM ✓    │
│                                                              │
│ Profile                                                      │
│ [ JI8-HOM ▼ ]                                  [Reload]      │
│ Authentication: SSO • Sign-in required          [Sign in]   │
│                                                              │
│ Ports                                                        │
│ [ 6106, 5432, 6379:16379                              ]      │
│ Use `remote` or `remote:local`; local defaults to remote     │
│                                                              │
│ Target: bastion-host • i-0123... • SSM Online               │
│                                                              │
│ [ Connect ]                                      [Stop all]  │
├──────────────────────────────────────────────────────────────┤
│ ● 6106 → localhost:6106                         Connected    │
│ ● 5432 → localhost:5432                         Connected    │
│ ○ 6379 → localhost:16379                        Stopped      │
│                                                              │
│ Ready                                                        │
└──────────────────────────────────────────────────────────────┘
```

Do not show Garnet/service names, AWS secrets, a remote-host input, EC2 filters, or verbose/raw CLI logs in the normal UI.

### 6.2 First-run behavior

1. Suggest `%USERPROFILE%\.aws` when it contains `config` or `credentials`.
2. Always keep the folder control visible and editable.
3. If no valid default exists, open the folder picker with a short explanation.
4. Validate the folder, discover profiles, and restore the last profile only if it still exists.

### 6.3 Normal connection flow

1. User selects folder.
2. App discovers profiles.
3. User selects profile.
4. App validates authentication.
5. If required, user approves SSO sign-in in the browser.
6. App resolves Region, bastion, and SSM status.
7. User enters ports and clicks **Connect**.
8. App opens one session per mapping and reports each result independently.

If credentials are already valid, no explicit authentication choice is shown.

### 6.4 Exceptional prompts

Normal flow must not ask for Region or target. Prompt only when required:

- **Region missing:** show a small selector and recommend saving the Region to the AWS profile.
- **Multiple eligible bastions:** show instance ID, Name, private IP, Availability Zone, and SSM status.
- **No eligible bastion:** show the automatic criteria used and stop.

Never silently choose the first of multiple targets.

### 6.5 Accessibility and feedback

- Support keyboard navigation and visible focus.
- Use text plus color for status.
- Disable **Connect** until prerequisites are valid.
- Keep UI responsive.
- Use concise states: `Checking`, `Sign-in required`, `Ready`, `Connecting`, `Connected`, `Stopping`, `Stopped`, `Failed`.

---

## 7. Port Input and Semantics

### 7.1 Input grammar

Accept comma-, semicolon-, whitespace-, or newline-separated entries:

```text
6106
6106,5432,6379
6379:16379
```

Grammar:

```text
entry := remotePort | remotePort ':' localPort
```

Rules:

- `6106` means remote `6106`, local `6106`.
- `6379:16379` means remote `6379`, local `16379`.
- Ports must be integers from 1 through 65535.
- Duplicate local ports are invalid; duplicate mappings are deduplicated.
- Empty tokens are ignored.
- More than 20 mappings requires confirmation.
- Return per-token validation messages.

### 7.2 Meaning

For `AWS-StartPortForwardingSession`, `portNumber` is on the selected managed EC2 instance; `localPortNumber` is on the user's Windows machine. The local client connects to `localhost:<localPortNumber>`.

The app never infers or requires the service name.

### 7.3 Local port validation

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

### 9.1 One session per mapping

For N mappings, create N independent processes/statuses.

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
startInfo.ArgumentList.Add("AWS-StartPortForwardingSession");
startInfo.ArgumentList.Add("--parameters");
startInfo.ArgumentList.Add(JsonSerializer.Serialize(new
{
    portNumber = new[] { request.RemotePort.ToString(CultureInfo.InvariantCulture) },
    localPortNumber = new[] { request.LocalPort.ToString(CultureInfo.InvariantCulture) }
}));
startInfo.ArgumentList.Add("--no-cli-pager");
```

Add selected AWS file overrides to `startInfo.Environment`.

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

The target must be an online managed node, use a compatible SSM Agent, allow the principal to start the document, and have the requested port listening/reachable on that node. AWS documents managed-node port forwarding as requiring SSM Agent 2.3.672.0 or later.

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

Scope `ssm:StartSession` to eligible managed instances (preferably by tags) and `AWS-StartPortForwardingSession`. `sts:GetCallerIdentity` validates identity. The app must not generate or attach IAM policies.

---

## 15. Settings

Persist only non-sensitive preferences under Local Application Data:

```json
{
  "schemaVersion": 1,
  "lastAwsFolder": "C:\\Users\\Paulo\\.aws",
  "lastProfile": "JI8-HOM",
  "lastPortsText": "6106"
}
```

Do not persist keys/tokens, target ID across runs, or raw CLI output. Write settings atomically and recover safely from corruption.

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
    Services/PortParser.cs
    Services/LocalPortChecker.cs
    State/
  AwsSsmPortForwarder.Infrastructure/
    AwsCli/AwsCliClient.cs
    AwsCli/AwsCliArgumentFactory.cs
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
public sealed record PortMapping(int RemotePort, int LocalPort);
public sealed record AwsTarget(string InstanceId, string Name, string? PrivateIpAddress,
    string AvailabilityZone, string Ec2State, string SsmPingStatus);
public sealed record StartPortForwardRequest(AwsContext Context, string InstanceId, PortMapping Mapping);
```

Do not create `ServiceName`, `GarnetHost`, `DatabaseType`, or `RemoteHost` fields in MVP.

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
Parse ports -> revalidate authentication -> refresh target if stale
-> validate local ports -> start each session with bounded concurrency
-> wait for each listener -> publish independent results
```

Use bounded concurrency of 3 by default.

---

## 19. Concurrency and Logging

- All AWS work is asynchronous; never block the UI thread with `.Result`/`.Wait()`.
- Cancel obsolete discovery on folder/profile change.
- Serialize Connect requests.
- Registry is keyed by local port; one active process per mapping.
- Drain stdout/stderr asynchronously.
- Keep a bounded sanitized ring buffer per session.
- Log versions, state transitions, target counts, port mappings, exit codes, and categorized errors.
- Never log full commands with unreviewed values or full environments.

---

## 20. Testing

Unit coverage must include folder/environment construction, port grammar/boundaries, duplicate conflicts, SSO classification without secret loading, Region resolution, AWS arguments, EC2 JSON, SSM correlation, target cardinality, parameter JSON, error mapping, state transitions, and safe settings.

Argument-safety tests must prove no shell is used, every argument is separate, AWS file paths are environment entries, empty input emits no port, and `6106` exists only in examples/tests—not production defaults.

Fake-CLI integration tests must simulate CLI/plugin missing, profile discovery from either/both files, valid/expired SSO, SSO cancellation, valid/invalid credentials, missing Region, access denied, zero/one/multiple bastions, SSM states, port conflicts, readiness, partial success, unexpected exit, Stop, and close cleanup.

CI must not require real AWS credentials. A real-AWS smoke test must be opt-in and use non-production resources.

---

## 21. Acceptance Criteria

1. The user selects an AWS folder and discovered profile, enters ports, and connects.
2. The user never enters AWS secrets in the app.
3. Valid credentials profiles proceed without SSO.
4. Valid cached SSO proceeds without redundant login.
5. Expired SSO requests approval, runs login, and revalidates STS.
6. Folder/profile changes invalidate stale state.
7. Region and exactly one running, SSM-online bastion resolve automatically.
8. Multiple targets require selection; zero targets fail clearly.
9. No target ID or port is hardcoded or resolved through shell substitution.
10. `6106` maps to `6106:6106` without any Garnet knowledge.
11. Multiple ports and optional `remote:local` mappings work.
12. Each mapping owns an independent `AWS-StartPortForwardingSession`.
13. Local conflicts and partial success are represented accurately.
14. Stop one/all and app-close cleanup affect only owned processes.
15. Secrets never appear in UI, logs, settings, or snapshots.
16. Target policy is externally configurable.
17. Tests pass and the UI remains responsive.

---

## 22. Definition of Done

- Acceptance criteria pass.
- Manual smoke tests confirm SSO, credentials-file auth, and a generic `6106` mapping when a service listens on the managed node.
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
- normal UI contains only AWS folder, profile, ports, and Connect;
- never accept AWS secrets in the app;
- use AWS CLI v2 as source of truth;
- support SSO approval and configured credential profiles;
- set AWS_CONFIG_FILE/AWS_SHARED_CREDENTIALS_FILE only on child processes;
- resolve target through JSON without shell substitution;
- use only AWS-StartPortForwardingSession in MVP;
- no Garnet/service-specific model;
- no hardcoded profile, instance ID, Region, or port;
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
