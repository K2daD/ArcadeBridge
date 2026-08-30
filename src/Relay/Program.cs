using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

string? configuredUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(configuredUrls))
{
    configuredUrls = "http://127.0.0.1:26761";
    builder.WebHost.UseUrls(configuredUrls);
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

const int MaxControllersPerRoom = 4;
const int MaxMessageBytes = 4096;
const int MaxMessagesPerSecond = 120;
const int MinimumTokenLength = 8;

var rooms = new ConcurrentDictionary<string, RelayRoom>(StringComparer.OrdinalIgnoreCase);

app.MapGet("/", () => Results.Text("ArcadeBridge Relay 1.0.0 is running."));
app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapGet("/status", () => Results.Json(new
{
    relay = "ArcadeBridge Relay",
    status = "running",
    rooms = rooms.Count,
    hosts = rooms.Values.Count(room => room.Host != null),
    controllers = rooms.Values.Sum(room => room.Controllers.Count),
    version = "1.0.0",
    features = new[] { "ready", "signal", "host-lock", "host-kick", "room-ban", "room-unban", "ban-reason" }
}));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connection required.");
        return;
    }

    string role = context.Request.Query["role"].ToString().Trim().ToLowerInvariant();
    string roomCode = context.Request.Query["room"].ToString().Trim().ToUpperInvariant();
    string clientIdText = context.Request.Query["clientId"].ToString().Trim();
    Guid clientId = Guid.TryParse(clientIdText, out Guid parsedClientId)
        ? parsedClientId
        : Guid.NewGuid();

    if (role != "host" && role != "controller")
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Role must be host or controller.");
        return;
    }

    if (!IsValidRoomCode(roomCode))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Room code is missing or invalid.");
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

    string? token = await ReceiveAuthenticationToken(
        socket,
        MaxMessageBytes,
        TimeSpan.FromSeconds(10)
    );

    if (token == null || token.Length < MinimumTokenLength || token.Length > 128)
    {
        await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation,
            "Authentication failed.");
        return;
    }

    RelayRoom? room;

    if (role == "host")
    {
        room = rooms.GetOrAdd(roomCode, _ => new RelayRoom(token));

        if (!TokensMatch(room.Token, token))
        {
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation,
                "Incorrect room password.");
            return;
        }

    }
    else
    {
        if (!rooms.TryGetValue(roomCode, out room) || room.Host == null)
        {
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation,
                "Room host is not connected.");
            return;
        }

        if (!TokensMatch(room.Token, token))
        {
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation,
                "Incorrect room password.");
            return;
        }

        if (room.IsLocked)
        {
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation,
                "This room is locked by the host.");
            return;
        }

        if (room.BannedClientIds.TryGetValue(clientId, out string? banReason))
        {
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation,
                "Banned from room: " + banReason);
            return;
        }

        if (room.Controllers.Count >= MaxControllersPerRoom)
        {
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation,
                "Room already has 4 controllers.");
            return;
        }
    }

    await SendText(socket, "{\"type\":\"auth\",\"ok\":true}");
    room.Touch();

    if (role == "host")
        await HandleHost(roomCode, room, socket);
    else
        await HandleController(roomCode, room, socket, clientId, MaxMessageBytes, MaxMessagesPerSecond);

    room.Touch();
    if (room.IsEmpty)
        rooms.TryRemove(roomCode, out _);
});

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromMinutes(1));
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);

        foreach (KeyValuePair<string, RelayRoom> entry in rooms)
        {
            if (entry.Value.IsEmpty && entry.Value.LastActivityUtc < cutoff)
                rooms.TryRemove(entry.Key, out _);
        }
    }
});

Console.WriteLine();
Console.WriteLine("ArcadeBridge Relay 1.0.0");
Console.WriteLine($"Listening on {configuredUrls}");
Console.WriteLine("Room password authentication: enabled");
Console.WriteLine($"Max controllers per room: {MaxControllersPerRoom}");
Console.WriteLine($"Rate limit: {MaxMessagesPerSecond} messages/sec");
Console.WriteLine();

app.Run();

static bool IsValidRoomCode(string roomCode)
{
    if (string.IsNullOrWhiteSpace(roomCode) || roomCode.Length > 32)
        return false;

    return roomCode.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
}

static bool TokensMatch(string expected, string supplied)
{
    byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
    byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);

    return expectedBytes.Length == suppliedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
}

