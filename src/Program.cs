using System;
using System.Threading;
using System.Windows.Forms;

namespace RemotePlayForge
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Single-instance guard so we don't spawn multiple tray icons.
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "RemotePlayForge_SingleInstance_Mutex", out createdNew))
            {
                if (!createdNew)
                    return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
