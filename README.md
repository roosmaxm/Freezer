# Freezer — PC Freeze Diagnostic Dashboard

A self-contained, single-file WinForms application (.NET 8) for diagnosing
real-time PC freezes on Windows 11. No installation required.

---

## Quick Start

### Prerequisites
- Windows 10/11 (64-bit)
- .NET 8 SDK (for building only; the published `.exe` is fully self-contained)

### Build & Publish as a single `.exe`

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The output `.exe` will be in:
```
bin\Release\net8.0-windows\win-x64\publish\Freezer.exe
```

### Run during development
```bash
dotnet run
```

> ⚠ **The application requests Administrator privileges** via its embedded manifest.
> Run as Administrator to ensure all performance counters and event log access work correctly.

---

## How to Use

1. Launch `Freezer.exe` (right-click → Run as Administrator if prompted)
2. Click **▶ Start Monitoring**
3. Use your PC normally and wait for a freeze to occur
4. When a freeze is detected, the window border flashes red and the title changes to `🧊 FREEZE DETECTED`
5. The freeze event appears in the **Freeze Event Log** with a cause analysis
6. Double-click any row for full details (metrics snapshot, top processes, USB devices)
7. Click **💾 Export Report** to save all events to a `.txt` file

---

## UI Overview

### Top Section — Live Graphs
Six scrolling line graphs updated every 500ms:

| Graph | Color | What it shows |
|-------|-------|---------------|
| CPU % | Green | Overall CPU utilization across all cores |
| RAM % | Blue | Committed memory as percentage of total |
| Disk Read (ms) | Yellow | Average NVMe read latency |
| DPC % | Red | Deferred Procedure Call CPU time (driver interrupt processing) |
| GPU % | Purple | GPU 3D engine utilization |
| Interrupt % | Orange | Hardware interrupt CPU time |

### Middle Section — Freeze Event Log
Columns: `#`, `Timestamp`, `Duration`, `Most Likely Cause`, `Details`
- Double-click any row to open a full detail popup

### Bottom — Toolbar
- **▶ Start Monitoring** / **⏹ Stop Monitoring**: Toggle monitoring
- **💾 Export Report**: Save all events to a `.txt` file
- **Status bar**: Current state and last detected freeze

---

## What Each Metric Means

### CPU % (`\Processor(_Total)\% Processor Time`)
The percentage of elapsed time that all processors are busy executing non-idle threads.
**During a freeze:** drops to near 0%, which is the key freeze indicator.

### RAM % (`\Memory\% Committed Bytes In Use`)
How much of the virtual address space is committed. High values (>90%) can cause
paging activity that stalls the system.

### Disk Read/Write Latency (ms) (`\PhysicalDisk(_Total)\Avg. Disk sec/Read|Write`)
Average time for disk I/O operations in milliseconds.
**Normal NVMe:** <1ms. **Problem threshold:** >50ms indicates stalling.

### DPC % (`\Processor(_Total)\% DPC Time`)
Time spent processing Deferred Procedure Calls — low-level driver callbacks.
**Normal:** <5%. **Problem:** >15% often means a badly written driver is holding the CPU.
Common culprits: audio drivers (Realtek, NVIDIA HD Audio), USB host controller drivers.

### GPU % (WMI `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine`)
GPU 3D engine utilization. **During a freeze:** drops to 0% simultaneously with CPU.

### Interrupt % (`\Processor(_Total)\% Interrupt Time`)
Time spent servicing hardware interrupts.
**Normal:** <5%. **Problem:** >20% can indicate an interrupt storm (often USB devices).

---

## Freeze Detection Logic

A freeze is detected when **all three** conditions are true for **≥ 2 consecutive samples (400ms)**:
- CPU usage drops below **5%**
- Disk read+write latency drops below **0.5ms** (near zero activity)
- GPU usage drops below **5%** (or GPU data is unavailable)

### Cause Analysis Priority

