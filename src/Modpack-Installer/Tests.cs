using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MakeshiftSetup
{
    internal static class Tests
    {
        private static List<string> checks = new List<string>();
        private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks.Add("PASS: " + name); }
        private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name); }
        private static void Put(string root, string relative, byte[] content) { string path = Engine.SafePath(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, content); }
        private static ushort MessageId(string name) { unchecked { int h = 23; foreach (char c in name) h = h * 31 + c; return (ushort)h; } }
        internal static void Run(string work)
        {
            if (work == null) throw new ArgumentException("Specify work folder.");
            string root = Path.Combine(Path.GetFullPath(work), "modpack-tests-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")); Directory.CreateDirectory(root);
            try
            {
                LocalDinkumMods.DepositRules.Test(); Check(true, "10000 live deposit planning cases and metadata/bounds regressions");
                LocalDinkumMods.DisplayRules.Test(); Check(true, "1000 lock-mask conservation cases; lock parsing; long-price and relic rounding");
                LocalDinkumMods.ItemLockRules.Test(); Check(true, "per-copy GUID identity; split/merge; native-save metadata roundtrip; stale and malformed record rejection");
                string configExample = "# keep\r\n[Keybinds]\r\nSendToChests = KeypadDivide\r\n[General]\r\nRangeTiles = 40";
                string updated = ShortcutSettings.SetValue(configExample, "Keybinds", "ToggleSlotLock", "F8 + LeftControl");
                Check(updated.Contains("# keep") && updated.Contains("RangeTiles = 40") && updated.IndexOf("ToggleSlotLock") < updated.IndexOf("[General]"), "INI insert stays in section and preserves unrelated entries");
                Check(ShortcutSettings.SetValue(updated, "Keybinds", "ToggleSlotLock", "F8 + LeftControl") == updated, "INI edits idempotent");
                ShortcutSettings.Validate(ShortcutSettings.Items.ToDictionary(i => i.Key, i => i.Default)); Check(true, "default shortcuts valid and distinct");
                Check(Catalog.Mods.Length == 8 && Catalog.Mods.Single(m => m.Key == "enhancement").DefaultSelected && !Catalog.Mods.Single(m => m.Key == "recovery").DefaultSelected, "eight selectable mods; enhancement enabled for participants, recovery opt-in");
                Check(!Catalog.Mods.Single(m => m.Key == "enhancement").Name.Contains("미리보기") && Catalog.Mods.Single(m => m.Key == "enhancement").Description.Contains("전원 동일 버전"), "enhancement catalog requires all clients and is not preview-only");
                Check(ShortcutSettings.Items.Single(i => i.Mod == "enhancement").Default == "F11", "executable enhancement shortcut is configurable and defaults to F11");
                foreach (string variant in new[] { "modern", "legacy" })
                    using (var stream = new MemoryStream(Engine.Resource("enhancement." + variant + ".dll")))
                    using (var module = Mono.Cecil.ModuleDefinition.ReadModule(stream))
                    {
                        var instructions = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
                        var plugin = module.GetTypes().Single(t => t.Name == "EquipmentEnhancement");
                        Check(plugin.CustomAttributes.Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin" && (string)a.ConstructorArguments[2].Value == "1.2.0"), variant + " enhancement executable plugin version is 1.2.0");
                        Check(instructions.Select(i => i.Operand).OfType<Mono.Cecil.MethodReference>().Any(m => m.DeclaringType.Name == "EnhancementTransactions" && m.Name == "Begin"), variant + " UI has executable paid-attempt call, not disabled preview");
                        Check(module.GetTypes().Any(t => t.Name == "EnhancementConversion") && module.GetTypes().Any(t => t.Name == "EnhancementNetwork") && module.GetTypes().Any(t => t.Name == "EnhancementRegistry"), variant + " runtime includes conversion, network and equipment registry");
                        var transaction = module.GetTypes().Single(t => t.Name == "EnhancementTransactions");
                        var costCalls = transaction.Methods.Where(m => m.Name == "Begin" || m.Name == "Receive").SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<Mono.Cecil.MethodReference>().ToArray();
                        Check(!costCalls.Any(m => m.DeclaringType.Name == "EnhancementTransactions" && (m.Name == "Material" || m.Name == "Take" || m.Name == "Count")), variant + " paid attempt never reads or removes material items");
                        Check(module.GetTypes().Any(t => t.Name == "EnhancementSaveUpgrade"), variant + " participant migration included");
                    }
                Reject(() => ShortcutSettings.Validate(new Dictionary<string, string> { { "SortInventory", "F6" }, { "SendToChests", "F6" } }), "duplicate shortcuts rejected");
                Reject(() => ShortcutSettings.Validate(new Dictionary<string, string> { { "ToggleSlotLock", "F8\n[General]" } }), "shortcut INI injection rejected");
                Reject(() => Engine.SafePath(root, "../escape"), "outside-root path rejection");
                Reject(() => Engine.SafePath(root, "BepInEx/plugins/../config/x"), "within-root traversal rejection");
                Reject(() => Engine.SafePath(root, "x:y"), "alternate stream rejection");
                Reject(() => Catalog.Select(new[] { "unknown" }), "unknown selection rejection");
                byte[] loader = File.ReadAllBytes(Path.Combine(work, "BepInEx-Unity.Mono-win-x64-6.0.0-be.785+6abdba4.zip"));
                Reject(() => Engine.LoaderFiles(new byte[] { 1, 2, 3 }), "loader hash validation");
                var loaderFiles = Engine.LoaderFiles(loader);
                TestEnhancementUpgrade(work, root, loader, loaderFiles);
                var originalOther = File.ReadAllBytes(typeof(object).Assembly.Location);
                Action<string> fixture = p => { if (!Directory.Exists(p)) throw new IOException("Missing fixture"); };
                string settings = Path.Combine(root, "shortcuts"); Directory.CreateDirectory(settings);
                Put(settings, Catalog.Mods[2].Config, Encoding.UTF8.GetBytes(configExample));
                var keys = ShortcutSettings.Items.ToDictionary(i => i.Key, i => i.Default); keys["SendToChests"] = "G + LeftControl";
                Engine.InstallCore(settings, fixture, loader, -1, new[] { "inventory" }, keys);
                Check(ShortcutSettings.Read(settings)["SendToChests"] == "G + LeftControl", "custom keys written and read back");
                Check(!File.Exists(Engine.SafePath(settings, Catalog.Mods[4].Config)) && !File.Exists(Engine.SafePath(settings, Catalog.Mods[5].Config)), "unchecked mods settings untouched");
                Check(!File.Exists(Engine.SafePath(settings, Catalog.Mods[6].Config)) && !File.Exists(Engine.SafePath(settings, Catalog.Mods[6].Target)), "unselected enhancement not installed or configured");
                keys["OpenEnhancement"] = "E + LeftControl";
                Engine.InstallCore(settings, fixture, loader, -1, new[] { "enhancement" }, keys);
                Check(ShortcutSettings.Read(settings)["OpenEnhancement"] == "E + LeftControl", "enhancement custom shortcut roundtrip");
                keys["Scan"] = "F4";
                Engine.InstallCore(settings, fixture, loader, -1, new[] { "recovery" }, keys);
                Check(ShortcutSettings.Read(settings)["Scan"] == "F4" && File.Exists(Engine.SafePath(settings, Catalog.Mods[7].Target)), "recovery-only install with existing enhancement and custom scan key");
                Engine.RestoreInitial(settings);
                Check(File.ReadAllText(Engine.SafePath(settings, Catalog.Mods[2].Config)) == configExample, "initial restore returns exact old shortcuts");
                Check(!File.Exists(Engine.SafePath(settings, Catalog.Mods[6].Config)) && !File.Exists(Engine.SafePath(settings, Catalog.Mods[6].Target)), "unused enhancement initial restore removes introduced plugin and shortcut");
                Check(!File.Exists(Engine.SafePath(settings, Catalog.Mods[7].Config)) && !File.Exists(Engine.SafePath(settings, Catalog.Mods[7].Target)), "initial restore removes introduced recovery plugin and shortcuts");
                string missingRecoveryDependency = Path.Combine(root, "missing-recovery-dependency"); Directory.CreateDirectory(missingRecoveryDependency);
                Reject(() => Engine.InstallCore(missingRecoveryDependency, fixture, loader, -1, new[] { "recovery" }), "recovery without enhancement rejected");
                Check(Directory.GetFileSystemEntries(missingRecoveryDependency).Length == 0, "dependency rejection writes no files or baseline");
                string oldMod = Path.Combine(root, "old-moreplayers"); Directory.CreateDirectory(oldMod);
                Put(oldMod, "BepInEx/plugins/nested/renamed-old.dll", File.ReadAllBytes(Path.Combine(work, "MorePlayers-0.3.0.dll")));
                Reject(() => Engine.InstallCore(oldMod, fixture, loader, -1), "renamed nested MorePlayers 0.3 rejected before changes");
                Check(!File.Exists(Engine.SafePath(oldMod, "winhttp.dll")) && Engine.ActiveBaseline(oldMod) == null, "old-mod rejection leaves install untouched");
                string upgrade = Path.Combine(root, "v2-upgrade"); Directory.CreateDirectory(upgrade);
                Engine.InstallCore(upgrade, fixture, loader, -1, new[] { "fancy" });
                string priorBaseline = Engine.ActiveBaseline(upgrade), manifestFile = Path.Combine(priorBaseline, "manifest.xml");
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Backup)); Backup prior;
                using (var input = File.OpenRead(manifestFile)) prior = (Backup)serializer.Deserialize(input);
                prior.Files.RemoveAll(f => Catalog.Mods.Skip(4).Any(m => f.Path == m.Config || f.Path == m.Target));
                using (var output = File.Create(manifestFile)) serializer.Serialize(output, prior);
                Put(upgrade, Catalog.Mods[4].Config, Encoding.UTF8.GetBytes("# pre-existing price config"));
                Engine.InstallCore(upgrade, fixture, loader, -1, new[] { "prices", "npc" }, keys);
                Check(Engine.ActiveBaseline(upgrade) == priorBaseline, "v2 baseline extended without resetting original targets");
                Engine.RestoreInitial(upgrade);
                Check(File.ReadAllText(Engine.SafePath(upgrade, Catalog.Mods[4].Config)) == "# pre-existing price config" && !File.Exists(Engine.SafePath(upgrade, Catalog.Mods[5].Config)), "upgraded baseline restores newly managed configs exactly");
                string fresh = Path.Combine(root, "fresh"); Directory.CreateDirectory(fresh);
                Engine.InstallCore(fresh, fixture, loader, -1, new[] { "fancy" });
                Check(File.Exists(Engine.SafePath(fresh, Catalog.Mods[1].Target)) && !File.Exists(Engine.SafePath(fresh, Catalog.Mods[2].Target)), "selected-only install");
                string first = Engine.ActiveBaseline(fresh); Check(first != null, "first apply creates initial baseline");
                Engine.InstallCore(fresh, fixture, loader, -1, Catalog.Mods.Where(m => m.Key != "fancy").Select(m => m.Key).ToArray());
                Check(Engine.ActiveBaseline(fresh) == first, "later selections preserve original baseline");
                foreach (var mod in Catalog.Mods) Check(Engine.FileHash(Engine.SafePath(fresh, mod.Target)) == Engine.Hash(Engine.Resource(mod.Key + ".modern.dll")), mod.Key + " payload hash");
                foreach (var mod in Catalog.Mods) Put(fresh, mod.Config, Encoding.UTF8.GetBytes("generated settings"));
                Put(fresh, "BepInEx/config/LocalInventoryTransfers/uncertain.txt", new byte[] { 3, 7, 9 });
                Put(fresh, "BepInEx/config/LocalEquipmentRecovery/recovery.log", new byte[] { 4, 1 });
                Engine.RestoreInitial(fresh);
                Check(Catalog.Mods.All(m => !File.Exists(Engine.SafePath(fresh, m.Target)) && !File.Exists(Engine.SafePath(fresh, m.Config))), "initial restore removes only introduced managed plugins/configs");
                Check(!File.Exists(Engine.SafePath(fresh, "winhttp.dll")), "initial restore removes introduced loader");
                Check(File.Exists(Engine.SafePath(fresh, "BepInEx/config/LocalInventoryTransfers/uncertain.txt")), "uncertain transfer journal preserved on restore");
                Check(File.ReadAllBytes(Engine.SafePath(fresh, "BepInEx/config/LocalEquipmentRecovery/recovery.log")).SequenceEqual(new byte[] { 4, 1 }), "recovery audit log preserved on restore");
                Check(Engine.ActiveBaseline(fresh) == null && Directory.Exists(first), "baseline archived but never erased");
                Reject(() => Engine.RestoreInitial(fresh), "repeat initial restore refused without new baseline");
                Check(Directory.GetDirectories(Engine.SafePath(fresh, "DinkumModpack-Backups"), "op-*").Length == 3, "restore operation is itself backed up");

                string existing = Path.Combine(root, "existing"); Directory.CreateDirectory(existing);
                foreach (var pair in loaderFiles) Put(existing, pair.Key, pair.Value);
                Put(existing, Catalog.Mods[0].Target, Engine.Resource("makeshift.legacy.dll"));
                Put(existing, "BepInEx/plugins/renamed/Fancy-old.dll", Engine.Resource("fancy.legacy.dll"));
                Put(existing, Catalog.Mods[2].Config, Encoding.UTF8.GetBytes("old configuration"));
                Put(existing, "BepInEx/plugins/Unrelated.dll", originalOther); Put(existing, "Saves/untouched.bin", new byte[] { 1, 5 });
                string coreHash = Engine.FileHash(Engine.SafePath(existing, "BepInEx/core/BepInEx.Core.dll"));
                Engine.InstallCore(existing, fixture, loader, -1);
                Check(!File.Exists(Engine.SafePath(existing, "BepInEx/plugins/renamed/Fancy-old.dll")), "duplicate plugin discovered by GUID");
                int beforeRepeat = Directory.GetDirectories(Engine.SafePath(existing, "DinkumModpack-Backups"), "op-*").Length;
                Engine.InstallCore(existing, fixture, loader, -1);
                Check(beforeRepeat == Directory.GetDirectories(Engine.SafePath(existing, "DinkumModpack-Backups"), "op-*").Length, "idempotent repeat install");
                Put(existing, Catalog.Mods[2].Config, Encoding.UTF8.GetBytes("new user configuration"));
                Engine.RestoreInitial(existing);
                Check(Engine.FileHash(Engine.SafePath(existing, Catalog.Mods[0].Target)) == Engine.Hash(Engine.Resource("makeshift.legacy.dll")), "pre-existing Makeshift restored exactly");
                Check(File.Exists(Engine.SafePath(existing, "BepInEx/plugins/renamed/Fancy-old.dll")), "renamed original restored to exact location");
                Check(File.ReadAllText(Engine.SafePath(existing, Catalog.Mods[2].Config)) == "old configuration", "initial config restored");
                Check(Engine.FileHash(Engine.SafePath(existing, "BepInEx/plugins/Unrelated.dll")) == Engine.Hash(originalOther), "unrelated plugin unchanged");
                Check(Engine.FileHash(Engine.SafePath(existing, "BepInEx/core/BepInEx.Core.dll")) == coreHash, "existing loader unchanged");
                Check(File.ReadAllBytes(Engine.SafePath(existing, "Saves/untouched.bin")).SequenceEqual(new byte[] { 1, 5 }), "save fixture unchanged");

                string legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
                foreach (string source in Directory.GetFiles(Path.Combine(work, "loader-pre1/BepInEx/core"))) Put(legacy, "BepInEx/core/" + Path.GetFileName(source), File.ReadAllBytes(source));
                foreach (string name in new[] { "winhttp.dll", "doorstop_config.ini" }) Put(legacy, name, File.ReadAllBytes(Path.Combine(work, "loader-pre1", name)));
                Engine.InstallCore(legacy, fixture, loader, -1);
                foreach (var mod in Catalog.Mods) Check(Engine.FileHash(Engine.SafePath(legacy, mod.Target)) == Engine.Hash(Engine.Resource(mod.Key + ".legacy.dll")), mod.Key + " legacy selection");
                string broken = Path.Combine(root, "rollback"); Directory.CreateDirectory(broken);
                Reject(() => Engine.InstallCore(broken, fixture, loader, 2), "forced installation failure");
                Check(!File.Exists(Engine.SafePath(broken, "winhttp.dll")) && Catalog.Mods.All(m => !File.Exists(Engine.SafePath(broken, m.Target))), "failed installation rolls back changed files");
                string conflict = Path.Combine(root, "unknown"); Directory.CreateDirectory(conflict); Put(conflict, "winhttp.dll", new byte[] { 1 });
                Reject(() => Engine.InstallCore(conflict, fixture, loader, -1), "unknown loader not overwritten");
                string protect = Path.Combine(root, "loader-dependent-plugin"); Directory.CreateDirectory(protect); Engine.InstallCore(protect, fixture, loader, -1, new[] { "fancy" });
                Put(protect, "BepInEx/plugins/Other.dll", originalOther);
                Reject(() => Engine.RestoreInitial(protect), "restoring introduced loader refuses to break unrelated added plugin");
                Check(File.Exists(Engine.SafePath(protect, "winhttp.dll")), "refused restore makes no loader changes");
                Check(Engine.FindGames().Any(), "Steam discovery");
                string enhancementGuard = Path.Combine(root, "enhancement-restore-guard"); Directory.CreateDirectory(enhancementGuard);
                Engine.InstallCore(enhancementGuard, fixture, loader, -1, new[] { "fancy" });
                string guardedBaseline = Engine.ActiveBaseline(enhancementGuard);
                string guardedPluginHash = Engine.FileHash(Engine.SafePath(enhancementGuard, Catalog.Mods[1].Target));
                Put(enhancementGuard, Engine.EnhancementRestoreFlag, Encoding.UTF8.GetBytes("fixture: conversion required"));
                Reject(() => Engine.RestoreInitial(enhancementGuard), "initial restore blocked until enhanced equipment conversion completes");
                Reject(() => Engine.Restore(Engine.LatestBackup(enhancementGuard)), "operation rollback blocked until enhanced equipment conversion completes");
                Reject(() => Engine.InstallCore(enhancementGuard, fixture, loader, -1, new[] { "enhancement" }), "effective enhancement replacement blocked with active conversion marker");
                Check(!File.Exists(Engine.SafePath(enhancementGuard, Catalog.Mods.Single(m => m.Key == "enhancement").Target)), "blocked enhancement replacement writes no payload");
                Check(Engine.ActiveBaseline(enhancementGuard) == guardedBaseline && Engine.FileHash(Engine.SafePath(enhancementGuard, Catalog.Mods[1].Target)) == guardedPluginHash && File.Exists(Engine.SafePath(enhancementGuard, Engine.EnhancementRestoreFlag)), "conversion guard leaves baseline, plugin and safety marker untouched");
                string managed = Path.Combine(Engine.FindGames().First(), "Dinkum_Data/Managed");
                using (var gameModule = Mono.Cecil.ModuleDefinition.ReadModule(Path.Combine(managed, "Assembly-CSharp.dll")))
                {
                    var calls = gameModule.GetType("ChestWindow").Methods.Single(m => m.Name == "autoStackIntoChest").Body.Instructions.Select(i => i.Operand).OfType<Mono.Cecil.MethodReference>().ToArray();
                    Check(calls.Count(m => m.DeclaringType.FullName == "Inventory" && m.Name == "getAmountOfItemInAllSlots") == 2 && calls.Count(m => m.DeclaringType.FullName == "Inventory" && m.Name == "removeAmountOfItem") == 2, "native quick-stack IL matches both lock-filter patch sites");
                    Check(gameModule.GetType("SaveAndLoad").Fields.Any(f => f.Name == "cacheFileSettings" && f.FieldType.Name == "ES3Settings") && gameModule.GetType("SaveAndLoad").Fields.Any(f => f.Name == "savedSucessfully" && f.FieldType.FullName == "System.Boolean"), "native cached-save fields match individual-lock persistence hooks");
                }
                var messageNames = new HashSet<string>(new[] { "MakeshiftFixed.CatalogRequest", "MakeshiftFixed.CatalogReply", "LocalDinkumMods.TransferRequest", "LocalDinkumMods.TransferReply", "LocalDinkumMods.NpcMapRequest", "LocalDinkumMods.NpcMapReply" });
                foreach (var mod in Catalog.Mods)
                    using (var stream = new MemoryStream(Engine.Resource(mod.Key + ".modern.dll")))
                    using (var module = Mono.Cecil.ModuleDefinition.ReadModule(stream))
                        foreach (var type in module.GetTypes().Where(t => t.Interfaces.Any(i => i.InterfaceType.FullName == "Mirror.NetworkMessage"))) messageNames.Add(type.FullName.Replace('/', '+'));
                foreach (string assemblyName in new[] { "Mirror.dll", "Assembly-CSharp.dll" })
                    using (var module = Mono.Cecil.ModuleDefinition.ReadModule(Path.Combine(managed, assemblyName)))
                        foreach (var type in module.GetTypes().Where(t => t.Interfaces.Any(i => i.InterfaceType.FullName == "Mirror.NetworkMessage"))) messageNames.Add(type.FullName.Replace('/', '+'));
                Check(messageNames.GroupBy(MessageId).All(g => g.Count() == 1), "all embedded pack messages including enhancement avoid stock/pack ID collisions");
                using (var form = new MainForm()) form.Render(Path.Combine(root, "installer-ui.png"));
                Check(File.Exists(Path.Combine(root, "installer-ui.png")), "GUI layout render");
                using (var form = new ShortcutForm(ShortcutSettings.Items.ToDictionary(i => i.Key, i => i.Default))) form.Render(Path.Combine(root, "shortcuts-ui.png"));
                Check(File.Exists(Path.Combine(root, "shortcuts-ui.png")), "shortcut editor render");
                checks.Add("ALL PASS: " + checks.Count + " checks. Real game files were not changed by fixtures.");
            }
            finally { File.WriteAllLines(Path.Combine(root, "results.txt"), checks); }
        }
        private static void TestEnhancementUpgrade(string work, string root, byte[] loader, Dictionary<string, byte[]> loaderFiles)
        {
            var mod = Catalog.Mods.Single(m => m.Key == "enhancement");
            var sources = new[] {
                "modpack-build/modern/EquipmentEnhancement/LocalDinkum.EquipmentEnhancement.dll",
                "compat-build/modern/EquipmentEnhancement/LocalDinkum.EquipmentEnhancement.dll",
                "modpack-build/legacy/EquipmentEnhancement/LocalDinkum.EquipmentEnhancement.dll"
            };
            for (int n = 0; n < sources.Length; n++)
            {
                string variant = n == 2 ? "legacy" : "modern";
                string game = Path.Combine(root, "verified-upgrade-" + n); Directory.CreateDirectory(game);
                if (variant == "modern") foreach (var pair in loaderFiles) Put(game, pair.Key, pair.Value);
                else
                {
                    foreach (string source in Directory.GetFiles(Path.Combine(work, "loader-pre1/BepInEx/core"))) Put(game, "BepInEx/core/" + Path.GetFileName(source), File.ReadAllBytes(source));
                    foreach (string name in new[] { "winhttp.dll", "doorstop_config.ini" }) Put(game, name, File.ReadAllBytes(Path.Combine(work, "loader-pre1", name)));
                }
                Put(game, mod.Target, File.ReadAllBytes(Path.Combine(work, sources[n])));
                Put(game, Engine.EnhancementRestoreFlag, Encoding.UTF8.GetBytes("CONVERSION_REQUIRED"));
                var plan = new Dictionary<string, byte[]> { { mod.Target, Engine.Resource("enhancement." + variant + ".dll") } };
                Check(Engine.VerifiedEnhancementUpgrade(game, plan, variant), "known enhancement upgrade accepted: " + n);
                var remove = new Dictionary<string, byte[]> { { mod.Target, null } };
                Check(!Engine.VerifiedEnhancementUpgrade(game, remove, variant), "upgrade exemption never authorizes removal: " + n);
                plan["winhttp.dll"] = new byte[] { 1 };
                Check(!Engine.VerifiedEnhancementUpgrade(game, plan, variant), "upgrade exemption rejects loader change: " + n); plan.Remove("winhttp.dll");
                Put(game, "BepInEx/plugins/duplicate/LocalDinkum.EquipmentEnhancement.dll", File.ReadAllBytes(Path.Combine(work, sources[n])));
                Check(!Engine.VerifiedEnhancementUpgrade(game, plan, variant), "duplicate enhancement blocks upgrade exemption: " + n);
                File.Move(Engine.SafePath(game, "BepInEx/plugins/duplicate/LocalDinkum.EquipmentEnhancement.dll"), Path.Combine(game, "duplicate-retained.bin"));
                string pending = "BepInEx/config/LocalEquipmentEnhancement/pending-slot0.txt";
                Put(game, pending, Encoding.UTF8.GetBytes("unresolved"));
                Reject(() => Engine.VerifiedEnhancementUpgrade(game, plan, variant), "pending transaction blocks upgrade: " + n);
                File.Move(Engine.SafePath(game, pending), Path.Combine(game, "pending-retained.txt"));
                Engine.InstallCore(game, p => { if (!Directory.Exists(p)) throw new IOException(); }, loader, -1, new[] { "enhancement" });
                Check(Engine.FileHash(Engine.SafePath(game, mod.Target)) == Engine.Hash(plan[mod.Target]), "verified upgrade installs exact new payload: " + n);
                Check(File.Exists(Engine.SafePath(game, Engine.EnhancementRestoreFlag)), "upgrade preserves conversion guard: " + n);
                Reject(() => Engine.RestoreInitial(game), "downgrade restore remains blocked after upgrade: " + n);
            }
            string unknown = Path.Combine(root, "unknown-enhancement-upgrade"); Directory.CreateDirectory(unknown);
            foreach (var pair in loaderFiles) Put(unknown, pair.Key, pair.Value);
            Put(unknown, mod.Target, new byte[] { 5, 4, 3 }); Put(unknown, Engine.EnhancementRestoreFlag, Encoding.UTF8.GetBytes("CONVERSION_REQUIRED"));
            Reject(() => Engine.InstallCore(unknown, p => { }, loader, -1, new[] { "enhancement" }), "unknown enhancement payload with guard cannot use exemption");
            Check(File.ReadAllBytes(Engine.SafePath(unknown, mod.Target)).SequenceEqual(new byte[] { 5, 4, 3 }), "unknown enhancement preserved after rejection");
        }
    }
}
