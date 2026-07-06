# RemotePlayForge

<!-- Replace OWNER with your GitHub username once the repo is pushed. -->
[![build](https://github.com/OWNER/RemotePlayForge/actions/workflows/build.yml/badge.svg)](https://github.com/OWNER/RemotePlayForge/actions/workflows/build.yml)

**Force Steam Remote Play Together on any game — invite friends, generate a shareable guest link, and hand out keyboard / mouse / controller control to each person individually.**

A lightweight Windows (C# / WinForms) tray app that drives Steam **Remote Play Together** directly through the running Steam client. Pick an online friend or generate a guest link, and your friends can play your game with you — with live, per-person control over what each of them can use. Built with the in-box .NET Framework compiler, so **no SDK or Visual Studio install is required**.

Inspired by [RemotePlayWhatever](https://github.com/m4dEngi/RemotePlayWhatever).

---

## Features

- **Invite an online friend** straight from an in-app list, or **generate a shareable guest link** that anyone with Steam can join — no friendship required.
- **Multiple guests at once** — invite several friends (or share one link with the group); everyone joins the same session and each appears in a live roster.
- **Per-person control** — each connected guest gets their own row with keyboard / mouse / controller toggles. Give or revoke any peripheral for any individual, live, mid-game.
- **"Everyone" controls** — pause or hand back control to all guests at once.
- **Global hotkeys** — flip control without alt-tabbing. Fully rebindable, and one key cycles which person the hotkeys act on.
- **Connect alerts** — tray balloon, sound, and taskbar flash the moment a guest joins.
- **System-tray friendly** — minimizes to the tray and keeps the session alive.

## How it works

Remote Play Together for arbitrary games lives in Steam's *internal* client interface
(`IClientRemoteClientManager` in `steamclient64.dll`) — the same path RemotePlayWhatever uses. These
interfaces are undocumented and their vtable layouts **change between Steam builds**, so RemotePlayForge
**resolves every method by name at runtime** (each client stub references its own name as a telemetry
string) rather than hardcoding offsets. This makes calls safe — you invoke exactly the method you named —
and self-adapting across builds.

The GUI (`RemotePlayForge.exe`) enumerates your friends and shells out to a small out-of-process helper
(`RPForgeSession.exe`) that hosts the session, because the Remote Play Together group only creates
reliably from a fresh, dedicated process. The two communicate over a simple stdout/stdin line protocol.

> **This is not piracy.** Remote Play Together is a legitimate Valve feature that *streams video* of the
> host's game to guests. Guests don't receive a copy of the game — nothing is cracked and no DRM is
> bypassed. RemotePlayForge only talks to your own local, signed-in Steam client.

## Requirements

- Windows 10 / 11 (x64)
- Steam installed, **running and signed in**
- .NET Framework 4.x (in-box on Windows — no install needed)

## Build

```powershell
# from the project root
./build.ps1
```

This compiles the GUI (`src/*.cs`) and the session helper (`helper/RPForgeSession.cs`) with the in-box
`csc.exe` into `bin/`.

## Run

1. Launch the game you want to play **through Steam**.
2. Run `bin/RemotePlayForge.exe`.
3. Either pick an online friend and click **Invite & Give Control**, or click **Generate guest link**
   and share the copied link with your friends.
4. When a friend connects, they appear under **Who's connected**. Use the per-person ⌨ / 🖱 / 🎮 toggles
   (or the hotkeys) to hand out or revoke control.
5. Click **Stop** to disconnect everyone and end the session.

## Hotkeys

Defaults (all rebindable via the **⚙ Hotkeys** button):

| Action | Default |
|---|---|
| I'll drive (pause target) | `Ctrl+Alt+D` |
| Hand control back | `Ctrl+Alt+G` |
| Toggle keyboard | `Ctrl+Alt+K` |
| Toggle mouse | `Ctrl+Alt+M` |
| Toggle controller | `Ctrl+Alt+C` |
| Next person (hotkey target) | `Ctrl+Alt+N` |

The control hotkeys act on the currently selected target — **Everyone** by default. Press *Next person*
to cycle the target to a specific guest, then the same hotkeys control just that person.

## Known limitations

- **Guest mouse-cursor movement on desktop-style menus** can be unreliable (clicks and scroll work, but
  the cursor may not move). This is a Steam Remote Play Together behavior, not a bug in this app — Steam
  gives guests a virtual cursor that many game menus don't follow. Mouse usually works normally in actual
  gameplay (mouse-look / aiming). Navigate menus with keyboard or controller if needed.
- Remote Play Together binds to the **currently running Steam game**, so a game must be running before you
  invite.

## Project layout

```
build.ps1                       PowerShell build (in-box csc, no SDK)
src/Program.cs                  entry point, single-instance guard
src/MainForm.cs                 tray UI, roster, per-person controls, hotkeys
src/SteamInternalForce.cs       Steam internal-interface interop (friends, RPT), name-resolved vtables
src/SteamPaths.cs               Steam install / running-state / AppID resolution
helper/RPForgeSession.cs        out-of-process session helper (hosts the RPT session)
```

## Credits

- [RemotePlayWhatever](https://github.com/m4dEngi/RemotePlayWhatever) by m4dEngi — the original technique
  this project is inspired by.
- [open-steamworks](https://github.com/SteamRE/open-steamworks) — internal interface references.

## License

[MIT](LICENSE). Not affiliated with or endorsed by Valve. "Steam" and "Remote Play Together" are
trademarks of Valve Corporation.
