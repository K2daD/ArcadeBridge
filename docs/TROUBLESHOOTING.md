# Troubleshooting ArcadeBridge

## Host says ViGEm is unavailable

- Install or repair ViGEmBus.
- Restart Windows.
- Close stale ArcadeBridge Host or virtual-controller processes.
- Reopen ArcadeBridge before launching the game.

## Player cannot connect

- Re-enter the exact room code.
- Re-enter the case-sensitive password.
- Ask whether the Host locked the room.
- Ask the Host to check whether this browser identity is temporarily banned.
- Confirm the Host and player can reach the Relay.

## Controller appears in the webpage but not at the Host

- Confirm the page says connected to the Relay and Host.
- Press Ready and watch the live input layout.
- Ask the Host to use the input tester.
- Disconnect and reconnect after checking room credentials.

## Browser does not list the controller

- Press a controller button after the page loads.
- Reconnect the USB cable or Bluetooth connection.
- Refresh the page.
- Close software that may exclusively capture the controller.
- Try another current browser or use keyboard mapping.

## Game does not react although Host input test works

- Select the matching virtual gamepad in the game/emulator.
- Start ArcadeBridge before launching the game.
- Confirm the player is assigned to the expected P1–P4 slot.
- Remove duplicate controller entries from the game's input configuration.

## Stuck movement or button

- Disconnect the player.
- Disconnect the Host room.
- Reconnect after the status shows all virtual controls released.

## Reporting a bug

Include the ArcadeBridge version, affected component, browser/controller details, exact reproduction steps, and the Host's password-safe diagnostic report when relevant. Never post room passwords, private keys, certificates, or personal information.
