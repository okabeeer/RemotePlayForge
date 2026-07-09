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
    const int k_EFriendFlagImmediate = 0x04;
    const int k_EChatEntryTypeChatMsg = 1;

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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_VoidB(IntPtr s, [MarshalAs(UnmanagedType.I1)] bool a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_Void(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_SetInputP(IntPtr s, IntPtr player, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_Bool(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CountFlags(IntPtr s, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_ByIndex(IntPtr s, IntPtr ret, int i, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_StateOfId(IntPtr s, ulong id);
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

    // Locate the Steam install from the registry so we work no matter which drive Steam is on
    // (e.g. D:\Steam). Falls back to the default only if the registry has nothing usable.
    static string SteamDir()
    {
        try
        {
            object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
            if (v != null) { string s = ((string)v).Replace('/', '\\'); if (Directory.Exists(s)) return s; }
        }
        catch { }
        try
        {
            object v = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null);
            if (v != null) { string s = (string)v; if (Directory.Exists(s)) return s; }
        }
        catch { }
        return @"C:\Program Files (x86)\Steam";
    }

    // NOTE: Main is intentionally MTA (no [STAThread]). Steam's async RPT group creation does not
    // complete when the calling thread is STA — verified: STA => group stays 0, MTA => group allocates.
    static int Main(string[] args)
    {
        string dir = SteamDir();
        string dll = Path.Combine(dir, "steamclient64.dll");
        if (!File.Exists(dll)) { Console.WriteLine("steamclient64.dll not found at " + dll + " — could not locate your Steam install."); Hold(); return 3; }
        // cwd must be the Steam dir so steamclient64.dll's own dependencies (tier0_s64.dll, etc.) resolve.
        Directory.SetCurrentDirectory(dir);
        _h = LoadLibrary(dll);
        if (_h == IntPtr.Zero) { Console.WriteLine("Could not load steamclient64.dll from " + dir + " (err " + Marshal.GetLastWin32Error() + ")."); Hold(); return 3; }
        var CI = (D_CI)Marshal.GetDelegateForFunctionPointer(GetProcAddress(_h, "CreateInterface"), typeof(D_CI));
        IntPtr cl = CI("SteamClient019", IntPtr.Zero);
        _pipe = V<D_I>(cl, 0)(cl);
        int user = V<D_Ii>(cl, 2)(cl, _pipe); _user = user;
        IntPtr eng = CI("CLIENTENGINE_INTERFACE_VERSION005", IntPtr.Zero); _eng = eng;
        _rcm = V<D_PtrInt>(eng, 56)(eng, _pipe);
        // IMPORTANT: do NOT acquire IClientFriends yet. Verified live that obtaining the friends
        // interface before creating the RPT group makes BCreateRemotePlayGroup allocate nothing
        // (group stays 0). We acquire friends LATER, only to send the URL, after the group exists.
        if (_rcm == IntPtr.Zero) { Console.WriteLine("RCM null — is Steam running & signed in?"); Hold(); return 2; }
        _bget = (D_BGet)Marshal.GetDelegateForFunctionPointer(GetProcAddress(_h, "Steam_BGetCallback"), typeof(D_BGet));
        _bfree = (D_Free)Marshal.GetDelegateForFunctionPointer(GetProcAddress(_h, "Steam_FreeLastCallback"), typeof(D_Free));
        _rmap = ScanNames(_rcm);
        DumpMethods("IClientRemoteClientManager", _rmap);

        // AppID: prefer an explicit arg2 (the GUI passes its resolved AppID); else read the registry.
        uint appId = 0;
        if (args.Length > 1) uint.TryParse(args[1], out appId);
        if (appId == 0) { try { object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0); if (v != null) appId = Convert.ToUInt32(v); } catch { } }
        Console.WriteLine("AppID = " + appId);
        _appId = appId;
        // Our own SteamID64 = individual base + the logged-in account id (from the registry). Used to
        // exclude the host when scanning callbacks for guest SteamIDs (both have the 0x01100001 signature).
        try { object av = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0); if (av != null) { uint acct = Convert.ToUInt32(av); if (acct != 0) _hostSteam = 76561197960265728UL + acct; } } catch { }
        if (appId == 0) { Console.WriteLine("No game running under Steam. Launch your game first, then rerun."); Hold(); return 4; }

        // If Steam isn't tracking this AppID as its running game, the game was launched OUTSIDE Steam —
        // e.g. EA FC 26 through the EA App. Steam then has no game window hooked to capture, so enable
        // DESKTOP streaming (share the whole screen), which works regardless of how the game launched.
        // For a normally-launched Steam game the ids match and we leave the default game-window capture.
        uint steamTracked = 0;
        try { object rv = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0); if (rv != null) steamTracked = Convert.ToUInt32(rv); } catch { }
        if (appId != steamTracked)
        {
            int iDesk = R("SetStreamingDesktopToRemotePlayTogetherEnabled");
            if (iDesk >= 0)
            {
                try { V<D_VoidB>(_rcm, iDesk)(_rcm, true); Console.WriteLine("Enabled desktop streaming (AppID " + appId + " launched outside Steam)."); }
                catch (Exception e) { Console.WriteLine("  desktop-stream enable threw " + e.GetType().Name); }
            }
            else if (_diag) Console.WriteLine("DIAG SetStreamingDesktopToRemotePlayTogetherEnabled not found in RCM.");
        }

        // resolve target: a friend SteamID64 (direct invite) OR "LINK"/0 for a shareable guest link
        // that anyone with Steam can join — no friendship required (guest slot has no specific
        // SteamID). Friend name is looked up later once the friends interface is acquired.
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
        Console.WriteLine(linkMode ? "Mode: GUEST LINK (shareable — no specific friend)"
                                   : ("Inviting friend: [" + target + "]"));

        // ---- group creation: mirror GroupProbe EXACTLY (it works reliably) ----
        Console.WriteLine("idx: Show=" + R("ShowRemotePlayTogetherUI") + " Create=" + R("BCreateRemotePlayGroup")
            + " LocalGroup=" + R("GetLocalRemotePlayTogetherGroupID") + " Guest=" + R("GetAvailableRemotePlayTogetherGuestID"));

        int iShow0 = R("ShowRemotePlayTogetherUI");
        if (iShow0 >= 0)
        {
            Console.WriteLine("calling ShowRemotePlayTogetherUI(" + appId + ") ...");
            try { V<D_VoidU>(_rcm, iShow0)(_rcm, appId); } catch (Exception e) { Console.WriteLine("  threw " + e.GetType().Name); }
            for (int t = 1; t <= 5; t++) { Pump(500); Console.WriteLine("group +" + (t * 500) + "ms after ShowUI = " + ReadGroup()); }
        }

        int iCreate = R("BCreateRemotePlayGroup");
        uint ret = 0; try { ret = V<D_U>(_rcm, iCreate)(_rcm); } catch (Exception e) { Console.WriteLine("BCreate threw " + e.GetType().Name); }
        Console.WriteLine("BCreateRemotePlayGroup returned = " + ret + " (0x" + ret.ToString("X") + ")");
        uint group = 0; var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000) { Pump(150); group = ReadGroup(); if (group != 0) break; }
        Console.WriteLine("group AFTER polling = " + group + " (took " + sw.ElapsedMilliseconds + "ms)");
        if (group == 0) { Console.WriteLine("Could not create a host group — aborting."); Hold(); return 5; }

        // 2-5) create the first invite/session into the group, pump for the join URL, deliver it.
        // The SAME logic runs later for each extra friend/link added to the LIVE session (see ADD).
        AddGuest(target, linkMode);

        // 6) open the host overlay for this session
        int iShow = R("ShowRemotePlayTogetherUI");
        if (iShow >= 0) { try { V<D_VoidU>(_rcm, iShow)(_rcm, appId); Console.WriteLine("Opened Remote Play Together overlay."); } catch { } }

        Console.WriteLine("\n================================================================");
        Console.WriteLine(" SESSION LIVE. Keep THIS window open until your friend joins.");
        Console.WriteLine(" Alt-Tab into your GAME: your friend should appear in the RPT");
        Console.WriteLine(" player list with controller/keyboard/mouse icons to grant control.");
        Console.WriteLine(" Press Ctrl+C or close this window to end the session.");
        Console.WriteLine("================================================================");

        int iUpdateAll = R("UpdateRemotePlayTogetherGroups");
        int iUpdateOne = R("UpdateRemotePlayTogetherGroup");
        int iKbd = R("SetPerUserKeyboardInputEnabled");
        int iMouse = R("SetPerUserMouseInputEnabled");
        int iCtrl = R("SetPerUserControllerInputEnabled");
        Console.WriteLine("idx: UpdateGroups=" + iUpdateAll + " UpdateGroup=" + iUpdateOne
            + " Kbd=" + iKbd + " Mouse=" + iMouse + " Ctrl=" + iCtrl);

        // Expose the input-method indices to the stdin command thread + heartbeat, then start
        // listening for live toggle commands from the GUI.
        _iKbd = iKbd; _iMouse = iMouse; _iCtrl = iCtrl;
        Thread cmdThread = new Thread(CommandLoop); cmdThread.IsBackground = true; cmdThread.Start();
        Console.WriteLine("READY — live input toggles active (KBD/MOUSE/CTRL 0|1, ALL 0|1).");

        // 7) stay alive, pumping callbacks. When the guest connects, PUBLISH the group so their
        // streaming session is registered as a player in the overlay list (UpdateRemotePlayTogetherGroups)
        // and refresh the overlay UI. We also do this periodically as a heartbeat.
        var seen = new Dictionary<int, int>();
        IntPtr m2 = Marshal.AllocHGlobal(64);
        var pub = Stopwatch.StartNew();
        while (true)
        {
            bool any = false;
            while (_bget(_pipe, m2))
            {
                any = true;
                int cb = Marshal.ReadInt32(m2, 4);
                int c; seen.TryGetValue(cb, out c); seen[cb] = c + 1;
                if (cb >= 1100000 && cb < 1100100 && cb != RPT_INVITE_RESULT)
                {
                    Console.WriteLine("  [callback " + cb + "] (group id now = " + ReadGroup() + ")");
                    // 1100042 = active streaming heartbeat -> the guest is really connected now.
                    if (cb == 1100042 && !_guestConnected) { _guestConnected = true; Console.WriteLine("GUEST CONNECTED"); }
                    // Discover connected guests: read any guest SteamIDs out of this callback's payload
                    // (read-only, cannot crash Steam) and add each as a player. This is how both link
                    // control AND the multi-guest roster work — each distinct guest becomes a row.
                    {
                        IntPtr param = Marshal.ReadIntPtr(m2, 8);
                        if (param != IntPtr.Zero) DiscoverPlayers(param);
                    }
                    // guest-connect-ish events: publish group membership (+ refresh overlay once)
                    if (cb == 1100026 || cb == 1100017 || cb == 1100033 || cb == 1100058 || cb == 1100032)
                        PublishGroup(iUpdateAll, iUpdateOne, appId, !_refreshedOnce, "on cb " + cb);
                }
                if (_bfree != null) _bfree(_pipe);
            }
            // Add another guest to the LIVE session if the GUI queued one (runs on THIS thread so the
            // callback pipe has a single reader). Existing guests stay connected.
            AddReq pending = null;
            lock (_addQueue) { if (_addQueue.Count > 0) pending = _addQueue.Dequeue(); }
            if (pending != null) AddGuest(pending.Target, pending.Link);
            if (pub.ElapsedMilliseconds > 2000)
            {
                PublishGroup(iUpdateAll, iUpdateOne, appId, false, "heartbeat");
                if (_guestConnected)
                {
                    // The link-guest's SteamID is resolved from the connect callback payload (see the
                    // callback loop) — a read-only scan, never a speculative Steam call. Just re-assert
                    // control here; _target is filled once a callback carrying the SteamID arrives.
                    int pc; lock (_players) pc = _players.Count;
                    if (pc == 0 && !_emptyWarned) { _emptyWarned = true; if (_diag) Console.WriteLine("DIAG guest connected but NO SteamID found in any callback payload yet — cannot target control."); }
                    ApplyControl("heartbeat");
                }
                pub.Restart();
            }
            if (!any) Thread.Sleep(200);
        }
    }

    static bool _refreshedOnce = false;

    // Publish group membership (so a connected guest's streaming session becomes a player in the
    // overlay list). UpdateRemotePlayTogetherGroups is no-arg; the singular variant takes the group id.
    // Only re-open the overlay (showUi) once, to avoid spamming it open.
    static void PublishGroup(int iUpdateAll, int iUpdateOne, uint appId, bool showUi, string why)
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
            Console.WriteLine("  -> published group" + (showUi ? " + refreshed overlay" : "") + " (" + why + ")");
        }
        catch (Exception e) { Console.WriteLine("  -> PublishGroup threw " + e.GetType().Name + " (" + why + ")"); }
    }

    static bool _guestConnected = false;
    static bool _emptyWarned = false;   // one-shot: warned that a guest connected but no SteamID resolved
    static bool _grantLogged = false;
    static IntPtr _grantBuf = IntPtr.Zero;
    // One connected guest. Discovered by reading SteamIDs out of connect callbacks (read-only).
    // Each keeps its own desired input state so control can be flipped per person.
    sealed class GPlayer { public ulong Steam; public uint Guest; public string Name; public bool Kbd = true, Mouse = true, Ctrl = true; }
    static readonly List<GPlayer> _players = new List<GPlayer>();
    static ulong _hostSteam = 0;   // our own SteamID — excluded so we never toggle the host's input

    // A queued request to add another guest (friend or link) to the LIVE group. Processed on the main
    // pump thread so only one thread ever reads the Steam callback pipe (concurrent readers are unsafe).
    sealed class AddReq { public ulong Target; public bool Link; }
    static readonly Queue<AddReq> _addQueue = new Queue<AddReq>();
    static readonly HashSet<ulong> _diagSeen = new HashSet<ulong>();   // dedupe candidate DIAG logging
    static readonly HashSet<string> _grpDiagSeen = new HashSet<string>();   // dedupe group-anchored slot DIAG
    static uint _maxInvitedGuest;   // highest guest id we actually created an invite for (bounds anonymous-guest acceptance)
    static readonly bool _diag = Environment.GetEnvironmentVariable("RPFORGE_DIAG") == "1";   // verbose DIAG logging (off for release)

    // Read live toggle commands from stdin (sent by the GUI) and apply them immediately.
    // Protocol (case-insensitive, one per line):
    //   KBD 0|1 | MOUSE 0|1 | CTRL 0|1 | ALL 0|1   (also accepts ON/OFF and full words)
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
                    Console.WriteLine("STOPPING — disconnecting the guest and ending the session.");
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
                    Console.WriteLine("queued ADD " + (req.Link ? "LINK" : req.Target.ToString()));
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
                    ApplyControl("player " + slot);
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
                ApplyControl("stdin");
            }
        }
        catch { }
    }

    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    // Enable/disable keyboard/mouse/controller input for the connected guest to match the desired
    // (_wantKbd/_wantMouse/_wantCtrl) state — the same thing clicking the overlay control icons does.
    // These methods take a RemotePlayPlayer_t* (the same 40-byte struct as the invite), NOT
    // (group,guest) ints. Steam identified the guest as "guest1"; we cover guest ids 1..2 to be safe.
    // Called both on the heartbeat (to re-assert state) and from the stdin thread (on a live toggle).
    static void ApplyControl(string why)
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
                    // No guest resolved yet — legacy single-target sweep (guest ids 1..2), global want state.
                    for (uint guest = 1; guest <= 2; guest++)
                        SetInputsFor(_target, guest, g, _wantKbd, _wantMouse, _wantCtrl);
                }
                else
                {
                    // Per player: write to that guest's OWN id. Prefer the TRUE id read from Steam's
                    // callback (p.Guest); fall back to join-order only if we haven't seen it yet. Never
                    // sweep all ids — with 2+ guests that cross-writes each other's slots so revoking one
                    // person's input gets undone by the next player's write.
                    int n = arr.Length;
                    for (int i = 0; i < n; i++)
                    {
                        GPlayer p = arr[i];
                        // p.Steam == 0 is a valid no-account Steam Link guest (identified by guest id),
                        // so don't skip it — Steam applies per-user input by the {steam, guest, group}
                        // slot and steam=0 is exactly what the working link path already used.
                        uint gid = p.Guest != 0 ? p.Guest : (uint)(i + 1);
                        SetInputsFor(p.Steam, gid, g, p.Kbd, p.Mouse, p.Ctrl);
                    }
                }
                if (!_grantLogged) { Console.WriteLine("  -> granted keyboard/mouse/controller input (group " + g + ", players " + arr.Length + ")"); _grantLogged = true; }
            }
            catch (Exception e) { if (!_grantLogged) { Console.WriteLine("  -> ApplyControl threw " + e.GetType().Name); _grantLogged = true; } }
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

    // Write the full vtable name->index map to rpforge_methods.txt next to the exe, so the exact
    // Steam method names for THIS build can be read afterwards and wired precisely if a heuristic misses.
    static void DumpMethods(string label, Dictionary<string, int> map)
    {
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rpforge_methods.txt");
            var list = new List<KeyValuePair<string, int>>(map);
            list.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return a.Value.CompareTo(b.Value); });
            var sb = new StringBuilder();
            sb.AppendLine("==== " + label + "  (" + map.Count + " methods)  " + DateTime.Now + " ====");
            foreach (KeyValuePair<string, int> kv in list) sb.AppendLine(kv.Value + "\t" + kv.Key);
            sb.AppendLine();
            File.AppendAllText(path, sb.ToString());
            Console.WriteLine("(dumped " + map.Count + " " + label + " methods to rpforge_methods.txt)");
        }
        catch (Exception e) { Console.WriteLine("(method dump failed: " + e.GetType().Name + ")"); }
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

    // Fully end the session so the guest STOPS watching: disable all input, then DESTROY the RPT group.
    // Killing the process alone leaves Steam's stream alive, so the guest keeps watching — this fixes that.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static void Teardown()
    {
        try { _wantKbd = _wantMouse = _wantCtrl = false; lock (_players) { foreach (GPlayer gp in _players) { gp.Kbd = gp.Mouse = gp.Ctrl = false; } } ApplyControl("teardown"); } catch { }
        try
        {
            uint g = ReadGroup();
            // On this build the group-kill method is DisbandRemotePlayTogetherGroup(group) — disbanding
            // the group disconnects the guest so they stop watching entirely. Fall back to a verb scan
            // (now incl. "Disband") if a future build renames it.
            string nm = "DisbandRemotePlayTogetherGroup"; int idx = R("DisbandRemotePlayTogetherGroup");
            if (idx < 0 && _rmap != null && _rmap.TryGetValue("DestroyRemotePlayGroup", out idx)) nm = "DestroyRemotePlayGroup";
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
                Console.WriteLine("teardown: calling " + nm + "(idx " + idx + ", group=" + g + ")");
                bool done = false;
                try { if (g != 0) { V<D_VoidU>(_rcm, idx)(_rcm, g); done = true; } } catch (Exception e) { Console.WriteLine("  D_VoidU threw " + e.GetType().Name); }
                if (!done) { try { V<D_Void>(_rcm, idx)(_rcm); } catch (Exception e) { Console.WriteLine("  D_Void threw " + e.GetType().Name); } }
            }
            else Console.WriteLine("teardown: no disband/destroy method matched (see rpforge_methods.txt to wire it precisely).");
        }
        catch (Exception e) { Console.WriteLine("teardown threw " + e.GetType().Name); }
    }

    // Read guests out of a Steam callback payload — pure read-only, never calls into steamclient64.dll
    // with guessed args (that crashed Steam). A connect callback carries a RemotePlayPlayer_t
    // {SteamID@0, guestId@8, groupId@12}. An individual SteamID64 has a fixed high dword
    // (universe=public/type=individual/instance=1 => (id>>32)==0x01100001), so we locate the SteamID,
    // then read the adjacent guestId+groupId. If groupId matches our LIVE group, this is a real
    // RemotePlayPlayer_t and guestId is the guest's TRUE id (what SetPerUser* needs) — no more guessing.
    // Excludes the host's own SteamID. Wrapped so an over-read past the struct's end can't crash us.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static void DiscoverPlayers(IntPtr param)
    {
        uint curGroup = ReadGroup();
        try
        {
            // Widened to 512 bytes: link guests weren't being found in the first 256. Reads are guarded
            // (break on failure) and the method is CSE-protected, so an over-read can't crash us.
            for (int off = 0; off + 16 <= 512; off += 4)
            {
                ulong v;
                try { v = (ulong)Marshal.ReadInt64(param, off); } catch { break; }

                // Adjacent guestId/groupId — trust guestId only when the group matches our live group.
                uint guestCand = 0, grpCand = 0;
                try { guestCand = (uint)Marshal.ReadInt32(param, off + 8); grpCand = (uint)Marshal.ReadInt32(param, off + 12); } catch { }
                bool structShape = (curGroup != 0 && grpCand == curGroup && guestCand >= 1 && guestCand <= 64);

                // A standard desktop Steam account has high dword 0x01100001 (universe=public,
                // type=individual, instance=desktop). A Steam Link guest with NO Steam account joins
                // with SteamID 0 and is identified ONLY by its {guest, group}.
                bool individual = ((v >> 32) == 0x01100001UL) && (uint)v != 0;

                // Accept ONLY:
                //   - realPlayer: a real desktop account, but only when it's CONFIRMED in our group
                //     (structShape) or is the friend we explicitly invited (_target). The callback
                //     payload also carries stray individual SteamIDs that are NOT current players
                //     (groupMatch=False); accepting those made a bogus duplicate "Player N" row that
                //     cross-wrote control. Requiring in-group confirmation drops them.
                //   - anonGuest: a SteamID-0 guest whose guest id is one we actually handed out. The
                //     guest-id bound drops the non-player structs that sit next to {group, guest=32/43}
                //     (some SteamID 0, some garbage 64-bit doubles) that would otherwise flood the roster.
                bool realPlayer = individual && (structShape || v == _target);
                bool anonGuest = (v == 0) && structShape && guestCand >= 1 && guestCand <= _maxInvitedGuest;
                if (!realPlayer && !anonGuest) continue;

                // One-time diagnostic per distinct SteamID-shaped value: shows exactly what's in the
                // payload (offset, adjacent guest/group, whether it's the host) — key for the link path.
                if (_diagSeen.Add(v))
                    if (_diag) Console.WriteLine("DIAG candidate steam=" + v + " off=" + off + " guest=" + guestCand + " grp=" + grpCand + " liveGroup=" + curGroup + " host=" + (v == _hostSteam) + " match=" + structShape);

                if (v != 0 && v == _hostSteam) continue;   // never list the host themselves (has a real id)

                // Real accounts are unique by SteamID; anonymous (steam==0) guests are unique by guest id.
                GPlayer existing = null;
                lock (_players)
                {
                    foreach (GPlayer pp in _players)
                        if (v != 0 ? pp.Steam == v : (pp.Steam == 0 && pp.Guest == guestCand)) { existing = pp; break; }
                }
                if (existing != null)
                {
                    // Learn/upgrade a guest id we didn't have yet, then re-assert control on the right slot.
                    if (structShape && existing.Guest != guestCand)
                    {
                        existing.Guest = guestCand;
                        if (_diag) Console.WriteLine("DIAG resolved guest id for " + existing.Name + " = " + guestCand + " (group " + grpCand + ")");
                        _grantLogged = false; ApplyControl("guest id learned");
                    }
                    continue;
                }

                GPlayer np = new GPlayer(); np.Steam = v; np.Guest = structShape ? guestCand : 0; np.Name = LookupName(v);
                int slot; lock (_players) { _players.Add(np); slot = _players.Count; }
                if (string.IsNullOrEmpty(np.Name)) np.Name = (v == 0 ? "Steam Link guest " : "Player ") + slot;
                if (_target == 0 && v != 0) { _target = v; }
                _grantLogged = false;
                Console.WriteLine("PLAYER JOINED " + np.Name);
                if (_diag) Console.WriteLine("DIAG new player slot=" + slot + " steam=" + v + " guest=" + (structShape ? guestCand.ToString() : "?") + " groupMatch=" + structShape);
                EmitPlayer(slot);
                ApplyControl("new player " + slot);
            }

            // Group-anchored diagnostic: a no-account Steam Link guest may sit in a RemotePlayPlayer_t
            // whose SteamID field is 0 or a non-individual value, so the SteamID-first scan above misses
            // them. Locate the struct by its group anchor instead (groupId == our live group + a plausible
            // guest id) and dump the raw layout so the guest's real id field is visible from the log. This
            // is read-only and never used to grant control — purely to diagnose the missing-roster report.
            if (_diag && curGroup != 0)
            {
                for (int off = 0; off + 16 <= 512; off += 4)
                {
                    uint grp;
                    try { grp = (uint)Marshal.ReadInt32(param, off + 12); } catch { break; }
                    if (grp != curGroup) continue;
                    ulong sid; uint gid;
                    try { sid = (ulong)Marshal.ReadInt64(param, off); gid = (uint)Marshal.ReadInt32(param, off + 8); } catch { continue; }
                    if (gid < 1 || gid > 64) continue;
                    // Only dump slots that are plausibly players (SteamID 0 or a real individual id);
                    // skip the garbage 64-bit values (doubles) that share the {group, guest=32} shape,
                    // otherwise desktop-streaming sessions spam dozens of grpslot lines.
                    if (sid != 0 && (sid >> 32) != 0x01100001UL) continue;
                    string key = off + ":" + sid + ":" + gid;
                    if (_diag && _grpDiagSeen.Add(key))
                        Console.WriteLine("DIAG grpslot off=" + off + " steam=" + sid + " guest=" + gid
                            + " group=" + grp + " host=" + (sid == _hostSteam));
                }
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
    // coexist), pump for its join URL, and deliver it: link mode just surfaces the URL (GUI copies it);
    // friend mode sends it to that friend in Steam chat. Runs on the main pump thread only.
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    static bool AddGuest(ulong target, bool link)
    {
        uint group = ReadGroup();
        if (group == 0) { Console.WriteLine("ADD failed — no active group yet."); return false; }

        // fresh guest id in that group (each additional guest gets the next slot)
        uint guest = 0; int iG = R("GetAvailableRemotePlayTogetherGuestID");
        if (iG >= 0) { try { guest = V<D_Uu>(_rcm, iG)(_rcm, group); } catch { } }
        if (guest == 0) guest = 1;
        if (guest > _maxInvitedGuest) _maxInvitedGuest = guest;   // bounds which guest ids we'll accept
        Console.WriteLine("guest id = " + guest);

        IntPtr player = Marshal.AllocHGlobal(40);
        Marshal.WriteInt64(player, 0, (long)target);
        Marshal.WriteInt32(player, 8, (int)guest);
        Marshal.WriteInt32(player, 12, (int)group);
        Marshal.WriteInt64(player, 16, 0); Marshal.WriteInt64(player, 24, 0); Marshal.WriteInt32(player, 32, 0); Marshal.WriteInt32(player, 36, 0);
        bool created = false;
        try { created = V<D_CreateInvite>(_rcm, R("BCreateRemotePlayInviteAndSession"))(_rcm, player, _appId); } catch { }
        Marshal.FreeHGlobal(player);
        Console.WriteLine("BCreateRemotePlayInviteAndSession=" + created + " (group=" + group + " guest=" + guest + " app=" + _appId + ")");

        string url = NormalizeUrl(PumpForUrl(8000));
        if (url != null) Console.WriteLine((link ? "GUEST LINK = " : "JOIN URL = ") + url);
        else Console.WriteLine("No join URL arrived (session may still work).");

        if (link)
        {
            if (url != null) { try { SetClipboard(url); Console.WriteLine("(link copied to clipboard)"); } catch { } }
        }
        else
        {
            EnsureFriends();   // safe now — the group already exists
            string tname = null;
            try { if (_cf != IntPtr.Zero) tname = Marshal.PtrToStringAnsi(V<D_NameOfId>(_cf, F("GetFriendPersonaName"))(_cf, target)); } catch { }
            if (!string.IsNullOrEmpty(tname)) Console.WriteLine("friend = " + tname);
            if (url != null && _cf != IntPtr.Zero)
            {
                bool sent = false;
                try { int i = F("SendMsgToFriend"); if (i >= 0) sent = V<D_SendMsg>(_cf, i)(_cf, target, k_EChatEntryTypeChatMsg, "Join my Remote Play Together game: " + url); } catch { }
                if (!sent) { try { int i = F("ReplyToFriendMessage"); if (i >= 0) sent = V<D_Reply>(_cf, i)(_cf, target, "Join my Remote Play Together game: " + url); } catch { } }
                Console.WriteLine(sent ? "Sent the link to " + (tname ?? "your friend") + " in Steam chat." : "Could not auto-send; give them the URL above manually.");
                try { SetClipboard(url); Console.WriteLine("(URL copied to clipboard)"); } catch { }
            }
        }
        return created;
    }

    // Steam sometimes returns the guest join URL without a scheme (e.g. "s.team/p/xxxx-xxxx"),
    // which isn't clickable when pasted into Discord/Steam chat. Prepend https:// unless the URL
    // already carries a scheme (http/https or the steam:// protocol) so the shared link always works.
    static string NormalizeUrl(string u)
    {
        if (string.IsNullOrEmpty(u)) return u;
        u = u.Trim();
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            return u;
        if (u.StartsWith("//")) return "https:" + u;   // protocol-relative
        return "https://" + u;
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

    static void Hold() { Console.WriteLine("\nPress Enter to close..."); try { Console.ReadLine(); } catch { } }

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
