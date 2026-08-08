using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RemotePlayForge
{
    // Locates the Steam install, detects whether Steam is running, and resolves the
    // Steam AppID for a given process by reading its library's appmanifest files.
    public static class SteamPaths
    {
        internal sealed class NonSteamShortcut
        {
            internal uint AppId;
            internal string Name;
            internal string Exe;
            internal string StartDir;
            internal uint LastPlayTime;
            internal bool IsActiveUser;
            internal string ExecutablePath;
            internal string ProcessName;
        }

        public static bool IsSteamRunning()
        {
            try { return Process.GetProcessesByName("steam").Length > 0; }
            catch { return false; }
        }

        public static string GetSteamInstallPath()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
                if (v != null)
                    return ((string)v).Replace('/', '\\');
            }
            catch { }
            try
            {
                object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null);
                if (v != null)
                    return (string)v;
            }
            catch { }
            string def = @"C:\Program Files (x86)\Steam";
            return Directory.Exists(def) ? def : null;
        }

        public static string GetSteamClientDllPath()
        {
            string root = GetSteamInstallPath();
            if (string.IsNullOrEmpty(root))
                return null;
            return Path.Combine(root, "steamclient64.dll");
        }

        // Registry.GetValue returns REG_DWORD values as signed Int32 objects. Non-Steam shortcut
        // AppIDs always have bit 31 set, so ToString()+uint.TryParse silently turned every one of
        // them into zero. Preserve the DWORD's raw bits instead.
        internal static bool TryConvertRegistryUInt32(object value, out uint result)
        {
            result = 0;
            if (value == null) return false;
            if (value is uint) { result = (uint)value; return true; }
            if (value is int) { result = unchecked((uint)(int)value); return true; }
            if (value is short) { result = unchecked((uint)(short)value); return true; }
            if (value is byte) { result = (byte)value; return true; }
            if (value is long)
            {
                long n = (long)value;
                if (n >= int.MinValue && n <= uint.MaxValue)
                {
                    result = unchecked((uint)n);
                    return true;
                }
            }

            string text = value.ToString();
            if (uint.TryParse(text, out result)) return true;
            int signed;
            if (int.TryParse(text, out signed))
            {
                result = unchecked((uint)signed);
                return true;
            }
            result = 0;
            return false;
        }

        static uint ReadRegistryUInt32(string keyName, string valueName)
        {
            try
            {
                uint value;
                if (TryConvertRegistryUInt32(Registry.GetValue(keyName, valueName, null), out value))
                    return value;
            }
            catch { }
            return 0;
        }

        // The AppID of the game Steam currently considers "running" (0 if none).
        public static uint GetRunningAppId()
        {
            uint reg = ReadRegistryUInt32(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID");

            // Trust RunningAppID only when it names an actually-installed Steam app. Some titles launch
            // through an external launcher and register a WRAPPER AppID that isn't in any library — e.g.
            // EA FC 26 sets RunningAppID to 1182480, whose app name is literally "EALink" and which has
            // no appmanifest. That id shows as "AppID <n>" and makes Remote Play Together label the
            // session as the launcher, not the game. When it isn't installed, detect the real game.
            if (reg != 0 && (IsInstalledApp(reg) || IsNonSteamShortcut(reg))) return reg;

            uint fb = DetectRunningGameFallback();
            return fb != 0 ? fb : reg; // nothing better resolved; keep whatever the registry had
        }

        static uint _fbCache;
        static readonly Stopwatch _fbAge = Stopwatch.StartNew();
        static bool _fbPrimed;

        // Resolve the real running game when RunningAppID is missing or a launcher wrapper. Cached ~4s
        // because the status timer polls every ~1.5s. Tries the cheap, anti-cheat-proof registry signal
        // first, then a process scan as a last resort.
        static uint DetectRunningGameFallback()
        {
            if (_fbPrimed && _fbAge.ElapsedMilliseconds < 4000) return _fbCache;
            uint id = DetectRunningAppViaSteamRegistry();
            if (id == 0) id = DetectRunningNonSteamShortcutByProcess();
            if (id == 0) id = DetectRunningSteamGameByProcess();
            _fbCache = id; _fbPrimed = true; _fbAge.Restart();
            return id;
        }

        // Even when RunningAppID is a launcher wrapper (e.g. EALink for EA FC 26), Steam still marks the
        // REAL game as running under HKCU\...\Steam\Apps\<appid>\Running = 1. Reading that is cheap and
        // works for anti-cheat-protected games whose process module path can't be read.
        static uint DetectRunningAppViaSteamRegistry()
        {
            try
            {
                using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps"))
                {
                    if (apps == null) return 0;
                    foreach (string name in apps.GetSubKeyNames())
                    {
                        uint id;
                        if (!uint.TryParse(name, out id) || id == 0) continue;
                        using (var k = apps.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            object run = k.GetValue("Running");
                            if (run != null && Convert.ToInt32(run) == 1 &&
                                (IsInstalledApp(id) || IsNonSteamShortcut(id)))
                                return id;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        // True if this AppID has an appmanifest in any Steam library (i.e. it's a real installed game,
        // not an external-launcher wrapper id).
        static bool IsInstalledApp(uint appId)
        {
            if (appId == 0) return false;
            try
            {
                foreach (string apps in GetSteamAppsFolders())
                    if (File.Exists(Path.Combine(apps, "appmanifest_" + appId + ".acf")))
                        return true;
            }
            catch { }
            return false;
        }

        static uint _procScanCache;
        static readonly Stopwatch _procScanAge = Stopwatch.StartNew();
        static bool _procScanPrimed;

        // Scan running processes for one whose executable sits inside a Steam library and resolve its
        // AppID from the appmanifest. Cached briefly because the status timer polls every ~1.5s and a
        // full process sweep is comparatively expensive.
        static uint DetectRunningSteamGameByProcess()
        {
            if (_procScanPrimed && _procScanAge.ElapsedMilliseconds < 4000)
                return _procScanCache;

            uint best = 0;
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    string exe = null;
                    bool hasWindow = false;
                    try
                    {
                        if (p.MainModule != null) exe = p.MainModule.FileName;
                        hasWindow = p.MainWindowHandle != IntPtr.Zero;
                    }
                    catch { continue; }   // access denied / bitness mismatch — skip this process
                    finally { try { p.Dispose(); } catch { } }

                    if (string.IsNullOrEmpty(exe)) continue;
                    if (exe.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string idStr = TryResolveSteamAppId(exe);
                    uint id;
                    if (idStr != null && uint.TryParse(idStr, out id) && id != 0)
                    {
                        if (hasWindow) { best = id; break; }   // the windowed process is the game itself
                        if (best == 0) best = id;              // else remember the first match
                    }
                }
            }
            catch { }

            _procScanCache = best;
            _procScanPrimed = true;
            _procScanAge.Restart();
            return best;
        }

        // Human-readable name of an installed AppID (from its appmanifest "name"), searching every
        // Steam library folder. Returns null if not found.
        public static string GetGameName(uint appId)
        {
            if (appId == 0) return null;
            try
            {
                foreach (string steamapps in GetSteamAppsFolders())
                {
                    string acf = Path.Combine(steamapps, "appmanifest_" + appId + ".acf");
                    if (!File.Exists(acf)) continue;
                    string txt = File.ReadAllText(acf);
                    Match m = Regex.Match(txt, "\"name\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }

            NonSteamShortcut shortcut = FindNonSteamShortcut(appId);
            if (shortcut != null && !string.IsNullOrWhiteSpace(shortcut.Name))
                return shortcut.Name;
            return null;
        }

        static readonly object _shortcutLock = new object();
        static readonly Stopwatch _shortcutAge = Stopwatch.StartNew();
        static List<NonSteamShortcut> _shortcutCache = new List<NonSteamShortcut>();
        static bool _shortcutPrimed;

        static bool IsNonSteamShortcut(uint appId)
        {
            return FindNonSteamShortcut(appId) != null;
        }

        static NonSteamShortcut FindNonSteamShortcut(uint appId)
        {
            if (appId == 0) return null;
            foreach (NonSteamShortcut shortcut in GetNonSteamShortcuts())
                if (shortcut.AppId == appId)
                    return shortcut;
            return null;
        }

        // Non-Steam games are stored per account in userdata/<account>/config/shortcuts.vdf. This is
        // binary KeyValues, not the text VDF used by appmanifest files. Keep a short cache because the
        // status timer asks for the current game every ~1.5 seconds.
        static List<NonSteamShortcut> GetNonSteamShortcuts()
        {
            lock (_shortcutLock)
            {
                if (_shortcutPrimed && _shortcutAge.ElapsedMilliseconds < 4000)
                    return _shortcutCache;

                _shortcutCache = LoadNonSteamShortcuts();
                _shortcutPrimed = true;
                _shortcutAge.Restart();
                return _shortcutCache;
            }
        }

        static List<NonSteamShortcut> LoadNonSteamShortcuts()
        {
            var result = new List<NonSteamShortcut>();
            string root = GetSteamInstallPath();
            if (string.IsNullOrEmpty(root)) return result;

            string userdata = Path.Combine(root, "userdata");
            if (!Directory.Exists(userdata)) return result;

            uint activeUser = ReadRegistryUInt32(
                @"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser");
            var files = new List<string>();
            if (activeUser != 0)
                AddShortcutFile(files, Path.Combine(userdata, activeUser.ToString(), "config", "shortcuts.vdf"));

            try
            {
                foreach (string accountDir in Directory.GetDirectories(userdata))
                    AddShortcutFile(files, Path.Combine(accountDir, "config", "shortcuts.vdf"));
            }
            catch { }

            var seenIds = new HashSet<uint>();
            foreach (string file in files)
            {
                bool isActive = activeUser != 0 && string.Equals(
                    Directory.GetParent(Directory.GetParent(file).FullName).Name,
                    activeUser.ToString(), StringComparison.OrdinalIgnoreCase);
                try
                {
                    foreach (NonSteamShortcut shortcut in ReadNonSteamShortcutsFile(file))
                    {
                        if (shortcut.AppId == 0 || seenIds.Contains(shortcut.AppId)) continue;
                        shortcut.IsActiveUser = isActive;
                        shortcut.ExecutablePath = ResolveShortcutExecutablePath(shortcut.Exe, shortcut.StartDir);
                        try
                        {
                            if (!string.IsNullOrEmpty(shortcut.ExecutablePath))
                                shortcut.ProcessName = Path.GetFileNameWithoutExtension(shortcut.ExecutablePath);
                        }
                        catch { }
                        seenIds.Add(shortcut.AppId);
                        result.Add(shortcut);
                    }
                }
                catch
                {
                    // Steam may be rewriting this file while we poll it. A later cache refresh retries.
                }
            }
            return result;
        }

        static void AddShortcutFile(List<string> files, string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (string existing in files)
                    if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                        return;
                files.Add(path);
            }
            catch { }
        }

        // Minimal Valve Binary KeyValues reader for shortcuts.vdf. The only record values Steam uses
        // here are objects (0), UTF-8 strings (1), DWORDs (2), and the object terminator (8), but the
        // skipper also understands the remaining legacy scalar types so extra fields do not break us.
        internal static List<NonSteamShortcut> ReadNonSteamShortcutsFile(string filePath)
        {
            var shortcuts = new List<NonSteamShortcut>();
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length == 0) return shortcuts;
                byte rootType = reader.ReadByte();
                string rootName = ReadBinaryString(reader);
                if (rootType != 0 || !string.Equals(rootName, "shortcuts", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Not a Steam shortcuts.vdf file.");

                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    byte type = reader.ReadByte();
                    if (type == 8) break;
                    string name = ReadBinaryString(reader);
                    if (type == 0)
                    {
                        NonSteamShortcut shortcut = ReadShortcutObject(reader);
                        if (IsDecimalKey(name) && shortcut.AppId != 0)
                            shortcuts.Add(shortcut);
                    }
                    else
                    {
                        SkipBinaryValue(reader, type);
                    }
                }
            }
            return shortcuts;
        }

        static NonSteamShortcut ReadShortcutObject(BinaryReader reader)
        {
            var shortcut = new NonSteamShortcut();
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte type = reader.ReadByte();
                if (type == 8) return shortcut;
                string name = ReadBinaryString(reader);
                if (type == 0)
                {
                    SkipBinaryObject(reader, 1);
                }
                else if (type == 1)
                {
                    string value = ReadBinaryString(reader);
                    if (name.Equals("AppName", StringComparison.OrdinalIgnoreCase)) shortcut.Name = value;
                    else if (name.Equals("Exe", StringComparison.OrdinalIgnoreCase)) shortcut.Exe = value;
                    else if (name.Equals("StartDir", StringComparison.OrdinalIgnoreCase)) shortcut.StartDir = value;
                }
                else if (type == 2)
                {
                    uint value = reader.ReadUInt32();
                    if (name.Equals("appid", StringComparison.OrdinalIgnoreCase)) shortcut.AppId = value;
                    else if (name.Equals("LastPlayTime", StringComparison.OrdinalIgnoreCase)) shortcut.LastPlayTime = value;
                }
                else
                {
                    SkipBinaryValue(reader, type);
                }
            }
            throw new EndOfStreamException("Unterminated Steam shortcut record.");
        }

        static void SkipBinaryObject(BinaryReader reader, int depth)
        {
            if (depth > 64) throw new InvalidDataException("Steam shortcut nesting is too deep.");
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte type = reader.ReadByte();
                if (type == 8) return;
                ReadBinaryString(reader); // field name
                if (type == 0) SkipBinaryObject(reader, depth + 1);
                else SkipBinaryValue(reader, type);
            }
            throw new EndOfStreamException("Unterminated Steam shortcut object.");
        }

        static void SkipBinaryValue(BinaryReader reader, byte type)
        {
            switch (type)
            {
                case 1: ReadBinaryString(reader); break;
                case 2:
                case 3:
                case 4:
                case 6: reader.ReadUInt32(); break;
                case 5: ReadBinaryWideString(reader); break;
                case 7: reader.ReadUInt64(); break;
                default: throw new InvalidDataException("Unknown Binary KeyValues type " + type + ".");
            }
        }

        static string ReadBinaryString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            while (true)
            {
                byte value = reader.ReadByte();
                if (value == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(value);
                if (bytes.Count > 1024 * 1024)
                    throw new InvalidDataException("Steam shortcut string is too large.");
            }
        }

        static string ReadBinaryWideString(BinaryReader reader)
        {
            var value = new StringBuilder();
            while (true)
            {
                ushort c = reader.ReadUInt16();
                if (c == 0) return value.ToString();
                value.Append((char)c);
                if (value.Length > 1024 * 1024)
                    throw new InvalidDataException("Steam shortcut string is too large.");
            }
        }

        static bool IsDecimalKey(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
                if (c < '0' || c > '9') return false;
            return true;
        }

        // Pull the executable out of Steam's stored command line. Paths containing spaces are normally
        // quoted; the .exe boundary handles legacy unquoted entries and entries with launch arguments.
        internal static string ResolveShortcutExecutablePath(string exeValue, string startDir)
        {
            if (string.IsNullOrWhiteSpace(exeValue)) return null;
            string command = Environment.ExpandEnvironmentVariables(exeValue.Trim());
            string candidate = null;

            if (command.Length > 1 && command[0] == '"')
            {
                int closing = command.IndexOf('"', 1);
                candidate = closing > 1 ? command.Substring(1, closing - 1) : command.Trim('"');
            }
            else if (File.Exists(command.Trim('"')))
            {
                candidate = command.Trim('"');
            }
            else
            {
                int exeEnd = FindExecutableExtensionEnd(command);
                if (exeEnd > 0) candidate = command.Substring(0, exeEnd);
                else
                {
                    int whitespace = -1;
                    for (int i = 0; i < command.Length; i++)
                        if (char.IsWhiteSpace(command[i])) { whitespace = i; break; }
                    candidate = whitespace > 0 ? command.Substring(0, whitespace) : command;
                }
            }

            candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (candidate.Length == 0 || candidate.IndexOf("://", StringComparison.Ordinal) >= 0)
                return null;

            try
            {
                if (!Path.IsPathRooted(candidate) && !string.IsNullOrWhiteSpace(startDir))
                {
                    string dir = Environment.ExpandEnvironmentVariables(startDir.Trim().Trim('"'));
                    candidate = Path.Combine(dir, candidate);
                }
                return NormalizeComparablePath(candidate);
            }
            catch { return null; }
        }

        static int FindExecutableExtensionEnd(string command)
        {
            int start = 0;
            while (start < command.Length)
            {
                int i = command.IndexOf(".exe", start, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return -1;
                int end = i + 4;
                if (end == command.Length || char.IsWhiteSpace(command[end]) || command[end] == '"')
                    return end;
                start = end;
            }
            return -1;
        }

        static string NormalizeComparablePath(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                full = @"\\" + full.Substring(8);
            else if (full.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                full = full.Substring(4);
            return full;
        }

        // RunningAppID is usually enough when a shortcut was launched from Steam. The process match is
        // the fallback for Steam builds/timing windows where that registry value stays zero. Exact path
        // matches are preferred; process-name matching is only used when Windows denies MainModule access
        // (common with anti-cheat-protected games).
        static uint DetectRunningNonSteamShortcutByProcess()
        {
            List<NonSteamShortcut> shortcuts = GetNonSteamShortcuts();
            if (shortcuts.Count == 0) return 0;

            uint best = 0;
            uint bestLastPlayTime = 0;
            int bestScore = -1;
            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    string processName = null;
                    string executablePath = null;
                    bool hasWindow = false;
                    try
                    {
                        try { processName = process.ProcessName; } catch { }
                        try { hasWindow = process.MainWindowHandle != IntPtr.Zero; } catch { }
                        try
                        {
                            if (process.MainModule != null)
                                executablePath = NormalizeComparablePath(process.MainModule.FileName);
                        }
                        catch { }
                    }
                    finally { try { process.Dispose(); } catch { } }

                    foreach (NonSteamShortcut shortcut in shortcuts)
                    {
                        if (string.IsNullOrEmpty(shortcut.ExecutablePath)) continue;
                        bool exactPath = !string.IsNullOrEmpty(executablePath) && string.Equals(
                            executablePath, shortcut.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                        bool nameFallback = string.IsNullOrEmpty(executablePath) &&
                            !string.IsNullOrEmpty(processName) && !string.IsNullOrEmpty(shortcut.ProcessName) &&
                            string.Equals(processName, shortcut.ProcessName, StringComparison.OrdinalIgnoreCase);
                        if (!exactPath && !nameFallback) continue;

                        int score = exactPath ? 100 : 50;
                        if (shortcut.IsActiveUser) score += 20;
                        if (hasWindow) score += 5;
                        if (score > bestScore || (score == bestScore && shortcut.LastPlayTime > bestLastPlayTime))
                        {
                            best = shortcut.AppId;
                            bestScore = score;
                            bestLastPlayTime = shortcut.LastPlayTime;
                        }
                    }
                }
            }
            catch { }
            return best;
        }

        // Every ...\steamapps folder across all configured Steam libraries.
        static List<string> GetSteamAppsFolders()
        {
            var list = new List<string>();
            string root = GetSteamInstallPath();
            if (string.IsNullOrEmpty(root)) return list;
            string mainApps = Path.Combine(root, "steamapps");
            if (Directory.Exists(mainApps)) list.Add(mainApps);
            try
            {
                string vdf = Path.Combine(mainApps, "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    string txt = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(txt, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        string apps = Path.Combine(p, "steamapps");
                        if (Directory.Exists(apps) && !list.Contains(apps)) list.Add(apps);
                    }
                }
            }
            catch { }
            return list;
        }

        // If the exe lives under a Steam library (...\steamapps\common\<InstallDir>\...),
        // read the sibling appmanifest_*.acf files to find the AppID whose "installdir"
        // matches. Returns null for non-Steam executables.
        public static string TryResolveSteamAppId(string exePath)
        {
            if (string.IsNullOrEmpty(exePath))
                return null;

            string norm = exePath.Replace('/', '\\');
            const string marker = @"\steamapps\common\";
            int i = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                return null;

            string rest = norm.Substring(i + marker.Length);
            int slash = rest.IndexOf('\\');
            string installDir = slash >= 0 ? rest.Substring(0, slash) : rest;
            string steamappsDir = norm.Substring(0, i) + @"\steamapps";

            try
            {
                foreach (string acf in Directory.GetFiles(steamappsDir, "appmanifest_*.acf"))
                {
                    string txt = File.ReadAllText(acf);
                    string instPattern = "\"installdir\"\\s+\"" + Regex.Escape(installDir) + "\"";
                    if (Regex.IsMatch(txt, instPattern, RegexOptions.IgnoreCase))
                    {
                        Match m = Regex.Match(txt, "\"appid\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase);
                        if (m.Success)
                            return m.Groups[1].Value;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
