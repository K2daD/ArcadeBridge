# ArcadeBridge Relay administrator guide

Ordinary Hosts and players do not install or run the Relay. This component is only for the administrator maintaining the public ArcadeBridge Relay.

## Responsibilities

- Maintain the server, Docker, DNS, reverse proxy, HTTPS/WSS certificate, backups, and monitoring.
- Keep port and proxy settings compatible with the existing production environment.
- Rebuild and restart the container after updating Relay source.
- Verify the public `/status` endpoint after every deployment.
- Never commit certificates, private keys, account credentials, or production secrets.

## Deploy

From `src/Relay` on the Relay server:

```powershell
docker compose up -d --build
docker compose ps
```

Then verify `https://relay.rommserver.org/status` and complete a real Host/player connection test.

## State

Rooms, assignments, and room bans are held in memory. Restarting the Relay clears active rooms. A Host disconnect also closes that room and clears its temporary blacklist.
