using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemotePlayForge
{
    // RemotePlayForge — force Steam Remote Play Together on the game you're running, invite a
    // friend, and automatically hand them control. The GUI is a thin, friendly front-end: it
    // enumerates your online friends via Steam's internal client interface, and launches the
    // out-of-process session helper (RPForgeSession.exe) that performs the proven invite +
    // group-create + auto-grant-control sequence and holds the session open.
    public class MainForm : Form
    {
        // ---- palette ----
        static readonly Color C_BG       = Color.FromArgb(0x12, 0x15, 0x1C);
        static readonly Color C_CARD     = Color.FromArgb(0x1B, 0x21, 0x2B);
        static readonly Color C_CARD_HI  = Color.FromArgb(0x23, 0x2B, 0x38);
        static readonly Color C_BORDER   = Color.FromArgb(0x2C, 0x35, 0x44);
        static readonly Color C_TEXT     = Color.FromArgb(0xE7, 0xEC, 0xF2);
        static readonly Color C_MUTED    = Color.FromArgb(0x8B, 0x97, 0xA6);
        static readonly Color C_ACCENT   = Color.FromArgb(0x3E, 0xA6, 0xFF);
        static readonly Color C_ACCENT_HI= Color.FromArgb(0x5C, 0xB8, 0xFF);
        static readonly Color C_GREEN    = Color.FromArgb(0x37, 0xD6, 0x7A);
        static readonly Color C_AMBER    = Color.FromArgb(0xF2, 0xC3, 0x4C);
        static readonly Color C_RED      = Color.FromArgb(0xEB, 0x5A, 0x5A);

        SteamInternalForce _live;               // persistent connection, used to list friends
        System.Diagnostics.Process _session;    // the running session helper
        string _currentFriend = "your friend";

        NotifyIcon _tray;
        ComboBox _cboFriends;
        Button _btnRefreshFriends, _btnInvite, _btnStop, _btnGenLink, _btnCopyLink;
        Button _tglKbd, _tglMouse, _tglCtrl, _btnTakeOver, _btnHandBack, _btnHotkeys;
        Panel _controlCard;
        TextBox _txtLog, _txtLink;
        Label _dotSteam, _lblSteam, _dotGame, _lblGame, _lblBanner, _lblHotkeys;
        Panel _banner;
        Timer _status;

        // Desired guest-input state, mirrored into the helper over stdin.
        bool _kbdOn = true, _mouseOn = true, _ctrlOn = true;
        string _activeLink;   // the current shareable guest link, if any

        readonly string _exeDir;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
        const int HK_DRIVE = 1, HK_GIVE = 2, HK_KBD = 3, HK_MOUSE = 4, HK_CTRL = 5, HK_NEXT = 6;
        static readonly int[] HK_IDS = { HK_DRIVE, HK_GIVE, HK_KBD, HK_MOUSE, HK_CTRL, HK_NEXT };

        // A user-rebindable global hotkey (modifiers + virtual key).
        class Hotkey { public uint Mods; public uint Vk; }
        Dictionary<int, Hotkey> _hotkeys;   // id -> binding, persisted to hotkeys.cfg

        // ---- connected-players roster ----
        // One row per guest currently connected. Slot matches the helper's PLAYER slot=N.
        class PlayerRow
        {
            public int Slot; public ulong Steam; public string Name;
            public bool Kbd = true, Mouse = true, Ctrl = true;
            public Panel Panel; public Label NameLbl; public Button BK, BM, BC;
        }
        Dictionary<int, PlayerRow> _rows = new Dictionary<int, PlayerRow>();
        FlowLayoutPanel _rosterPanel;
        Label _lblRoster, _lblTarget;
        int _selectedSlot = 0;   // hotkey target: 0 = Everyone, else a player slot

        public MainForm()
        {
            _exeDir = AppDomain.CurrentDomain.BaseDirectory;
            LoadHotkeys();
            BuildUi();
            _status = new Timer(); _status.Interval = 1500; _status.Tick += delegate { UpdateStatus(); }; _status.Start();
            UpdateStatus();
            Shown += delegate { RefreshFriends(); };
            Log("Welcome to RemotePlayForge. Launch your game, pick a friend, and hit Invite & Give Control.");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Win11/10 immersive dark title bar.
            try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); DwmSetWindowAttribute(Handle, 19, ref on, 4); } catch { }
            // Global in-game hotkeys — user-rebindable (defaults: Ctrl+Alt+ D/G/K/M/C).
            RegisterHotkeys();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) OnHotkey(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        void OnHotkey(int id)
        {
            if (id == HK_NEXT) { CycleTarget(); return; }   // pick which person the hotkeys act on
            if (!ControlLive) { Log("Hotkey ignored — no one is connected yet."); return; }
            if (_selectedSlot == 0)
            {
                switch (id)   // Everyone
                {
                    case HK_DRIVE: SetAllInputs(false); break;
                    case HK_GIVE:  SetAllInputs(true);  break;
                    case HK_KBD:   ToggleInput("KBD");  break;
                    case HK_MOUSE: ToggleInput("MOUSE"); break;
                    case HK_CTRL:  ToggleInput("CTRL"); break;
                }
            }
            else
            {
                if (!_rows.ContainsKey(_selectedSlot)) { _selectedSlot = 0; UpdateTargetLabel(); return; }
                switch (id)   // one specific person
                {
                    case HK_DRIVE: SetPlayerAll(_selectedSlot, false); break;
                    case HK_GIVE:  SetPlayerAll(_selectedSlot, true);  break;
                    case HK_KBD:   TogglePlayerInput(_selectedSlot, "KBD");  break;
                    case HK_MOUSE: TogglePlayerInput(_selectedSlot, "MOUSE"); break;
                    case HK_CTRL:  TogglePlayerInput(_selectedSlot, "CTRL"); break;
                }
            }
        }

        bool ControlLive { get { return _tglKbd != null && _tglKbd.Enabled; } }

        // ------------------------------------------------------------------ UI

        void BuildUi()
        {
            Text = "RemotePlayForge";
            ClientSize = new Size(680, 980);
            MinimumSize = new Size(660, 900);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = C_BG;
            ForeColor = C_TEXT;
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = SystemIcons.Application; } catch { }

            // ---- header ----
            Panel header = new Panel();
            header.SetBounds(0, 0, ClientSize.Width, 92);
            header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (LinearGradientBrush b = new LinearGradientBrush(header.ClientRectangle,
                    Color.FromArgb(0x16, 0x2C, 0x45), C_BG, 20f))
                    e.Graphics.FillRectangle(b, header.ClientRectangle);
                using (Pen p = new Pen(C_BORDER)) e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            Controls.Add(header);

            Label title = new Label();
            title.Text = "⚡  RemotePlayForge";
            title.Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold);
            title.ForeColor = C_TEXT;
            title.AutoSize = true;
            title.BackColor = Color.Transparent;
            title.Location = new Point(22, 18);
            header.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Force Remote Play Together on any game — invite a friend and hand them control.";
            sub.Font = new Font("Segoe UI", 9.5f);
            sub.ForeColor = C_MUTED;
            sub.AutoSize = true;
            sub.BackColor = Color.Transparent;
            sub.Location = new Point(24, 56);
            header.Controls.Add(sub);

            // ---- status card ----
            Panel status = Card(22, 108, ClientSize.Width - 44, 74);
            status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _dotSteam = Dot(18, 18); status.Controls.Add(_dotSteam);
            status.Controls.Add(Caption(38, 15, "STEAM"));
            _lblSteam = Value(38, 32, "—"); status.Controls.Add(_lblSteam);

            _dotGame = Dot(320, 18); status.Controls.Add(_dotGame);
            status.Controls.Add(Caption(340, 15, "GAME"));
            _lblGame = Value(340, 32, "—"); status.Controls.Add(_lblGame);

            // ---- friend card ----
            Panel friends = Card(22, 198, ClientSize.Width - 44, 96);
            friends.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            friends.Controls.Add(Caption(18, 14, "1 · CHOOSE A FRIEND"));

            _cboFriends = new ComboBox();
            _cboFriends.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboFriends.FlatStyle = FlatStyle.Flat;
            _cboFriends.BackColor = C_CARD_HI;
            _cboFriends.ForeColor = C_TEXT;
            _cboFriends.Font = new Font("Segoe UI", 10.5f);
            _cboFriends.SetBounds(18, 40, friends.Width - 200, 30);
            _cboFriends.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            friends.Controls.Add(_cboFriends);

            _btnRefreshFriends = MakeButton("⟳  Refresh", C_CARD_HI, C_BORDER, C_TEXT, false);
            _btnRefreshFriends.SetBounds(friends.Width - 170, 39, 152, 32);
            _btnRefreshFriends.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnRefreshFriends.Click += delegate { RefreshFriends(); };
            friends.Controls.Add(_btnRefreshFriends);

            // ---- primary action ----
            _btnInvite = MakeButton("🎮   Invite && Give Control", C_ACCENT, C_ACCENT_HI, Color.White, true);
            _btnInvite.SetBounds(22, 308, ClientSize.Width - 44 - 140, 56);
            _btnInvite.Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold);
            _btnInvite.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _btnInvite.Click += delegate { InviteSelectedFriend(); };
            Controls.Add(_btnInvite);
            Round(_btnInvite, 12);

            _btnStop = MakeButton("⏹  Stop", C_CARD, C_CARD_HI, C_MUTED, false);
            _btnStop.SetBounds(ClientSize.Width - 22 - 128, 308, 128, 56);
            _btnStop.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnStop.Enabled = false;
            _btnStop.Click += delegate { StopSession(); };
            Controls.Add(_btnStop);
            Round(_btnStop, 12);

            // ---- generate a shareable guest link (no Steam friend needed) ----
            _btnGenLink = MakeButton("🔗   Generate guest link", C_CARD_HI, C_BORDER, C_TEXT, false);
            _btnGenLink.SetBounds(22, 372, ClientSize.Width - 44, 38);
            _btnGenLink.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            _btnGenLink.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _btnGenLink.Click += delegate { GenerateGuestLink(); };
            Controls.Add(_btnGenLink);
            Round(_btnGenLink, 10);

            _txtLink = new TextBox();
            _txtLink.ReadOnly = true;
            _txtLink.BorderStyle = BorderStyle.FixedSingle;
            _txtLink.BackColor = Color.FromArgb(0x0E, 0x11, 0x17);
            _txtLink.ForeColor = C_ACCENT_HI;
            _txtLink.Font = new Font("Consolas", 9.5f);
            _txtLink.Text = "No guest link yet — click Generate to create one.";
            _txtLink.SetBounds(22, 418, ClientSize.Width - 44 - 100, 26);
            _txtLink.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_txtLink);

            _btnCopyLink = MakeButton("Copy", C_CARD_HI, C_BORDER, C_TEXT, false);
            _btnCopyLink.SetBounds(ClientSize.Width - 22 - 90, 416, 90, 28);
            _btnCopyLink.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnCopyLink.Enabled = false;
            _btnCopyLink.Click += delegate { if (!string.IsNullOrEmpty(_activeLink)) { SafeCopy(_activeLink); Log("🔗  Guest link copied again."); } };
            Controls.Add(_btnCopyLink);

            // ---- live control card ----
            _controlCard = Card(22, 462, ClientSize.Width - 44, 300);
            _controlCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _controlCard.Controls.Add(Caption(18, 14, "2 · CONTROL WHILE PLAYING"));

            Label ctlHint = new Label();
            ctlHint.Text = "Toggle each person's keyboard / mouse / controller — the top row is Everyone.";
            ctlHint.Font = new Font("Segoe UI", 8.5f);
            ctlHint.ForeColor = C_MUTED;
            ctlHint.AutoSize = true;
            ctlHint.BackColor = Color.Transparent;
            ctlHint.Location = new Point(20, 30);
            _controlCard.Controls.Add(ctlHint);

            int cw = _controlCard.Width;
            int tglW = (cw - 18 - 18 - 2 * 12) / 3;   // 18px side padding, 12px gaps
            _tglKbd   = MakeToggle("⌨  Keyboard", "KBD");
            _tglMouse = MakeToggle("🖱  Mouse",   "MOUSE");
            _tglCtrl  = MakeToggle("🎮  Controller", "CTRL");
            _tglKbd.SetBounds(18, 52, tglW, 40);
            _tglMouse.SetBounds(18 + tglW + 12, 52, tglW, 40);
            _tglCtrl.SetBounds(18 + 2 * (tglW + 12), 52, tglW, 40);
            _controlCard.Controls.Add(_tglKbd);
            _controlCard.Controls.Add(_tglMouse);
            _controlCard.Controls.Add(_tglCtrl);

            _btnTakeOver = MakeButton("🙋  I'll drive (pause everyone)", C_CARD_HI, C_BORDER, C_TEXT, false);
            _btnTakeOver.SetBounds(18, 98, (cw - 18 - 18 - 12) / 2, 26);
            _btnTakeOver.Font = new Font("Segoe UI Semibold", 8.75f, FontStyle.Bold);
            _btnTakeOver.Click += delegate { SetAllInputs(false); };
            _controlCard.Controls.Add(_btnTakeOver);

            _btnHandBack = MakeButton("🤝  Hand control back", C_CARD_HI, C_BORDER, C_TEXT, false);
            _btnHandBack.SetBounds(18 + (cw - 18 - 18 - 12) / 2 + 12, 98, (cw - 18 - 18 - 12) / 2, 26);
            _btnHandBack.Font = new Font("Segoe UI Semibold", 8.75f, FontStyle.Bold);
            _btnHandBack.Click += delegate { SetAllInputs(true); };
            _controlCard.Controls.Add(_btnHandBack);

            // roster: who's connected + per-person icons
            _lblRoster = Caption(18, 134, "WHO'S CONNECTED");
            _controlCard.Controls.Add(_lblRoster);
            _lblTarget = new Label();
            _lblTarget.Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold);
            _lblTarget.ForeColor = C_ACCENT_HI;
            _lblTarget.AutoSize = true;
            _lblTarget.BackColor = Color.Transparent;
            _lblTarget.Location = new Point(150, 134);
            _controlCard.Controls.Add(_lblTarget);

            _rosterPanel = new FlowLayoutPanel();
            _rosterPanel.FlowDirection = FlowDirection.TopDown;
            _rosterPanel.WrapContents = false;
            _rosterPanel.AutoScroll = true;
            _rosterPanel.BackColor = Color.FromArgb(0x0E, 0x11, 0x17);
            _rosterPanel.SetBounds(18, 152, cw - 36, 92);
            _rosterPanel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _rosterPanel.Padding = new Padding(4);
            _controlCard.Controls.Add(_rosterPanel);
            RebuildRoster();

            _btnHotkeys = MakeButton("⚙  Hotkeys", C_CARD_HI, C_BORDER, C_TEXT, false);
            _btnHotkeys.SetBounds(_controlCard.Width - 18 - 118, 10, 118, 24);
            _btnHotkeys.Font = new Font("Segoe UI Semibold", 8.25f, FontStyle.Bold);
            _btnHotkeys.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnHotkeys.Enabled = true;
            _btnHotkeys.Click += delegate { ShowHotkeyDialog(); };
            _controlCard.Controls.Add(_btnHotkeys);

            _lblHotkeys = new Label();
            _lblHotkeys.Font = new Font("Segoe UI", 7.75f);
            _lblHotkeys.ForeColor = C_MUTED;
            _lblHotkeys.AutoSize = true;
            _lblHotkeys.BackColor = Color.Transparent;
            _lblHotkeys.Location = new Point(20, 250);
            _controlCard.Controls.Add(_lblHotkeys);
            UpdateHotkeyLegend();

            SyncToggleVisuals();
            SetControlsEnabled(false);

            // ---- session banner ----
            _banner = new Panel();
            _banner.SetBounds(22, 774, ClientSize.Width - 44, 40);
            _banner.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _banner.BackColor = C_CARD;
            _banner.Paint += delegate(object s, PaintEventArgs e) { PaintCard(e.Graphics, _banner, C_BORDER); };
            Controls.Add(_banner);
            _lblBanner = new Label();
            _lblBanner.AutoSize = false;
            _lblBanner.Dock = DockStyle.Fill;
            _lblBanner.TextAlign = ContentAlignment.MiddleLeft;
            _lblBanner.Padding = new Padding(14, 0, 0, 0);
            _lblBanner.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            _lblBanner.ForeColor = C_MUTED;
            _lblBanner.BackColor = Color.Transparent;
            _lblBanner.Text = "○  Idle — no session running.";
            _banner.Controls.Add(_lblBanner);

            // ---- activity log ----
            Label lblLog = Caption(22, 824, "ACTIVITY");
            Controls.Add(lblLog);

            _txtLog = new TextBox();
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BorderStyle = BorderStyle.FixedSingle;
            _txtLog.BackColor = Color.FromArgb(0x0E, 0x11, 0x17);
            _txtLog.ForeColor = Color.FromArgb(0xC7, 0xD5, 0xE0);
            _txtLog.Font = new Font("Consolas", 9.5f);
            _txtLog.SetBounds(22, 844, ClientSize.Width - 44, ClientSize.Height - 844 - 18);
            _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_txtLog);

            // ---- tray ----
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miShow = new ToolStripMenuItem("Show"); miShow.Click += delegate { RestoreFromTray(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit"); miExit.Click += delegate { Close(); };
            menu.Items.Add(miShow); menu.Items.Add(miExit);
            _tray = new NotifyIcon(); _tray.Icon = SystemIcons.Application; _tray.Text = "RemotePlayForge";
            _tray.Visible = true; _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RestoreFromTray(); };
        }

        // ---- small UI builders ----
        Panel Card(int x, int y, int w, int h)
        {
            Panel p = new Panel();
            p.SetBounds(x, y, w, h);
            p.BackColor = C_CARD;
            p.Paint += delegate(object s, PaintEventArgs e) { PaintCard(e.Graphics, p, C_BORDER); };
            Controls.Add(p);
            return p;
        }

        static void PaintCard(Graphics g, Control c, Color border)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, c.Width - 1, c.Height - 1);
            using (GraphicsPath path = RoundedRect(r, 10))
            using (Pen p = new Pen(border)) g.DrawPath(p, path);
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        Label Caption(int x, int y, string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            l.ForeColor = C_MUTED;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.Location = new Point(x, y);
            return l;
        }

        Label Value(int x, int y, string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            l.ForeColor = C_TEXT;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.Location = new Point(x, y);
            return l;
        }

        Label Dot(int x, int y)
        {
            Label l = new Label();
            l.Text = "●";
            l.Font = new Font("Segoe UI", 12f);
            l.ForeColor = C_MUTED;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.Location = new Point(x, y + 10);
            return l;
        }

        Button MakeButton(string text, Color back, Color hover, Color fore, bool bold)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = fore;
            b.Font = new Font("Segoe UI", 9.75f, bold ? FontStyle.Bold : FontStyle.Regular);
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = hover;
            Color baseC = back;
            b.EnabledChanged += delegate { b.BackColor = b.Enabled ? baseC : C_CARD; b.ForeColor = b.Enabled ? fore : C_MUTED; };
            return b;
        }

        void Round(Control c, int radius)
        {
            c.Region = System.Drawing.Region.FromHrgn(CreateRoundRectRgn(0, 0, c.Width + 1, c.Height + 1, radius, radius));
            c.Resize += delegate { c.Region = System.Drawing.Region.FromHrgn(CreateRoundRectRgn(0, 0, c.Width + 1, c.Height + 1, radius, radius)); };
        }

        // A live input toggle button. Its Tag holds the helper command key ("KBD"/"MOUSE"/"CTRL").
        Button MakeToggle(string label, string cmd)
        {
            Button b = new Button();
            b.Tag = cmd;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Cursor = Cursors.Hand;
            b.Text = label + "\nON";
            b.Click += delegate { ToggleInput(cmd); };
            return b;
        }

        // ------------------------------------------------------------ live control

        static readonly Color C_TGL_ON_BG  = Color.FromArgb(0x14, 0x33, 0x27);
        static readonly Color C_TGL_OFF_BG = Color.FromArgb(0x2A, 0x1C, 0x1F);

        bool StateOf(string cmd)
        { return cmd == "KBD" ? _kbdOn : cmd == "MOUSE" ? _mouseOn : _ctrlOn; }

        void ToggleInput(string cmd)
        {
            bool on;
            if (cmd == "KBD") on = (_kbdOn = !_kbdOn);
            else if (cmd == "MOUSE") on = (_mouseOn = !_mouseOn);
            else on = (_ctrlOn = !_ctrlOn);

            SendCmd(cmd + (on ? " 1" : " 0"));
            SyncToggleVisuals();
            string what = cmd == "KBD" ? "keyboard" : cmd == "MOUSE" ? "mouse" : "controller";
            Log((on ? "🔓  Gave " : "🔒  Took back ") + what + " " + (on ? "to " : "from ") + _currentFriend + ".");
        }

        void SetAllInputs(bool on)
        {
            _kbdOn = _mouseOn = _ctrlOn = on;
            SendCmd("ALL " + (on ? "1" : "0"));
            SyncToggleVisuals();
            if (on) { Log("🤝  Handed full control back to " + _currentFriend + "."); Banner("●  Live — " + _currentFriend + " can play your game!", C_GREEN); }
            else    { Log("🙋  You're driving now — " + _currentFriend + "'s input is paused."); Banner("⏸  You have control — " + _currentFriend + " is paused.", C_AMBER); }
        }

        void PaintToggle(Button b, string label, bool on)
        {
            bool live = b.Enabled;
            b.Text = label + "\n" + (!live ? "—" : on ? "ON" : "OFF");
            if (!live) { b.BackColor = C_CARD; b.ForeColor = C_MUTED; b.FlatAppearance.BorderColor = C_BORDER; b.FlatAppearance.MouseOverBackColor = C_CARD; return; }
            if (on) { b.BackColor = C_TGL_ON_BG; b.ForeColor = C_GREEN; b.FlatAppearance.BorderColor = C_GREEN; b.FlatAppearance.MouseOverBackColor = C_TGL_ON_BG; }
            else    { b.BackColor = C_TGL_OFF_BG; b.ForeColor = C_RED; b.FlatAppearance.BorderColor = Color.FromArgb(0x6E, 0x33, 0x38); b.FlatAppearance.MouseOverBackColor = C_TGL_OFF_BG; }
        }

        void SyncToggleVisuals()
        {
            if (_tglKbd.InvokeRequired) { _tglKbd.BeginInvoke((MethodInvoker)delegate { SyncToggleVisuals(); }); return; }
            PaintToggle(_tglKbd, "⌨  Keyboard", _kbdOn);
            PaintToggle(_tglMouse, "🖱  Mouse", _mouseOn);
            PaintToggle(_tglCtrl, "🎮  Controller", _ctrlOn);
        }

        // Enable/disable the whole control card (only meaningful while a session is live).
        void SetControlsEnabled(bool enabled)
        {
            if (_tglKbd.InvokeRequired) { _tglKbd.BeginInvoke((MethodInvoker)delegate { SetControlsEnabled(enabled); }); return; }
            _tglKbd.Enabled = _tglMouse.Enabled = _tglCtrl.Enabled = enabled;
            _btnTakeOver.Enabled = _btnHandBack.Enabled = enabled;
            _tglKbd.Cursor = _tglMouse.Cursor = _tglCtrl.Cursor = enabled ? Cursors.Hand : Cursors.Default;
            SyncToggleVisuals();
        }

        // --------------------------------------------------------- players roster

        int RowWidth() { return _rosterPanel.ClientSize.Width - 24; }

        // Rebuild the roster panel from _rows (empty-state placeholder when nobody is connected).
        void RebuildRoster()
        {
            if (_rosterPanel == null) return;
            if (_rosterPanel.InvokeRequired) { _rosterPanel.BeginInvoke((MethodInvoker)delegate { RebuildRoster(); }); return; }
            _rosterPanel.SuspendLayout();
            _rosterPanel.Controls.Clear();
            if (_rows.Count == 0)
            {
                Label empty = new Label();
                empty.Text = "No one connected yet — invite a friend or share a link.";
                empty.ForeColor = C_MUTED;
                empty.Font = new Font("Segoe UI", 8.5f, FontStyle.Italic);
                empty.AutoSize = true;
                empty.Margin = new Padding(6, 8, 0, 0);
                _rosterPanel.Controls.Add(empty);
            }
            else
            {
                List<int> slots = new List<int>(_rows.Keys); slots.Sort();
                foreach (int slot in slots) { _rows[slot].Panel.Width = RowWidth(); _rosterPanel.Controls.Add(_rows[slot].Panel); }
            }
            _rosterPanel.ResumeLayout();
            UpdateTargetLabel();
        }

        PlayerRow MakePlayerRow(int slot, string name, ulong steam)
        {
            PlayerRow r = new PlayerRow();
            r.Slot = slot; r.Name = name; r.Steam = steam;

            Panel row = new Panel();
            row.Width = RowWidth(); row.Height = 30;
            row.Margin = new Padding(2, 2, 2, 2);
            row.BackColor = C_CARD_HI;
            r.Panel = row;

            Label nm = new Label();
            nm.Text = "👤  " + name;
            nm.ForeColor = C_TEXT;
            nm.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            nm.AutoEllipsis = true;
            nm.TextAlign = ContentAlignment.MiddleLeft;
            nm.SetBounds(8, 0, row.Width - 8 - 3 * 34 - 6, 30);
            nm.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            nm.Cursor = Cursors.Hand;
            nm.Click += delegate { SelectSlot(slot); };
            row.Controls.Add(nm);
            r.NameLbl = nm;

            r.BK = MakeMini("⌨", slot, "KBD");
            r.BM = MakeMini("🖱", slot, "MOUSE");
            r.BC = MakeMini("🎮", slot, "CTRL");
            r.BK.SetBounds(row.Width - 3 * 34 - 2, 2, 30, 26); r.BK.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            r.BM.SetBounds(row.Width - 2 * 34 - 2, 2, 30, 26); r.BM.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            r.BC.SetBounds(row.Width - 1 * 34 - 2, 2, 30, 26); r.BC.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            row.Controls.Add(r.BK); row.Controls.Add(r.BM); row.Controls.Add(r.BC);

            row.Click += delegate { SelectSlot(slot); };
            PaintRowMini(r);
            return r;
        }

        Button MakeMini(string icon, int slot, string cmd)
        {
            Button b = new Button();
            b.Text = icon;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.Font = new Font("Segoe UI", 9f);
            b.Cursor = Cursors.Hand;
            b.Tag = cmd;
            b.Click += delegate { TogglePlayerInput(slot, cmd); };
            return b;
        }

        void PaintMini(Button b, bool on)
        {
            if (on) { b.BackColor = C_TGL_ON_BG; b.ForeColor = C_GREEN; b.FlatAppearance.BorderColor = C_GREEN; b.FlatAppearance.MouseOverBackColor = C_TGL_ON_BG; }
            else    { b.BackColor = C_TGL_OFF_BG; b.ForeColor = C_RED;   b.FlatAppearance.BorderColor = Color.FromArgb(0x6E, 0x33, 0x38); b.FlatAppearance.MouseOverBackColor = C_TGL_OFF_BG; }
        }

        void PaintRowMini(PlayerRow r)
        {
            PaintMini(r.BK, r.Kbd); PaintMini(r.BM, r.Mouse); PaintMini(r.BC, r.Ctrl);
            bool sel = (_selectedSlot == r.Slot);
            r.Panel.BackColor = sel ? Color.FromArgb(0x17, 0x2A, 0x3C) : C_CARD_HI;
            r.NameLbl.ForeColor = sel ? C_ACCENT_HI : C_TEXT;
        }

        // Create/update a roster row from a helper "PLAYER slot=…" line.
        void UpsertPlayer(int slot, ulong steam, string name, bool k, bool m, bool c)
        {
            if (_rosterPanel != null && _rosterPanel.InvokeRequired)
            { _rosterPanel.BeginInvoke((MethodInvoker)delegate { UpsertPlayer(slot, steam, name, k, m, c); }); return; }
            PlayerRow r; bool isNew = !_rows.TryGetValue(slot, out r);
            if (isNew) { r = MakePlayerRow(slot, name, steam); _rows[slot] = r; }
            else { r.Name = name; r.Steam = steam; r.NameLbl.Text = "👤  " + name; }
            r.Kbd = k; r.Mouse = m; r.Ctrl = c;
            if (isNew) RebuildRoster();
            PaintRowMini(r);
            SetControlsEnabled(true);
        }

        // Toggle one input for one specific player, and tell the helper.
        void TogglePlayerInput(int slot, string cmd)
        {
            PlayerRow r; if (!_rows.TryGetValue(slot, out r)) return;
            bool on;
            if (cmd == "KBD") on = (r.Kbd = !r.Kbd);
            else if (cmd == "MOUSE") on = (r.Mouse = !r.Mouse);
            else on = (r.Ctrl = !r.Ctrl);
            SendCmd("P" + cmd + " " + slot + " " + (on ? "1" : "0"));
            PaintRowMini(r);
            string what = cmd == "KBD" ? "keyboard" : cmd == "MOUSE" ? "mouse" : "controller";
            Log((on ? "🔓  Gave " : "🔒  Took ") + what + " " + (on ? "to " : "from ") + r.Name + ".");
        }

        // Pause/resume ALL of one player's inputs at once.
        void SetPlayerAll(int slot, bool on)
        {
            PlayerRow r; if (!_rows.TryGetValue(slot, out r)) return;
            r.Kbd = r.Mouse = r.Ctrl = on;
            SendCmd("PALL " + slot + " " + (on ? "1" : "0"));
            PaintRowMini(r);
            Log((on ? "🤝  Gave full control to " : "🙋  Paused ") + r.Name + ".");
        }

        void SelectSlot(int slot)
        {
            _selectedSlot = slot;
            foreach (PlayerRow r in _rows.Values) PaintRowMini(r);
            UpdateTargetLabel();
        }

        void UpdateTargetLabel()
        {
            if (_lblTarget == null) return;
            if (_lblTarget.InvokeRequired) { _lblTarget.BeginInvoke((MethodInvoker)delegate { UpdateTargetLabel(); }); return; }
            string who = "Everyone";
            if (_selectedSlot != 0) { PlayerRow r; if (_rows.TryGetValue(_selectedSlot, out r)) who = r.Name; else _selectedSlot = 0; }
            _lblTarget.Text = "🎯 hotkeys → " + who;
        }

        // Cycle the hotkey target: Everyone -> P1 -> P2 -> … -> Everyone.
        void CycleTarget()
        {
            List<int> order = new List<int>(); order.Add(0);
            List<int> slots = new List<int>(_rows.Keys); slots.Sort(); order.AddRange(slots);
            int idx = order.IndexOf(_selectedSlot); if (idx < 0) idx = 0;
            SelectSlot(order[(idx + 1) % order.Count]);
            string who = _selectedSlot == 0 ? "Everyone" : (_rows.ContainsKey(_selectedSlot) ? _rows[_selectedSlot].Name : "Everyone");
            Log("🎯  Hotkey target: " + who + ".");
        }

        // Send one line to the running helper's stdin.
        void SendCmd(string cmd)
        {
            try
            {
                if (_session != null && !SafeHasExited(_session))
                    _session.StandardInput.WriteLine(cmd);
            }
            catch { }
        }

        // Show a generated guest link in the link bar and enable Copy.
        void SetLinkBar(string url)
        {
            if (_txtLink.InvokeRequired) { _txtLink.BeginInvoke((MethodInvoker)delegate { SetLinkBar(url); }); return; }
            _txtLink.Text = url;
            _txtLink.SelectionStart = 0; _txtLink.SelectionLength = 0;
            _btnCopyLink.Enabled = true;
        }

        // Alert the user the moment a guest actually connects (works even minimized to tray).
        void NotifyConnected()
        {
            try { if (_tray != null) _tray.ShowBalloonTip(4000, "RemotePlayForge", _currentFriend + " connected — you can hand over control!", ToolTipIcon.Info); } catch { }
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            try { FlashTaskbar(); } catch { }
        }

        void FlashTaskbar()
        {
            FLASHWINFO fi = new FLASHWINFO();
            fi.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
            fi.hwnd = Handle;
            fi.dwFlags = 3; // FLASHW_ALL (caption + taskbar)
            fi.uCount = 3;
            fi.dwTimeout = 0;
            FlashWindowEx(ref fi);
        }

        // ---------------------------------------------------------- customizable hotkeys

        Dictionary<int, Hotkey> DefaultHotkeys()
        {
            uint m = MOD_CONTROL | MOD_ALT;
            Dictionary<int, Hotkey> d = new Dictionary<int, Hotkey>();
            d[HK_DRIVE] = new Hotkey { Mods = m, Vk = 0x44 }; // D
            d[HK_GIVE]  = new Hotkey { Mods = m, Vk = 0x47 }; // G
            d[HK_KBD]   = new Hotkey { Mods = m, Vk = 0x4B }; // K
            d[HK_MOUSE] = new Hotkey { Mods = m, Vk = 0x4D }; // M
            d[HK_CTRL]  = new Hotkey { Mods = m, Vk = 0x43 }; // C
            d[HK_NEXT]  = new Hotkey { Mods = m, Vk = 0x4E }; // N — cycle hotkey target (Everyone/person)
            return d;
        }

        string HkConfigPath { get { return Path.Combine(_exeDir, "hotkeys.cfg"); } }

        // Load bindings from hotkeys.cfg ("id=mods,vk" per line); fall back to defaults for anything missing.
        void LoadHotkeys()
        {
            _hotkeys = DefaultHotkeys();
            try
            {
                if (!File.Exists(HkConfigPath)) return;
                foreach (string raw in File.ReadAllLines(HkConfigPath))
                {
                    string ln = raw.Trim();
                    if (ln.Length == 0 || ln[0] == '#') continue;
                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    int id; if (!int.TryParse(ln.Substring(0, eq).Trim(), out id)) continue;
                    string[] mv = ln.Substring(eq + 1).Split(',');
                    if (mv.Length != 2) continue;
                    uint mods, vk;
                    if (!uint.TryParse(mv[0].Trim(), out mods) || !uint.TryParse(mv[1].Trim(), out vk)) continue;
                    if (vk == 0) continue;
                    _hotkeys[id] = new Hotkey { Mods = mods, Vk = vk };
                }
            }
            catch { }
        }

        void SaveHotkeys()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# RemotePlayForge hotkeys — id=modifiers,virtualkey (ctrl=2 alt=1 shift=4 win=8)");
                foreach (int id in HK_IDS)
                {
                    Hotkey h; if (!_hotkeys.TryGetValue(id, out h)) continue;
                    sb.AppendLine(id + "=" + h.Mods + "," + h.Vk);
                }
                File.WriteAllText(HkConfigPath, sb.ToString());
            }
            catch { }
        }

        // Re-register all global hotkeys from the current bindings (unregister first so a rebind takes effect).
        void RegisterHotkeys()
        {
            if (!IsHandleCreated) return;
            try
            {
                foreach (int id in HK_IDS) UnregisterHotKey(Handle, id);
                foreach (int id in HK_IDS)
                {
                    Hotkey h; if (!_hotkeys.TryGetValue(id, out h) || h.Vk == 0) continue;
                    RegisterHotKey(Handle, id, h.Mods | MOD_NOREPEAT, h.Vk);
                }
            }
            catch { }
            UpdateHotkeyLegend();
        }

        string HkAction(int id)
        {
            switch (id)
            {
                case HK_DRIVE: return "I'll drive";
                case HK_GIVE:  return "Hand control back";
                case HK_KBD:   return "Toggle keyboard";
                case HK_MOUSE: return "Toggle mouse";
                case HK_CTRL:  return "Toggle controller";
                case HK_NEXT:  return "Next person (hotkey target)";
            }
            return "?";
        }

        // Human-readable combo, e.g. "Ctrl+Alt+D".
        string HkStr(Hotkey h)
        {
            if (h == null || h.Vk == 0) return "(unset)";
            StringBuilder sb = new StringBuilder();
            if ((h.Mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((h.Mods & MOD_ALT) != 0) sb.Append("Alt+");
            if ((h.Mods & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((h.Mods & MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(VkName(h.Vk));
            return sb.ToString();
        }

        static string VkName(uint vk)
        {
            try { string s = ((Keys)vk).ToString(); return string.IsNullOrEmpty(s) ? ("0x" + vk.ToString("X")) : s; }
            catch { return "0x" + vk.ToString("X"); }
        }

        void UpdateHotkeyLegend()
        {
            if (_lblHotkeys == null || _hotkeys == null) return;
            if (_lblHotkeys.InvokeRequired) { _lblHotkeys.BeginInvoke((MethodInvoker)delegate { UpdateHotkeyLegend(); }); return; }
            _lblHotkeys.Text = "In-game hotkeys:  " + HkStr(_hotkeys[HK_DRIVE]) + "  drive  ·  "
                + HkStr(_hotkeys[HK_GIVE]) + "  give back  ·  "
                + HkStr(_hotkeys[HK_KBD]) + " / " + HkStr(_hotkeys[HK_MOUSE]) + " / " + HkStr(_hotkeys[HK_CTRL])
                + "  toggle kbd / mouse / controller  ·  " + HkStr(_hotkeys[HK_NEXT]) + "  next person";
        }

        // Modal rebind dialog: click a key's button, press the combo you want, Save.
        void ShowHotkeyDialog()
        {
            Dictionary<int, Hotkey> working = new Dictionary<int, Hotkey>();
            foreach (int id in HK_IDS) working[id] = new Hotkey { Mods = _hotkeys[id].Mods, Vk = _hotkeys[id].Vk };

            Form dlg = new Form();
            dlg.Text = "Customize hotkeys";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = C_BG; dlg.ForeColor = C_TEXT;
            dlg.Font = new Font("Segoe UI", 9.5f);
            dlg.ClientSize = new Size(430, 60 + HK_IDS.Length * 42 + 66);
            dlg.KeyPreview = true;

            Label tip = new Label();
            tip.Text = "Click a key, then press the combo you want (e.g. Ctrl+Alt+D).";
            tip.ForeColor = C_MUTED; tip.AutoSize = false;
            tip.SetBounds(20, 14, 390, 34);
            dlg.Controls.Add(tip);

            int[] arm = { 0 };                             // id currently capturing (0 = none)
            Dictionary<int, Button> btns = new Dictionary<int, Button>();

            int y = 54;
            foreach (int id in HK_IDS)
            {
                Label name = new Label();
                name.Text = HkAction(id);
                name.ForeColor = C_TEXT; name.AutoSize = false;
                name.TextAlign = ContentAlignment.MiddleLeft;
                name.SetBounds(20, y, 180, 32);
                dlg.Controls.Add(name);

                Button cap = MakeButton(HkStr(working[id]), C_CARD_HI, C_BORDER, C_TEXT, true);
                cap.SetBounds(210, y, 200, 32);
                cap.Tag = id;
                cap.Click += delegate(object s, EventArgs e)
                {
                    int cid = (int)((Button)s).Tag;
                    // reset any other armed button label, arm this one
                    foreach (int oid in HK_IDS) if (oid != cid) btns[oid].Text = HkStr(working[oid]);
                    arm[0] = cid;
                    ((Button)s).Text = "press keys…";
                };
                btns[id] = cap;
                dlg.Controls.Add(cap);
                y += 42;
            }

            dlg.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (arm[0] == 0) return;
                Keys kc = e.KeyCode;
                if (kc == Keys.ControlKey || kc == Keys.Menu || kc == Keys.ShiftKey
                    || kc == Keys.LWin || kc == Keys.RWin) { e.Handled = true; e.SuppressKeyPress = true; return; }
                if (kc == Keys.Escape) { int c = arm[0]; arm[0] = 0; btns[c].Text = HkStr(working[c]); e.Handled = true; e.SuppressKeyPress = true; return; }
                uint mods = 0;
                if ((Control.ModifierKeys & Keys.Control) != 0) mods |= MOD_CONTROL;
                if ((Control.ModifierKeys & Keys.Alt) != 0) mods |= MOD_ALT;
                if ((Control.ModifierKeys & Keys.Shift) != 0) mods |= MOD_SHIFT;
                int cid = arm[0];
                working[cid] = new Hotkey { Mods = mods, Vk = (uint)kc };
                btns[cid].Text = HkStr(working[cid]);
                arm[0] = 0;
                e.Handled = true; e.SuppressKeyPress = true;
            };

            int by = 54 + HK_IDS.Length * 42 + 12;
            Button reset = MakeButton("Reset defaults", C_CARD_HI, C_BORDER, C_MUTED, false);
            reset.SetBounds(20, by, 130, 34);
            reset.Click += delegate
            {
                Dictionary<int, Hotkey> d = DefaultHotkeys();
                foreach (int id in HK_IDS) { working[id] = d[id]; btns[id].Text = HkStr(working[id]); }
                arm[0] = 0;
            };
            dlg.Controls.Add(reset);

            Button save = MakeButton("Save", C_ACCENT, C_ACCENT_HI, Color.White, true);
            save.SetBounds(210, by, 95, 34);
            Button cancel = MakeButton("Cancel", C_CARD_HI, C_BORDER, C_TEXT, false);
            cancel.SetBounds(315, by, 95, 34);
            save.Click += delegate
            {
                _hotkeys = working;
                SaveHotkeys();
                RegisterHotkeys();
                Log("⚙  Hotkeys updated.");
                dlg.Close();
            };
            cancel.Click += delegate { dlg.Close(); };
            dlg.Controls.Add(save);
            dlg.Controls.Add(cancel);
            // note: no AcceptButton — Enter is a bindable key, so it must reach the capture handler.

            try { dlg.ShowDialog(this); } catch { }
            try { dlg.Dispose(); } catch { }
        }

        // Adopt the helper's reported input state ("INPUT KBD=1 MOUSE=1 CTRL=0").
        void SyncFromHelper(string s)
        {
            _kbdOn   = ParseFlag(s, "KBD=", _kbdOn);
            _mouseOn = ParseFlag(s, "MOUSE=", _mouseOn);
            _ctrlOn  = ParseFlag(s, "CTRL=", _ctrlOn);
            SyncToggleVisuals();
        }

        static bool ParseFlag(string s, string key, bool fallback)
        {
            int i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0 || i + key.Length >= s.Length) return fallback;
            char c = s[i + key.Length];
            return c == '1' ? true : c == '0' ? false : fallback;
        }

        // Parse "PLAYER slot=1 steam=765… k=1 m=1 c=1 name=Bob" (name is the rest of the line).
        // Tokenized by whole key so "m=" can't match inside "steam=" (that bug pinned mouse to green).
        void ParsePlayerLine(string s)
        {
            try
            {
                int nameIdx = s.IndexOf(" name=", StringComparison.Ordinal);
                string head = nameIdx >= 0 ? s.Substring(0, nameIdx) : s;
                string name = nameIdx >= 0 ? s.Substring(nameIdx + " name=".Length).Trim() : "";

                int slot = 0; ulong steam = 0; bool k = true, m = true, c = true;
                foreach (string tok in head.Split(' '))
                {
                    int eq = tok.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = tok.Substring(0, eq);
                    string val = tok.Substring(eq + 1);
                    if (key == "slot") int.TryParse(val, out slot);
                    else if (key == "steam") ulong.TryParse(val, out steam);
                    else if (key == "k") k = (val == "1");
                    else if (key == "m") m = (val == "1");
                    else if (key == "c") c = (val == "1");
                }
                if (slot < 1) return;
                if (string.IsNullOrEmpty(name)) name = "Player " + slot;
                UpsertPlayer(slot, steam, name, k, m, c);
            }
            catch { }
        }

        // -------------------------------------------------------------- status

        void UpdateStatus()
        {
            bool steamRunning = SteamPaths.IsSteamRunning();
            bool live = _live != null && _live.Connected;
            if (live)      { _dotSteam.ForeColor = C_GREEN; _lblSteam.Text = "Connected"; }
            else if (steamRunning) { _dotSteam.ForeColor = C_AMBER; _lblSteam.Text = "Running"; }
            else           { _dotSteam.ForeColor = C_RED;   _lblSteam.Text = "Not running"; }

            uint appId = SteamPaths.GetRunningAppId();
            if (appId != 0)
            {
                string name = SteamPaths.GetGameName(appId);
                _dotGame.ForeColor = C_GREEN;
                _lblGame.Text = (name ?? ("AppID " + appId));
            }
            else
            {
                _dotGame.ForeColor = C_MUTED;
                _lblGame.Text = "Launch a game";
            }

            bool active = _session != null && !SafeHasExited(_session);
            _btnStop.Enabled = active;
            _btnInvite.Enabled = true;
        }

        static bool SafeHasExited(System.Diagnostics.Process p)
        { try { return p.HasExited; } catch { return true; } }

        void Banner(string text, Color color)
        {
            if (_lblBanner.InvokeRequired) { _lblBanner.BeginInvoke((MethodInvoker)delegate { Banner(text, color); }); return; }
            _lblBanner.Text = text;
            _lblBanner.ForeColor = color;
        }

        // -------------------------------------------------------------- friends

        void RefreshFriends()
        {
            string err;
            SteamInternalForce f = EnsureLive(out err);
            if (f == null) { Log("Couldn't reach Steam: " + err + "  (is Steam running and signed in?)"); UpdateStatus(); return; }
            List<SteamFriend> friends = f.EnumerateOnlineFriends(out err);
            if (err != null) Log("Friends: " + err);
            _cboFriends.BeginUpdate();
            _cboFriends.Items.Clear();
            foreach (SteamFriend fr in friends) _cboFriends.Items.Add(fr);
            _cboFriends.EndUpdate();
            if (_cboFriends.Items.Count > 0) _cboFriends.SelectedIndex = 0;
            Log("Loaded " + friends.Count + " online friend" + (friends.Count == 1 ? "" : "s") + ".");
            UpdateStatus();
        }

        SteamInternalForce EnsureLive(out string error)
        {
            error = null;
            if (_live != null && _live.Connected) return _live;
            if (_live != null) { try { _live.Dispose(); } catch { } _live = null; }
            SteamInternalForce f = new SteamInternalForce();
            string steamDir = SteamPaths.GetSteamInstallPath();
            if (!f.Connect(steamDir, out error)) { try { f.Dispose(); } catch { } return null; }
            string detail; f.ValidateAlignment(out detail);
            _live = f;
            return _live;
        }

        // --------------------------------------------------------------- invite

        void InviteSelectedFriend()
        {
            SteamFriend fr = _cboFriends.SelectedItem as SteamFriend;
            if (fr == null) { Log("Pick a friend first — click Refresh."); return; }

            uint appId = SteamPaths.GetRunningAppId();
            if (appId == 0)
            {
                Log("Launch your game through Steam first, then invite.");
                Banner("⚠  Launch your game first, then invite.", C_AMBER);
                return;
            }

            string helper = Path.Combine(_exeDir, "RPForgeSession.exe");
            if (!File.Exists(helper)) { Log("Session helper missing: " + helper); return; }

            // If a session is already live, ADD this friend to it — earlier guests stay connected.
            if (_session != null && !SafeHasExited(_session))
            {
                _currentFriend = fr.Name;
                SendCmd("ADD " + fr.Id);
                Log("➕  Inviting " + fr.Name + " to the current session — everyone already in stays connected.");
                Banner("◐  Inviting " + fr.Name + " …", C_ACCENT);
                return;
            }

            StopSession(); // no session yet — start fresh
            _currentFriend = fr.Name;
            _kbdOn = _mouseOn = _ctrlOn = true;   // fresh session grants everything by default
            SyncToggleVisuals();

            string gameName = SteamPaths.GetGameName(appId) ?? ("AppID " + appId);
            Log("Starting Remote Play Together for " + fr.Name + " · " + gameName + " …");
            Banner("◐  Setting up session for " + fr.Name + " …", C_ACCENT);

            if (StartHelper(helper, fr.Id + " " + appId)) _btnStop.Enabled = true;
        }

        // Generate a shareable guest link — anyone with Steam can join, no friendship required.
        void GenerateGuestLink()
        {
            uint appId = SteamPaths.GetRunningAppId();
            if (appId == 0)
            {
                Log("Launch your game through Steam first, then generate a link.");
                Banner("⚠  Launch your game first, then generate a link.", C_AMBER);
                return;
            }
            string helper = Path.Combine(_exeDir, "RPForgeSession.exe");
            if (!File.Exists(helper)) { Log("Session helper missing: " + helper); return; }

            // If a session is already live, ADD a guest link to it — earlier guests stay connected.
            if (_session != null && !SafeHasExited(_session))
            {
                _txtLink.Text = "Generating link…";
                SendCmd("ADD LINK");
                Log("➕  Adding a guest link to the current session — everyone already in stays connected.");
                Banner("◐  Creating a guest link …", C_ACCENT);
                return;
            }

            StopSession(); // no session yet — start fresh (also clears the link bar)
            _currentFriend = "your guest";
            _kbdOn = _mouseOn = _ctrlOn = true;
            SyncToggleVisuals();
            _txtLink.Text = "Generating link…";

            string gameName = SteamPaths.GetGameName(appId) ?? ("AppID " + appId);
            Log("Creating a shareable guest link for " + gameName + " …");
            Banner("◐  Creating a guest link …", C_ACCENT);

            if (StartHelper(helper, "LINK " + appId)) _btnStop.Enabled = true;
        }

        // Launch the out-of-process session helper with the given args and wire up its output.
        bool StartHelper(string helper, string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(helper, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;   // lets us flip guest input live
                psi.CreateNoWindow = true;
                _session = new System.Diagnostics.Process();
                _session.StartInfo = psi;
                _session.EnableRaisingEvents = true;
                _session.OutputDataReceived += SessionOutput;
                _session.ErrorDataReceived += SessionOutput;
                _session.Exited += delegate { Banner("○  Session ended.", C_MUTED); Log("Session ended."); };
                _session.Start();
                _session.BeginOutputReadLine();
                _session.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex) { Log("Could not start session helper: " + ex.Message); return false; }
        }

        // Translate the helper's raw output into friendly, filtered status.
        void SessionOutput(object sender, System.Diagnostics.DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string s = e.Data.Trim();
            if (s.Length == 0) return;

            // Roster machine lines from the helper.
            if (s.StartsWith("PLAYER slot=", StringComparison.Ordinal)) { ParsePlayerLine(s); return; }
            if (s.StartsWith("PLAYER JOINED ", StringComparison.Ordinal))
            {
                string nm = s.Substring("PLAYER JOINED ".Length).Trim();
                Log("👤  " + nm + " joined — control them individually in the roster below.");
                Banner("●  " + nm + " connected — hand over control below!", C_GREEN);
                NotifyConnected();
                return;
            }

            if (s.IndexOf("BCreateRemotePlayInviteAndSession=True", StringComparison.Ordinal) >= 0)
            { Log("✅  Session created."); return; }

            int k = s.IndexOf("JOIN URL = ", StringComparison.Ordinal);
            if (k >= 0)
            {
                string url = s.Substring(k + "JOIN URL = ".Length).Trim();
                SafeCopy(url);
                Log("🔗  Invite sent to " + _currentFriend + "  ·  link copied to clipboard:");
                Log("     " + url);
                Banner("◐  Waiting for " + _currentFriend + " to join …", C_ACCENT);
                return;
            }

            int gl = s.IndexOf("GUEST LINK = ", StringComparison.Ordinal);
            if (gl >= 0)
            {
                string url = s.Substring(gl + "GUEST LINK = ".Length).Trim();
                _activeLink = url;
                SafeCopy(url);
                SetLinkBar(url);
                Log("🔗  Guest link ready — copied to clipboard. Share it with your friends:");
                Log("     " + url);
                Banner("🔗  Guest link ready — share it with your friends!", C_ACCENT);
                return;
            }

            if (s.IndexOf("GUEST CONNECTED", StringComparison.Ordinal) >= 0)
            { Log("👤  " + _currentFriend + " connected to your game."); Banner("●  " + _currentFriend + " connected — granting control…", C_ACCENT); NotifyConnected(); return; }

            if (s.IndexOf("granted keyboard/mouse/controller input", StringComparison.Ordinal) >= 0)
            {
                Log("🎮  Control granted — " + _currentFriend + " can play now!");
                Banner("●  Live — " + _currentFriend + " can play your game!", C_GREEN);
                SetControlsEnabled(true);
                return;
            }

            // Helper echoes its authoritative input state as "INPUT KBD=1 MOUSE=1 CTRL=0" — mirror it.
            if (s.IndexOf("INPUT KBD=", StringComparison.Ordinal) >= 0)
            { SyncFromHelper(s); return; }

            if (s.IndexOf("Could not create a host group", StringComparison.Ordinal) >= 0)
            { Log("⚠  Steam wouldn't start a session. Make sure your game is running, then try again."); Banner("⚠  Couldn't start the session.", C_AMBER); return; }
            if (s.IndexOf("No game running", StringComparison.Ordinal) >= 0)
            { Log("⚠  Launch your game first, then invite."); return; }
            if (s.IndexOf("RCM null", StringComparison.Ordinal) >= 0)
            { Log("⚠  Couldn't reach Steam. Is it running and signed in?"); return; }
            if (s.IndexOf("GrantControl threw", StringComparison.Ordinal) >= 0)
            { Log("⚠  " + s); return; }
            // everything else (raw callbacks, heartbeats, banners) is suppressed to keep this clean.
        }

        void StopSession()
        {
            try
            {
                if (_session != null)
                {
                    if (!SafeHasExited(_session))
                    {
                        // Ask the helper to DESTROY the group first so the guest actually stops watching —
                        // killing the process alone leaves Steam's stream alive. Fall back to Kill if it hangs.
                        try { _session.StandardInput.WriteLine("STOP"); } catch { }
                        try { if (!_session.WaitForExit(1600)) _session.Kill(); }
                        catch { try { _session.Kill(); } catch { } }
                    }
                    _session.Dispose();
                    _session = null;
                    Banner("○  Idle — no session running.", C_MUTED);
                }
            }
            catch { }
            _btnStop.Enabled = false;
            SetControlsEnabled(false);
            _rows.Clear(); _selectedSlot = 0; RebuildRoster();
            _activeLink = null;
            if (_txtLink != null) { _btnCopyLink.Enabled = false; _txtLink.Text = "No guest link yet — click Generate to create one."; }
        }

        // ------------------------------------------------------------- plumbing

        void SafeCopy(string s)
        {
            try { if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s); }
            catch { }
        }

        void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n";
            if (_txtLog.InvokeRequired) _txtLog.BeginInvoke((MethodInvoker)delegate { AppendLog(line); });
            else AppendLog(line);
        }
        void AppendLog(string line)
        {
            _txtLog.AppendText(line);
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
        }

        void RestoreFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try { StopSession(); } catch { }
            try { foreach (int id in HK_IDS) UnregisterHotKey(Handle, id); } catch { }
            try { if (_status != null) _status.Stop(); } catch { }
            try { if (_live != null) { _live.Dispose(); _live = null; } } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        }
    }
}
