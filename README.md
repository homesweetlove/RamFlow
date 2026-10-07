# RamFlow

[![English](https://img.shields.io/badge/Language-English-2F81F7)](README.md)
[![한국어](https://img.shields.io/badge/언어-한국어-2F81F7)](README.ko.md)

<img src="native/RamFlow.UI/Assets/ramflow.png" width="72" alt="RamFlow icon" />

RamFlow is a Windows resource orchestrator designed to protect the work you are actively using. The latest release, **0.3.3, is implemented in C#/.NET 10 + WPF**. The Python/PySide6 0.2.0 version is preserved for compatibility and regression validation.

## Run

Download `RamFlow-0.3.3-windows-x64.zip` from the [latest public release](https://github.com/homesweetlove/RamFlow/releases/latest), extract it, and run `RamFlow-native/RamFlow.exe`. The .NET runtime is bundled, so Python or an SDK installation is not required. Keep the bundled DLLs and `RamFlow.Service.exe` in the same folder.

The default mode is **Dry Run**. On every launch, real optimization is disabled first so you can review the expected behavior before applying changes. To enable real changes, choose **Reopen as administrator** in Settings, disable Dry Run, and confirm. Exiting from the system tray restores the resource settings changed by RamFlow. The X button hides the window and its running taskbar button while the tray and background engine keep running. Launch RamFlow again or double-click the tray icon to reopen the existing window. Choose Exit in the tray menu to shut down completely.

## Features

| Area | Implementation |
|---|---|
| Monitoring | RAM/Commit/cache/Standby/Modified, CPU, disk, page-in/out, battery, and supported GPU/temperature counters |
| Pressure | Memory Pressure EMA, hysteresis, consecutive confirmation, Commit crisis detection, and System Pressure calculated only from available sensors |
| Foreground First | Protects the active app, same-name processes, and task groups; restoration is checked about every 250 ms |
| App Nap | Memory Priority/CPU Below Normal/EcoQoS for apps idle for 30 minutes, with limited Working Set reduction after 2 hours of deep idle under high pressure |
| CPU Sets | Uses official CPU topology and EfficiencyClass data; can select efficient cores for idle apps on hybrid, single-group systems; OFF by default |
| Prediction | Optional learning of time-of-day and app-switch frequency to protect frequently used apps; prediction alone never trims Working Sets |
| Safe restore | Records original values before changes, retries selected API failures, validates process creation time, and replays the restore journal on the next launch after a forced shutdown |
| Model storage | HOT/WARM/COLD analysis, streamed archiving, SHA-256/size/atomic metadata, explicit space reclamation, and model execution after restore |
| Cloud | Connected sync folders such as Google Drive/OneDrive or HTTPS WebDAV. Passwords are kept in memory only |
| Pagefile | Windows automatic/balanced/Low RAM/development/gaming/AI/manual modes, recommendations based on observed Commit, first-state backup, and confirmed apply/restore |
| Benchmarking | Before/After measurements, selected-program ready/completion timing, JSON/CSV export, and CSV analysis of numeric Frame Time/tokens/sec columns |
| UI | Nine Korean-language screens, graphs, process/group/protection reasons, tray controls, pause, and work modes |
| Separate engine | Dedicated .NET process in the user session, per-user-SID Named Pipe, local-only access, and size/connection/admin-token validation |
| Install/update | Per-user install, Start Menu integration, optional startup, verified update install/restart, and restore-before-uninstall behavior |

## Protection and limited intervention

RamFlow protects system, service, security, driver, AI, music, download, render, compile, server, and insufficiently identified applications. Whitelisting is name-based. It does not intervene when memory conditions are healthy, and it also considers CPU load and battery state. New changes are suspended while a full-screen application is active. Working Set reduction is limited to one application at a time, at least two minutes apart globally, and at least 15 minutes apart for the same PID. RamFlow does not use automatic process termination, Realtime Priority, kernel hooking, or Standby List clearing.

Immediately before applying a real change and again during restoration, RamFlow verifies the PID creation time, user ownership, session, system path, and Critical status. Restoration uses the first observed value, and restore records are retained even if a query temporarily fails. Immediate restoration after a power loss or forced shutdown is impossible, but the restore journal is replayed on the next launch. The OS itself reloads Working Set pages when they are accessed again.

## Model archiving and restoration

In Model Storage, choose the source folder and an archive folder or HTTPS WebDAV collection. Analyze first, select files, and then run **Archive · Keep original**. When Dry Run is enabled, transfers are simulated as well.

A matching hash inside a sync folder verifies the local copy only; it does not guarantee that the cloud provider has completed the server-side upload. Removing the original requires a separate confirmation. Verify the provider's upload status and archived file yourself. For WebDAV, RamFlow performs a full GET hash check after a successful PUT response. Cloud storage is never used as RAM or a pagefile.

COLD files are managed using GUID metadata. You can restore them from the list or use **Auto-restore model and run** to define a runtime executable and a JSON array of arguments. The restored model path is appended as the final argument. Operations are rejected for files currently in use, modified originals, damaged archives, conflicts, path escapes, junctions, symbolic links, sparse files, and online-only files. HOT/WARM/COLD classification based on file timestamps may differ from actual usage frequency.

## Installation, service, and removal

Portable use requires no installation. Per-user installation is available through the UI or `Install.ps1` in the distribution folder.

```powershell
./Install.ps1                    # User Programs + Start Menu
./Install.ps1 -DesktopShortcut   # Above + desktop shortcut with dedicated icon
./Install.ps1 -Startup           # Above + startup for the current user
./Install.ps1 -MonitorService    # Administrator: also install the separate system monitor service
./Uninstall.ps1                 # Exit from the tray first
```

The SCM service only performs monitoring because a service running in Session 0 cannot observe the user's foreground application. Real resource adjustments are handled by the separate engine in the user session. The administrative service executable is stored under write-restricted Program Files and its state under write-restricted ProgramData. The service is not installed by default.

From the Update screen, RamFlow checks the public repository, verifies SHA-256, and then offers installation and restart of the new version. Installation requires user confirmation and is initiated from a normally privileged window. If the optional SCM monitor service is installed, updating it requires removal and reinstallation with administrator privileges.

Uninstall removes the installation folder, the Start Menu/startup entries created by that installation, and the optional service. If RamFlow changed the pagefile configuration, restore the original setting with administrator privileges first. Model-recovery metadata under `%LOCALAPPDATA%\RamFlowNative`, user models, and cloud files are preserved. Deleting this metadata can remove the information needed to automatically restore archived models.

## Build and validation

Windows, the .NET 10 SDK, and Python 3 are required. Python is used only for validation scripts.

```powershell
dotnet run --project native/RamFlow.Tests -c Release
dotnet run --project native/RamFlow.Tests -c Release -- --windows --ipc
dotnet run --project native/RamFlow.Storage.Tests -c Release
./scripts/build-native.ps1
```

Validation covers mock regressions, Windows API round trips that modify only self-created child processes, mock Named Pipe authorization and clean shutdown, model archive/restore using test files only, creation of the real WPF window, and install/uninstall flows inside test-only folders. GitHub Actions performs the same Windows build. Automated validation does not modify a real user's pagefile, register SCM services, or upload to external cloud storage.

## Windows-aware design choices and measurement scope

The documented Background I/O Mode can only be applied to the calling process, so RamFlow applies it to its own engine rather than changing another application's I/O priority through undocumented Nt APIs. Instead, it protects active I/O work and adjusts CPU/EcoQoS for idle apps. Restorable CPU Sets are used instead of permanently pinning CPU affinity. RamFlow does not force the foreground app onto P-cores.

If GPU/ACPI temperature counters are unavailable, they are shown as unsupported. GPU values represent observable dedicated usage only; they do not imply total VRAM capacity or guarantee per-model KV cache availability. Page-in is a page count, not a hard-fault event count. Application-ready time and scheduling jitter are not equivalent to real input latency. Game/LLM/IDE results are measured using the runtime or CSV selected by the user. Performance improvements are not guaranteed across environments.

WPF was chosen instead of WinUI 3 to avoid an additional deployment dependency. RamFlow does not use kernel drivers or hooks. See [Architecture](docs/NATIVE_ARCHITECTURE.md) and [Validation](docs/VALIDATION.md) for details on official APIs and validation scope. Instructions for the Python 0.2.0 version are available in the [legacy version documentation](docs/LEGACY_PYTHON.md).