static async Task<string?> ReceiveAuthenticationToken(
    WebSocket socket,
    int maxMessageBytes,
    TimeSpan timeout)
{
    using var cancellation = new CancellationTokenSource(timeout);
    byte[] buffer = new byte[maxMessageBytes];
    using var message = new MemoryStream();

    try
    {
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellation.Token
            );

            if (result.MessageType != WebSocketMessageType.Text ||
                message.Length + result.Count > maxMessageBytes)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        using JsonDocument json = JsonDocument.Parse(message.ToArray());
        JsonElement root = json.RootElement;

        if (!root.TryGetProperty("type", out JsonElement type) ||
            type.GetString() != "auth" ||
            !root.TryGetProperty("token", out JsonElement token))
        {
            return null;
        }

        return token.GetString();
    }
    catch
    {
        return null;
    }
}

static async Task HandleHost(string roomCode, RelayRoom room, WebSocket socket)
{
    WebSocket? oldHost;

    lock (room.Sync)
    {
        oldHost = room.Host;
        room.Host = socket;
        room.IsLocked = false;
        room.Touch();
    }

    if (oldHost != null && oldHost != socket)
        await CloseQuietly(oldHost, WebSocketCloseStatus.NormalClosure,
            "A new host connected.");

    Console.WriteLine($"[{roomCode}] Authenticated host connected");

    try
    {
        await ReceiveHostCommands(room, socket);
    }
    finally
    {
        List<WebSocket> controllersToClose = new();
        lock (room.Sync)
        {
            if (room.Host == socket)
            {
                room.Host = null;
                room.IsLocked = false;
                room.BannedClientIds.Clear();
                controllersToClose = room.Controllers.Values.ToList();
            }
            room.Touch();
        }

        foreach (WebSocket controller in controllersToClose)
            await CloseQuietly(controller, WebSocketCloseStatus.NormalClosure,
                "The room host closed the room.");

        Console.WriteLine($"[{roomCode}] Host disconnected");
    }
}

static async Task HandleController(
    string roomCode,
    RelayRoom room,
    WebSocket socket,
    Guid clientId,
    int maxMessageBytes,
    int maxMessagesPerSecond)
{
    Guid controllerId = Guid.NewGuid();
    room.Controllers[controllerId] = socket;
    room.ControllerClientIds[controllerId] = clientId;
    room.Touch();
    Console.WriteLine($"[{roomCode}] Authenticated controller connected");

    byte[] buffer = new byte[4096];
    DateTime rateWindowStart = DateTime.UtcNow;
    int messagesThisWindow = 0;

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseQuietly(socket, WebSocketCloseStatus.NormalClosure,
                        "Controller disconnected.");
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                if (message.Length + result.Count > maxMessageBytes)
                {
                    await CloseQuietly(socket, WebSocketCloseStatus.MessageTooBig,
                        "Message too large.");
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            DateTime now = DateTime.UtcNow;
            if (now - rateWindowStart >= TimeSpan.FromSeconds(1))
            {
                rateWindowStart = now;
                messagesThisWindow = 0;
            }

            if (++messagesThisWindow > maxMessagesPerSecond)
            {
                await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation,
                    "Rate limit exceeded.");
                return;
            }

            room.Touch();
            string controllerJson = Encoding.UTF8.GetString(message.ToArray());
            string relayJson =
                $$"""{"type":"controller","controllerId":"{{controllerId}}","clientId":"{{clientId}}","payload":{{controllerJson}}}""";

            WebSocket? host;
            lock (room.Sync)
                host = room.Host;

            if (host == null || host.State != WebSocketState.Open)
                continue;

            try
            {
                await room.HostSendLock.WaitAsync();
                try
                {
                    if (host.State == WebSocketState.Open)
                        await SendText(host, relayJson);
                }
                finally
                {
                    room.HostSendLock.Release();
                }
            }
            catch
            {
            }
        }
    }
    finally
    {
        room.Controllers.TryRemove(controllerId, out _);
        room.ControllerClientIds.TryRemove(controllerId, out _);
        room.Touch();
        Console.WriteLine($"[{roomCode}] Controller disconnected");
    }
}

