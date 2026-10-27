# BarControl

A lightweight Windows system tray utility designed for seamless HDMI soundbar and AV receiver integration. BarControl solves the everyday headaches of connecting an HDMI soundbar to a PC: guarding phantom displays, preventing audio sleep/clipping, bridging Dolby Atmos volume controls, and providing **HDMI-CEC hardware power & volume management** via the **Pulse-Eight USB-CEC Adapter**.

![BarControl Screenshot](Assets/barcontrol-ui.png)

---

## 1. What It Does and Why

### The Problems
1. **Phantom Displays & Window Trapping**:
   When connecting an HDMI soundbar (such as a Panasonic SoundSlayer) or AV receiver to a Windows PC, Windows treats the HDMI connection as an active display monitor. Disabling it in Display Settings cuts HDMI audio completely; leaving it enabled causes application windows, dialogs, and mouse cursors to get lost on a non-existent screen.
2. **Audio Sleep / Startup Clipping**:
   Digital HDMI soundbars frequently enter a power-saving sleep state when silent, cutting off the first 1–2 seconds of notification sounds or audio playback while the HDMI clock re-syncs.
3. **Dolby Atmos / Bitstream Volume Lockout**:
   When streaming Dolby Atmos, DTS, or exclusive bitstream audio (e.g., Netflix, games, or media players), Windows volume controls are bypassed and locked at 100%, leaving keyboard volume keys/wheels non-functional.
4. **Power State Desync**:
   Soundbars stay powered on indefinitely when the PC sleeps or shuts down, or fail to wake automatically when the PC resumes.

### The BarControl Solution
- **Phantom Display Guard**: Keeps your HDMI soundbar enabled for audio while actively guarding the phantom display—instantly bouncing any window dragged, launched, or moved (e.g., via `Win + Shift + Arrow`) back to your primary monitor with its original size and coordinates preserved.
- **Continuous Audio Keep-Alive**: Streams an imperceptible sub-bit WASAPI signal targeted strictly to your soundbar endpoint so it never falls into idle sleep or clips the beginning of sounds.
- **HDMI-CEC Power & Volume Management**:
  - **Closed-Loop Volume Slider**: Directly controls and monitors your soundbar's hardware amplifier volume (`0–100%`) over HDMI-CEC with dual target and actual indicators.
  - **Auto-Power Sync (Sleep / Wake / Shutdown)**: Automatically puts the soundbar into discrete CEC Standby when the PC sleeps or shuts down, and wakes it when Windows resumes from sleep.
  - **Turn Soundbar On with PC Boot**: Separate toggle to choose whether starting Windows / BarControl automatically wakes the soundbar.
  - **Manual Turn On / Turn Off**: One-click discrete power control directly from the UI.
- **Dolby Atmos / Bitstream Volume Proxy**:
  - Automatically detects when an exclusive bitstream (Dolby Atmos / DTS) takes over the audio endpoint, snapshots your pre-stream Windows & Soundbar volume levels, transparently relays keyboard volume/mute keys directly to the soundbar over HDMI-CEC, and restores your prior volume levels when playback stops.

---

## 2. How to Use It

### ⚡ Getting Started

#### Option A: One-Line PowerShell Installer (Recommended)
Open PowerShell (no administrator rights required) and paste:
```powershell
irm https://raw.githubusercontent.com/goldcd/BarControl/main/install.ps1 | iex
```
*What this does: downloads the latest `BarControl.exe` from GitHub Releases, installs it cleanly to `%LocalAppData%\Programs\BarControl`, creates a Start Menu shortcut, registers it in **Windows Settings > Apps > Installed Apps** (with full uninstaller support), and launches BarControl.*

