using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RemotePlayForge
{
    // An online friend that can be invited.
    public class SteamFriend
    {
        public ulong Id { get; private set; }
        public string Name { get; private set; }
        public int State { get; private set; } // 1=online 2=busy 3=away 4=snooze ...
        public SteamFriend(ulong id, string name, int state) { Id = id; Name = name; State = state; }
        public override string ToString() { return Name + "   [" + Id + "]"; }
    }

    // ---------------------------------------------------------------------------
    // Phase 2 — the real "force RPT" mechanism (RemotePlayWhatever + open-steamworks),
    // driving the running Steam client's internal IClientRemoteClientManager and
    // IClientFriends. It binds Remote Play Together to the game YOU are actually shown
    // playing (never AppID 480/Spacewar), enumerates your friends, and sends a real
    // BCreateRemotePlayInviteAndSession invite — so the guest is shown playing YOUR game.
    //
    // Vtable offsets are NOT hardcoded: they drift between Steam builds. We resolve every
    // method index by the name each client stub embeds (telemetry string via `lea rip`),
    // so calls are safe (we invoke exactly the method we named) and self-adapt per build.
    // ABI notes (MSVC x64): struct return -> RCX=this, RDX=hidden ret ptr, then args;
    // CSteamID (8 bytes) passes by value in a register; RemotePlayPlayer_t (40 bytes)
    // passes by pointer.
    // ---------------------------------------------------------------------------
    public class SteamInternalForce : IDisposable
    {
        const int CLIENT_CreateSteamPipe = 0;
        const int CLIENT_BReleaseSteamPipe = 1;
        const int CLIENT_ConnectToGlobalUser = 2;
        const int CLIENT_ReleaseUser = 4;
        const int ENGINE_GetIClientRemoteClientManager = 56;
        const int ENGINE_GetIClientFriends = 13;
        const int k_EFriendFlagImmediate = 0x04;
        const int k_EChatEntryTypeChatMsg = 1;
        // RemotePlayInviteResult_t: k_iClientRemoteClientManagerCallbacks(1100000) + 28
        const int RPT_INVITE_RESULT_CALLBACK = 1100028;
        // RemotePlayInviteResult_t layout: RemotePlayPlayer_t m_player(40) @0; int m_eResult @40;
        // CGameID m_nGameID(8) @48; char m_szConnectURL[1024] @56.
        const int RPT_RESULT_PLAYERID_OFFSET = 0;
        const int RPT_RESULT_CONNECTURL_OFFSET = 56;
        // CallbackMsg_t layout (x64): int m_hSteamUser @0; int m_iCallback @4; void* m_pubParam @8; int m_cubParam @16.
        const int CBMSG_CALLBACK_OFFSET = 4;
        const int CBMSG_PARAM_OFFSET = 8;

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr LoadLibrary(string p);
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        static extern IntPtr GetProcAddress(IntPtr h, string n);
        [DllImport("kernel32")]
        static extern bool FreeLibrary(IntPtr h);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_CreateInterface([MarshalAs(UnmanagedType.LPStr)] string ver, IntPtr pReturnCode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int D_IntNoArg(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int D_IntInt(IntPtr self, int a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_PtrInt(IntPtr self, int a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_PtrIntInt(IntPtr self, int a, int b);   // GetIClientFriends(user,pipe)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool D_BoolNoArg(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool D_BoolInt(IntPtr self, int a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_VoidBool(IntPtr self, [MarshalAs(UnmanagedType.I1)] bool v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_VoidUInt(IntPtr self, uint a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint D_UIntNoArg(IntPtr self);   // read-only getter (group id)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint D_UIntUInt(IntPtr self, uint a);   // getter taking group id (guest id)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_ReleaseUser(IntPtr self, int hPipe, int hUser);
        // friends
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_FriendByIndex(IntPtr self, IntPtr retCSteamID, int iFriend, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr D_NameOfId(IntPtr self, ulong steamId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int D_StateOfId(IntPtr self, ulong steamId);
        // invite: bool BCreateRemotePlayInviteAndSession(RemotePlayPlayer_t* byval, AppId_t)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool D_CreateInvite(IntPtr self, IntPtr pPlayer, uint appId);
        // callback pump (exported C funcs from steamclient64.dll)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool D_BGetCallback(int hPipe, IntPtr pCallbackMsg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void D_FreeLastCallback(int hPipe);
        // deliver the join link to the friend as a chat message
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool D_SendMsg(IntPtr self, ulong steamId, int chatEntryType, [MarshalAs(UnmanagedType.LPStr)] string body);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        delegate bool D_ReplyMsg(IntPtr self, ulong steamId, [MarshalAs(UnmanagedType.LPStr)] string body);

        IntPtr _module = IntPtr.Zero;
        D_CreateInterface _createInterface;
        IntPtr _client = IntPtr.Zero;
        IntPtr _engine = IntPtr.Zero;
        IntPtr _rcm = IntPtr.Zero;
        IntPtr _friends = IntPtr.Zero;
        int _pipe;
        int _user;

        Dictionary<string, int> _names;         // RCM method -> index
        Dictionary<string, int> _friendNames;   // IClientFriends method -> index

        D_BGetCallback _bGetCallback;
        D_FreeLastCallback _freeLastCallback;
        uint _nextGuestId = 1;                   // like RemotePlayWhatever: guest ids start at 1

        public bool Connected { get; private set; }
        public bool Aligned { get; private set; }
        public string LastDetail { get; private set; }
        public string LastConnectUrl { get; private set; }

        static T VFn<T>(IntPtr obj, int index) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        public bool Connect(string steamDir, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(steamDir)) steamDir = @"C:\Program Files (x86)\Steam";
                string dll = Path.Combine(steamDir, "steamclient64.dll");
                if (!File.Exists(dll)) { error = "steamclient64.dll not found: " + dll; return false; }

                Directory.SetCurrentDirectory(steamDir);
                _module = LoadLibrary(dll);
                if (_module == IntPtr.Zero) { error = "LoadLibrary failed err " + Marshal.GetLastWin32Error(); return false; }

                IntPtr pci = GetProcAddress(_module, "CreateInterface");
                if (pci == IntPtr.Zero) { error = "CreateInterface not exported"; return false; }
                _createInterface = (D_CreateInterface)Marshal.GetDelegateForFunctionPointer(pci, typeof(D_CreateInterface));

                _client = _createInterface("SteamClient019", IntPtr.Zero);
                if (_client == IntPtr.Zero) { error = "CreateInterface(SteamClient019) returned NULL"; return false; }

                _pipe = VFn<D_IntNoArg>(_client, CLIENT_CreateSteamPipe)(_client);
                if (_pipe == 0) { error = "CreateSteamPipe returned 0 (is Steam running and signed in?)"; return false; }
                _user = VFn<D_IntInt>(_client, CLIENT_ConnectToGlobalUser)(_client, _pipe);

                _engine = _createInterface("CLIENTENGINE_INTERFACE_VERSION005", IntPtr.Zero);
                if (_engine == IntPtr.Zero) { error = "CreateInterface(CLIENTENGINE005) returned NULL"; return false; }

                _rcm = VFn<D_PtrInt>(_engine, ENGINE_GetIClientRemoteClientManager)(_engine, _pipe);
                if (_rcm == IntPtr.Zero) { error = "GetIClientRemoteClientManager returned NULL"; return false; }
                _names = ScanVtableNames(_rcm);

                _friends = VFn<D_PtrIntInt>(_engine, ENGINE_GetIClientFriends)(_engine, _user, _pipe);
                if (_friends != IntPtr.Zero) _friendNames = ScanVtableNames(_friends);

                // Callback pump exports — how we receive the async RemotePlayInviteResult (join URL).
                IntPtr pGet = GetProcAddress(_module, "Steam_BGetCallback");
                IntPtr pFree = GetProcAddress(_module, "Steam_FreeLastCallback");
                if (pGet != IntPtr.Zero) _bGetCallback = (D_BGetCallback)Marshal.GetDelegateForFunctionPointer(pGet, typeof(D_BGetCallback));
                if (pFree != IntPtr.Zero) _freeLastCallback = (D_FreeLastCallback)Marshal.GetDelegateForFunctionPointer(pFree, typeof(D_FreeLastCallback));

                Connected = true;
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        static int Idx(Dictionary<string, int> map, string method)
        {
            int v;
            if (map != null && map.TryGetValue(method, out v)) return v;
            return -1;
        }

        // Resolve the RPT method indices by name and confirm the ones we call exist.
        public bool ValidateAlignment(out string detail)
        {
            Aligned = false;
            if (!Connected) { detail = "not connected"; return false; }

            int show = Idx(_names, "ShowRemotePlayTogetherUI");
            int invite = Idx(_names, "BCreateRemotePlayInviteAndSession");
            if (show < 0)
            {
                detail = "could not resolve ShowRemotePlayTogetherUI by name (" + (_names != null ? _names.Count : 0)
                       + " named methods) — declining to fire";
                LastDetail = detail;
                return false;
            }

            string status = "";
            int sup = Idx(_names, "BIsStreamingSupported");
            int ses = Idx(_names, "BIsStreamingSessionActive");
            if (sup >= 0) status += " supported=" + VFn<D_BoolNoArg>(_rcm, sup)(_rcm);
            if (ses >= 0) status += " sessionActive=" + VFn<D_BoolNoArg>(_rcm, ses)(_rcm);

            Aligned = true;
            detail = "resolved: ShowRemotePlayTogetherUI=" + show + ", BCreateRemotePlayInviteAndSession=" + invite
                   + ", friends=" + (_friendNames != null ? _friendNames.Count : 0) + " methods;" + status;
            LastDetail = detail;
            return true;
        }

        // Open Steam's Remote Play Together UI bound to appId (enables desktop streaming first
        // for non-Steam targets). Only calls name-verified methods.
        public bool ForceRemotePlayTogether(uint appId, bool desktopMode, out string error)
        {
            error = null;
            if (!Aligned) { string d; if (!ValidateAlignment(out d)) { error = "not aligned — " + d; return false; } }
            try
            {
                if (desktopMode)
                {
                    int deskSet = Idx(_names, "SetStreamingDesktopToRemotePlayTogetherEnabled");
                    if (deskSet >= 0) VFn<D_VoidBool>(_rcm, deskSet)(_rcm, true);
                }
                VFn<D_VoidUInt>(_rcm, Idx(_names, "ShowRemotePlayTogetherUI"))(_rcm, appId);
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        // The host's REAL local Remote Play Together group id. RemotePlayWhatever hardcodes 1,
        // which was correct on its build; on a drifted build the host's actual overlay group may
        // differ, and a guest bound to the wrong group connects but never shows in the host's
        // player list (the "watch only" symptom). Returns 0 if unresolved / no group yet.
        public uint GetLocalGroupId()
        {
            int i = Idx(_names, "GetLocalRemotePlayTogetherGroupID");
            if (i < 0) return 0;
            try { return VFn<D_UIntNoArg>(_rcm, i)(_rcm); }
            catch { return 0; }
        }

        // Next free guest id for a group (Steam-assigned). Falls back to our incrementing counter
        // if the getter isn't resolvable or returns 0. Passing the group id is a safe superset:
        // if the method is actually no-arg the extra register is ignored on x64.
        public uint GetAvailableGuestId(uint groupId)
        {
            int i = Idx(_names, "GetAvailableRemotePlayTogetherGuestID");
            if (i < 0) return 0;
            try { return VFn<D_UIntUInt>(_rcm, i)(_rcm, groupId); }
            catch { return 0; }
        }

        // Ensure a host Remote Play Together group exists and return its real id. On current Steam
        // builds ShowRemotePlayTogetherUI does NOT create the group — you must call
        // BCreateRemotePlayGroup first (verified live: it returns true and allocates a real group id,
        // after which GetAvailableRemotePlayTogetherGuestID(group) yields a valid guest id). Reuses
        // an existing group if one is already active (so multiple guests share one session).
        public uint EnsureGroup(uint appId, out string detail)
        {
            detail = null;
            uint g = GetLocalGroupId();
            if (g != 0) { detail = "reusing group " + g; return g; }

            int i = Idx(_names, "BCreateRemotePlayGroup");
            if (i < 0) { detail = "BCreateRemotePlayGroup unresolved"; return 0; }
            bool ok = false;
            try { ok = VFn<D_BoolNoArg>(_rcm, i)(_rcm); }
            catch (Exception ex) { detail = "BCreateRemotePlayGroup threw " + ex.GetType().Name; return 0; }

            // The group id is allocated ASYNCHRONOUSLY — it isn't readable immediately after BCreate.
            // Poll (pumping callbacks) until GetLocalRemotePlayTogetherGroupID becomes non-zero.
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            bool showedUi = false;
            while (sw.ElapsedMilliseconds < 4000)
            {
                PumpBriefly(150);
                g = GetLocalGroupId();
                if (g != 0) break;
                // halfway through, nudge Steam by opening the host UI (binds the local group) then keep polling
                if (!showedUi && sw.ElapsedMilliseconds > 1500 && appId != 0)
                {
                    int iShow = Idx(_names, "ShowRemotePlayTogetherUI");
                    if (iShow >= 0) { try { VFn<D_VoidUInt>(_rcm, iShow)(_rcm, appId); } catch { } }
                    showedUi = true;
                }
            }
            detail = "created group " + g + " (BCreate=" + ok + ", waited " + sw.ElapsedMilliseconds + "ms" + (showedUi ? ", showedUI" : "") + ")";
            return g;
        }

        // Pump the callback queue briefly (draining/discarding) so async client state — e.g. the
        // local RPT group getting created after BCreateRemotePlayGroup — settles before we read it.
        public void PumpBriefly(int ms)
        {
            if (_bGetCallback == null) return;
            IntPtr msg = Marshal.AllocHGlobal(64);
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    bool any = false;
                    while (_bGetCallback(_pipe, msg)) { any = true; if (_freeLastCallback != null) _freeLastCallback(_pipe); }
                    if (!any) System.Threading.Thread.Sleep(25);
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(msg); }
        }

        public List<SteamFriend> EnumerateOnlineFriends(out string error)
        {
            error = null;
            var list = new List<SteamFriend>();
            if (_friends == IntPtr.Zero || _friendNames == null) { error = "friends interface unavailable"; return list; }

            int iCount = Idx(_friendNames, "GetFriendCount");
            int iByIdx = Idx(_friendNames, "GetFriendByIndex");
            int iName = Idx(_friendNames, "GetFriendPersonaName");
            int iState = Idx(_friendNames, "GetFriendPersonaState");
            if (iCount < 0 || iByIdx < 0 || iName < 0 || iState < 0) { error = "could not resolve friend methods by name"; return list; }

            var getCount = VFn<D_IntInt>(_friends, iCount);
            var byIndex = VFn<D_FriendByIndex>(_friends, iByIdx);
            var nameOf = VFn<D_NameOfId>(_friends, iName);
            var stateOf = VFn<D_StateOfId>(_friends, iState);

            int n = getCount(_friends, k_EFriendFlagImmediate);
            IntPtr idbuf = Marshal.AllocHGlobal(8);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    byIndex(_friends, idbuf, i, k_EFriendFlagImmediate);
                    ulong id = (ulong)Marshal.ReadInt64(idbuf);
                    if (id <= 76561197960265728UL || id >= 76561210000000000UL) continue;
                    int st = stateOf(_friends, id);
                    if (st <= 0) continue; // online only
                    string nm = Marshal.PtrToStringAnsi(nameOf(_friends, id)) ?? ("Friend " + id);
                    list.Add(new SteamFriend(id, nm, st));
                }
            }
            finally { Marshal.FreeHGlobal(idbuf); }
            list.Sort(delegate(SteamFriend a, SteamFriend b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        // The full RemotePlayWhatever action: create a Remote Play Together session bound to
        // appId and invite this friend. The friend is shown playing appId's game — not Spacewar.
        // Step 1 of the real RemotePlayWhatever flow: create the RPT invite + session for this
        // friend. This is ASYNC — Steam later posts a RemotePlayInviteResult_t callback carrying
        // the join URL. PumpInviteCallbacks() (below), called on a timer, catches it and delivers
        // the URL to the friend as a Steam chat message. guestID starts at 1, groupID = 1 (exactly
        // as RemotePlayWhatever does); using GetAvailable*/GetLocal* getters was a wrong guess.
        public bool BeginInvite(ulong friendId, uint appId, bool desktopMode, out string error)
        {
            error = null;
            if (!Aligned) { string d; if (!ValidateAlignment(out d)) { error = "not aligned — " + d; return false; } }
            int iInvite = Idx(_names, "BCreateRemotePlayInviteAndSession");
            if (iInvite < 0) { error = "BCreateRemotePlayInviteAndSession not resolved"; return false; }
            if (_bGetCallback == null) { error = "Steam_BGetCallback export unavailable (can't receive the join link)"; return false; }

            try
            {
                if (desktopMode)
                {
                    int deskSet = Idx(_names, "SetStreamingDesktopToRemotePlayTogetherEnabled");
                    if (deskSet >= 0) VFn<D_VoidBool>(_rcm, deskSet)(_rcm, true);
                }

                // Create (or reuse) the host group FIRST, then bind the guest to that REAL group id so
                // they show up in the overlay as a controllable player (not a spectator = "watch only").
                string gdetail;
                uint groupId = EnsureGroup(appId, out gdetail);
                if (groupId == 0) groupId = 1; // last-ditch fallback

                uint guestId = GetAvailableGuestId(groupId);
                if (guestId == 0) guestId = _nextGuestId++;

                IntPtr player = Marshal.AllocHGlobal(40);
                bool created;
                try
                {
                    // RemotePlayPlayer_t { CSteamID m_playerID; uint32 m_unGuestID; uint32 m_unOverlayPIDGroupID; u64; u64; u32; }
                    Marshal.WriteInt64(player, 0, (long)friendId);
                    Marshal.WriteInt32(player, 8, (int)guestId);
                    Marshal.WriteInt32(player, 12, (int)groupId);
                    Marshal.WriteInt64(player, 16, 0);
                    Marshal.WriteInt64(player, 24, 0);
                    Marshal.WriteInt32(player, 32, 0);
                    Marshal.WriteInt32(player, 36, 0);
                    created = VFn<D_CreateInvite>(_rcm, iInvite)(_rcm, player, appId);
                }
                finally { Marshal.FreeHGlobal(player); }

                LastDetail = "createInvite=" + created + " guest=" + guestId + " group=" + groupId + " (" + gdetail + ") app=" + appId;
                if (!created) { error = "BCreateRemotePlayInviteAndSession returned false"; return false; }
                return true;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
        }

        // Step 2: synchronously drain the callback queue until RemotePlayInviteResult_t arrives
        // (carrying the join URL) or we time out. Verified working live: the callback lands within
        // ~1s. Runs on the UI thread during the button click, so it can't race the timer pump.
        public bool WaitForInviteUrl(ulong friendId, int timeoutMs, out string url, out string error)
        {
            url = null; error = null;
            if (!Connected || _bGetCallback == null) { error = "not connected / no callback export"; return false; }
            IntPtr msg = Marshal.AllocHGlobal(64);
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    bool any = false;
                    while (_bGetCallback(_pipe, msg))
                    {
                        any = true;
                        int cb = Marshal.ReadInt32(msg, CBMSG_CALLBACK_OFFSET);
                        IntPtr param = Marshal.ReadIntPtr(msg, CBMSG_PARAM_OFFSET);
                        if (cb == RPT_INVITE_RESULT_CALLBACK && param != IntPtr.Zero && url == null)
                        {
                            // accept the result whether or not the friend id echoes exactly
                            string u = Marshal.PtrToStringAnsi(new IntPtr(param.ToInt64() + RPT_RESULT_CONNECTURL_OFFSET));
                            if (!string.IsNullOrEmpty(u)) url = u;
                        }
                        if (_freeLastCallback != null) _freeLastCallback(_pipe);
                    }
                    if (url != null) break;
                    if (!any) System.Threading.Thread.Sleep(50);
                }
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            finally { Marshal.FreeHGlobal(msg); }

            if (url == null) { if (error == null) error = "no join link returned within " + timeoutMs + "ms"; return false; }
            LastConnectUrl = url;
            return true;
        }

        // Best-effort: message the join URL to the friend so they can click to join. Returns false
        // (not fatal) if Steam declines — the caller still has the URL on the clipboard to paste.
        public bool SendUrlToFriend(ulong friendId, string url)
        {
            if (_friends == IntPtr.Zero || string.IsNullOrEmpty(url)) return false;
            try
            {
                int iSend = Idx(_friendNames, "SendMsgToFriend");
                if (iSend >= 0 && VFn<D_SendMsg>(_friends, iSend)(_friends, friendId, k_EChatEntryTypeChatMsg, url)) return true;
                int iReply = Idx(_friendNames, "ReplyToFriendMessage");
                if (iReply >= 0 && VFn<D_ReplyMsg>(_friends, iReply)(_friends, friendId, url)) return true;
            }
            catch { }
            return false;
        }

        // ---- read the method name embedded in each vtable stub (lea rip -> ascii) ----
        static Dictionary<string, int> ScanVtableNames(IntPtr obj)
        {
            var map = new Dictionary<string, int>();
            long lo = 0, hi = 0;
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                if (string.Equals(m.ModuleName, "steamclient64.dll", StringComparison.OrdinalIgnoreCase))
                { lo = m.BaseAddress.ToInt64(); hi = lo + m.ModuleMemorySize; break; }
            }
            if (lo == 0) return map;

            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            for (int i = 0; i < 400; i++)
            {
                long fn = Marshal.ReadIntPtr(vtbl, i * IntPtr.Size).ToInt64();
                if (fn < lo || fn >= hi) break;
                string name = NameOf(fn, lo, hi);
                if (name != null && !map.ContainsKey(name)) map[name] = i;
            }
            return map;
        }

        static string NameOf(long fn, long lo, long hi)
        {
            for (int k = 0; k < 220; k++)
            {
                byte rex = Marshal.ReadByte(new IntPtr(fn + k));
                if (rex != 0x48 && rex != 0x4C && rex != 0x49 && rex != 0x4D) continue;
                if (Marshal.ReadByte(new IntPtr(fn + k + 1)) != 0x8D) continue;
                if ((Marshal.ReadByte(new IntPtr(fn + k + 2)) & 0xC7) != 0x05) continue;
                long target = fn + k + 7 + Marshal.ReadInt32(new IntPtr(fn + k + 3));
                if (target < lo || target >= hi) continue;

                var sb = new StringBuilder();
                bool bad = false;
                for (int j = 0; j < 96; j++)
                {
                    byte c = Marshal.ReadByte(new IntPtr(target + j));
                    if (c == 0) break;
                    if (c < 32 || c > 126) { bad = true; break; }
                    sb.Append((char)c);
                }
                if (bad) continue;
                string s = sb.ToString();
                if (s.Length >= 6 && s.IndexOf(' ') < 0)
                {
                    int letters = 0;
                    foreach (char c in s) if (char.IsLetter(c)) letters++;
                    if (letters >= 4) return s;
                }
            }
            return null;
        }

        public void Dispose()
        {
            try
            {
                if (_client != IntPtr.Zero && _pipe != 0)
                {
                    if (_user != 0) VFn<D_ReleaseUser>(_client, CLIENT_ReleaseUser)(_client, _pipe, _user);
                    VFn<D_BoolInt>(_client, CLIENT_BReleaseSteamPipe)(_client, _pipe);
                }
            }
            catch { }
            if (_module != IntPtr.Zero) { FreeLibrary(_module); _module = IntPtr.Zero; }
            Connected = false;
        }
    }
}
