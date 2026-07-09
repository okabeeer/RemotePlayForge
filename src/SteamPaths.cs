using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RemotePlayForge
{
    // Locates the Steam install, detects whether Steam is running, and resolves the
    // Steam AppID for a given process by reading its library's appmanifest files.
    public static class SteamPaths
    {
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

        // The AppID of the game Steam currently considers "running" (0 if none).
        public static uint GetRunningAppId()
        {
            uint reg = 0;
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", null);
                if (v != null) uint.TryParse(v.ToString(), out reg);
            }
            catch { }

            // Trust RunningAppID only when it names an actually-installed Steam app. Some titles launch
            // through an external launcher and register a WRAPPER AppID that isn't in any library — e.g.
            // EA FC 26 sets RunningAppID to 1182480, whose app name is literally "EALink" and which has
            // no appmanifest. That id shows as "AppID <n>" and makes Remote Play Together label the
            // session as the launcher, not the game. When it isn't installed, detect the real game.
            if (reg != 0 && IsInstalledApp(reg)) return reg;

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
                            if (run != null && Convert.ToInt32(run) == 1 && IsInstalledApp(id))
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
            return null;
        }

        // Every ...\steamapps folder across all configured Steam libraries.
        static System.Collections.Generic.List<string> GetSteamAppsFolders()
        {
            var list = new System.Collections.Generic.List<string>();
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