1. **DPC Latency spike** — DPC % > 15% in the 3s before freeze → driver issue
2. **Interrupt storm** — Interrupt % > 20% → check USB devices (Xbox controller, wireless peripherals)
3. **NVMe disk stall** — Disk latency > 50ms before freeze → storage driver or thermal issue
4. **GPU TDR event** — EventID 4101 or nvlddmkm Event 13 in System event log → update GPU drivers
5. **Background process burst** — MsMpEng, SearchIndexer, WmiPrvSE, TiWorker, NvContainerLocalSystem spiking
6. **Unknown** — No clear pattern found; use Windows Performance Recorder for deeper analysis

---

## Common Causes for Your Symptoms

**Symptoms: 1–4s freezes, RTX 3070, NVMe, USB devices (Xbox controller, wireless mouse, keyboard, headphones)**

### Most Likely: USB Interrupt Storm
The Xbox controller and wireless headphones are known to generate frequent USB interrupts.
A polling rate mismatch or driver bug can cause an interrupt storm that briefly freezes the OS.
**Fix:** Try disconnecting USB devices one by one while monitoring interrupt %.

### Also Likely: NVIDIA Driver / DPC
The RTX 3070's NVIDIA drivers (nvlddmkm.sys) are a common source of high DPC latency.
**Fix:** Update to the latest Studio or Game Ready driver. Try DDU (Display Driver Uninstaller)
for a clean reinstall.

### NVMe Thermal Throttling
High-performance NVMe drives throttle under sustained load if they lack a heatsink.
**Fix:** Check NVMe temperature with CrystalDiskInfo. Add a heatsink if temps exceed 70°C.

### Windows Background Services
Windows Defender (MsMpEng), Windows Update (TiWorker), and Search Indexer can cause
brief CPU spikes. These should show in the "Top Processes at Freeze Time" in the report.

---

## How to Interpret the Freeze Report

The exported `.txt` report contains:
- **Event header**: timestamp, duration, cause, TDR flag
- **Connected USB devices**: all USB devices present at freeze time
- **Top processes**: by memory usage — look for suspect processes
- **Pre-freeze metric snapshot**: the last 15 samples (3 seconds) before the freeze

Look for patterns across multiple freeze events:
- Same cause every time → targeted fix is possible
- Random causes → likely hardware instability (RAM, storage)
- Always after GPU usage → driver or power-delivery issue

---

## Project Structure

```
Freezer/
├── Freezer.sln             — Visual Studio solution
├── Freezer.csproj          — .NET 8 WinForms project (single-file publish configured)
├── app.manifest            — requireAdministrator manifest
├── Program.cs              — Entry point
├── MainForm.cs             — Main dashboard logic
├── MainForm.Designer.cs    — WinForms layout
├── FreezeDetailForm.cs     — Freeze event detail popup
├── FreezeDetector.cs       — Freeze detection and cause analysis
├── SystemMonitor.cs        — Performance counter polling + rolling buffers
├── GpuMonitor.cs           — GPU usage via WMI performance counters
├── UsbMonitor.cs           — USB device enumeration via WMI
├── DpcLatencyMonitor.cs    — DPC latency performance counters
├── FreezeEvent.cs          — Freeze event data model
├── FreezeReport.cs         — Text report export
└── README.md               — This file
```

---

## Troubleshooting

**"GPU usage shows N/A"**
GPU monitoring uses WMI `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine`.
If unavailable, install HWiNFO64 or GPU-Z which expose additional counters, or use
Task Manager's GPU performance view alongside this tool.

**"Counter unavailable" warnings in status bar**
Some performance counters may not be registered on your system. The app continues
monitoring with available counters. Run as Administrator to maximize counter access.

**App doesn't detect my freezes**
The detection threshold requires CPU <5% AND disk near-zero AND GPU <5% simultaneously
for 400ms+. If your freezes manifest differently (e.g., audio stutter but CPU stays high),
use Windows Performance Recorder (WPR) for ETW-level analysis:
```
wpr -start GeneralProfile -start CPU
# ... trigger freeze ...
wpr -stop FreezeTrace.etl
```
Then analyze with Windows Performance Analyzer (WPA).
