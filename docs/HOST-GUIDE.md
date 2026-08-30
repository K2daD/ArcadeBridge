# ArcadeBridge Host guide

## Before the first room

1. Use a 64-bit Windows 10 or 11 PC.
2. Install ViGEmBus and restart Windows if its installer requests it.
3. Extract the ArcadeBridge release to a normal folder.
4. Verify the published SHA-256 checksum if the build is unsigned.

## Create a room

1. Open `ArcadeBridgeHost.exe`.
2. Follow the setup wizard or open it from Help.
3. Keep the generated room code or enter a valid one.
4. Enter an 8–128 character case-sensitive password.
5. Select **Connect Host + P1–P4**.
6. Wait for both the Relay and virtual-controller status to show connected.

The Host connects outbound to the Relay. Ordinary Host users do not run Docker or the Relay source.

## Invite players

Use **Copy room code/invite** and send the controller webpage address to each player. The password is intentionally excluded from the copied invite; share it separately through a trusted private channel.

## Manage the lobby

Each connected player appears on a card with their display name, slot, ready state, signal, and input activity. Use the card controls to move or swap slots. Locking the room prevents new joins. Removing a player disconnects that session but permits a later reconnect. A room ban blocks that player's browser identity until unbanned or until the room closes.

## Test without a game

Open the Host input tester. Ask a player to press buttons and move sticks. If the tester responds, ArcadeBridge is delivering input to the Host. If the game still does not respond, select the matching virtual controller inside the game/emulator or restart the game after the virtual controller exists.

## End a room safely

Select **Disconnect** before closing the app. This releases every virtual control and closes the room. If the app was closed unexpectedly, reopen it, disconnect any active room, and close stale controller/Host processes before testing again.
