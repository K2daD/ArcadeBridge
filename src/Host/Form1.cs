using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ArcadeBridgeHost;

public partial class Form1 : Form
{
    private const int PlayerCount = 4;
    private const string DefaultRoomCode = "VRTEST";
    private const string RelayUrl = "wss://relay.rommserver.org/ws";

    private readonly IXbox360Controller?[] _virtualControllers = new IXbox360Controller?[PlayerCount];
    private readonly IDualShock4Controller?[] _dualShockControllers = new IDualShock4Controller?[PlayerCount];
    private readonly string[] _controllerModes = Enumerable.Repeat("xbox", PlayerCount).ToArray();
    private readonly DateTime[] _lastPacketTimes = new DateTime[PlayerCount];
    private readonly ControllerPacket?[] _lastPackets = new ControllerPacket?[PlayerCount];
    private readonly string?[] _slotControllerIds = new string?[PlayerCount];
    private readonly Dictionary<string, int> _controllerSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _controllerNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _controllerReady = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ControllerTelemetry> _controllerTelemetry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _controllerClientIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bannedPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bannedReasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _controllerLock = new();
    private readonly object _outputLock = new();
    private readonly object _activityLock = new();
    private readonly Queue<string> _activityLog = new();
    private bool _shutdownComplete;
    private readonly System.Windows.Forms.Timer _watchdogTimer;

    private ViGEmClient? _vigemClient;
    private ClientWebSocket? _relaySocket;
    private CancellationTokenSource? _relayCancellation;

    private Label _relayStatus = null!;
    private Label _controllerStatus = null!;
    private Label _inputStatus = null!;
    private Label _roomStatus = null!;
    private TextBox _roomPasswordInput = null!;
    private TextBox _roomCodeInput = null!;
    private ComboBox _themeSelector = null!;
    private Button _customThemeButton = null!;
    private Button _lockLobbyButton = null!;
    private readonly SemaphoreSlim _hostCommandSendLock = new(1, 1);
    private bool _lobbyLocked;
    private string? _pendingKickControllerId;
    private string? _pendingKickName;
    private string? _pendingBanName;
    private string? _pendingBanReason;
    private string? _pendingUnbanClientId;
    private GroupBox _lobbyGroup = null!;
    private readonly Panel[] _lobbyCards = new Panel[PlayerCount];
    private readonly Label[] _lobbyNameLabels = new Label[PlayerCount];
    private readonly Label[] _lobbyTypeLabels = new Label[PlayerCount];
    private readonly Label[] _lobbyStateLabels = new Label[PlayerCount];
    private readonly Label[] _lobbyQualityLabels = new Label[PlayerCount];
    private Color _themeBackground = Color.FromArgb(28, 28, 32);
    private Color _themeForeground = Color.WhiteSmoke;
    private Color _themeAccent = Color.RoyalBlue;
    private Color _customBackground = Color.FromArgb(24, 24, 32);
    private Color _customForeground = Color.White;
    private Color _customAccent = Color.DeepSkyBlue;

