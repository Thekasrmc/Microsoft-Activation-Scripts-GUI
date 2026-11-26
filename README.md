# Microsoft Activation Scripts GUI

An independent Windows desktop interface for selected workflows from [Microsoft Activation Scripts (MAS)](https://github.com/massgravel/Microsoft-Activation-Scripts). The app brings common actions, system checks, troubleshooting, and an official Microsoft 365 installer download together in one place.

<p align="center">
  <img src="logo.png" alt="Microsoft Activation Scripts GUI logo" width="112">
</p>

<p align="center">
  <a href="https://github.com/Thekasrmc/Microsoft-Activation-Scripts-GUI/releases/latest">Download the latest version</a>
  ·
  <a href="https://github.com/Thekasrmc/Microsoft-Activation-Scripts-GUI/issues">Report a problem</a>
</p>

<p align="center">
  <img src="docs/screenshots/main-window.png" alt="Microsoft Activation Scripts GUI main window" width="900">
</p>

## Download and start

1. Download **[MAS-GUI.exe](https://github.com/Thekasrmc/Microsoft-Activation-Scripts-GUI/releases/latest/download/MAS-GUI.exe)** from the latest GitHub Release.
2. Open the downloaded file. Windows may request administrator permission because some system and MAS workflows require it.
3. Choose a tool and follow its confirmation prompts. Progress and messages appear in the Activity Log.

The release is a self-contained, single-file Windows x64 app; you do not need to install .NET or download `MAS_AIO.cmd` separately.

## What it includes

- **MAS workflows:** Open supported HWID, Ohook, TSforge, and Online KMS actions.
- **Windows and Office tools:** Check activation status and open supported edition and licensing menus.
- **Microsoft 365 setup:** Download the official Microsoft installer to a temporary folder (or reuse a cached copy), show download progress in the Activity Log, and open it for an interactive setup. The app does not install Office silently.
- **Troubleshooting and extras:** Access supported repair tasks and utility options.
- **Activity Log:** Review session output or open the full log in a separate window.

Some MAS workflows open the original MAS console so its own checks, choices, and confirmation steps are preserved. Follow the prompts shown there as well.

## Temporary files and limitations

- The embedded MAS script is written to a uniquely named temporary file when needed and removed after its workflow ends. If a workflow cannot be stopped safely or Windows prevents cleanup, the file may remain.
- The Microsoft installer is cached at `%TEMP%\MAS GUI\OfficeSetup.exe` and reused if it passes basic file checks. The app does not automatically delete this cached installer.
- The app checks Office Click-to-Run registry information before and after setup and records the installer's exit code. This does not confirm that Word or Excel starts correctly or that Office is activated.
- MAS cache files created by older versions are not automatically deleted by this update.
- An Internet connection is required to download the Microsoft installer and for online MAS workflows.

## Important

- This is an independent graphical frontend, not the official MAS project. The MAS script comes from the upstream project linked above; see its documentation and license for details.
- Activation and repair workflows may change Windows or Office licensing and system state. Read prompts and review the output before proceeding.
- **Fix Licensing** may clear licensing records and require Windows or Office to be activated again.
- Use only software and product keys you are licensed to use. This app does not provide a license or product key.

## Technology and license

Built with **WPF**, **.NET 10**, and [WPF-UI](https://github.com/lepoco/wpfui). See [LICENSE](LICENSE) for this repository's license. The upstream MAS project has its own source, documentation, and licensing.
