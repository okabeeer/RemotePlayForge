using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RemotePlayForge
{
    internal static class SteamPathsTests
    {
        static int Main(string[] args)
        {
            try
            {
                RegistryDwordKeepsHighBitAppId();
                BinaryShortcutsVdfIsParsed();
                ShortcutCommandLineIsResolved();
                if (args.Length > 0) ProbeRealShortcuts(args[0]);
                Console.WriteLine("SteamPaths tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        static void RegistryDwordKeepsHighBitAppId()
        {
            const uint expected = 0xF1E593D9;
            uint actual;
            Assert(SteamPaths.TryConvertRegistryUInt32(unchecked((int)expected), out actual),
                   "signed REG_DWORD was rejected");
            Assert(actual == expected, "signed REG_DWORD lost its high bit");
        }

        static void BinaryShortcutsVdfIsParsed()
        {
            string file = Path.Combine(Path.GetTempPath(), "rpf-shortcuts-" + Guid.NewGuid().ToString("N") + ".vdf");
            try
            {
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write((byte)0); WriteString(writer, "shortcuts");
                    writer.Write((byte)0); WriteString(writer, "0");
                    WriteUInt32(writer, "appid", 0xF1E593D9);
                    WriteText(writer, "AppName", "Test Game");
                    WriteText(writer, "Exe", "\"E:\\Games\\Test Game\\game.exe\" --safe");
                    WriteText(writer, "StartDir", "E:\\Games\\Test Game\\");
                    WriteUInt32(writer, "LastPlayTime", 1234567890);
                    writer.Write((byte)0); WriteString(writer, "tags");
                    WriteText(writer, "0", "Co-op");
                    writer.Write((byte)8); // tags
                    writer.Write((byte)8); // shortcut 0
                    writer.Write((byte)8); // shortcuts
                }

                List<SteamPaths.NonSteamShortcut> shortcuts = SteamPaths.ReadNonSteamShortcutsFile(file);
                Assert(shortcuts.Count == 1, "expected one shortcut");
                SteamPaths.NonSteamShortcut shortcut = shortcuts[0];
                Assert(shortcut.AppId == 0xF1E593D9, "shortcut AppID was parsed incorrectly");
                Assert(shortcut.Name == "Test Game", "shortcut name was parsed incorrectly");
                Assert(shortcut.Exe == "\"E:\\Games\\Test Game\\game.exe\" --safe", "shortcut command was parsed incorrectly");
                Assert(shortcut.StartDir == "E:\\Games\\Test Game\\", "shortcut start directory was parsed incorrectly");
                Assert(shortcut.LastPlayTime == 1234567890, "shortcut last-play time was parsed incorrectly");
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        static void ShortcutCommandLineIsResolved()
        {
            string actual = SteamPaths.ResolveShortcutExecutablePath(
                "\"E:\\Games\\Test Game\\game.exe\" --safe", "E:\\Games\\Test Game\\");
            Assert(string.Equals(actual, @"E:\Games\Test Game\game.exe", StringComparison.OrdinalIgnoreCase),
                   "quoted shortcut executable was not resolved");

            actual = SteamPaths.ResolveShortcutExecutablePath("game.exe --safe", @"E:\Games\Test Game");
            Assert(string.Equals(actual, @"E:\Games\Test Game\game.exe", StringComparison.OrdinalIgnoreCase),
                   "relative shortcut executable was not resolved");
        }

        static void ProbeRealShortcuts(string file)
        {
            List<SteamPaths.NonSteamShortcut> shortcuts = SteamPaths.ReadNonSteamShortcutsFile(file);
            Assert(shortcuts.Count > 0, "real shortcuts.vdf contained no readable records");
            foreach (SteamPaths.NonSteamShortcut shortcut in shortcuts)
            {
                string executable = SteamPaths.ResolveShortcutExecutablePath(shortcut.Exe, shortcut.StartDir);
                Assert(string.Equals(SteamPaths.GetGameName(shortcut.AppId), shortcut.Name,
                                     StringComparison.Ordinal),
                       "public name lookup did not find " + shortcut.Name);
                Console.WriteLine("0x{0:X8} ({1}) | {2} | {3}",
                                  shortcut.AppId, shortcut.AppId, shortcut.Name, executable ?? "<no executable>");
            }
        }

        static void WriteText(BinaryWriter writer, string name, string value)
        {
            writer.Write((byte)1);
            WriteString(writer, name);
            WriteString(writer, value);
        }

        static void WriteUInt32(BinaryWriter writer, string name, uint value)
        {
            writer.Write((byte)2);
            WriteString(writer, name);
            writer.Write(value);
        }

        static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.UTF8.GetBytes(value));
            writer.Write((byte)0);
        }

        static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