    public Form1()
    {
        InitializeComponent();

        _watchdogTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _watchdogTimer.Tick += WatchdogTick;

        BuildUi();
        _watchdogTimer.Start();

        FormClosing += FormClosingSafely;
        Shown += async (_, _) =>
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\VRArcadeHostGui");
                if (Convert.ToInt32(key.GetValue("SetupWizardSeen", 0)) == 0)
                {
                    key.SetValue("SetupWizardSeen", 1);
                    await ShowHostSetupWizardAsync();
                }
            }
            catch
            {
                // The Setup Wizard button remains available if the preference cannot be saved.
            }
        };
    }

    private void BuildUi()
    {
        Text = "ArcadeBridge Host 1.0.0";
        Width = 650;
        Height = 850;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label
        {
            Text = "ArcadeBridge Host",
            Left = 20,
            Top = 20,
            Width = 450,
            Height = 35,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold)
        };
        Controls.Add(title);

        _roomStatus = new Label
        {
            Text = "Room Code:",
            Left = 20,
            Top = 70,
            Width = 100,
            Height = 25
        };
        Controls.Add(_roomStatus);

        _roomCodeInput = new TextBox
        {
            Text = DefaultRoomCode,
            Left = 125,
            Top = 62,
            Width = 120,
            MaxLength = 32,
            CharacterCasing = CharacterCasing.Upper,
            BackColor = Color.White,
            ForeColor = Color.Black,
            Font = new Font(Font.FontFamily, 10f)
        };
        Controls.Add(_roomCodeInput);

        var randomRoomButton = new Button
        {
            Text = "Random", Left = 255, Top = 59, Width = 85, Height = 38,
            Font = new Font(Font.FontFamily, 9f)
        };
        randomRoomButton.Click += (_, _) =>
            _roomCodeInput.Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        Controls.Add(randomRoomButton);

        var copyRoomButton = new Button
        {
            Text = "Copy", Left = 350, Top = 59, Width = 75, Height = 38,
            Font = new Font(Font.FontFamily, 9f)
        };
        copyRoomButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_roomCodeInput.Text))
                Clipboard.SetText(_roomCodeInput.Text.Trim().ToUpperInvariant());
        };
        Controls.Add(copyRoomButton);

        var copyInviteButton = new Button
        {
            Text = "Copy Invite", Left = 435, Top = 59, Width = 105, Height = 38,
            Font = new Font(Font.FontFamily, 9f)
        };
        copyInviteButton.Click += (_, _) => CopyInviteLink();
        Controls.Add(copyInviteButton);


        var passwordLabel = new Label
        {
            Text = "Room Password:",
            Left = 20,
            Top = 100,
            Width = 120,
            Height = 25
        };
        Controls.Add(passwordLabel);

        _roomPasswordInput = new TextBox
        {
            Left = 145,
            Top = 96,
            Width = 220,
            UseSystemPasswordChar = true,
            MaxLength = 128
        };
        Controls.Add(_roomPasswordInput);

        var showPassword = new CheckBox
        {
            Text = "Show password", Left = 380, Top = 98, Width = 135, Height = 25
        };
        showPassword.CheckedChanged += (_, _) =>
            _roomPasswordInput.UseSystemPasswordChar = !showPassword.Checked;
        Controls.Add(showPassword);

        var passwordHelp = new Label
        {
            Text = "8-128 characters; case-sensitive. Letters, numbers, spaces, and symbols are allowed.",
            Left = 145,
            Top = 123,
            Width = 460,
            Height = 25,
            ForeColor = SystemColors.GrayText
        };
        Controls.Add(passwordHelp);

        _relayStatus = new Label
        {
            Text = "Relay: Disconnected",
            Left = 20,
            Top = 155,
            Width = 580,
            Height = 25
        };
        Controls.Add(_relayStatus);

        _controllerStatus = new Label
        {
            Text = "Virtual P1-P4: Disconnected",
            Left = 20,
            Top = 185,
            Width = 580,
            Height = 50
        };
        Controls.Add(_controllerStatus);

        _inputStatus = new Label
        {
            Text = "Input: Waiting",
            Left = 20,
            Top = 240,
            Width = 600,
            Height = 45
        };
        Controls.Add(_inputStatus);

        var startButton = new Button
        {
            Text = "Connect Host + P1-P4",
            Left = 20,
            Top = 295,
            Width = 190,
            Height = 45
        };
        startButton.Click += async (_, _) => await StartHostAsync();
        Controls.Add(startButton);

        var stopButton = new Button
        {
            Text = "Disconnect",
            Left = 230,
            Top = 295,
            Width = 140,
            Height = 45
        };
        stopButton.Click += async (_, _) => await StopHostAsync();
        Controls.Add(stopButton);

        var managePlayersButton = new Button
        {
            Text = "Manage Players",
            Left = 390,
            Top = 295,
            Width = 160,
            Height = 45
        };
        managePlayersButton.Click += (_, _) => ShowManagePlayersDialog();
        Controls.Add(managePlayersButton);

        _lockLobbyButton = new Button
        {
            Text = "Lock",
            Left = 560,
            Top = 295,
            Width = 65,
            Height = 45
        };
        _lockLobbyButton.Click += async (_, _) => await ToggleLobbyLockAsync();
        Controls.Add(_lockLobbyButton);

        BuildLobbyPanel();

        var instructions = new Label
        {
            Text =
                "ArcadeBridge 1.0.0\r\n\r\n" +
                $"Relay: {RelayUrl}\r\n" +
                "Use the room code and password shown above.\r\n\r\n" +
                "The Host connects OUTBOUND to the relay.",
            Left = 20,
            Top = 615,
            Width = 590,
            Height = 110
        };
        Controls.Add(instructions);

        var themeLabel = new Label
        {
            Text = "Theme:", Left = 20, Top = 555, Width = 70, Height = 25
        };
        Controls.Add(themeLabel);

        _themeSelector = new ComboBox
        {
            Left = 95, Top = 550, Width = 150,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _themeSelector.Items.AddRange(new object[]
        {
            "System", "Dark", "Light", "Retro Arcade", "Custom"
        });
        _themeSelector.SelectedIndexChanged += (_, _) => ApplySelectedTheme();
        Controls.Add(_themeSelector);

        _customThemeButton = new Button
        {
            Text = "Customize...", Left = 255, Top = 548, Width = 110, Height = 30
        };
        _customThemeButton.Click += (_, _) => ChooseCustomTheme();
        Controls.Add(_customThemeButton);

        var testInputsButton = new Button
        {
            Text = "Test Inputs", Left = 380, Top = 548, Width = 105, Height = 30
        };
        testInputsButton.Click += (_, _) => ShowInputTester();
        Controls.Add(testInputsButton);

        var activityButton = new Button
        {
            Text = "Activity Log", Left = 495, Top = 548, Width = 115, Height = 30
        };
        activityButton.Click += (_, _) => ShowActivityLog();
        Controls.Add(activityButton);

        var exportButton = new Button
        {
            Text = "Export Diagnostic Report", Left = 380, Top = 585, Width = 230, Height = 32
        };
        exportButton.Click += (_, _) => ExportDiagnosticReport();
        Controls.Add(exportButton);

        var helpButton = new Button
        {
            Text = "Help", Left = 20, Top = 585, Width = 120, Height = 32
        };
        helpButton.Click += (_, _) => ShowHostHelp();
        Controls.Add(helpButton);

        var setupWizardButton = new Button
        {
            Text = "Setup Wizard", Left = 150, Top = 585, Width = 140, Height = 32
        };
        setupWizardButton.Click += async (_, _) => await ShowHostSetupWizardAsync();
        Controls.Add(setupWizardButton);

        instructions.Top = 635;

        LoadThemePreference();
        UpdateControllerStatus();
        LogActivity("ArcadeBridge Host opened.");
    }

    private void ShowHostHelp()
    {
        const string help =
            "ROOM SETUP\r\n" +
            "Room Code identifies this session. Random creates a new code. Copy copies only the code. Copy Invite creates the webpage link.\r\n\r\n" +
            "Room Password protects the room and is case-sensitive. Players must enter the exact same password.\r\n\r\n" +
            "HOST CONTROLS\r\n" +
            "Connect Host + P1-P4 connects to the relay and creates four virtual controller slots.\r\n" +
            "Disconnect safely releases every virtual button and closes the room connection.\r\n" +
            "Lock prevents new players from joining. Unlock allows new players again.\r\n" +
            "Manage Players moves or swaps players between P1-P4, removes players, bans them for the current room, or removes a ban.\r\n\r\n" +
            "PLAYER CARDS\r\n" +
            "Each card shows the player's name, controller type, Ready state, and connection signal. Empty cards are waiting for a player.\r\n\r\n" +
            "TOOLS\r\n" +
            "Test Inputs shows every button, stick, and trigger currently reaching the Host.\r\n" +
            "Activity Log shows joins, moves, removals, bans, locks, and connection events.\r\n" +
            "Export Diagnostic Report saves troubleshooting information without including the room password.\r\n" +
            "Theme changes the Host appearance; Customize lets you choose your own colors.\r\n\r\n" +
            "Controller mapping and calibration happen on each player's webpage before inputs are sent to the Host.";

        MessageBox.Show(this, help, "ArcadeBridge Host Help",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task ShowHostSetupWizardAsync()
    {
        using var dialog = new Form
        {
            Text = "ArcadeBridge Host Setup Wizard",
            Width = 610,
            Height = 520,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var title = new Label
        {
            Text = "ArcadeBridge Host Setup", Left = 24, Top = 18, Width = 400, Height = 32,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold)
        };
        var stepLabel = new Label { Left = 25, Top = 52, Width = 530, Height = 25 };
        var content = new Panel { Left = 20, Top = 82, Width = 550, Height = 315 };
        var back = new Button { Text = "Back", Left = 245, Top = 410, Width = 95, Height = 40 };
        var next = new Button { Text = "Next", Left = 350, Top = 410, Width = 110, Height = 40 };
        var cancel = new Button
        {
            Text = "Close", Left = 470, Top = 410, Width = 95, Height = 40,
            DialogResult = DialogResult.Cancel
        };

        var roomCode = new TextBox
        {
            Text = _roomCodeInput.Text, Left = 20, Top = 48, Width = 220,
            MaxLength = 32, CharacterCasing = CharacterCasing.Upper
        };
        var random = new Button { Text = "Random", Left = 250, Top = 44, Width = 95, Height = 36 };
        random.Click += (_, _) => roomCode.Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var password = new TextBox
        {
            Text = _roomPasswordInput.Text, Left = 20, Top = 132, Width = 325,
            MaxLength = 128, UseSystemPasswordChar = true
        };
        var showPassword = new CheckBox
        {
            Text = "Show password", Left = 360, Top = 135, Width = 145, Height = 28
        };
        showPassword.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !showPassword.Checked;
        var startWhenFinished = new CheckBox
        {
            Text = "Connect the Host when I finish", Left = 20, Top = 248,
            Width = 300, Height = 30, Checked = true
        };

        int step = 0;
        string[] headings =
        {
            "Step 1 of 4 — Create the room",
            "Step 2 of 4 — Virtual controllers",
            "Step 3 of 4 — Manage the lobby",
            "Step 4 of 4 — Review and start"
        };

        void AddText(string text, int top, int height = 55, bool bold = false)
        {
            content.Controls.Add(new Label
            {
                Text = text, Left = 20, Top = top, Width = 505, Height = height,
                Font = new Font(Font.FontFamily, 10, bold ? FontStyle.Bold : FontStyle.Regular)
            });
        }

        void RenderStep()
        {
            content.Controls.Clear();
            stepLabel.Text = headings[step];
            back.Enabled = step > 0;
            next.Text = step == 3 ? "Finish" : "Next";

            if (step == 0)
            {
                AddText("Room Code", 15, 28, true);
                content.Controls.Add(roomCode);
                content.Controls.Add(random);
                AddText("Room Password", 98, 28, true);
                content.Controls.Add(password);
                content.Controls.Add(showPassword);
                AddText("Use 8–128 characters. The password is case-sensitive, and players must enter it exactly.", 178, 55);
            }
            else if (step == 1)
            {
                AddText("The Host creates four virtual controller positions: P1, P2, P3, and P4.", 20, 40, true);
                AddText("Players normally appear in the first available position. Their browser can request a preferred slot, and you can move or swap them later.", 72, 65);
                AddText("Xbox 360 is the recommended player mode for broad game compatibility. PlayStation mode creates a virtual DualShock 4 when a player selects it.", 150, 70);
                AddText("Disconnect safely releases every virtual button so games are not left receiving a stuck input.", 235, 50);
            }
            else if (step == 2)
            {
                AddText("Player cards show each name, virtual controller type, Ready state, and signal quality.", 20, 55, true);
                AddText("Manage Players lets you move or swap slots, remove someone, ban them for this room, or allow a banned player to return.", 85, 65);
                AddText("Lock prevents new players from joining. Players already connected remain in the room.", 160, 55);
                AddText("Test Inputs confirms exactly what is reaching P1–P4. Activity Log and Export Diagnostic Report help troubleshoot problems.", 225, 65);
            }
            else
            {
                AddText("Setup complete", 15, 32, true);
                AddText($"Room Code: {roomCode.Text.Trim().ToUpperInvariant()}\r\nPassword: {(password.Text.Length >= 8 ? "Ready" : "Too short")}", 58, 65);
                AddText("Share the room code and password with players. Never post the password publicly unless you want anyone to join.", 135, 60);
                content.Controls.Add(startWhenFinished);
            }
        }

        back.Click += (_, _) => { if (step > 0) { step--; RenderStep(); } };
        next.Click += (_, _) =>
        {
            if (step == 0)
            {
                if (string.IsNullOrWhiteSpace(roomCode.Text))
                {
                    MessageBox.Show(dialog, "Enter a room code or select Random.", "Room Code");
                    return;
                }
                if (password.Text.Length < 8)
                {
                    MessageBox.Show(dialog, "The room password must be at least 8 characters.", "Room Password");
                    return;
                }
            }

            if (step < 3)
            {
                step++;
                RenderStep();
            }
            else
                dialog.DialogResult = DialogResult.OK;
        };

        dialog.Controls.AddRange(new Control[] { title, stepLabel, content, back, next, cancel });
        dialog.CancelButton = cancel;
        RenderStep();

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _roomCodeInput.Text = roomCode.Text.Trim().ToUpperInvariant();
        _roomPasswordInput.Text = password.Text;
        LogActivity("Host setup wizard completed.");
        if (startWhenFinished.Checked)
            await StartHostAsync();
    }

    private void BuildLobbyPanel()
    {
        _lobbyGroup = new GroupBox
        {
            Text = "Player Lobby",
            Left = 20,
            Top = 355,
            Width = 590,
            Height = 185
        };

        for (int slot = 0; slot < PlayerCount; slot++)
        {
            var card = new Panel
            {
                Left = 12 + slot * 143,
                Top = 28,
                Width = 133,
                Height = 140,
                BorderStyle = BorderStyle.FixedSingle
            };
            var title = new Label
            {
                Text = $"PLAYER {slot + 1}",
                Left = 8, Top = 8, Width = 115, Height = 22,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font.FontFamily, 10, FontStyle.Bold)
            };
            var name = new Label
            {
                Text = "Waiting",
                Left = 6, Top = 37, Width = 119, Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true
            };
            var type = new Label
            {
                Text = "Virtual: Off",
                Left = 6, Top = 68, Width = 119, Height = 20,
                TextAlign = ContentAlignment.MiddleCenter
            };
            var state = new Label
            {
                Text = "Waiting",
                Left = 6, Top = 94, Width = 119, Height = 20,
                TextAlign = ContentAlignment.MiddleCenter
            };
            var quality = new Label
            {
                Text = "Signal: --",
                Left = 6, Top = 116, Width = 119, Height = 18,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font.FontFamily, 8)
            };
            card.Controls.AddRange(new Control[] { title, name, type, state, quality });
            _lobbyGroup.Controls.Add(card);
            _lobbyCards[slot] = card;
            _lobbyNameLabels[slot] = name;
            _lobbyTypeLabels[slot] = type;
            _lobbyStateLabels[slot] = state;
            _lobbyQualityLabels[slot] = quality;
        }

        Controls.Add(_lobbyGroup);
    }

    private void LogActivity(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_activityLock)
        {
            _activityLog.Enqueue(entry);
            while (_activityLog.Count > 500)
                _activityLog.Dequeue();
        }
    }

    private string ActivityLogText()
    {
        lock (_activityLock)
            return string.Join(Environment.NewLine, _activityLog);
    }

    private void ShowActivityLog()
    {
        var dialog = new Form
        {
            Text = "Host Activity Log", Width = 760, Height = 500,
            StartPosition = FormStartPosition.CenterParent
        };
        var output = new TextBox
        {
            Left = 15, Top = 15, Width = 710, Height = 385,
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 9),
            Text = ActivityLogText()
        };
        var copy = new Button { Text = "Copy Log", Left = 180, Top = 410, Width = 140, Height = 35 };
        copy.Click += (_, _) => Clipboard.SetText(output.Text);
        var clear = new Button { Text = "Clear Log", Left = 335, Top = 410, Width = 140, Height = 35 };
        clear.Click += (_, _) =>
        {
            lock (_activityLock)
                _activityLog.Clear();
            LogActivity("Activity log cleared.");
            output.Text = ActivityLogText();
        };
        var refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        refreshTimer.Tick += (_, _) => output.Text = ActivityLogText();
        dialog.FormClosed += (_, _) => refreshTimer.Dispose();
        dialog.Controls.AddRange(new Control[] { output, copy, clear });
        refreshTimer.Start();
        dialog.Show(this);
    }

    private void ShowInputTester()
    {
        var dialog = new Form
        {
            Text = "P1-P4 Live Input Tester", Width = 800, Height = 570,
            StartPosition = FormStartPosition.CenterParent
        };
        var outputs = new Label[PlayerCount];
        for (int slot = 0; slot < PlayerCount; slot++)
        {
            outputs[slot] = new Label
            {
                Left = 20, Top = 20 + slot * 120, Width = 740, Height = 105,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font(FontFamily.GenericMonospace, 9),
                Padding = new Padding(8)
            };
            dialog.Controls.Add(outputs[slot]);
        }

        void RefreshTester()
        {
            lock (_controllerLock)
            {
                for (int slot = 0; slot < PlayerCount; slot++)
                    outputs[slot].Text = BuildInputTesterText(slot, _lastPackets[slot]);
            }
        }

        var refreshTimer = new System.Windows.Forms.Timer { Interval = 100 };
        refreshTimer.Tick += (_, _) => RefreshTester();
        dialog.FormClosed += (_, _) => refreshTimer.Dispose();
        RefreshTester();
        refreshTimer.Start();
        dialog.Show(this);
    }

    private string BuildInputTesterText(int slot, ControllerPacket? packet)
    {
        string? controllerId = _slotControllerIds[slot];
        string name = controllerId != null && _controllerNames.TryGetValue(controllerId, out string? value)
            ? value : "Waiting";
        if (packet == null || controllerId == null)
            return $"PLAYER {slot + 1}   {name}\r\nNo live controller input";

        var buttons = new List<string>();
        if (packet.A) buttons.Add("A/Cross"); if (packet.B) buttons.Add("B/Circle");
        if (packet.X) buttons.Add("X/Square"); if (packet.Y) buttons.Add("Y/Triangle");
        if (packet.LB) buttons.Add("LB"); if (packet.RB) buttons.Add("RB");
        if (packet.Back) buttons.Add("Back/Share"); if (packet.Start) buttons.Add("Start/Options");
        if (packet.LS) buttons.Add("LS"); if (packet.RS) buttons.Add("RS");
        if (packet.Up) buttons.Add("Up"); if (packet.Down) buttons.Add("Down");
        if (packet.Left) buttons.Add("Left"); if (packet.Right) buttons.Add("Right");
        if (packet.PS) buttons.Add("PS"); if (packet.Touchpad) buttons.Add("Touchpad");
        string pressed = buttons.Count == 0 ? "None" : string.Join(", ", buttons);
        return $"PLAYER {slot + 1}   {name}   {_controllerModes[slot].ToUpperInvariant()}\r\n" +
            $"Buttons: {pressed}\r\n" +
            $"LX {packet.LX,6:F2}  LY {packet.LY,6:F2}  RX {packet.RX,6:F2}  RY {packet.RY,6:F2}  LT {packet.LT:F2}  RT {packet.RT:F2}";
    }

    private void ExportDiagnosticReport()
    {
        using var save = new System.Windows.Forms.SaveFileDialog
        {
            Title = "Save Arcade Diagnostic Report",
            Filter = "Text report (*.txt)|*.txt",
            FileName = $"Arcade-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;

        var report = new StringBuilder();
        report.AppendLine("ARCADEBRIDGE HOST 1.0.0 DIAGNOSTIC REPORT");
        report.AppendLine($"Created: {DateTime.Now:O}");
        report.AppendLine("Release: Reliability v5");
        report.AppendLine($"Windows: {Environment.OSVersion}");
        report.AppendLine($".NET: {Environment.Version}");
        report.AppendLine($"Relay state: {_relaySocket?.State.ToString() ?? "Not created"}");
        report.AppendLine($"Room code: {_roomCodeInput.Text.Trim().ToUpperInvariant()}");
        report.AppendLine("Room password: EXCLUDED FOR SAFETY");
        report.AppendLine($"Lobby locked: {_lobbyLocked}");
        report.AppendLine($"ViGEm client: {(_vigemClient == null ? "Not active" : "Active")}");
        lock (_controllerLock)
        {
            for (int slot = 0; slot < PlayerCount; slot++)
                report.AppendLine(BuildInputTesterText(slot, _lastPackets[slot]));
        }
        report.AppendLine();
        report.AppendLine("ACTIVITY LOG");
        report.AppendLine(ActivityLogText());
        try
        {
            File.WriteAllText(save.FileName, report.ToString(), new UTF8Encoding(false));
            LogActivity("Diagnostic report exported.");
            MessageBox.Show(this, "Diagnostic report saved. The room password was not included.",
                "Report Exported");
        }
        catch (Exception ex)
        {
            LogActivity("Diagnostic export failed: " + ex.Message);
            MessageBox.Show(this, "The report could not be saved: " + ex.Message,
                "Export Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplySelectedTheme()
    {
        string selected = _themeSelector?.SelectedItem?.ToString() ?? "System";
        bool systemLight = IsWindowsLightTheme();

        Color background;
        Color foreground;
        Color accent;

        switch (selected)
        {
            case "Light":
                background = Color.WhiteSmoke;
                foreground = Color.FromArgb(25, 25, 30);
                accent = Color.RoyalBlue;
                break;
            case "Retro Arcade":
                background = Color.FromArgb(12, 8, 28);
                foreground = Color.FromArgb(80, 245, 255);
                accent = Color.FromArgb(255, 45, 180);
                break;
            case "Custom":
                background = _customBackground;
                foreground = _customForeground;
                accent = _customAccent;
                break;
            case "Dark":
                background = Color.FromArgb(28, 28, 32);
                foreground = Color.WhiteSmoke;
                accent = Color.FromArgb(0, 150, 220);
                break;
            default:
                background = systemLight ? Color.WhiteSmoke : Color.FromArgb(28, 28, 32);
                foreground = systemLight ? Color.FromArgb(25, 25, 30) : Color.WhiteSmoke;
                accent = Color.RoyalBlue;
                break;
        }

        BackColor = background;
        ForeColor = foreground;
        _themeBackground = background;
        _themeForeground = foreground;
        _themeAccent = accent;

        foreach (Control control in Controls)
        {
            if (control is Label or CheckBox)
                control.ForeColor = foreground;
            else if (control is Button button)
            {
                button.BackColor = accent;
                button.ForeColor = Color.White;
                button.FlatStyle = FlatStyle.Flat;
            }
        }

        _customThemeButton.Enabled = selected == "Custom";
        ApplyLobbyTheme();
        UpdateControllerStatus();
        SaveThemePreference();
    }

    private void ApplyLobbyTheme()
    {
        if (_lobbyGroup == null)
            return;

        _lobbyGroup.ForeColor = _themeForeground;
        foreach (Panel card in _lobbyCards)
        {
            if (card == null)
                continue;
            card.BackColor = Color.FromArgb(
                (_themeBackground.R * 3 + _themeAccent.R) / 4,
                (_themeBackground.G * 3 + _themeAccent.G) / 4,
                (_themeBackground.B * 3 + _themeAccent.B) / 4);
            foreach (Control child in card.Controls)
                child.ForeColor = _themeForeground;
        }
    }

    private void ChooseCustomTheme()
    {
        using var dialog = new ColorDialog { FullOpen = true };

        MessageBox.Show(
            "First, choose the window background color.",
            "Custom Theme — Background"
        );
        dialog.Color = _customBackground;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _customBackground = dialog.Color;

        MessageBox.Show(
            "Next, choose the text color.",
            "Custom Theme — Text"
        );
        dialog.Color = _customForeground;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _customForeground = dialog.Color;

        MessageBox.Show(
            "Finally, choose the accent and button color.",
            "Custom Theme — Accent"
        );
        dialog.Color = _customAccent;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _customAccent = dialog.Color;
        ApplySelectedTheme();
        SaveThemePreference();
    }

    private void LoadThemePreference()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\VRArcadeHostGui");
            _customBackground = Color.FromArgb(Convert.ToInt32(key.GetValue("CustomBackground", _customBackground.ToArgb())));
            _customForeground = Color.FromArgb(Convert.ToInt32(key.GetValue("CustomForeground", _customForeground.ToArgb())));
            _customAccent = Color.FromArgb(Convert.ToInt32(key.GetValue("CustomAccent", _customAccent.ToArgb())));
            string theme = Convert.ToString(key.GetValue("Theme", "System")) ?? "System";
            _themeSelector.SelectedItem = _themeSelector.Items.Contains(theme) ? theme : "System";
        }
        catch
        {
            _themeSelector.SelectedItem = "System";
        }
    }

    private void SaveThemePreference()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\VRArcadeHostGui");
            key.SetValue("Theme", _themeSelector.SelectedItem?.ToString() ?? "System");
            key.SetValue("CustomBackground", _customBackground.ToArgb());
            key.SetValue("CustomForeground", _customForeground.ToArgb());
            key.SetValue("CustomAccent", _customAccent.ToArgb());
        }
        catch
        {
        }
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
            );
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) != 0;
        }
        catch
        {
            return true;
        }
    }

    private async Task StartHostAsync()
    {
        if (_relaySocket?.State == WebSocketState.Open)
            return;

        try
        {
            string roomCode = _roomCodeInput.Text.Trim().ToUpperInvariant();
            string roomPassword = _roomPasswordInput.Text;

            if (string.IsNullOrWhiteSpace(roomCode) ||
                roomCode.Length > 32 ||
                roomCode.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            {
                MessageBox.Show(
                    "Use 1-32 letters, numbers, hyphens, or underscores for the room code.",
                    "Invalid Room Code"
                );
                return;
            }

            if (roomPassword.Length < 8)
            {
                MessageBox.Show(
                    "Choose a room password containing at least 8 characters.",
                    "Room Password Required"
                );
                _roomPasswordInput.Focus();
                return;
            }

            _relayStatus.Text = "Relay: Connecting...";
            LogActivity($"Connecting Host to room {roomCode}.");
            CreateVirtualControllers();

            _relayCancellation = new CancellationTokenSource();
            _relaySocket = new ClientWebSocket();

            string url =
                $"{RelayUrl}?role=host&room={Uri.EscapeDataString(roomCode)}";

            await _relaySocket.ConnectAsync(
                new Uri(url),
                _relayCancellation.Token
            );

            await SendAuthenticationAsync(
                _relaySocket,
                roomPassword,
                _relayCancellation.Token
            );

            _relayStatus.Text = "Relay: Connected";
            _lobbyLocked = false;
            _lockLobbyButton.Text = "Lock";
            _inputStatus.Text = "Input: Waiting for controllers";
            LogActivity("Relay authenticated; P1-P4 virtual controllers are ready.");

            _ = Task.Run(() => ReceiveRelayLoopAsync(
                _relaySocket,
                _relayCancellation.Token
            ));
        }
        catch (Exception ex)
        {
            LogActivity("Host startup failed: " + ex.Message);
            MessageBox.Show(ex.ToString(), "ArcadeBridge Host Error");
            await StopHostAsync();
        }
    }

    private static async Task SendAuthenticationAsync(
        ClientWebSocket socket,
        string roomPassword,
        CancellationToken cancellationToken)
    {
        byte[] authenticationMessage = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                type = "auth",
                token = roomPassword
            })
        );

        await socket.SendAsync(
            new ArraySegment<byte>(authenticationMessage),
            WebSocketMessageType.Text,
            true,
            cancellationToken
        );
    }

    private void CreateVirtualControllers()
    {
        if (_virtualControllers.All(controller => controller != null))
            return;

        try
        {
            _vigemClient ??= new ViGEmClient();

            for (int slot = 0; slot < PlayerCount; slot++)
            {
                if (_virtualControllers[slot] != null)
                    continue;

                IXbox360Controller controller =
                    _vigemClient.CreateXbox360Controller();

                controller.Connect();
                _virtualControllers[slot] = controller;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "ViGEm could not create the virtual controllers. Verify that the ViGEmBus driver is installed, then restart the Host.", ex);
        }

        UpdateControllerStatus();
        LogActivity("ViGEm created four virtual Xbox 360 controller slots.");
    }

    private async Task ReceiveRelayLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open &&
                   !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken
                    );

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        SafeUi(() =>
                        {
                            ReleaseAllControls();
                            _relayStatus.Text = "Relay: Disconnected";
                            _inputStatus.Text =
                                "Input: Relay disconnected - controls released";
                            LogActivity("Relay disconnected; all controls were released.");
                        });
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                HandleRelayMessage(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SafeUi(() =>
            {
                ReleaseAllControls();
                _relayStatus.Text = "Relay: Connection lost";
                _inputStatus.Text = "Input: " + ex.Message;
                LogActivity("Relay connection lost: " + ex.Message);
            });
        }
    }

    private void HandleRelayMessage(string json)
    {
        try
        {
            RelayMessage? relayMessage = JsonSerializer.Deserialize<RelayMessage>(
                json,
                JsonOptions
            );

            if (relayMessage == null)
                return;

            if (string.Equals(relayMessage.Type, "hostCommand", StringComparison.OrdinalIgnoreCase))
            {
                HandleHostCommandResponse(relayMessage);
                return;
            }

            if (!string.Equals(relayMessage.Type, "controller", StringComparison.OrdinalIgnoreCase))
                return;

            ControllerPacket? packet =
                relayMessage.Payload.Deserialize<ControllerPacket>(JsonOptions);

            if (packet == null)
                return;

            if (!string.IsNullOrWhiteSpace(relayMessage.ControllerId))
            {
                lock (_controllerLock)
                {
                    _controllerNames[relayMessage.ControllerId] = CleanPlayerName(packet.PlayerName);
                    if (!string.IsNullOrWhiteSpace(relayMessage.ClientId))
                        _controllerClientIds[relayMessage.ControllerId] = relayMessage.ClientId;
                    _controllerReady[relayMessage.ControllerId] = packet.PlayerReady;
                    if (!_controllerTelemetry.TryGetValue(relayMessage.ControllerId, out ControllerTelemetry? telemetry))
                    {
                        telemetry = new ControllerTelemetry();
                        _controllerTelemetry[relayMessage.ControllerId] = telemetry;
                    }
                    telemetry.RecordPacket(packet.Sequence);
                }
            }

            int? requestedSlot = GetRequestedSlot(relayMessage, packet);
            int? slot = ResolveControllerSlot(relayMessage.ControllerId, requestedSlot);

            if (slot == null)
            {
                SafeUi(() =>
                {
                    _inputStatus.Text =
                        "Input: Four controller slots are already assigned";
                });
                return;
            }

            HandleControllerPacket(slot.Value, packet);
        }
        catch (Exception ex)
        {
            SafeUi(() =>
            {
                _inputStatus.Text = "Input error: " + ex.Message;
                LogActivity("Controller processing error: " + ex.Message);
            });
        }
    }

    private void HandleHostCommandResponse(RelayMessage message)
    {
        SafeUi(() =>
        {
            if (string.Equals(message.Command, "lock", StringComparison.OrdinalIgnoreCase))
            {
                _lockLobbyButton.Enabled = true;
                if (message.Ok == true && message.Locked.HasValue)
                {
                    _lobbyLocked = message.Locked.Value;
                    _lockLobbyButton.Text = _lobbyLocked ? "Unlock" : "Lock";
                    _relayStatus.Text = _lobbyLocked
                        ? "Relay: Connected — lobby locked"
                        : "Relay: Connected — lobby open";
                    LogActivity(_lobbyLocked ? "Lobby locked." : "Lobby unlocked.");
                }
                else
                {
                    _lockLobbyButton.Text = _lobbyLocked ? "Unlock" : "Lock";
                    _relayStatus.Text = "Relay: Lock command failed";
                }
                return;
            }

            if (string.Equals(message.Command, "kick", StringComparison.OrdinalIgnoreCase))
            {
                string name = _pendingKickName ?? "Player";
                if (message.Ok == true)
                {
                    _inputStatus.Text = $"{name} was removed by the relay.";
                    LogActivity($"{name} was removed from the room.");
                }
                else
                    _inputStatus.Text = "Remove failed: " +
                        (string.IsNullOrWhiteSpace(message.Error)
                            ? "the relay could not find that player."
                            : message.Error);

                _pendingKickControllerId = null;
                _pendingKickName = null;
                return;
            }

            if (string.Equals(message.Command, "ban", StringComparison.OrdinalIgnoreCase))
            {
                string name = _pendingBanName ?? "Player";
                if (message.Ok == true && !string.IsNullOrWhiteSpace(message.ClientId))
                {
                    _bannedPlayers[message.ClientId] = name;
                    _bannedReasons[message.ClientId] = string.IsNullOrWhiteSpace(message.Reason)
                        ? _pendingBanReason ?? "No reason provided"
                        : message.Reason;
                    _inputStatus.Text = $"{name} is banned until this room closes.";
                    LogActivity($"{name} was banned. Reason: {_bannedReasons[message.ClientId]}");
                }
                else
                    _inputStatus.Text = "Ban failed: " +
                        (string.IsNullOrWhiteSpace(message.Error) ? "player not found." : message.Error);
                _pendingBanName = null;
                _pendingBanReason = null;
                return;
            }

            if (string.Equals(message.Command, "unban", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Ok == true && !string.IsNullOrWhiteSpace(message.ClientId))
                {
                    string name = _bannedPlayers.TryGetValue(message.ClientId, out string? value)
                        ? value : "Player";
                    _bannedPlayers.Remove(message.ClientId);
                    _bannedReasons.Remove(message.ClientId);
                    _inputStatus.Text = $"{name} may join this room again.";
                    LogActivity($"{name} was unbanned.");
                }
                else
                    _inputStatus.Text = "Unban failed: " + message.Error;
                _pendingUnbanClientId = null;
            }
        });
    }

    private static int? GetRequestedSlot(
        RelayMessage relayMessage,
        ControllerPacket packet)
    {
        int? slot =
            relayMessage.Slot ??
            relayMessage.PlayerSlot ??
            relayMessage.Player ??
            packet.Slot ??
            packet.PlayerSlot ??
            packet.Player;

        return slot is >= 1 and <= PlayerCount ? slot - 1 : null;
    }

    private int? ResolveControllerSlot(string controllerId, int? requestedSlot)
    {
        lock (_controllerLock)
        {
            if (!string.IsNullOrWhiteSpace(controllerId) &&
                _controllerSlots.TryGetValue(controllerId, out int existingSlot))
            {
                return existingSlot;
            }

            int? slot = requestedSlot;

            if (slot != null &&
                _slotControllerIds[slot.Value] != null &&
                !string.Equals(
                    _slotControllerIds[slot.Value],
                    controllerId,
                    StringComparison.OrdinalIgnoreCase))
            {
                slot = null;
            }

            if (slot == null)
            {
                for (int index = 0; index < PlayerCount; index++)
                {
                    if (_slotControllerIds[index] == null)
                    {
                        slot = index;
                        break;
                    }
                }
            }

            if (slot == null)
                return null;

            // The original relay/client path may omit ControllerId. Keep that
            // proven single-controller traffic on P1 without reserving more slots.
            if (string.IsNullOrWhiteSpace(controllerId))
                return requestedSlot ?? 0;

            _controllerSlots[controllerId] = slot.Value;
            _slotControllerIds[slot.Value] = controllerId;
            string joinedName = _controllerNames.TryGetValue(controllerId, out string? value) ? value : "Player";
            LogActivity($"{joinedName} joined and was assigned to P{slot.Value + 1}.");
            return slot;
        }
    }

    private void HandleControllerPacket(int slot, ControllerPacket packet)
    {
        string requestedMode;
        lock (_outputLock)
        {
            requestedMode = string.Equals(packet.OutputMode, "playstation", StringComparison.OrdinalIgnoreCase)
                ? "playstation"
                : "xbox";

            EnsureControllerMode(slot, requestedMode);

            lock (_controllerLock)
            {
                _lastPacketTimes[slot] = DateTime.UtcNow;
                _lastPackets[slot] = packet;
            }

            if (requestedMode == "playstation")
                ApplyDualShockPacket(slot, packet);
            else
                ApplyXboxPacket(slot, packet);
        }

        SafeUi(() =>
        {
            UpdateControllerStatus();
            string name = PlayerNameForSlot(slot);
            _inputStatus.Text =
                $"Input: P{slot + 1} {name} ({(requestedMode == "playstation" ? "DS4" : "Xbox 360")})   " +
                $"LX {packet.LX:F2}  LY {packet.LY:F2}  " +
                $"LT {packet.LT:F2}  RT {packet.RT:F2}";
        });
    }

    private void EnsureControllerMode(int slot, string mode)
    {
        if (_controllerModes[slot] == mode &&
            (mode == "xbox" ? _virtualControllers[slot] != null : _dualShockControllers[slot] != null))
            return;

        ReleaseAllControls(slot);

        try { _virtualControllers[slot]?.Disconnect(); } catch { }
        try { _dualShockControllers[slot]?.Disconnect(); } catch { }
        _virtualControllers[slot] = null;
        _dualShockControllers[slot] = null;

        if (mode == "playstation")
        {
            IDualShock4Controller controller = _vigemClient!.CreateDualShock4Controller();
            controller.Connect();
            _dualShockControllers[slot] = controller;
        }
        else
        {
            IXbox360Controller controller = _vigemClient!.CreateXbox360Controller();
            controller.Connect();
            _virtualControllers[slot] = controller;
        }

        _controllerModes[slot] = mode;
    }

    private void ApplyXboxPacket(int slot, ControllerPacket packet)
    {
        IXbox360Controller? controller = _virtualControllers[slot];
        if (controller == null)
            return;

        SetButton(controller, Xbox360Button.A, packet.A);
        SetButton(controller, Xbox360Button.B, packet.B);
        SetButton(controller, Xbox360Button.X, packet.X);
        SetButton(controller, Xbox360Button.Y, packet.Y);
        SetButton(controller, Xbox360Button.LeftShoulder, packet.LB);
        SetButton(controller, Xbox360Button.RightShoulder, packet.RB);
        SetButton(controller, Xbox360Button.Start, packet.Start);
        SetButton(controller, Xbox360Button.Back, packet.Back);
        SetButton(controller, Xbox360Button.LeftThumb, packet.LS);
        SetButton(controller, Xbox360Button.RightThumb, packet.RS);
        SetButton(controller, Xbox360Button.Up, packet.Up);
        SetButton(controller, Xbox360Button.Down, packet.Down);
        SetButton(controller, Xbox360Button.Left, packet.Left);
        SetButton(controller, Xbox360Button.Right, packet.Right);

        controller.SetAxisValue(Xbox360Axis.LeftThumbX, AxisToShort(packet.LX));
        controller.SetAxisValue(Xbox360Axis.LeftThumbY, AxisToShort(packet.LY));
        controller.SetAxisValue(Xbox360Axis.RightThumbX, AxisToShort(packet.RX));
        controller.SetAxisValue(Xbox360Axis.RightThumbY, AxisToShort(packet.RY));
        controller.SetSliderValue(Xbox360Slider.LeftTrigger, TriggerToByte(packet.LT));
        controller.SetSliderValue(Xbox360Slider.RightTrigger, TriggerToByte(packet.RT));
    }

    private void ApplyDualShockPacket(int slot, ControllerPacket packet)
    {
        IDualShock4Controller? controller = _dualShockControllers[slot];
        if (controller == null)
            return;

        controller.SetButtonState(DualShock4Button.Cross, packet.A);
        controller.SetButtonState(DualShock4Button.Circle, packet.B);
        controller.SetButtonState(DualShock4Button.Square, packet.X);
        controller.SetButtonState(DualShock4Button.Triangle, packet.Y);
        controller.SetButtonState(DualShock4Button.ShoulderLeft, packet.LB);
        controller.SetButtonState(DualShock4Button.ShoulderRight, packet.RB);
        controller.SetButtonState(DualShock4Button.Share, packet.Back);
        controller.SetButtonState(DualShock4Button.Options, packet.Start);
        controller.SetButtonState(DualShock4Button.ThumbLeft, packet.LS);
        controller.SetButtonState(DualShock4Button.ThumbRight, packet.RS);
        controller.SetDPadDirection(GetDualShockDPad(packet));
        controller.SetAxisValue(DualShock4Axis.LeftThumbX, AxisToByte(packet.LX));
        controller.SetAxisValue(DualShock4Axis.LeftThumbY, AxisToByte(-packet.LY));
        controller.SetAxisValue(DualShock4Axis.RightThumbX, AxisToByte(packet.RX));
        controller.SetAxisValue(DualShock4Axis.RightThumbY, AxisToByte(-packet.RY));
        controller.SetSliderValue(DualShock4Slider.LeftTrigger, TriggerToByte(packet.LT));
        controller.SetSliderValue(DualShock4Slider.RightTrigger, TriggerToByte(packet.RT));
        controller.SetSpecialButtonsFull((byte)((packet.PS ? 1 : 0) | (packet.Touchpad ? 2 : 0)));
    }

    private static DualShock4DPadDirection GetDualShockDPad(ControllerPacket packet)
    {
        if (packet.Up && packet.Left) return DualShock4DPadDirection.Northwest;
        if (packet.Up && packet.Right) return DualShock4DPadDirection.Northeast;
        if (packet.Down && packet.Left) return DualShock4DPadDirection.Southwest;
        if (packet.Down && packet.Right) return DualShock4DPadDirection.Southeast;
        if (packet.Up) return DualShock4DPadDirection.North;
        if (packet.Down) return DualShock4DPadDirection.South;
        if (packet.Left) return DualShock4DPadDirection.West;
        if (packet.Right) return DualShock4DPadDirection.East;
        return DualShock4DPadDirection.None;
    }

    private void WatchdogTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;

        for (int slot = 0; slot < PlayerCount; slot++)
        {
            DateTime lastPacketTime;
            string disconnectedName;
            lock (_controllerLock)
            {
                lastPacketTime = _lastPacketTimes[slot];
                string? id = _slotControllerIds[slot];
                disconnectedName = id != null && _controllerNames.TryGetValue(id, out string? value)
                    ? value : $"P{slot + 1}";
            }

            if (lastPacketTime == DateTime.MinValue ||
                (now - lastPacketTime).TotalMilliseconds <= 750)
            {
                continue;
            }

            ReleaseAllControls(slot);

            UnassignControllerSlot(slot);

            _inputStatus.Text =
                $"Input: P{slot + 1} disconnected - controls released";
            LogActivity($"{disconnectedName} disconnected from P{slot + 1}; controls released.");

            UpdateControllerStatus();
        }
    }

    private void UnassignControllerSlot(int slot)
    {
        lock (_controllerLock)
        {
            string? controllerId = _slotControllerIds[slot];

            if (!string.IsNullOrWhiteSpace(controllerId))
            {
                _controllerSlots.Remove(controllerId);
                _controllerNames.Remove(controllerId);
                _controllerReady.Remove(controllerId);
                _controllerTelemetry.Remove(controllerId);
                _controllerClientIds.Remove(controllerId);
            }

            _slotControllerIds[slot] = null;
            _lastPacketTimes[slot] = DateTime.MinValue;
            _lastPackets[slot] = null;
        }
    }

    private static void SetButton(
        IXbox360Controller controller,
        Xbox360Button button,
        bool pressed)
    {
        controller.SetButtonState(button, pressed);
    }

    private static short AxisToShort(double value)
    {
        value = Math.Clamp(value, -1.0, 1.0);
        return value >= 0
            ? (short)(value * short.MaxValue)
            : (short)(value * 32768);
    }

    private static byte TriggerToByte(double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        return (byte)(value * 255);
    }

    private static byte AxisToByte(double value)
    {
        value = Math.Clamp(value, -1.0, 1.0);
        return (byte)Math.Round((value + 1.0) * 127.5);
    }

    private void ReleaseAllControls()
    {
        for (int slot = 0; slot < PlayerCount; slot++)
            ReleaseAllControls(slot);
    }

    private void ReleaseAllControls(int slot)
    {
        IXbox360Controller? controller = _virtualControllers[slot];

        Xbox360Button[] buttons =
        {
            Xbox360Button.A,
            Xbox360Button.B,
            Xbox360Button.X,
            Xbox360Button.Y,
            Xbox360Button.LeftShoulder,
            Xbox360Button.RightShoulder,
            Xbox360Button.Start,
            Xbox360Button.Back,
            Xbox360Button.LeftThumb,
            Xbox360Button.RightThumb,
            Xbox360Button.Up,
            Xbox360Button.Down,
            Xbox360Button.Left,
            Xbox360Button.Right
        };

        if (controller != null)
        {
            foreach (Xbox360Button button in buttons)
                controller.SetButtonState(button, false);

            controller.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
            controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
            controller.SetAxisValue(Xbox360Axis.RightThumbX, 0);
            controller.SetAxisValue(Xbox360Axis.RightThumbY, 0);
            controller.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
            controller.SetSliderValue(Xbox360Slider.RightTrigger, 0);
        }

        IDualShock4Controller? dualShock = _dualShockControllers[slot];
        if (dualShock != null)
        {
            foreach (DualShock4Button button in new[]
            {
                DualShock4Button.Cross, DualShock4Button.Circle,
                DualShock4Button.Square, DualShock4Button.Triangle,
                DualShock4Button.ShoulderLeft, DualShock4Button.ShoulderRight,
                DualShock4Button.Share, DualShock4Button.Options,
                DualShock4Button.ThumbLeft, DualShock4Button.ThumbRight
            }) dualShock.SetButtonState(button, false);

            dualShock.SetDPadDirection(DualShock4DPadDirection.None);
            dualShock.SetAxisValue(DualShock4Axis.LeftThumbX, 128);
            dualShock.SetAxisValue(DualShock4Axis.LeftThumbY, 128);
            dualShock.SetAxisValue(DualShock4Axis.RightThumbX, 128);
            dualShock.SetAxisValue(DualShock4Axis.RightThumbY, 128);
            dualShock.SetSliderValue(DualShock4Slider.LeftTrigger, 0);
            dualShock.SetSliderValue(DualShock4Slider.RightTrigger, 0);
            dualShock.SetSpecialButtonsFull(0);
        }
    }

    private void UpdateControllerStatus()
    {
        var statuses = new string[PlayerCount];

        lock (_controllerLock)
        {
            for (int slot = 0; slot < PlayerCount; slot++)
            {
                string virtualState =
                    (_virtualControllers[slot] == null && _dualShockControllers[slot] == null)
                        ? "off"
                        : (_controllerModes[slot] == "playstation" ? "DS4 ready" : "Xbox ready");
                string? controllerId = _slotControllerIds[slot];
                string remoteState = controllerId == null
                    ? "waiting"
                    : _controllerNames.TryGetValue(controllerId, out string? name)
                        ? name
                        : "Player";

                statuses[slot] = $"P{slot + 1}: {virtualState}, {remoteState}";

                if (_lobbyNameLabels[slot] != null)
                {
                    bool connected = controllerId != null;
                    bool ready = connected && _controllerReady.TryGetValue(controllerId!, out bool isReady) && isReady;
                    string quality = connected && _controllerTelemetry.TryGetValue(controllerId!, out ControllerTelemetry? telemetry)
                        ? telemetry.QualityText
                        : "--";
                    _lobbyNameLabels[slot].Text = connected ? remoteState : "Open Seat";
                    _lobbyTypeLabels[slot].Text = virtualState == "off"
                        ? "Virtual: Off"
                        : _controllerModes[slot] == "playstation"
                            ? "DualShock 4"
                            : "Xbox 360";
                    _lobbyStateLabels[slot].Text = connected
                        ? ready ? "✓ Ready" : "● Not Ready"
                        : "○ Waiting";
                    _lobbyStateLabels[slot].ForeColor = connected
                        ? ready ? Color.LimeGreen : _themeAccent
                        : Color.Gray;
                    _lobbyQualityLabels[slot].Text = "Signal: " + quality;
                    _lobbyCards[slot].AccessibleName = $"Player {slot + 1}, {_lobbyNameLabels[slot].Text}, {_lobbyTypeLabels[slot].Text}, {_lobbyStateLabels[slot].Text}, signal {quality}";
                }
            }
        }

        _controllerStatus.Text = string.Join("   |   ", statuses);
    }

    private static string CleanPlayerName(string? value)
    {
        string cleaned = new((value ?? "")
            .Where(character => !char.IsControl(character))
            .Take(24)
            .ToArray());
        cleaned = cleaned.Trim();
        return cleaned.Length == 0 ? "Player" : cleaned;
    }

    private string PlayerNameForSlot(int slot)
    {
        lock (_controllerLock)
        {
            string? id = _slotControllerIds[slot];
            return id != null && _controllerNames.TryGetValue(id, out string? name)
                ? name
                : "Player";
        }
    }

    private void CopyInviteLink()
    {
        string room = _roomCodeInput.Text.Trim().ToUpperInvariant();
        if (room.Length == 0)
        {
            MessageBox.Show(this, "Enter a room code first.", "Copy Invite");
            return;
        }

        string invite = $"https://controller.rommserver.org/?room={Uri.EscapeDataString(room)}";
        Clipboard.SetText(invite);
        MessageBox.Show(
            this,
            "The controller-page invitation was copied.\r\n\r\nFor safety, send the room password separately.",
            "Invite Copied");
    }

    private async Task SendHostCommandAsync(object command)
    {
        if (_relaySocket?.State != WebSocketState.Open)
            throw new InvalidOperationException("Connect the host to the relay first.");

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command));
        await _hostCommandSendLock.WaitAsync();
        try
        {
            await _relaySocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
        finally
        {
            _hostCommandSendLock.Release();
        }
    }

    private async Task ToggleLobbyLockAsync()
    {
        try
        {
            bool requested = !_lobbyLocked;
            _lockLobbyButton.Enabled = false;
            _lockLobbyButton.Text = "Waiting...";
            _relayStatus.Text = "Relay: Waiting for lock confirmation...";
            await SendHostCommandAsync(new { type = "lock", locked = requested });

            await Task.Delay(2500);
            if (!_lockLobbyButton.Enabled)
            {
                _lockLobbyButton.Enabled = true;
                _lockLobbyButton.Text = _lobbyLocked ? "Unlock" : "Lock";
                _relayStatus.Text = "Relay: Lock not confirmed — update the relay";
            }
        }
        catch (Exception ex)
        {
            _lockLobbyButton.Enabled = true;
            _lockLobbyButton.Text = _lobbyLocked ? "Unlock" : "Lock";
            MessageBox.Show(this, ex.Message, "Lobby Lock");
        }
    }

    private void ShowManagePlayersDialog()
    {
        List<PlayerChoice> players;
        lock (_controllerLock)
        {
            players = _controllerSlots
                .OrderBy(pair => pair.Value)
                .Select(pair => new PlayerChoice(
                    pair.Key,
                    pair.Value,
                    _controllerNames.TryGetValue(pair.Key, out string? name) ? name : "Player"))
                .ToList();
        }

        if (players.Count == 0)
        {
            if (_bannedPlayers.Count > 0)
                ShowBannedPlayersDialog();
            else
                MessageBox.Show(this, "No remote players are currently connected.", "Manage Players");
            return;
        }

        using var dialog = new Form
        {
            Text = "Manage Players",
            Width = 560,
            Height = 285,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var help = new Label
        {
            Text = "Choose a connected player and move them to P1-P4.\r\nIf that slot is occupied, the two players will swap places.",
            Left = 20, Top = 15, Width = 420, Height = 45
        };
        var playerSelector = new ComboBox
        {
            Left = 20, Top = 70, Width = 260,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        playerSelector.Items.AddRange(players.Cast<object>().ToArray());
        playerSelector.SelectedIndex = 0;

        var slotSelector = new ComboBox
        {
            Left = 295, Top = 70, Width = 130,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        slotSelector.Items.AddRange(new object[] { "Player 1", "Player 2", "Player 3", "Player 4" });
        slotSelector.SelectedIndex = players[0].Slot;
        playerSelector.SelectedIndexChanged += (_, _) =>
        {
            if (playerSelector.SelectedItem is PlayerChoice selected)
                slotSelector.SelectedIndex = selected.Slot;
        };

        var apply = new Button
        {
            Text = "Move / Swap", Left = 55, Top = 125, Width = 135, Height = 42,
            DialogResult = DialogResult.OK
        };
        var remove = new Button
        {
            Text = "Remove Player", Left = 205, Top = 125, Width = 135, Height = 42,
            DialogResult = DialogResult.Abort
        };
        var ban = new Button
        {
            Text = "Ban From Room", Left = 355, Top = 125, Width = 150, Height = 42,
            DialogResult = DialogResult.Retry
        };
        var unban = new Button
        {
            Text = "Unban Players", Left = 205, Top = 180, Width = 135, Height = 36,
            DialogResult = DialogResult.Ignore
        };
        dialog.Controls.AddRange(new Control[] { help, playerSelector, slotSelector, apply, remove, ban, unban });
        dialog.AcceptButton = apply;

        DialogResult result = dialog.ShowDialog(this);
        if (result == DialogResult.OK &&
            playerSelector.SelectedItem is PlayerChoice choice && slotSelector.SelectedIndex >= 0)
        {
            MoveControllerToSlot(choice.ControllerId, slotSelector.SelectedIndex);
        }
        else if (result == DialogResult.Abort && playerSelector.SelectedItem is PlayerChoice removed)
        {
            _ = RemoveControllerAsync(removed.ControllerId, removed.Name);
        }
        else if (result == DialogResult.Retry && playerSelector.SelectedItem is PlayerChoice banned)
        {
            _ = BanControllerAsync(banned.ControllerId, banned.Name);
        }
        else if (result == DialogResult.Ignore)
        {
            ShowBannedPlayersDialog();
        }
    }

    private async Task BanControllerAsync(string controllerId, string name)
    {
        string? reason = PromptForBanReason(name);
        if (reason == null)
            return;

        if (MessageBox.Show(this,
            $"Ban {name} from this room?\r\n\r\nReason: {reason}\r\n\r\nThey cannot reconnect until you unban them or close the Host room.",
            "Ban From Room", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try
        {
            _pendingBanName = name;
            _pendingBanReason = reason;
            _inputStatus.Text = $"Waiting for the relay to ban {name}...";
            await SendHostCommandAsync(new { type = "ban", controllerId, reason });
            await Task.Delay(2500);
            if (_pendingBanName != null)
            {
                _pendingBanName = null;
                _pendingBanReason = null;
                _inputStatus.Text = "Ban not confirmed — update the public relay, then try again.";
            }
        }
        catch (Exception ex)
        {
            _pendingBanName = null;
            _pendingBanReason = null;
            MessageBox.Show(this, ex.Message, "Ban From Room");
        }
    }

    private string? PromptForBanReason(string name)
    {
        using var dialog = new Form
        {
            Text = "Reason For Ban", Width = 480, Height = 230,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var label = new Label
        {
            Text = $"Why is {name} being banned?\r\nThe player will see this message. (60 characters maximum)",
            Left = 25, Top = 20, Width = 420, Height = 45
        };
        var input = new TextBox
        {
            Left = 25, Top = 70, Width = 420, MaxLength = 60,
            Text = "Host removed you from this room"
        };
        var accept = new Button
        {
            Text = "Continue", Left = 125, Top = 120, Width = 105,
            Height = 38, DialogResult = DialogResult.OK
        };
        var cancel = new Button
        {
            Text = "Cancel", Left = 245, Top = 120, Width = 105,
            Height = 38, DialogResult = DialogResult.Cancel
        };
        dialog.Controls.AddRange(new Control[] { label, input, accept, cancel });
        dialog.AcceptButton = accept;
        dialog.CancelButton = cancel;
        input.SelectAll();
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return null;
        string cleaned = new string(input.Text.Where(character => !char.IsControl(character)).Take(60).ToArray()).Trim();
        return cleaned.Length == 0 ? "No reason provided" : cleaned;
    }

    private void ShowBannedPlayersDialog()
    {
        if (_bannedPlayers.Count == 0)
        {
            MessageBox.Show(this, "No players are banned from this room.", "Unban Players");
            return;
        }

        using var dialog = new Form
        {
            Text = "Unban Players", Width = 430, Height = 190,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var selector = new ComboBox
        {
            Left = 30, Top = 30, Width = 350,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        foreach (KeyValuePair<string, string> player in _bannedPlayers)
            selector.Items.Add(new BannedPlayerChoice(
                player.Key,
                player.Value,
                _bannedReasons.TryGetValue(player.Key, out string? reason) ? reason : "No reason provided"));
        selector.SelectedIndex = 0;
        var button = new Button
        {
            Text = "Allow Player To Join", Left = 115, Top = 80,
            Width = 190, Height = 40, DialogResult = DialogResult.OK
        };
        dialog.Controls.AddRange(new Control[] { selector, button });
        dialog.AcceptButton = button;
        if (dialog.ShowDialog(this) == DialogResult.OK &&
            selector.SelectedItem is BannedPlayerChoice selected)
            _ = UnbanPlayerAsync(selected.ClientId);
    }

    private async Task UnbanPlayerAsync(string clientId)
    {
        try
        {
            _pendingUnbanClientId = clientId;
            await SendHostCommandAsync(new { type = "unban", clientId });
            await Task.Delay(2500);
            if (string.Equals(_pendingUnbanClientId, clientId, StringComparison.OrdinalIgnoreCase))
            {
                _pendingUnbanClientId = null;
                _inputStatus.Text = "Unban not confirmed — update the public relay, then try again.";
            }
        }
        catch (Exception ex)
        {
            _pendingUnbanClientId = null;
            MessageBox.Show(this, ex.Message, "Unban Player");
        }
    }

    private async Task RemoveControllerAsync(string controllerId, string name)
    {
        if (MessageBox.Show(this, $"Remove {name} from this room?", "Remove Player",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            _pendingKickControllerId = controllerId;
            _pendingKickName = name;
            _inputStatus.Text = $"Waiting for the relay to remove {name}...";
            await SendHostCommandAsync(new { type = "kick", controllerId });

            await Task.Delay(2500);
            if (string.Equals(_pendingKickControllerId, controllerId,
                StringComparison.OrdinalIgnoreCase))
            {
                _pendingKickControllerId = null;
                _pendingKickName = null;
                _inputStatus.Text =
                    "Remove not confirmed — update the public relay, then try again.";
            }
        }
        catch (Exception ex)
        {
            _pendingKickControllerId = null;
            _pendingKickName = null;
            MessageBox.Show(this, ex.Message, "Remove Player");
        }
    }

    private void MoveControllerToSlot(string controllerId, int targetSlot)
    {
        lock (_outputLock)
        {
            int oldSlot;
            string? displacedController;
            lock (_controllerLock)
            {
                if (!_controllerSlots.TryGetValue(controllerId, out oldSlot) || oldSlot == targetSlot)
                    return;
                displacedController = _slotControllerIds[targetSlot];
            }

            ReleaseAllControls(oldSlot);
            ReleaseAllControls(targetSlot);

            lock (_controllerLock)
            {
                _slotControllerIds[targetSlot] = controllerId;
                _controllerSlots[controllerId] = targetSlot;
                _lastPacketTimes[targetSlot] = DateTime.UtcNow;
                (_lastPackets[targetSlot], _lastPackets[oldSlot]) =
                    (_lastPackets[oldSlot], _lastPackets[targetSlot]);

                _slotControllerIds[oldSlot] = displacedController;
                _lastPacketTimes[oldSlot] = displacedController == null
                    ? DateTime.MinValue
                    : DateTime.UtcNow;
                if (displacedController != null)
                    _controllerSlots[displacedController] = oldSlot;
            }
        }

        UpdateControllerStatus();
        _inputStatus.Text = $"Player assignment updated: {PlayerNameForSlot(targetSlot)} moved to P{targetSlot + 1}";
        LogActivity($"Player slots changed; {PlayerNameForSlot(targetSlot)} is now P{targetSlot + 1}.");
    }

    private async Task StopHostAsync()
    {
        LogActivity("Host shutdown started; releasing controllers and room state.");
        ReleaseAllControls();
        _lobbyLocked = false;
        if (_lockLobbyButton != null)
            _lockLobbyButton.Text = "Lock";

        lock (_controllerLock)
        {
            _controllerSlots.Clear();
            _controllerNames.Clear();
            _controllerReady.Clear();
            _controllerTelemetry.Clear();
            _controllerClientIds.Clear();
            _bannedPlayers.Clear();
            _bannedReasons.Clear();
            Array.Fill(_slotControllerIds, null);
            Array.Fill(_lastPacketTimes, DateTime.MinValue);
            Array.Fill(_lastPackets, null);
        }

        try
        {
            _relayCancellation?.Cancel();
        }
        catch
        {
        }

        if (_relaySocket != null)
        {
            try
            {
                if (_relaySocket.State == WebSocketState.Open)
                {
                    await _relaySocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Host disconnected",
                        CancellationToken.None
                    );
                }
            }
            catch
            {
            }

            _relaySocket.Dispose();
            _relaySocket = null;
        }

        _relayCancellation?.Dispose();
        _relayCancellation = null;

        for (int slot = 0; slot < PlayerCount; slot++)
        {
            try { _virtualControllers[slot]?.Disconnect(); } catch { }
            try { _dualShockControllers[slot]?.Disconnect(); } catch { }

            _virtualControllers[slot] = null;
            _dualShockControllers[slot] = null;
            _controllerModes[slot] = "xbox";
        }

        _vigemClient?.Dispose();
        _vigemClient = null;

        _relayStatus.Text = "Relay: Disconnected";
        _controllerStatus.Text = "Virtual P1-P4: Disconnected";
        _inputStatus.Text = "Input: Waiting";
        LogActivity("Host disconnected cleanly.");
    }

    private async void FormClosingSafely(object? sender, FormClosingEventArgs e)
    {
        if (_shutdownComplete)
            return;

        e.Cancel = true;
        _watchdogTimer.Stop();
        try
        {
            Enabled = false;
            await StopHostAsync();
        }
        catch
        {
        }
        _shutdownComplete = true;
        Close();
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch
            {
            }
        }
        else
        {
            action();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public class RelayMessage
{
    public string Type { get; set; } = "";
    public string Command { get; set; } = "";
    public string ControllerId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public bool? Ok { get; set; }
    public bool? Locked { get; set; }
    public string Error { get; set; } = "";
    public string Reason { get; set; } = "";
    public int? Slot { get; set; }
    public int? PlayerSlot { get; set; }
    public int? Player { get; set; }
    public JsonElement Payload { get; set; }
}

internal sealed class BannedPlayerChoice
{
    public BannedPlayerChoice(string clientId, string name, string reason)
    {
        ClientId = clientId;
        Name = name;
        Reason = reason;
    }

    public string ClientId { get; }
    public string Name { get; }
    public string Reason { get; }
    public override string ToString() => $"{Name} — {Reason}";
}

public class ControllerPacket
{
    public string OutputMode { get; set; } = "xbox";
    public string PlayerName { get; set; } = "Player";
    public bool PlayerReady { get; set; }
    public long Sequence { get; set; }
    public bool PS { get; set; }
    public bool Touchpad { get; set; }
    public int? Slot { get; set; }
    public int? PlayerSlot { get; set; }
    public int? Player { get; set; }

    public bool A { get; set; }
    public bool B { get; set; }
    public bool X { get; set; }
    public bool Y { get; set; }
    public bool LB { get; set; }
    public bool RB { get; set; }
    public bool Back { get; set; }
    public bool Start { get; set; }
    public bool LS { get; set; }
    public bool RS { get; set; }
    public bool Up { get; set; }
    public bool Down { get; set; }
    public bool Left { get; set; }
    public bool Right { get; set; }
    public double LX { get; set; }
    public double LY { get; set; }
    public double RX { get; set; }
    public double RY { get; set; }
    public double LT { get; set; }
    public double RT { get; set; }
}

internal sealed class PlayerChoice
{
    public string ControllerId { get; }
    public int Slot { get; }
    public string Name { get; }

    public PlayerChoice(string controllerId, int slot, string name)
    {
        ControllerId = controllerId;
        Slot = slot;
        Name = name;
    }

    public override string ToString()
    {
        string shortId = ControllerId.Length > 4 ? ControllerId[^4..] : ControllerId;
        return $"{Name} (P{Slot + 1}, {shortId})";
    }
}

internal sealed class ControllerTelemetry
{
    private DateTime _windowStarted = DateTime.UtcNow;
    private int _packetsInWindow;
    private double _packetsPerSecond;

    public void RecordPacket(long sequence)
    {
        _packetsInWindow++;
        DateTime now = DateTime.UtcNow;
        double seconds = (now - _windowStarted).TotalSeconds;
        if (seconds < 1)
            return;

        _packetsPerSecond = _packetsInWindow / seconds;
        _packetsInWindow = 0;
        _windowStarted = now;
    }

    public string QualityText => _packetsPerSecond <= 0
        ? "Starting"
        : _packetsPerSecond >= 45
            ? $"Excellent ({_packetsPerSecond:F0}/s)"
            : _packetsPerSecond >= 25
                ? $"Good ({_packetsPerSecond:F0}/s)"
                : $"Slow ({_packetsPerSecond:F0}/s)";
}