static async Task ReceiveHostCommands(RelayRoom room, WebSocket socket)
{
    byte[] buffer = new byte[4096];

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None
                );
                if (result.MessageType == WebSocketMessageType.Text)
                    message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            room.Touch();
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseQuietly(socket, WebSocketCloseStatus.NormalClosure,
                    "Connection closed.");
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text || message.Length > 4096)
                continue;

            try
            {
                using JsonDocument json = JsonDocument.Parse(message.ToArray());
                JsonElement root = json.RootElement;
                if (!root.TryGetProperty("type", out JsonElement type))
                    continue;

                if (type.GetString() == "lock" &&
                    root.TryGetProperty("locked", out JsonElement locked))
                {
                    room.IsLocked = locked.GetBoolean();
                    Console.WriteLine($"Lobby lock changed: {room.IsLocked}");
                    await SendHostResponse(room, socket,
                        JsonSerializer.Serialize(new
                        {
                            type = "hostCommand",
                            command = "lock",
                            ok = true,
                            locked = room.IsLocked
                        }));
                }
                else if (type.GetString() == "kick" &&
                    root.TryGetProperty("controllerId", out JsonElement id) &&
                    Guid.TryParse(id.GetString(), out Guid controllerId))
                {
                    bool found = room.Controllers.TryGetValue(controllerId, out WebSocket? controller);
                    if (found && controller != null)
                        await CloseQuietly(controller, WebSocketCloseStatus.PolicyViolation,
                            "Removed by the room host.");
                    await SendHostResponse(room, socket,
                        JsonSerializer.Serialize(new
                        {
                            type = "hostCommand",
                            command = "kick",
                            ok = found,
                            controllerId = id.GetString(),
                            error = found ? (string?)null : "Controller was not found."
                        }));
                }
                else if (type.GetString() == "ban" &&
                    root.TryGetProperty("controllerId", out JsonElement banId) &&
                    Guid.TryParse(banId.GetString(), out Guid bannedControllerId))
                {
                    string reason = root.TryGetProperty("reason", out JsonElement reasonElement)
                        ? new string((reasonElement.GetString() ?? "")
                            .Where(character => character >= ' ' && character <= '~')
                            .Take(60).ToArray()).Trim()
                        : "";
                    if (reason.Length == 0)
                        reason = "No reason provided";
                    Guid bannedClientId = Guid.Empty;
                    bool found = room.Controllers.TryGetValue(bannedControllerId, out WebSocket? controller) &&
                        room.ControllerClientIds.TryGetValue(bannedControllerId, out bannedClientId);
                    if (found)
                    {
                        room.BannedClientIds[bannedClientId] = reason;
                        if (controller != null)
                            await CloseQuietly(controller, WebSocketCloseStatus.PolicyViolation,
                                "Banned from room: " + reason);
                    }
                    await SendHostResponse(room, socket,
                        JsonSerializer.Serialize(new
                        {
                            type = "hostCommand",
                            command = "ban",
                            ok = found,
                            controllerId = banId.GetString(),
                            clientId = found ? bannedClientId.ToString() : null,
                            reason,
                            error = found ? (string?)null : "Controller was not found."
                        }));
                }
                else if (type.GetString() == "unban" &&
                    root.TryGetProperty("clientId", out JsonElement unbanId) &&
                    Guid.TryParse(unbanId.GetString(), out Guid unbannedClientId))
                {
                    bool removed = room.BannedClientIds.TryRemove(unbannedClientId, out _);
                    await SendHostResponse(room, socket,
                        JsonSerializer.Serialize(new
                        {
                            type = "hostCommand",
                            command = "unban",
                            ok = removed,
                            clientId = unbanId.GetString(),
                            error = removed ? (string?)null : "That player was not on the room blacklist."
                        }));
                }
            }
            catch (JsonException)
            {
            }
        }
    }
    catch (WebSocketException)
    {
    }
}

static async Task SendHostResponse(RelayRoom room, WebSocket host, string text)
{
    await room.HostSendLock.WaitAsync();
    try
    {
        if (host.State == WebSocketState.Open)
            await SendText(host, text);
    }
    finally
    {
        room.HostSendLock.Release();
    }
}

static Task SendText(WebSocket socket, string text)
{
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    return socket.SendAsync(
        new ArraySegment<byte>(bytes),
        WebSocketMessageType.Text,
        true,
        CancellationToken.None
    );
}

static async Task CloseQuietly(
    WebSocket socket,
    WebSocketCloseStatus status,
    string reason)
{
    try
    {
        if (socket.State == WebSocketState.Open ||
            socket.State == WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(status, reason, CancellationToken.None);
        }
    }
    catch
    {
    }
}

sealed class RelayRoom
{
    public RelayRoom(string token) => Token = token;

    public string Token { get; }
    public object Sync { get; } = new();
    public SemaphoreSlim HostSendLock { get; } = new(1, 1);
    public WebSocket? Host { get; set; }
    public bool IsLocked { get; set; }
    public ConcurrentDictionary<Guid, WebSocket> Controllers { get; } = new();
    public ConcurrentDictionary<Guid, Guid> ControllerClientIds { get; } = new();
    public ConcurrentDictionary<Guid, string> BannedClientIds { get; } = new();
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    public void Touch() => LastActivityUtc = DateTime.UtcNow;

    public bool IsEmpty
    {
        get
        {
            lock (Sync)
                return Host == null && Controllers.IsEmpty;
        }
    }
}
