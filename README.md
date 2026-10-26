<p align="center">
  <img src="Branding/README-banner.png" alt="CratePilot" width="100%">
</p>

# CratePilot

A lightweight, portable Windows utility that automates opening **Low, Mid, and High Grade** crates in Roblox using screen capture and OCR.

- **Non-intrusive:** Does not inject code, hook processes, or read game memory. Interacts strictly through visible pixels and simulated mouse clicks.
- **Standalone:** Zero external dependencies. No Python, no .NET runtimes to install, no Visual C++ redistributable, and no separate Tesseract installation.

---

## Features

- **Automatic Window Detection:** Automatically finds and attaches to the running Roblox player window.
- **OCR State Machine:** Reads crate UI states (`OPEN`, `NEXT`, `CLAIM`, stock count) in real time via Windows OCR with embedded fallback.
- **Single-Key Controls:** Press **F9** (configurable in UI) or click **Start** to run, pause, or resume. Closing the window immediately stops all automation.
- **Safety Watchdogs:** Suspends clicks immediately on focus loss, window minimization, or human mouse input.
- **In-Memory Logging:** Keeps an internal 500-line event log with a one-click Copy button. Never writes log files to disk.

---

## Requirements

- Windows 10 / 11 (64-bit)
- .NET Framework 4.8 (built into Windows)

---

## How to Use

1. Launch Roblox and enter the game.
2. Open the crate menu (**Low**, **Mid**, or **High Grade**) so it is clearly visible.
3. Open **CratePilot.exe** and click **Start** (or press **F9**).
4. Press **F9** to pause or resume at any time. Close the window to stop.

---

## Build from Source

Requirements: .NET SDK 9 and .NET Framework 4.8 Developer Pack.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The output executable will be generated at `dist\CratePilot.exe`.

---

## Disclaimer

Roblox is a registered trademark of Roblox Corporation. CratePilot is an independent project and is not affiliated with, endorsed by, or sponsored by Roblox Corporation.
