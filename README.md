<p align="center">
  <img src="Branding/README-banner.png" alt="CratePilot" width="100%">
</p>

# CratePilot

Portable Windows app that opens **Low, Mid and High Grade** crates in Roblox via screen capture and OCR. It reads visible state and moves the mouse; it does not modify the game, read game memory, or buy crates.

## Behavior
- Detects the Roblox player by process, PID, start time and window tree; no manual selection.
- One **Start / Pause / Resume** button in the footer plus a global hotkey (**F9** by default, rebindable in the UI). Closing the window stops the session.
- State is recognized from visible content, not a fixed screen sequence. Recovering an open collection is not counted as a new opening; stock must be confirmed.
- In-memory log, 500 lines max, with a **Copy** button. Normal runs write nothing to disk.
- Classic Windows UI: Tahoma/Consolas, bevel borders, pixel icons. Layout checked at 640×480, 760×540, 880×560, 920×580 and 1040×760.

## Requirements
- Windows 10 or 11, preferably 64-bit, with **.NET Framework 4.8**.
- No other installs: no .NET 8/9, Python, Tesseract, Visual C++ Redistributable or NuGet packages.
- Native binaries are Intel/AMD x64/x86 (x86 verified under WOW64, not on physical 32-bit Windows). No ARM64 payload.
- Prefers native Windows OCR when present; the embedded Tesseract fallback (English model) covers `OPEN`, `NEXT`, `CLAIM` and `Remaining`. The libraries, C++ runtime and model are embedded in the EXE.
- Whether it works on a given PC also depends on local policies, antivirus, OCR cache extraction rights and window visibility. No guarantee for every machine.
- Runs as a normal user, no administrator.

## Usage
1. Open Roblox and enter the target experience. Wait for **Roblox connected**.
2. In-game, open the available Low, Mid or High Grade crate menu, with the content visible and unobstructed.
3. Check the detected state and start (footer button or hotkey). The engine resynchronizes by recognized content and follows confirmed stock down to zero. Unknown or ambiguous states never authorize a click.
4. The optional manual quantity limits how many existing crates are opened; it does not buy crates or open other menus.
5. The footer button and the hotkey start when idle and pause/resume during a run. Close the window to stop early; it stops the session before exiting.
6. Activity is shown in the UI. **Copy** exports the session context with local paths and recognized sensitive values redacted. Check before sharing.

Keep Roblox visible, focused and unminimized while running. Losing focus suspends clicks; returning to the game requires a fresh read before continuing. Do not resize, move monitors or change scaling during a sequence; re-check the selection after doing so. Speed is limited by animations, latency and server response; shorter intervals do not guarantee instant crates.

### Safety guards
- Before `OPEN`, `NEXT` or `CLAIM`, OCR older than 2 s is discarded and fresh small captures of the supporting texts must match. Visual proof must stay current (350 ms) until input; a changed screen forces a re-read, no click.
- Title, stock, button and prompt samples must belong to the selected window, not an overlay. The human left mouse button is checked before any movement and again before each click; drags are never taken over by the app.
- Manual pause, focus loss, minimization, confirmed occlusion and a pressed human mouse suspend the clock without forcing focus. Unknown states and changing images do not disable the watchdogs.
- Default stop after 25 s without real visual progress. Repeated clicks, frozen SPAM and background animation do not renew open-phase progress. A maximum limit per crate also applies. PCs whose OCR misses the freshness limit may need re-reads until timeout; safety outranks speed.

Use only where the experience and platform rules allow. Stop when recognition looks wrong. This project does not promise protection against game restrictions or penalties.

## Build
Prerequisites: .NET SDK 9, **.NET Framework 4.8 Developer Pack**, Windows SDK 10 WinRT metadata (or `Windows\System32\WinMetadata`) and the OCR payload already present in `PortableOcr/Native/x64/` and `PortableOcr/Data/eng.traineddata`. Local references are mandatory; the build downloads nothing and does not embed the Framework runtime. Target is x64; it becomes AnyCPU only when both x64 and x86 DLL payloads are present.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The production build has no `Tests/` dependency, does not start the app and generates no test reports/images. Required output in `dist/`: **CratePilot.exe**, its **.sha256** and the **seven notices/licenses** in `licenses/`, which must accompany redistribution. Libraries, model and licenses are also embedded in the EXE. The build verifies input stability before/after compilation and publishes the checksum; compile output stays on the console. Recompiling creates normal intermediates in `bin/obj`, removed from this delivery.

## Delivery verification
- Before cleanup: **132 passed, zero failed, five explicit skips**; build warning/error free; UI smoke exit 0. Skips were Windows OCR without languages (availability plus three images) and real focus/minimization integration.
- After cleanup: production EXE without test types/resources, verified externally as a single file in x64 and x86/WOW64, 16 intact payload resources, seven legal notices and portable OCR on a synthetic image.
- Diagnostic UI without engine, hotkeys, discovery or runtime timers; five layouts with actions ≥44 DIP / footer ≥16 and Tahoma/Consolas measured on the glyphs actually rendered. Clipboard untouched in tests.
- Tests, autotest/smoke hooks, logs/reports, previews and session export were removed from the project at the user's request. The final EXE does not contain the test suite; no claim is made that it ran inside it after removal.

Final executable: **18,451,456 bytes**, AnyCPU (Intel/AMD x64/x86 payload).

```text
SHA256 9210e66a185e50fb81a07bff77b72507359ecedb7d0629818c6db9fabbab5e86
```

## Signature and SmartScreen
The executable is **NotSigned**. SmartScreen may warn or block; a hash is not a signature nor a reputation guarantee. This project does not disable Windows protections.

## License and trademarks
- Source and original interface: **MIT** (`LICENSE`). App art (icon and glyphs) is original, documented in `Branding/LICENSE.txt` and `Assets/THIRD-PARTY.md`; the look is inspired by classic interfaces, with no third-party logos or images.
- OCR/redistributable components keep their own licenses in `PortableOcr/Licenses/`; the same seven notices ship in `dist/licenses/`.
- Roblox is a trademark of Roblox Corporation. CratePilot is an independent utility; no affiliation, sponsorship or endorsement. Use it per the experience and platform rules.

## Publishing on GitHub
- Public repository: <https://github.com/lobnho/cratepilot> — the executable, `.sha256` and licenses are published as artifacts of the matching release (current: [v1.0.0](https://github.com/lobnho/cratepilot/releases/tag/v1.0.0)).
- The repository versions source code only; `bin/`, `obj/` and `dist/` stay out of Git (`.gitignore`).
- The offline build (`build.ps1`) compares input digests before/after compilation and publishes the checksum in `dist/CratePilot.exe.sha256`.

## Verification limits
Build, synthetic OCR and UI smoke checks are local verifications. In-game behavior, menus, counting, server timing and per-experience compatibility require supervised testing by the user. Roblox has not been tested automatically.

The fallback covers missing Windows OCR at startup. If a successfully created Windows engine fails later, the session stops safely; there is no dynamic backend switch during a read. Unicode profiles outside the local ANSI code page may be refused by the native runtime when no short representable path exists; the manifest does not change the code page globally. See `PortableOcr/README.md` for limits and component provenance.
