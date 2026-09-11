# AWS SSM Port Forwarding Launcher

Simple Windows UI for AWS Systems Manager port forwarding (.NET 10).

Normal flow: **AWS folder → profile → ports → Connect**.

## Prerequisites

- Windows 10/11 x64
- [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)
- [Session Manager plugin](https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html)
- An AWS folder with `config` and/or `credentials` (default suggestion: `%USERPROFILE%\.aws`)

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

Output EXE: `src\AwsSsmPortForwarder.App\bin\Release\net10.0-windows\win-x64\publish\AwsSsmPortForwarder.exe`

`appsettings.json` (beside the EXE) controls the bastion tag policy (`Name=bastion-host` by default) without recompilation.

## First run

1. Confirm or browse to the AWS folder containing `config` / `credentials`.
2. Select a profile.
3. Sign in only if SSO approval is required.
4. Enter ports (`6106` or `6379:16379`) and click **Connect**.

The app never asks for AWS access keys. Credentials stay in your AWS files; the app only passes the folder paths to child `aws.exe` processes via `AWS_CONFIG_FILE` / `AWS_SHARED_CREDENTIALS_FILE`.

## Credential safety

- No secret fields in the UI
- Settings store only folder path, last profile, and last ports text under `%LOCALAPPDATA%\AwsSsmPortForwarder\`
- Logs redact secret-like values
