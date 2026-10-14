using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MakeshiftSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => new AssemblyName(e.Name).Name == "Mono.Cecil" ? Assembly.Load(Engine.Resource("Mono.Cecil.dll")) : null;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Contains("--verify-package")) { PackageChecks.Run(Value(args, "--verify-package")); return 0; }
                if (args.Contains("--self-test")) { Tests.Run(Value(args, "--self-test")); return 0; }
                if (args.Contains("--render-ui")) { using (var form = new MainForm()) form.Render(Value(args, "--render-ui")); return 0; }
                if (args.Contains("--render-shortcuts")) { using (var form = new ShortcutForm(ShortcutSettings.Items.ToDictionary(i => i.Key, i => i.Default))) form.Render(Value(args, "--render-shortcuts")); return 0; }
                string game = Value(args, "--install");
                if (game != null)
                {
                    string result = Engine.InstallSelection(game, (Value(args, "--mods") ?? "makeshift,fancy,inventory,fish,prices,npc,enhancement").Split(','));
                    if (Value(args, "--log") != null) File.WriteAllText(Value(args, "--log"), result);
                    return 0;
                }
                if (Value(args, "--restore-initial") != null)
                {
                    string result = Engine.RestoreInitial(Value(args, "--restore-initial"));
                    if (Value(args, "--log") != null) File.WriteAllText(Value(args, "--log"), result);
                    return 0;
                }
                Application.Run(new MainForm()); return 0;
            }
            catch (Exception e)
            {
                if (Value(args, "--log") != null) File.WriteAllText(Value(args, "--log"), e.ToString());
                if (!args.Contains("--silent") && !args.Contains("--self-test")) MessageBox.Show(e.Message, "통합팩 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        private static string Value(string[] args, string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
    }
}
