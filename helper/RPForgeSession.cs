using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// RPForgeSession — the out-of-process session helper launched by RemotePlayForge.exe.
//
// Steam's Remote Play Together group only creates reliably from a fresh, dedicated process, so the
// GUI shells out to this console app to host the session and hold it open. It:
//   1. attaches to the running Steam client (steamclient64.dll, internal interfaces),
//   2. creates a Remote Play Together group bound to the running game,
//   3. creates an invite/session (for a friend SteamID64, or a shareable "LINK" guest slot),
//   4. delivers the join URL (chat message to the friend, or printed for the GUI to copy),
//   5. stays alive: discovers connected guests, grants/revokes each guest's keyboard/mouse/controller
//      per live commands from the GUI (stdin), and disbands the group on STOP.
//
// It talks to the GUI over stdout/stdin with a small line protocol (PLAYER…, GUEST LINK…, INPUT…,
// KBD/MOUSE/CTRL/ALL, PKBD/PMOUSE/PCTRL/PALL <slot>, ADD <id|LINK>, STOP).
//
// Steam client vtable indices are resolved BY NAME at runtime (never hardcoded) since they drift
// between builds. Args: [0] = friend SteamID64 or "LINK"; [1] = optional AppID.
internal static class RPForgeSession
{
    const int RPT_INVITE_RESULT = 1100028;
    const int k_EChatEntryTypeChatMsg = 1;
    const ulong STEAMID_INDIVIDUAL_BASE = 76561197960265728UL;

    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr LoadLibrary(string p);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr h, string n);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_CI([MarshalAs(UnmanagedType.LPStr)] string v, IntPtr rc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_I(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Ii(IntPtr s, int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_PtrInt(IntPtr s, int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_PtrIntInt(IntPtr s, int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_U(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_Uu(IntPtr s, uint a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_VoidU(IntPtr s, uint a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_Void(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_SetInputP(IntPtr s, IntPtr player, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_NameOfId(IntPtr s, ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_CreateInvite(IntPtr s, IntPtr player, uint appId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_SendMsg(IntPtr s, ulong id, int type, [MarshalAs(UnmanagedType.LPStr)] string body);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_Reply(IntPtr s, ulong id, [MarshalAs(UnmanagedType.LPStr)] string body);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_BGet(int pipe, IntPtr msg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_Free(int pipe);

    static IntPtr _rcm, _cf, _h;
    static IntPtr _eng; static int _user; static uint _appId;
    static ulong _target;
    static int _pipe;
    static Dictionary<string, int> _rmap, _fmap;
    static D_BGet _bget; static D_Free _bfree;

    // Live per-input control. The GUI flips these over stdin ("KBD 0", "CTRL 1", "ALL 0", ...).
    // The heartbeat continuously re-applies the desired state so Steam can't silently reset it.
    static readonly object _inputLock = new object();
    static int _iKbd = -1, _iMouse = -1, _iCtrl = -1;
    static volatile bool _wantKbd = true, _wantMouse = true, _wantCtrl = true;

    static T V<T>(IntPtr o, int i) where T : class
    { return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(o), i * 8), typeof(T)); }
    static int R(string k) { int v; return _rmap.TryGetValue(k, out v) ? v : -1; }
    static int F(string k) { int v; return _fmap.TryGetValue(k, out v) ? v : -1; }

    static void Pump(int ms)
    {
        IntPtr msg = Marshal.AllocHGlobal(64);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { bool any = false; while (_bget(_pipe, msg)) { any = true; if (_bfree != null) _bfree(_pipe); } if (!any) Thread.Sleep(25); }
        Marshal.FreeHGlobal(msg);
    }

    static uint ReadGroup() { int i = R("GetLocalRemotePlayTogetherGroupID"); if (i < 0) return 0; try { return V<D_U>(_rcm, i)(_rcm); } catch { return 0; } }

    // NOTE: Main is intentionally MTA (no [STAThread]). Steam's async RPT group creation does not
    // complete when the calling thread is STA (STA => group stays 0, MTA => group allocates).
    static int Main(string[] args)
    {
        string dir = @"C:\Program Files (x86)\Steam";
        Directory.SetCurrentDirectory(dir);
        _h = LoadLibrary(Path.Combine(dir, "steamclient64.dll"));
        var CI = (D_CI)Marshal.GetDelegateForFunctionPointer(GetProcAddress(_h, "CreateInterface"), typeof(D_CI));
        IntPtr cl = CI("SteamClient019", IntPtr.Zero);
        _pipe = V<D_I>(cl, 0)(cl);
        _user = V<D_Ii>(cl, 2)(cl, _pipe);
        IntPtr eng = CI("CLIENTENGINE_INTERFACE_VERSION005", IntPtr.Zero); _eng = eng;
        _rcm = V<D_PtrInt>(eng, 56)(eng, _pipe);
        // Do NOT acquire IClientFriends yet: obtaining the friends interface before creating the RPT
        // group makes BCreateRemotePlayGroup allocate nothing. We acquire it later, after the group exists.
        if (_rcm == IntPtr.Zero) { Console.WriteLine("RCM null — is Steam running & signed in?"); Hold(); return 2; }
        _bget = (D_BGet)Marshal.GetDelegateForFunctionPointer(GetProcAddress(_h, "Steam_BGetCallback"), typeof(D_BGet));
        _bfree = (D_Free)Marshal.GetDelegateForFunctionPointer(GetProcAddress(_h, "Steam_FreeLastCallback"), typeof(D_Free));
        _rmap = ScanNames(_rcm);

        // AppID: prefer an explicit arg (the GUI passes its resolved AppID); else read the registry.
        uint appId = 0;
        if (args.Length > 1) uint.TryParse(args[1], out appId);
        if (appId == 0) { try { object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0); if (v != null) appId = Convert.ToUInt32(v); } catch { } }
        _appId = appId;
        // Our own SteamID64 = individual base + logged-in account id (registry). Used to exclude the
        // host when scanning callbacks for guest SteamIDs (both carry the 0x01100001 signature).
        try { object av = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0); if (av != null) { uint acct = Convert.ToUInt32(av); if (acct != 0) _hostSteam = STEAMID_INDIVIDUAL_BASE + acct; } } catch { }
        if (appId == 0) { Console.WriteLine("No game running under Steam. Launch your game first, then rerun."); Hold(); return 4; }

        // Target: a friend SteamID64 (direct invite) OR "LINK"/0 for a shareable guest link that anyone
        // with Steam can join (the guest slot has no specific SteamID).
        bool linkMode = false;
        ulong target = 0;
        if (args.Length > 0)
        {
            if (args[0].Equals("LINK", StringComparison.OrdinalIgnoreCase) || args[0] == "0") linkMode = true;
            else if (!ulong.TryParse(args[0], out target)) linkMode = true;
        }
        else linkMode = true;
        if (!linkMode && target == 0) linkMode = true;
        _target = target; // 0 in link mode

        // ---- create the host Remote Play Together group ----
        int iShow0 = R("ShowRemotePlayTogetherUI");
        if (iShow0 >= 0)
        {
            try { V<D_VoidU>(_rcm, iShow0)(_rcm, appId); } catch { }
            // Reading the group id while it settles is what makes Steam bind it.
            for (int t = 1; t <= 5; t++) { Pump(500); ReadGroup(); }
        }

        int iCreate = R("BCreateRemotePlayGroup");
        try { V<D_U>(_rcm, iCreate)(_rcm); } catch { }
        uint group = 0; var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000) { Pump(150); group = ReadGroup(); if (group != 0) break; }
        if (group == 0) { Console.WriteLine("Could not create a host group — aborting."); Hold(); return 5; }

        // Create the first invite/session into the group, pump for the join URL, deliver it. The same
        // logic runs for each extra friend/link added to the LIVE session (see the ADD command).
        AddGuest(target, linkMode);

        // Open the host overlay for this session.
        int iShow = R("ShowRemotePlayTogetherUI");
        if (iShow >= 0) { try { V<D_VoidU>(_rcm, iShow)(_rcm, appId); } catch { } }

        int iUpdateAll = R("UpdateRemotePlayTogetherGroups");
        int iUpdateOne = R("UpdateRemotePlayTogetherGroup");
        _iKbd = R("SetPerUserKeyboardInputEnabled");
        _iMouse = R("SetPerUserMouseInputEnabled");
        _iCtrl = R("SetPerUserControllerInputEnabled");

        // Listen for live commands from the GUI on stdin.
        Thread cmdThread = new Thread(CommandLoop); cmdThread.IsBackground = true; cmdThread.Start();

        // Stay alive: pump callbacks, publish group membership so connected guests register as players,
        // discover guests, add queued guests, and re-assert the desired control state on a heartbeat.
        IntPtr m2 = Marshal.AllocHGlobal(64);
        var pub = Stopwatch.StartNew();
        while (true)
        {
            bool any = false;
            while (_bget(_pipe, m2))
            {
                any = true;
                int cb = Marshal.ReadInt32(m2, 4);
                if (cb >= 1100000 && cb < 1100100 && cb != RPT_INVITE_RESULT)
                {
                    // 1100042 = active streaming heartbeat -> a guest is really connected now.
                    if (cb == 1100042 && !_guestConnected) { _guestConnected = true; Console.WriteLine("GUEST CONNECTED"); }
                    IntPtr param = Marshal.ReadIntPtr(m2, 8);
                    if (param != IntPtr.Zero) DiscoverPlayers(param);
                    if (cb == 1100026 || cb == 1100017 || cb == 1100033 || cb == 1100058 || cb == 1100032)
                        PublishGroup(iUpdateAll, iUpdateOne, appId, !_refreshedOnce);
                }
                if (_bfree != null) _bfree(_pipe);
            }
            // Add another guest to the LIVE session if the GUI queued one (on THIS thread, so the
            // callback pipe has a single reader). Existing guests stay connected.
            AddReq pending = null;
            lock (_addQueue) { if (_addQueue.Count > 0) pending = _addQueue.Dequeue(); }
            if (pending != null) AddGuest(pending.Target, pending.Link);
            if (pub.ElapsedMilliseconds > 2000)
            {
                PublishGroup(iUpdateAll, iUpdateOne, appId, false);
                if (_guestConnected) ApplyControl();
                pub.Restart();
            }
            if (!any) Thread.Sleep(200);
        }
    }

    static bool _refreshedOnce = false;

    // Publish group membership so a connected guest's streaming session shows as a player in the overlay
    // list. UpdateRemotePlayTogetherGroups is no-arg; the singular variant takes the group id. Only
    // re-open the overlay once, to avoid spamming it open.
    static void PublishGroup(int iUpdateAll, int iUpdateOne, uint appId, bool showUi)
    {
        try
        {
            if (iUpdateAll >= 0) V<D_Void>(_rcm, iUpdateAll)(_rcm);
            if (iUpdateOne >= 0) { uint g = ReadGroup(); if (g != 0) V<D_VoidU>(_rcm, iUpdateOne)(_rcm, g); }
            if (showUi)
            {
                int iShow = R("ShowRemotePlayTogetherUI");
                if (iShow >= 0) V<D_VoidU>(_rcm, iShow)(_rcm, appId);
                _refreshedOnce = true;
            }
        }
        catch { }
    }

    static bool _guestConnected = false;
    static bool _grantLogged = false;
    static IntPtr _grantBuf = IntPtr.Zero;

    // One connected guest. Discovered by reading its SteamID + guest id out of connect callbacks
    // (read-only). Each keeps its own desired input state so control can be flipped per person.
    sealed class GPlayer { public ulong Steam; public uint Guest; public string Name; public bool Kbd = true, Mouse = true, Ctrl = true; }
    static readonly List<GPlayer> _players = new List<GPlayer>();
    static ulong _hostSteam = 0;   // our own SteamID — excluded so we never toggle the host's input

    // A queued request to add another guest (friend or link) to the LIVE group. Processed on the main
    // pump thread so only one thread ever reads the Steam callback pipe (concurrent readers are unsafe).
    sealed class AddReq { public ulong Target; public bool Link; }
    static readonly Queue<AddReq> _addQueue = new Queue<AddReq>();

    // Read live commands from stdin (sent by the GUI) and apply them immediately.
    //   Global: KBD|MOUSE|CTRL|ALL 0|1        (also accepts ON/OFF and full words)
    //   Per player: PKBD|PMOUSE|PCTRL|PALL <slot> 0|1
    //   ADD <friendSteamID64|LINK>            add another guest to the live session
    //   STOP                                  disconnect everyone and end the session
    static void CommandLoop()
    {
        try
        {
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                line = line.Trim().ToUpperInvariant();
                if (line.Length == 0) continue;
                string[] p = line.Split(new char[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
                if (p[0] == "STOP" || p[0] == "QUIT" || p[0] == "END" || p[0] == "KICK" || p[0] == "EXIT")
                {
                    Teardown();
                    Environment.Exit(0);
                }
                // Add another friend/link to the running session (keeps existing guests connected).
                if (p[0] == "ADD")
                {
                    if (p.Length < 2) continue;
                    AddReq req = new AddReq();
                    if (p[1] == "LINK" || p[1] == "0" || !ulong.TryParse(p[1], out req.Target)) req.Link = true;
                    if (!req.Link && req.Target == 0) req.Link = true;
                    lock (_addQueue) _addQueue.Enqueue(req);
                    continue;
                }
                // Per-player commands from the roster rows: P<field> <slot> <0|1>.
                if (p[0] == "PKBD" || p[0] == "PMOUSE" || p[0] == "PCTRL" || p[0] == "PALL")
                {
                    if (p.Length < 3) continue;
                    int slot; if (!int.TryParse(p[1], out slot)) continue;
                    bool pon = (p[2] == "1" || p[2] == "ON" || p[2] == "TRUE" || p[2] == "YES");
                    GPlayer gp = null; lock (_players) { if (slot >= 1 && slot <= _players.Count) gp = _players[slot - 1]; }
                    if (gp == null) continue;
                    if (p[0] == "PKBD") gp.Kbd = pon;
                    else if (p[0] == "PMOUSE") gp.Mouse = pon;
                    else if (p[0] == "PCTRL") gp.Ctrl = pon;
                    else { gp.Kbd = pon; gp.Mouse = pon; gp.Ctrl = pon; }
                    EmitPlayer(slot);
                    ApplyControl();
                    continue;
                }

                if (p.Length < 2) continue;
                bool on = (p[1] == "1" || p[1] == "ON" || p[1] == "TRUE" || p[1] == "YES");
                bool changed = true;
                switch (p[0])
                {
                    case "KBD": case "KEYBOARD": _wantKbd = on; break;
                    case "MOUSE": _wantMouse = on; break;
                    case "CTRL": case "CONTROLLER": case "PAD": case "GAMEPAD": _wantCtrl = on; break;
                    case "ALL": _wantKbd = on; _wantMouse = on; _wantCtrl = on; break;
                    default: changed = false; break;
                }
                if (!changed) continue;
                // A global command applies to everyone — mirror it into every player's state.
                lock (_players)
                {
                    foreach (GPlayer gp in _players)
                    {
                        switch (p[0])
                        {
                            case "KBD": case "KEYBOARD": gp.Kbd = on; break;
                            case "MOUSE": gp.Mouse = on; break;
                            case "CTRL": case "CONTROLLER": case "PAD": case "GAMEPAD": gp.Ctrl = on; break;
                            case "ALL": gp.Kbd = on; gp.Mouse = on; gp.Ctrl = on; break;
                        }
                    }
                }
                Console.WriteLine("INPUT KBD=" + (_wantKbd ? 1 : 0) + " MOUSE=" + (_wantMouse ? 1 : 0) + " CTRL=" + (_wantCtrl ? 1 : 0));
                for (int si = 1; si <= _players.Count; si++) EmitPlayer(si);
                ApplyControl();
            }
        }
        catch { }
    }

    // Enable/disable keyboard/mouse/controller for each connected guest to match its desired state
    // (the same thing clicking the overlay control icons does). These methods take a RemotePlayPlayer_t*
    // (40-byte struct), NOT (group,guest) ints. Called on the heartbeat and on live toggles.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static void ApplyControl()
    {
        uint g = ReadGroup();
        if (g == 0) return;
        lock (_inputLock)
        {
            if (_grantBuf == IntPtr.Zero) _grantBuf = Marshal.AllocHGlobal(40);
            try
            {
                GPlayer[] arr; lock (_players) { arr = _players.ToArray(); }
                if (arr.Length == 0)
                {
                    // No guest resolved yet — single-target fallback (guest ids 1..2), global want state.
                    for (uint guest = 1; guest <= 2; guest++)
                        SetInputsFor(_target, guest, g, _wantKbd, _wantMouse, _wantCtrl);
                }
                else
                {
                    // Per player: write to that guest's OWN id — the TRUE id read from Steam's callback
                    // (p.Guest), or join-order as a fallback. Never sweep all ids: with 2+ guests that
                    // cross-writes each other's slots, so revoking one person gets undone by the next.
                    int n = arr.Length;
                    for (int i = 0; i < n; i++)
                    {
                        GPlayer p = arr[i];
                        if (p.Steam == 0) continue;
                        uint gid = p.Guest != 0 ? p.Guest : (uint)(i + 1);
                        SetInputsFor(p.Steam, gid, g, p.Kbd, p.Mouse, p.Ctrl);
                    }
                }
                if (!_grantLogged) { Console.WriteLine("granted keyboard/mouse/controller input"); _grantLogged = true; }
            }
            catch { }
        }
    }

    // Write a RemotePlayPlayer_t {playerID, guestID, group} into the shared buffer and push all three
    // per-user input settings for it. Caller holds _inputLock.
    static void SetInputsFor(ulong steam, uint guest, uint group, bool k, bool m, bool c)
    {
        Marshal.WriteInt64(_grantBuf, 0, (long)steam);
        Marshal.WriteInt32(_grantBuf, 8, (int)guest);
        Marshal.WriteInt32(_grantBuf, 12, (int)group);
        Marshal.WriteInt64(_grantBuf, 16, 0); Marshal.WriteInt64(_grantBuf, 24, 0);
        Marshal.WriteInt32(_grantBuf, 32, 0); Marshal.WriteInt32(_grantBuf, 36, 0);
        if (_iKbd >= 0) V<D_SetInputP>(_rcm, _iKbd)(_rcm, _grantBuf, k);
        if (_iMouse >= 0) V<D_SetInputP>(_rcm, _iMouse)(_rcm, _grantBuf, m);
        if (_iCtrl >= 0) V<D_SetInputP>(_rcm, _iCtrl)(_rcm, _grantBuf, c);
    }

    // Lowest-index vtable slot whose name contains ALL of mustAll and NONE of mustNone (case-insensitive).
    static int FindEntry(Dictionary<string, int> map, string[] mustAll, string[] mustNone, out string name)
    {
        name = null; int best = -1;
        foreach (KeyValuePair<string, int> kv in map)
        {
            string n = kv.Key; bool ok = true;
            if (mustAll != null) foreach (string s in mustAll) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0) { ok = false; break; }
            if (ok && mustNone != null) foreach (string s in mustNone) if (n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) { ok = false; break; }
            if (ok && (best < 0 || kv.Value < best)) { best = kv.Value; name = n; }
        }
        return best;
    }

    // Fully end the session so guests STOP watching: disable all input, then disband the RPT group.
    // Killing the process alone leaves Steam's stream alive, so a guest would keep watching.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static void Teardown()
    {
        try { _wantKbd = _wantMouse = _wantCtrl = false; lock (_players) { foreach (GPlayer gp in _players) gp.Kbd = gp.Mouse = gp.Ctrl = false; } ApplyControl(); } catch { }
        try
        {
            uint g = ReadGroup();
            // DisbandRemotePlayTogetherGroup(group) disconnects the guests. Fall back to a verb scan if
            // a future build renames it.
            string nm; int idx = R("DisbandRemotePlayTogetherGroup");
            if (idx < 0 && (_rmap == null || !_rmap.TryGetValue("DestroyRemotePlayGroup", out idx))) idx = -1;
            if (idx < 0)
            {
                string[] verbs = { "Disband", "Destroy", "Leave", "Disconnect", "Remove", "Close", "End", "Kick", "Cancel" };
                foreach (string v in verbs)
                {
                    idx = FindEntry(_rmap, new string[] { v, "RemotePlay", "Group" },
                                    new string[] { "Update", "Get", "Create", "Available", "Local", "Show", "Set" }, out nm);
                    if (idx >= 0) break;
                }
            }
            if (idx >= 0)
            {
                try { if (g != 0) { V<D_VoidU>(_rcm, idx)(_rcm, g); return; } } catch { }
                try { V<D_Void>(_rcm, idx)(_rcm); } catch { }
            }
        }
        catch { }
    }

    // Read guests out of a Steam callback payload — pure read-only, never calls into steamclient64.dll
    // with guessed args. A connect callback carries a RemotePlayPlayer_t {SteamID@0, guestId@8, groupId@12}.
    // An individual SteamID64 has a fixed high dword ((id>>32)==0x01100001), so we locate the SteamID and
    // read the adjacent guestId+groupId. If groupId matches our LIVE group it's a real RemotePlayPlayer_t
    // and guestId is the guest's true id (what SetPerUser* needs). Excludes the host's own SteamID.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static void DiscoverPlayers(IntPtr param)
    {
        uint curGroup = ReadGroup();
        try
        {
            for (int off = 0; off + 16 <= 512; off += 4)
            {
                ulong v;
                try { v = (ulong)Marshal.ReadInt64(param, off); } catch { break; }
                if ((v >> 32) != 0x01100001UL || (uint)v == 0) continue;

                uint guestCand = 0, grpCand = 0;
                try { guestCand = (uint)Marshal.ReadInt32(param, off + 8); grpCand = (uint)Marshal.ReadInt32(param, off + 12); } catch { }
                bool structShape = (curGroup != 0 && grpCand == curGroup && guestCand >= 1 && guestCand <= 64);

                if (v == _hostSteam || v <= STEAMID_INDIVIDUAL_BASE) continue;

                GPlayer existing = null;
                lock (_players) { foreach (GPlayer pp in _players) if (pp.Steam == v) { existing = pp; break; } }
                if (existing != null)
                {
                    // Learn/upgrade a guest id we didn't have yet, then re-assert control on the right slot.
                    if (structShape && existing.Guest != guestCand)
                    {
                        existing.Guest = guestCand;
                        _grantLogged = false; ApplyControl();
                    }
                    continue;
                }

                GPlayer np = new GPlayer(); np.Steam = v; np.Guest = structShape ? guestCand : 0; np.Name = LookupName(v);
                int slot; lock (_players) { _players.Add(np); slot = _players.Count; }
                if (string.IsNullOrEmpty(np.Name)) np.Name = "Player " + slot;
                if (_target == 0) { _target = v; _grantLogged = false; }
                Console.WriteLine("PLAYER JOINED " + np.Name);
                EmitPlayer(slot);
                ApplyControl();
            }
        }
        catch { }
    }

    // Emit one player's current state as a machine-readable line the GUI parses:
    //   PLAYER slot=1 steam=765... k=1 m=1 c=1 name=Bob
    static void EmitPlayer(int slot)
    {
        GPlayer p; lock (_players) { if (slot < 1 || slot > _players.Count) return; p = _players[slot - 1]; }
        Console.WriteLine("PLAYER slot=" + slot + " steam=" + p.Steam
            + " k=" + (p.Kbd ? 1 : 0) + " m=" + (p.Mouse ? 1 : 0) + " c=" + (p.Ctrl ? 1 : 0)
            + " name=" + p.Name);
    }

    // Lazily acquire IClientFriends (only safe AFTER the group exists) so we can resolve guest names.
    static void EnsureFriends()
    {
        if (_cf != IntPtr.Zero) return;
        try { _cf = V<D_PtrIntInt>(_eng, 13)(_eng, _user, _pipe); if (_cf != IntPtr.Zero) _fmap = ScanNames(_cf); } catch { }
    }

    // Best-effort persona name for a SteamID (blank for non-friends Steam doesn't know).
    static string LookupName(ulong steam)
    {
        EnsureFriends();
        try
        {
            int i = F("GetFriendPersonaName");
            if (i >= 0 && _cf != IntPtr.Zero)
            {
                string s = Marshal.PtrToStringAnsi(V<D_NameOfId>(_cf, i)(_cf, steam));
                if (!string.IsNullOrEmpty(s) && s != "[unknown]" && s != "?") return s;
            }
        }
        catch { }
        return null;
    }

    // Create one invite/session into the CURRENT group (a fresh guest id each time, so multiple guests
    // coexist), pump for its join URL, and deliver it: link mode surfaces the URL (GUI copies it); friend
    // mode also sends it to that friend in Steam chat. Runs on the main pump thread only.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static bool AddGuest(ulong target, bool link)
    {
        uint group = ReadGroup();
        if (group == 0) return false;

        uint guest = 0; int iG = R("GetAvailableRemotePlayTogetherGuestID");
        if (iG >= 0) { try { guest = V<D_Uu>(_rcm, iG)(_rcm, group); } catch { } }
        if (guest == 0) guest = 1;

        IntPtr player = Marshal.AllocHGlobal(40);
        Marshal.WriteInt64(player, 0, (long)target);
        Marshal.WriteInt32(player, 8, (int)guest);
        Marshal.WriteInt32(player, 12, (int)group);
        Marshal.WriteInt64(player, 16, 0); Marshal.WriteInt64(player, 24, 0); Marshal.WriteInt32(player, 32, 0); Marshal.WriteInt32(player, 36, 0);
        bool created = false;
        try { created = V<D_CreateInvite>(_rcm, R("BCreateRemotePlayInviteAndSession"))(_rcm, player, _appId); } catch { }
        Marshal.FreeHGlobal(player);
        Console.WriteLine("BCreateRemotePlayInviteAndSession=" + created);

        string url = PumpForUrl(8000);
        if (url != null) Console.WriteLine((link ? "GUEST LINK = " : "JOIN URL = ") + url);

        if (link)
        {
            if (url != null) { try { SetClipboard(url); } catch { } }
        }
        else
        {
            EnsureFriends();   // safe now — the group already exists
            if (url != null && _cf != IntPtr.Zero)
            {
                bool sent = false;
                try { int i = F("SendMsgToFriend"); if (i >= 0) sent = V<D_SendMsg>(_cf, i)(_cf, target, k_EChatEntryTypeChatMsg, "Join my Remote Play Together game: " + url); } catch { }
                if (!sent) { try { int i = F("ReplyToFriendMessage"); if (i >= 0) V<D_Reply>(_cf, i)(_cf, target, "Join my Remote Play Together game: " + url); } catch { } }
                try { SetClipboard(url); } catch { }
            }
        }
        return created;
    }

    // Pump the callback pipe until a RemotePlayInviteResult (join URL) arrives or the timeout elapses.
    // Only ever called on the main thread, so it never races another pipe reader.
    static string PumpForUrl(int ms)
    {
        string url = null;
        IntPtr msg = Marshal.AllocHGlobal(64);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms && url == null)
        {
            bool any = false;
            while (_bget(_pipe, msg))
            {
                any = true;
                int cb = Marshal.ReadInt32(msg, 4); IntPtr param = Marshal.ReadIntPtr(msg, 8);
                if (cb == RPT_INVITE_RESULT && param != IntPtr.Zero)
                {
                    string u = Marshal.PtrToStringAnsi(new IntPtr(param.ToInt64() + 56));
                    if (!string.IsNullOrEmpty(u)) url = u;
                }
                if (_bfree != null) _bfree(_pipe);
            }
            if (!any) Thread.Sleep(30);
        }
        Marshal.FreeHGlobal(msg);
        return url;
    }

    static void Hold() { try { Console.ReadLine(); } catch { } }

    static void SetClipboard(string s)
    {
        // Clipboard needs an STA thread, but Main must stay MTA (see note on Main). Run it isolated.
        try
        {
            var t = new Thread(delegate() { try { System.Windows.Forms.Clipboard.SetText(s); } catch { } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start(); t.Join(2000);
        }
        catch { }
    }

    // Read the method name embedded in each vtable stub (a telemetry string referenced via `lea rip`),
    // building a name -> index map so calls resolve by name and self-adapt to any Steam build.
    static Dictionary<string, int> ScanNames(IntPtr obj)
    {
        var map = new Dictionary<string, int>();
        long lo = 0, hi = 0;
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            if (string.Equals(m.ModuleName, "steamclient64.dll", StringComparison.OrdinalIgnoreCase))
            { lo = m.BaseAddress.ToInt64(); hi = lo + m.ModuleMemorySize; break; }
        IntPtr vt = Marshal.ReadIntPtr(obj);
        for (int i = 0; i < 400; i++)
        {
            long fn = Marshal.ReadIntPtr(vt, i * 8).ToInt64();
            if (fn < lo || fn >= hi) break;
            string nm = NameOf(fn, lo, hi);
            if (nm != null && !map.ContainsKey(nm)) map[nm] = i;
        }
        return map;
    }

    static string NameOf(long fn, long lo, long hi)
    {
        for (int k = 0; k < 260; k++)
        {
            byte rex = Marshal.ReadByte(new IntPtr(fn + k));
            if (rex != 0x48 && rex != 0x4C && rex != 0x49 && rex != 0x4D) continue;
            if (Marshal.ReadByte(new IntPtr(fn + k + 1)) != 0x8D) continue;
            if ((Marshal.ReadByte(new IntPtr(fn + k + 2)) & 0xC7) != 0x05) continue;
            long target = fn + k + 7 + Marshal.ReadInt32(new IntPtr(fn + k + 3));
            if (target < lo || target >= hi) continue;
            var sb = new StringBuilder(); bool bad = false;
            for (int j = 0; j < 96; j++)
            { byte c = Marshal.ReadByte(new IntPtr(target + j)); if (c == 0) break; if (c < 32 || c > 126) { bad = true; break; } sb.Append((char)c); }
            if (bad) continue;
            string s = sb.ToString();
            if (s.Length >= 6 && s.IndexOf(' ') < 0) { int letters = 0; foreach (char c in s) if (char.IsLetter(c)) letters++; if (letters >= 4) return s; }
        }
        return null;
    }
}
