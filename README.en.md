# TaskMonitor

[한국어](README.md) | **English**

TaskMonitor is a Windows task manager built to answer one question: *what is using my CPU, and why?* It shows the CPU time that the built-in Task Manager hides (interrupts, DPCs, kernel time not charged to any process), records every process start and exit, warns about overheating and runaway processes, and writes a cause report for the last hour. Everything stays on your PC.

![Processes](docs/images/processes-en.png)

## Download

Get `TaskMonitor.exe` from [Releases](https://github.com/minia1234/TaskManager/releases/latest). It is a single exe. It needs no installer and no .NET install.

## How it differs from Task Manager

- **Hidden CPU time.** The `[System] Interrupts / DPCs` and `[System] Unattributed kernel time` rows show CPU use that no process row accounts for.
- **A record you can look back on.** Spikes are recorded even when the window is closed. Process starts and exits, alerts, and game sessions build up on the Events page and in record files. When TaskMonitor sits in the tray, a new alert shows a Windows notification.
- **Cause report.** Compares the last hour with the hour before: resource averages and peaks, alerts, the programs started most often, and cumulative use by app, in one document. You can also pick two periods to compare.
- **Network and FPS per process.** TCP/UDP throughput and frame rate (FPS) per process, without administrator rights.
- **Hardware health.** NVIDIA GPU temperature, hot spot, memory junction, power and clock limit reasons, and disk health (temperature, life left, SMART). CPU temperature too, while MSI Afterburner or HWiNFO runs.
- **Safe process actions.** End task, priority and efficiency mode check the target's creation time first, so a reused PID never ends the wrong program.
- **Korean and English.** Switch under Options > Language. The first run follows the Windows display language.

## Screens

**Performance.** Graphs and details for CPU (per core), memory, disks, network adapters and GPUs.

![Performance](docs/images/performance-en.png)

**Events.** Process starts and exits, alerts and game sessions. "Alerts only" filters to alerts. Records from before today show their date.

![Events](docs/images/events-en.png)

The other pages:

- **Processes.** Programs with the same name group as `name(count)` and expand. The large numbers in the header are total CPU, memory, and the busiest disk and GPU. The View menu switches to a parent-child tree. Choose columns adds connection count, network and FPS. Right-click for end task, end process tree, efficiency mode, priority, affinity, dump file, the analysis window and more.
- **App history.** Cumulative CPU time and I/O transferred (file, network and pipe) per program.
- **Startup apps, Users, Details, Services.** The same roles as in Task Manager. Stopping or restarting a service asks first.
- **Records menu.** Cause report, compare records, export to CSV, open the records folder.

Alert limits are GPU 85°C, GPU memory junction 100°C, disk 70°C, CPU 95°C, memory 90%, total CPU 90% for 20 s, and one process over 50% of total CPU for 20 s.

## Install and run

1. Put `TaskMonitor.exe` in any folder.
2. Run it. If Windows shows "Windows protected your PC", choose "More info" and then "Run anyway" (the build is not code-signed yet).
3. For background recording, turn on **Options > Start with Windows** and **Options > Close button hides to tray**. TaskMonitor then starts as a tray icon with no window.

Only one copy runs at a time. Starting it again brings the open window to the front.

## Requirements

Windows 11 x64 (tested) or Windows 10 x64 (supported, not yet tested). No administrator rights needed. Some values have conditions.

| Feature | Condition |
|---|---|
| Network and FPS per process | The account is in the `Performance Log Users` group, or TaskMonitor runs as administrator. Otherwise the status bar says so and the values show "-". |
| SMART for SATA and USB disks | Administrator rights (File > Run as administrator). NVMe works without them. |
| CPU temperature | MSI Afterburner running with "CPU temperature" enabled in its monitoring list, or HWiNFO running with "Shared Memory Support" on. Windows gives no CPU temperature without a driver, so TaskMonitor reads the value these tools already read. |
| Motherboard fan speed | HWiNFO running with "Shared Memory Support" on. |
| GPU details | NVIDIA driver (NVML). AMD GPUs are beta. |

## Privacy and records

TaskMonitor sends nothing over the network. All records live in `%LOCALAPPDATA%\TaskMonitor`.

| File | Contents |
|---|---|
| `activity-log.jsonl` | Process starts and exits (name, PID), alerts, game sessions. Rolls over to `.1` every 10 MB. |
| `resource-log.jsonl` | CPU, memory and GPU utilization, GPU temperature and VRAM every 5 s. Rolls over every 20 MB. |
| `app-history.json` | Cumulative CPU time and I/O per executable path. |
| `spike-log.csv` | The top 5 processes whenever total CPU passes the threshold (70% by default). |
| `settings.json` | Settings. |
| `crash.log`, `freeze.log` | Unexpected errors and hangs. Please attach these when reporting a problem. |

**Records > Open records folder** opens the folder. To remove the app, delete `TaskMonitor.exe` and this folder. If you turned on Start with Windows, turn it off in Options first.

## Known limits

- A process that starts and exits between two samples (1 s by default) is not recorded.
- A game session is inferred as "a full-screen window using 30% or more GPU 3D for over 10 s". TaskMonitor does not check that it is really a game.
- FPS is measured only for programs that present through DXGI.

## Development

Building, making the release exe and the self-test are covered in [docs/DEVELOPING.md](docs/DEVELOPING.md) (Korean). Open-source components are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
