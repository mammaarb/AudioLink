# AudioLink

Use one keyboard, mouse, pair of headphones, microphone and clipboard across two Windows computers on the same network.

- **Sound**: everything the laptop plays comes out of the main PC's headphones.
- **Keyboard & mouse**: move the pointer off the edge of the PC's screen and it carries on onto the laptop (like Deskflow / Synergy). Scroll Lock jumps between the two.
- **Clipboard**: copy text or pictures on one computer, paste on the other.
- **Microphone**: use the main PC's microphone in apps on the laptop (needs the free [VB-Cable](https://vb-audio.com/Cable/) on the laptop).
- Starts with Windows and lives in the system tray.

Keyboard, mouse, clipboard and microphone traffic is encrypted (AES + HMAC) with a pairing code shown on the main PC, so other devices on the network can't read your typing or listen to your microphone.

## Download

Get `AudioLink.exe` from the [Releases](../../releases) page. It's a single file with nothing to install. Run the same file on both computers.

## Setup

1. **Main PC** (the one with the keyboard, mouse and headphones): open AudioLink, click **Main PC**, drag the laptop picture to the side of the screen where it sits, tick **Start with Windows**, click **Start**. If a yellow firewall bar appears, click **Allow**.
2. **Laptop**: open AudioLink, click **Laptop**, press **Find PC**, type the pairing code shown on the PC, tick **Start with Windows**, click **Start**.

See [HOW-TO-USE.txt](HOW-TO-USE.txt) for the microphone setup, options and troubleshooting.

## Building from source

No SDK needed. `build.bat` uses the C# compiler that ships with Windows (.NET Framework 4.x):

```
build.bat
```

## How it works

| Feature | Technique | Port |
|---|---|---|
| Laptop sound → PC | WASAPI loopback capture (falls back to Windows 11 process loopback) | TCP 50505 |
| Find the PC | UDP broadcast | UDP 50506 |
| Keyboard, mouse, clipboard | Low-level hooks on the PC, `SendInput` on the laptop, encrypted channel | TCP 50507 |
| PC microphone → laptop | WASAPI capture, played into VB-Cable, encrypted channel | TCP 50508 |

Limits: the laptop's lock screen and admin (UAC) prompts can't be controlled remotely, as Windows blocks that. Ctrl+Alt+Del always goes to the PC.
