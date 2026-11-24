# Microsoft-Activation-Scripts-GUI

## Publish a single-file Windows app

From the repository root, run:

```powershell
dotnet publish .\MySystemGui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o .\publish\win-x64
```

The distributable is `publish\win-x64\MySystemGui.exe`. The embedded `MAS_AIO.cmd` is extracted to the current user's Local Application Data folder when needed, so it does not need to be distributed separately. The app requires administrator privileges and displays the Windows UAC prompt when launched.

The published executable is self-contained; the .NET runtime does not need to be installed separately on the target PC.