#### Option B: Download Standalone Executable
1. Go to **[Releases](https://github.com/goldcd/BarControl/releases/latest)**.
2. Download **`BarControl.exe`**.
3. Run it from anywhere — no installation or external runtimes required.

#### Option C: Local One-Click Installer
1. Download or clone this repository.
2. Double-click **`install.cmd`** (no administrator rights needed).
   - Installs cleanly to `%LocalAppData%\Programs\BarControl`
   - Creates a Start Menu shortcut
   - Adds BarControl to **Windows Settings > Apps > Installed Apps**

#### Option D: Build from Source
```cmd
git clone https://github.com/goldcd/BarControl.git
cd BarControl
publish.cmd
```
*`publish.cmd` builds the standalone, self-contained single-file executable to `dist\BarControl.exe` (automatically stamped with the latest Git tag version).*

---

### How to Use BarControl
1. **Select Soundbar / HDMI Audio Device**:
   - Open BarControl (it opens on start, or left-click its soundbar icon in the system tray).
   - Select your HDMI soundbar from the dropdown at the top (e.g., `SOUNDSLAYER`).
   - **Phantom Display Guard**: Prevents windows and dialogs from getting trapped on the phantom soundbar display (your primary monitor is automatically protected from exclusion).
   - **Continuous Audio Keep-Alive**: Streams an imperceptible signal to prevent soundbar idle sleep and audio startup clipping.
2. **HDMI-CEC Power Management**:
   - Automatically discovers your Pulse-Eight USB-CEC adapter and connected HDMI soundbar.
   - Drag the **volume slider** or click the mute/unmute buttons to adjust hardware soundbar volume in real time.
   - **Auto-Power Sync (Sleep / Wake / Shutdown)**: Puts the soundbar into standby when your PC sleeps or shuts down, and wakes it when your PC resumes from sleep.
   - **Turn Soundbar On with PC Boot**: Independently choose whether the soundbar should automatically power on when your PC boots up.
   - **Turn On / Turn Off**: Manually toggle your soundbar's power state at any time.
3. **Dolby Atmos / Bitstream Volume Proxy**:
   - Enable **Seamless Keyboard Volume Bridge & Auto-Restore**.
   - While in normal audio playback, the status badge reads `STANDBY (PCM)`.
   - As soon as Dolby Atmos / DTS bitstream playback begins, the card switches to `⚡ ATMOS / BITSTREAM ACTIVE`, forwarding keyboard volume & mute keys to your soundbar over HDMI-CEC and restoring your prior volume levels once the stream ends.
4. **In-App Auto-Updater**:
   - Automatically checks GitHub Releases for newer versions and provides a one-click **Update** button in the footer bar.
5. **Tray Controls (by the Clock)**:
   - **Left-Click**: Instantly opens / restores the BarControl window.
   - **Right-Click**: Opens the quick menu (*Open BarControl*, *Guard: Active/Paused toggle*, and *Exit*).

To uninstall completely, run `uninstall.cmd` or uninstall directly from **Windows Settings > Apps > Installed Apps**.

---

## 3. Technical Details

### Architecture & Stack
- **Target Runtime**: .NET 8 LTS (`net8.0-windows`), Win-x64 standalone single-file binary.
- **UI Framework**: WPF (Windows Presentation Foundation) styled to match Windows 11 Fluent dark mode.
- **Resource Footprint**: ~25 MB RAM, 0% CPU at idle.

### HDMI-CEC Engine & Closed-Loop Volume Controller
- **Strict Unicast Addressing**: All CEC opcodes (`<Standby> 0x36`, `<Give Device Power Status> 0x8F`, `<Give Audio Status> 0x71`, `<System Audio Mode Request> 0x70`, and `<User Control Pressed/Released> 0x44/0x45`) are strictly unicast to Logical Address `5` (`CECDEVICE_AUDIOSYSTEM`). BarControl never broadcasts to address `15` or TV address `0`, ensuring your primary monitor never flickers or switches inputs.
- **Synchronous Sleep & Shutdown Standby**: Hooks `SystemEvents.PowerModeChanged` (`PowerModes.Suspend` / `PowerModes.Resume`), `SystemEvents.SessionEnding`, and WPF `Application.OnSessionEnding` to transmit discrete idempotent standby opcodes (`0x36` + `0x6C`) synchronously before Windows suspends user-mode threads or terminates the session, and automatically re-enumerates the USB-CEC COM port on wake.
- **Closed-Loop Target-Chasing Volume (`CecVolumeController`)**: Uses a non-blocking background worker that sends explicit keypress/release bursts toward the requested volume slider target and verifies actual hardware volume via `<Report Audio Status> (0x7A)`.

### Dolby Atmos / Bitstream Volume Proxy
- **Exclusive Bitstream Detection**: Monitors WASAPI session states and endpoint volume lock conditions on the selected soundbar endpoint while continuously snapshotting user PCM volume levels (`SavedWindowsVolume` and `SavedSoundbarVolume`).
- **Two-Way Keyboard Hook & Endpoint Bridge**: Uses a low-level keyboard hook (`WH_KEYBOARD_LL` for `VK_VOLUME_UP`, `VK_VOLUME_DOWN`, `VK_VOLUME_MUTE`) combined with `IAudioEndpointVolumeCallback` notifications to intercept volume adjustments during exclusive bitstream playback and dispatch them to `CecVolumeController`, restoring pre-stream volumes when returning to shared PCM.

### Win32 Relocation Engine
- **Event Hooks**: Uses native `SetWinEventHook` out-of-context for `EVENT_OBJECT_LOCATIONCHANGE`, `EVENT_SYSTEM_MOVESIZEEND`, `EVENT_OBJECT_SHOW`, and `EVENT_SYSTEM_FOREGROUND`, paired with a lightweight fallback sweep and `WM_DISPLAYCHANGE` hot-plug detection.
- **Display Friendly Names**: Reads monitor model names (e.g., `AW3423DWF`, `SOUNDSLAYER`) directly via WMI (`WmiMonitorID` EDID parsing).
- **Primary Monitor Safety Guard**: Automatically prevents the OS Primary display from ever being marked as excluded.

### Audio Keep-Alive (WASAPI Engine)
- **Per-Device & Power-State Aware**: Targets only the selected soundbar's WASAPI endpoint in shared mode (`IAudioClient`) using a 50Hz zero-DC-bias sub-bit `Fluctuate` pulse (`0x38000100` for 16-bit, `0x34000001` for 24/32-bit), automatically pausing when the soundbar is powered off so headphones or desktop speakers are unaffected.
- **Acknowledgements & Credit**: The audio keep-alive feature and `Fluctuate` stream implementation are directly inspired by and based on the work of **VEG** in [SoundKeeper](https://github.com/vrubleg/soundkeeper).

### Build & Utility Scripts
| Script | Description |
| :--- | :--- |
| `run.cmd` | Runs the app from source (`dotnet run`). |
| `build.cmd` | Builds the solution in Release mode (`dotnet build`). |
| `publish.cmd` | Builds the self-contained single-file executable to `dist\BarControl.exe`. |
| `install.cmd` | Installs BarControl to `%LocalAppData%\Programs\BarControl`, Start Menu, and Windows Installed Apps. |
| `uninstall.cmd` | Cleanly removes BarControl, shortcuts, and registry entries. |

### Repository Structure
```
BarControl/
├── Core/
│   ├── Audio/
│   │   ├── AudioKeepAliveService.cs       # WASAPI background keep-alive worker
│   │   ├── BitstreamVolumeProxyService.cs # Dolby Atmos / Bitstream detection & CEC volume proxy
│   │   └── Wasapi.cs                      # Native WASAPI & IAudioEndpointVolume COM interop
│   ├── Cec/
│   │   ├── CecService.cs                  # Pulse-Eight USB-CEC discovery, power sync & reconnect
│   │   ├── CecVolumeController.cs         # Closed-loop target-chasing CEC volume controller
│   │   └── Native/LibCec.cs               # Native libcec P/Invoke bindings
│   ├── Native/
│   │   └── Win32.cs                       # Win32 P/Invoke APIs (SetWinEventHook, DWM, RedrawWindow)
│   ├── Display/
│   │   ├── DisplayInfo.cs                 # Monitor geometry, DPI, and friendly name models
│   │   └── DisplayService.cs              # Display enumeration via EnumDisplayMonitors & WMI
│   ├── Engine/
│   │   └── WindowRelocationEngine.cs      # WinEvent hook engine, window position cache & redirection
│   ├── Config/
│   │   ├── AppSettings.cs                 # Settings schema
│   │   └── ConfigService.cs               # JSON settings in %LocalAppData%\BarControl\settings.json
│   ├── Logging/
│   │   └── LogService.cs                  # Diagnostic logger in %LocalAppData%\BarControl\barcontrol.log
│   ├── Startup/
│   │   └── StartupService.cs              # Windows Run & Uninstall registry key manager
│   ├── Update/
│   │   └── UpdateService.cs               # GitHub Releases auto-update checker & file swapper
│   └── Tray/
│       └── TrayIconService.cs             # System tray icon & context menu management
├── UI/
│   ├── MainWindowViewModel.cs             # MVVM ViewModel coordinating engine, CEC, proxy, and UI
│   └── Converters.cs                      # WPF value converters
├── MainWindow.xaml / .cs                  # Main Fluent dark-mode dashboard
├── Assets/
│   ├── app.ico                            # Multi-resolution application icon
│   ├── generate_icon.ps1                  # Icon generator script
│   └── screenshot.png                     # Application UI screenshot
├── BarControl.sln                         # Visual Studio Solution
├── BarControl.csproj                      # Project configuration (.NET 8 Windows)
├── install.cmd / install.ps1              # User-level installer
├── uninstall.cmd / uninstall.ps1          # Complete uninstaller
├── run.cmd                                # Debug runner
├── build.cmd                              # Release builder
└── publish.cmd / publish.ps1              # Single-file publisher
```

## Acknowledgements
- [SoundKeeper by VEG (vrubleg)](https://github.com/vrubleg/soundkeeper) — for the WASAPI audio keep-alive concept and the `Fluctuate` stream design that keeps digital audio devices awake without audible sound.

## License
MIT License.
