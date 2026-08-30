# ArcadeBridge 1.0.0

ArcadeBridge turns remote browser controllers into up to four virtual Xbox 360 or DualShock 4 controllers on a Windows Host PC.

## Highlights

- Self-contained 64-bit Windows Host EXE; no separate .NET installation is required.
- Browser controller and keyboard input with visual Xbox and PlayStation mapping.
- Four independently assignable virtual controller slots.
- Button remapping, duplicate warnings, live input highlighting, profiles, and calibration.
- Player names, ready status, signal display, slot moving/swapping, room lock, removal, and temporary room bans with reasons.
- Setup wizards and built-in Help for Hosts and players.
- System, Dark, Light, Retro Arcade, and Custom themes.
- Safe disconnect, stuck-input watchdog, activity log, Host input tester, and password-safe diagnostics.

## Requirements

- Windows 10 or 11, 64-bit, for the Host.
- ViGEmBus installed on the Host PC.
- Players need only the ArcadeBridge Controller webpage and a compatible browser.
- Internet access to the ArcadeBridge Relay.

## Recommended download

Download `ArcadeBridge-Master-1.0.0-Final.zip`. It contains the Host, player webpage, Relay administrator files, matching source, setup documentation, and checksums.

## Unsigned build notice

Version 1.0.0 is not digitally signed. Windows may display **Unknown publisher** or a Microsoft Defender SmartScreen warning. Verify the SHA-256 checksum before running the Host:

```text
ArcadeBridgeHost-1.0.0.exe
8F99DCFCF7737887455AF676C3487942685FF18B1F8E0D378B6D07DF31833148
```

## Relay note

Ordinary Hosts and players do not install the Relay. Relay files are included only for the server administrator.
