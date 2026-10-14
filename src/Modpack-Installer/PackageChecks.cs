using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Mono.Cecil;

namespace MakeshiftSetup
{
    internal static class PackageChecks
    {
        // Package/config/dependency checks only. Never installs into a game folder.
        internal static string Run(string work)
        {
            var report = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("FAIL: " + name); report.Add("PASS: " + name); };
            Action<Action, string> reject = (action, name) => { bool failed = false; try { action(); } catch { failed = true; } check(failed, name); };
            string root = Path.Combine(Path.GetFullPath(work), "package-4.2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.Combine(Path.GetFullPath(work), "loader-pre1", "BepInEx", "core"));
            check(Catalog.Mods.Length == 8 && Catalog.Mods.Count(m => m.DefaultSelected) == 7, "8 mods, 7 defaults; recovery host-only opt-in");
            var keys = ShortcutSettings.Items.ToDictionary(k => k.Key, k => k.Default);
            ShortcutSettings.Validate(keys);
            check(keys.Count == 10 && keys["Scan"] == "F5" && keys["Recall"] == "F5 + LeftControl", "10 distinct configurable shortcuts");
            foreach (string variant in new[] { "modern", "legacy" })
            {
                foreach (var mod in Catalog.Mods)
                using (var stream = new MemoryStream(Engine.Resource(mod.Key + "." + variant + ".dll")))
                using (var module = ModuleDefinition.ReadModule(stream, new ReaderParameters { AssemblyResolver = resolver }))
                    check(module.GetTypes().Any(t => t.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin" && (string)a.ConstructorArguments[0].Value == mod.Guid)), variant + " embedded " + mod.Key + " GUID");
                using (var stream = new MemoryStream(Engine.Resource("recovery." + variant + ".dll")))
                using (var module = ModuleDefinition.ReadModule(stream, new ReaderParameters { AssemblyResolver = resolver }))
                {
                    var type = module.GetTypes().Single(t => t.Name == "Recovery");
                    check(type.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInDependency" && (string)a.ConstructorArguments[0].Value == "LocalDinkum.EquipmentEnhancement"), variant + " recovery declares enhancement dependency");
                    var calls = type.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
                    check(calls.Any(m => m.Name == "set_NetworkdesiredPos") && !calls.Any(m => m.DeclaringType.Name == "NetworkServer" && (m.Name == "Spawn" || m.Name == "Destroy")), variant + " recovery moves native drop, no network spawn/destroy");
                    string game = Path.Combine(root, variant); Directory.CreateDirectory(game);
                    var selected = Catalog.Select(new[] { "recovery" });
                    reject(() => Engine.ValidateRecoveryDependency(game, selected, variant), variant + " missing dependency blocked");
                    var enhancement = Catalog.Mods.Single(m => m.Key == "enhancement");
                    string path = Engine.SafePath(game, enhancement.Target); Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, Engine.Resource("enhancement." + variant + ".dll"));
                    Engine.ValidateRecoveryDependency(game, selected, variant); check(true, variant + " exact installed dependency accepted");
                    File.WriteAllBytes(path, Engine.Resource("enhancement." + (variant == "modern" ? "legacy" : "modern") + ".dll"));
                    reject(() => Engine.ValidateRecoveryDependency(game, selected, variant), variant + " wrong-loader dependency blocked");
                }
            }
            keys["Scan"] = "F4"; keys["Recall"] = "R + LeftControl";
            ShortcutSettings.Validate(keys);
            var recovery = Catalog.Mods.Single(m => m.Key == "recovery");
            string config = Engine.SafePath(root, recovery.Config); Directory.CreateDirectory(Path.GetDirectoryName(config));
            string text = "# existing\r\n[Other]\r\nKeep = yes";
            foreach (var key in ShortcutSettings.Items.Where(i => i.Mod == "recovery")) text = ShortcutSettings.SetValue(text, "Keybinds", key.Key, keys[key.Key]);
            File.WriteAllText(config, text, new UTF8Encoding(false));
            var read = ShortcutSettings.Read(root);
            check(read["Scan"] == "F4" && read["Recall"] == "R + LeftControl" && text.Contains("Keep = yes"), "recovery custom keys roundtrip; unrelated config preserved");
            string invalid = Path.Combine(root, "unsupported-game"); Directory.CreateDirectory(Path.Combine(invalid, "Dinkum_Data", "Managed"));
            File.WriteAllBytes(Path.Combine(invalid, "Dinkum.exe"), new byte[] { 0 });
            File.WriteAllBytes(Path.Combine(invalid, "Dinkum_Data", "Managed", "Assembly-CSharp.dll"), new byte[] { 0 });
            reject(() => typeof(Engine).GetMethod("ValidateGame", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { invalid }), "unsupported game hash still rejected");
            check(!Directory.Exists(Path.Combine(invalid, "BepInEx")), "unsupported fixture left unmodified");
            using (var form = new MainForm()) form.Render(Path.Combine(root, "installer-ui.png"));
            using (var form = new ShortcutForm(read)) form.Render(Path.Combine(root, "shortcuts-ui.png"));
            report.Add("ALL PASS: " + report.Count + " package checks. No install/restore or live-game test.");
            File.WriteAllLines(Path.Combine(root, "results.txt"), report);
            return root;
        }
    }
}
