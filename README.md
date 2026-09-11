# AWS SSM Port Forwarding Launcher

Windows UI for AWS Systems Manager port forwarding (.NET 10).

Normal flow: **AWS folder → profile → connection rows → Start enabled**.

Supports:
- **Managed node** → `AWS-StartPortForwardingSession`
- **Remote host** → `AWS-StartPortForwardingSessionToRemoteHost` (via the resolved bastion)

## Prerequisites

- Windows 10/11 x64
- [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)
- [Session Manager plugin](https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html)
- An AWS folder with `config` and/or `credentials`

## Build / test / publish

```powershell
dotnet build .\AwsSsmPortForwarder.slnx -c Release
dotnet test .\AwsSsmPortForwarder.slnx -c Release

dotnet publish .\src\AwsSsmPortForwarder.App\AwsSsmPortForwarder.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true
```

`appsettings.json` beside the EXE controls the bastion tag policy (`Name=bastion-host` by default).

## First run

1. Confirm or browse to the AWS folder.
2. Select a profile (Sign in only if SSO approval is required).
3. Add connection rows; choose Managed node or Remote host.
4. Enter destination only for Remote host, then ports, and click **Start enabled**.

Connection rows are remembered only when **Remember connections** is checked.

## Credential safety

- No secret fields in the UI
- Settings under `%LOCALAPPDATA%\AwsSsmPortForwarder\` store folder, profile, and optional non-secret connection rows
- Child `aws.exe` processes receive folder paths only via `AWS_CONFIG_FILE` / `AWS_SHARED_CREDENTIALS_FILE`
