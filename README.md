# AWS Session Launcher

Windows desktop UI for AWS SSO login and Systems Manager port-forwarding tunnels (.NET 10 WinForms).

## Prerequisites

- Windows 10/11 x64
- [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html)
- [Session Manager plugin](https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html)
- At least one AWS profile (`aws configure` / `aws configure sso`)

## Build

```powershell
dotnet build .\AwsSessionLauncher.slnx -c Release
```

## Test

```powershell
dotnet test .\AwsSessionLauncher.slnx -c Release
```

## Publish (self-contained single EXE)

```powershell
dotnet publish .\src\AwsSessionLauncher.WinForms\AwsSessionLauncher.WinForms.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true
```

Output: `src\AwsSessionLauncher.WinForms\bin\Release\net10.0-windows\win-x64\publish\AwsSessionLauncher.exe`

## Manual validation checklist

1. Launch the EXE — UI opens and stays responsive.
2. Standard `~/.aws` config is detected without a folder picker.
3. Profiles list; secrets never appear in the UI/logs.
4. SSO profile: Sign in → browser → Validate shows account/ARN.
5. Non-SSO profile: Validate works; Sign in stays disabled/inappropriate.
6. Refresh targets by Name tag; multiple matches require selection; SSM status visible.
7. Start managed-node and remote-host forwards; multiple rules concurrently.
8. Local port conflict blocked before start.
9. Stop one / Stop All; close app stops tunnels.
10. Settings persist under `%LOCALAPPDATA%\AwsSessionLauncher\` (no credentials stored).

## Known limitations

- AWS CLI + Session Manager plugin remain workstation prerequisites.
- No custom Session Manager WebSocket implementation.
- No SSH key management, DB clients, or IAM policy provisioning.
