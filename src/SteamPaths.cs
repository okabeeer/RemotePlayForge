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
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", null);
                if (v != null)
                {
                    uint id;
                    if (uint.TryParse(v.ToString(), out id)) return id;
                }
            }
            catch { }
            return 0;
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
