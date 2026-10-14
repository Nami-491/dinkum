using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;
using Mono.Cecil;

namespace MakeshiftSetup
{
    public sealed class Backup
    {
        public string GamePath;
        public string Status;
        public List<Change> Files = new List<Change>();
    }
    public sealed class Change
    {
        public string Path;
        public string BeforeHash;
        public string AfterHash;
        public bool Applied;
    }
    internal static partial class Engine
    {
        internal const string GameHash = "5106DA5D624AAEDABB16159A9BDAA9AB3CE4D09EB992BD4759D881DD9D9BEA24";
        internal const string LoaderHash = "DB430F14D6661EB38BA96FCC13C07A163E87E553710821D87E5129F915A1B26B";
        internal const string Target = "BepInEx/plugins/Snaitf.Dinkum.Makeshift.dll";
        internal const string LoaderUrl = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.Mono-win-x64-6.0.0-be.785+6abdba4.zip";
        private const string BackupFolder = "DinkumModpack-Backups";
        private const string NoticeFolder = "DinkumModpack-Info";

        internal static byte[] Resource(string name)
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var output = new MemoryStream())
            {
                if (input == null) throw new FileNotFoundException("설치 파일 내부 리소스 누락: " + name);
                input.CopyTo(output); return output.ToArray();
            }
        }
        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
        internal static string FileHash(string path)
        {
            if (!File.Exists(path)) return null;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        internal static string SafePath(string root, string relative)
        {
            root = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains(':')) throw new IOException("허용하지 않는 파일 경로입니다.");
            if (relative.Split('/', '\\').Any(part => part == "." || part == "..")) throw new IOException("상대 경로 이동은 허용하지 않습니다.");
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', '\\')));
            if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("설치 폴더 밖의 경로는 사용할 수 없습니다.");
            for (string cursor = path; cursor != null && cursor.Length >= root.Length; cursor = System.IO.Path.GetDirectoryName(cursor))
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("링크/정션 경로는 안전을 위해 지원하지 않습니다: " + cursor);
            return path;
        }
        internal static void EnsureGameClosed()
        {
            var processes = Process.GetProcessesByName("Dinkum");
            bool running = processes.Length != 0;
            foreach (var p in processes) p.Dispose();
            if (running) throw new IOException("Dinkum이 실행 중입니다. 게임을 완전히 종료해야 설치/복원할 수 있습니다.");
        }
        private static void ValidateGame(string game)
        {
            if (!Environment.Is64BitOperatingSystem) throw new IOException("Windows 64비트만 지원합니다.");
            if (!File.Exists(SafePath(game, "Dinkum.exe"))) throw new IOException("선택한 폴더에 Dinkum.exe가 없습니다.");
            if (FileHash(SafePath(game, "Dinkum_Data/Managed/Assembly-CSharp.dll")) != GameHash)
                throw new IOException("확인한 Dinkum 버전과 다릅니다. 호환성 재확인 전에는 게임 파일을 변경하지 않습니다.");
        }

        internal static string LoaderVariant(string game)
        {
            string core = SafePath(game, "BepInEx/core/BepInEx.Core.dll");
            bool modern = File.Exists(SafePath(game, "BepInEx/core/BepInEx.Unity.Mono.dll"));
            bool legacy = File.Exists(SafePath(game, "BepInEx/core/BepInEx.Unity.dll"));
            bool bep5 = File.Exists(SafePath(game, "BepInEx/core/BepInEx.dll"));
            if (bep5 || (modern && legacy)) throw new IOException("BepInEx 5 또는 혼합된 로더입니다. 기존 파일은 보존했습니다. 로더 확인이 필요합니다.");
            if (File.Exists(core))
            {
                using (var assembly = AssemblyDefinition.ReadAssembly(core))
                    if (assembly.Name.Version.Major != 6) throw new IOException("지원하지 않는 BepInEx 버전입니다.");
                if (!modern && !legacy) throw new IOException("BepInEx Unity 로더 파일이 누락됐습니다.");
                string unity = SafePath(game, modern ? "BepInEx/core/BepInEx.Unity.Mono.dll" : "BepInEx/core/BepInEx.Unity.dll");
                using (var assembly = AssemblyDefinition.ReadAssembly(unity))
                    if (!assembly.MainModule.Types.Any(t => t.FullName == (modern ? "BepInEx.Unity.Mono.BaseUnityPlugin" : "BepInEx.BaseUnityPlugin")))
                        throw new IOException("예상한 BepInEx API가 없습니다.");
                if (!File.Exists(SafePath(game, "winhttp.dll")) || !File.Exists(SafePath(game, "doorstop_config.ini")))
                    throw new IOException("BepInEx 시작 파일이 누락됐습니다. 기존 로더를 먼저 확인하세요.");
                foreach (string required in new[] { "BepInEx/core/0Harmony.dll", "BepInEx/core/Mono.Cecil.dll",
                    modern ? "BepInEx/core/BepInEx.Unity.Mono.Preloader.dll" : "BepInEx/core/BepInEx.Preloader.Unity.dll" })
                    if (!File.Exists(SafePath(game, required))) throw new IOException("로더 필수 파일이 누락됐습니다: " + required);
                string doorstop = File.ReadAllText(SafePath(game, "doorstop_config.ini"));
                if (!Regex.IsMatch(doorstop, @"(?im)^\s*enabled\s*=\s*true\s*$"))
                    throw new IOException("기존 Doorstop 로더가 비활성화되어 있습니다. 자동으로 설정을 덮어쓰지 않았습니다.");
                string expected = modern ? @"BepInEx\core\BepInEx.Unity.Mono.Preloader.dll" : @"BepInEx\core\BepInEx.Preloader.Unity.dll";
                Match loaderTarget = Regex.Match(doorstop, @"(?im)^\s*(?:target_assembly|targetAssembly)\s*=\s*(.+?)\s*$");
                if (!loaderTarget.Success || !loaderTarget.Groups[1].Value.Trim('"').Replace('/', '\\').Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("기존 로더 시작 경로가 예상과 다릅니다. 확인 후 설치해야 합니다.");
                return modern ? "modern" : "legacy";
            }
            if (modern || legacy || File.Exists(SafePath(game, "winhttp.dll")) || File.Exists(SafePath(game, "doorstop_config.ini")) ||
                (Directory.Exists(SafePath(game, "BepInEx/core")) && Directory.EnumerateFileSystemEntries(SafePath(game, "BepInEx/core")).Any()))
                throw new IOException("기존 로더/프록시와 충돌할 수 있어 자동 설치를 중단했습니다.");
            return "none";
        }

        private static IEnumerable<string> PluginFiles(string root)
        {
            var directory = SafePath(root, "BepInEx/plugins");
            if (!Directory.Exists(directory)) yield break;
            var pending = new Stack<string>(); pending.Push(directory);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var path in Directory.EnumerateFiles(current, "*.dll"))
                {
                    SafePath(root, path.Substring(root.Length + 1));
                    yield return path;
                }
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    SafePath(root, child.Substring(root.Length + 1));
                    pending.Push(child);
                }
            }
        }
        private static bool IsMakeshift(string path)
        {
            if (System.IO.Path.GetFileName(path).Equals("Snaitf.Dinkum.Makeshift.dll", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                using (var module = ModuleDefinition.ReadModule(path))
                    return module.Types.SelectMany(t => t.CustomAttributes).Any(a => a.AttributeType.FullName == "BepInEx.BepInPlugin" &&
                        a.ConstructorArguments.Count > 0 && (string)a.ConstructorArguments[0].Value == "Snaitf.Dinkum.Makeshift");
            }
            catch (BadImageFormatException) { return false; } // Native dependency, not a managed plugin.
        }
        internal static Dictionary<string, byte[]> LoaderFiles(byte[] archive)
        {
            if (Hash(archive) != LoaderHash) throw new IOException("공식 로더 다운로드의 SHA-256이 다릅니다. 설치하지 않았습니다.");
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith("/")) continue;
                    string name = entry.FullName.Replace('\\', '/');
                    if (!(name.StartsWith("BepInEx/core/", StringComparison.Ordinal) || name == "winhttp.dll" || name == "doorstop_config.ini" || name == ".doorstop_version")) continue;
                    if (entry.Length > 10000000) throw new IOException("로더 파일 크기가 예상과 다릅니다.");
                    using (var stream = entry.Open())
                    using (var memory = new MemoryStream()) { stream.CopyTo(memory); result.Add(name, memory.ToArray()); }
                }
            return result;
        }
        private static byte[] DownloadLoader()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(LoaderUrl);
            request.Timeout = 30000; request.ReadWriteTimeout = 30000;
            request.AllowAutoRedirect = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[32768]; int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > 10000000) throw new IOException("로더 다운로드 크기 제한을 초과했습니다.");
                }
                return memory.ToArray();
            }
        }
        internal static string Install(string game) => InstallCore(game, null, null, -1);
        internal static string InstallSelection(string game, string[] selected, Dictionary<string, string> shortcuts = null) => InstallCore(game, null, null, -1, selected, shortcuts);

        // The fixture hooks are internal test-only entry points; the CLI cannot bypass validation.
        internal static string InstallCore(string game, Action<string> testValidator, byte[] testLoader, int failAfter, string[] selected = null, Dictionary<string, string> shortcuts = null)
        {
            game = System.IO.Path.GetFullPath(game).TrimEnd('\\', '/');
            EnsureGameClosed();
            if (testValidator == null) ValidateGame(game); else testValidator(game);
            var choices = Catalog.Select(selected ?? Catalog.Mods.Select(m => m.Key).ToArray());
            if (choices.Length == 0) throw new IOException("설치할 모드를 하나 이상 선택하세요.");
            RejectOldMorePlayers(game);
            if (shortcuts != null) ShortcutSettings.Validate(shortcuts);
            string variant = LoaderVariant(game);
            ValidateRecoveryDependency(game, choices, variant == "none" ? "modern" : variant);
            var plan = variant == "none" ? LoaderFiles(testLoader ?? DownloadLoader()) : new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (variant == "none") variant = "modern";
            foreach (var file in PluginFiles(game))
                if (choices.Any(m => Matches(file, m))) plan[file.Substring(game.Length + 1).Replace('\\', '/')] = null;
            foreach (var mod in choices) plan[mod.Target] = Resource(mod.Key + "." + variant + ".dll");
            if (shortcuts != null) foreach (var mod in choices)
            {
                var entries = ShortcutSettings.Items.Where(s => s.Mod == mod.Key && shortcuts.ContainsKey(s.Key)).ToArray();
                if (entries.Length == 0) continue;
                string configPath = SafePath(game, mod.Config);
                string content = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
                foreach (var entry in entries) content = ShortcutSettings.SetValue(content, "Keybinds", entry.Key, shortcuts[entry.Key]);
                plan[mod.Config] = new System.Text.UTF8Encoding(false).GetBytes(content);
            }
            plan[NoticeFolder + "/NOTICE.txt"] = Resource("NOTICE.txt");
            plan[NoticeFolder + "/Mono.Cecil-LICENSE.txt"] = Resource("Mono.Cecil-LICENSE.txt");
            // An effective DLL/loader replacement can remove variant registration.
            // Same-byte reinstallation is harmless; incompatible updates require
            // conversion before even creating a baseline or operation backup.
            var enhancement = Catalog.Mods.First(m => m.Key == "enhancement");
            bool changesEnhancementRuntime = plan.Any(pair =>
                FileHash(SafePath(game, pair.Key)) != (pair.Value == null ? null : Hash(pair.Value)) &&
                ((File.Exists(SafePath(game, pair.Key)) && pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && Matches(SafePath(game, pair.Key), enhancement)) ||
                pair.Key.Equals(enhancement.Target, StringComparison.OrdinalIgnoreCase) ||
                pair.Key.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)));
            if (changesEnhancementRuntime && !VerifiedEnhancementUpgrade(game, plan, variant)) RequireEnhancementConversion(game);
            EnsureBaseline(game, plan);
            return ExecutePlan(game, plan, "선택 모드 적용 완료 (" + variant + ")", failAfter);
        }

        internal static void ValidateRecoveryDependency(string game, Mod[] choices, string variant)
        {
            if (!choices.Any(m => m.Key == "recovery") || choices.Any(m => m.Key == "enhancement")) return;
            var enhancement = Catalog.Mods.Single(m => m.Key == "enhancement");
            var found = PluginFiles(game).Where(f => Matches(f, enhancement)).ToArray();
            if (found.Length != 1 || FileHash(found[0]) != Hash(Resource("enhancement." + variant + ".dll")))
                throw new IOException("강화 장비 찾기는 이 팩의 장비 강화 1.2.0이 필요합니다. [장비 강화]도 함께 선택하거나 기존 설치를 확인하세요. 기존 파일은 변경하지 않았습니다.");
        }

        internal static bool VerifiedEnhancementUpgrade(string game, Dictionary<string, byte[]> plan, string variant)
        {
            var mod = Catalog.Mods.Single(m => m.Key == "enhancement");
            if (!plan.TryGetValue(mod.Target, out var replacement) || replacement == null || Hash(replacement) != Hash(Resource("enhancement." + variant + ".dll"))) return false;
            var found = PluginFiles(game).Where(p => Matches(p, mod)).ToArray();
            if (found.Length != 1) return false;
            string digest = FileHash(found[0]);
            var known = variant == "modern" ? new[] { "89BEC5379DE40A2859F4D17C38745869102066DE561B4572D3D26027CC30C819", "CAF8AE39946C4C95D544102B8A3C4B500F01BCAB2FF0A6E66338FB1E78D685E1", "24C5647BEAE1BF50B999D5B60378FDE1DB1F773B24852F977400E4BF978EBA82" } : new[] { "B68A007CA83C20F8A75600E74EA0F9936B56E9D074888652E1339AF68B02B8FB", "FDB80E2A24A272028AD73408B737A2BBE2FBDF0F3DEAE4B1DC90F3CC7D8E23D0" };
            if (!known.Contains(digest)) return false;
            if (plan.Any(p => (p.Key.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)) && FileHash(SafePath(game, p.Key)) != (p.Value == null ? null : Hash(p.Value)))) return false;
            string journals = SafePath(game, "BepInEx/config/LocalEquipmentEnhancement");
            if (Directory.Exists(journals) && Directory.EnumerateFiles(journals, "pending-slot*.txt").Any()) throw new IOException("미완료 강화 요청이 있어 업데이트를 중단했습니다. 요청 복구 확인이 먼저 필요합니다.");
            // This does NOT authorize removal or rollback. The exact replacement
            // retains existing IDs or blocks old saves until explicit migration.
            return true;
        }

        private static string ExecutePlan(string game, Dictionary<string, byte[]> plan, string label, int failAfter = -1)
        {
            foreach (string file in plan.Keys.ToList())
                if (FileHash(SafePath(game, file)) == (plan[file] == null ? null : Hash(plan[file]))) plan.Remove(file);
            if (plan.Count == 0) return "선택한 모드가 이미 적용되어 있습니다. 변경할 파일이 없습니다.\n" + game;

            string backupRelative = BackupFolder + "/op-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string backup = SafePath(game, backupRelative);
            var manifest = new Backup { GamePath = game, Status = "Preparing" };
            foreach (var pair in plan)
            {
                string target = SafePath(game, pair.Key);
                manifest.Files.Add(new Change { Path = pair.Key, BeforeHash = FileHash(target), AfterHash = pair.Value == null ? null : Hash(pair.Value) });
            }
            Directory.CreateDirectory(backup);
            foreach (var change in manifest.Files)
                if (change.BeforeHash != null)
                {
                    string copy = SafePath(backup, "original/" + change.Path);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(copy));
                    File.Copy(SafePath(game, change.Path), copy, false);
                    if (FileHash(copy) != change.BeforeHash) throw new IOException("백업 검증 실패: " + change.Path);
                }
            SaveManifest(backup, manifest);
            try
            {
                EnsureGameClosed();
                int written = 0;
                foreach (var change in manifest.Files)
                {
                    string path = SafePath(game, change.Path);
                    if (FileHash(path) != change.BeforeHash) throw new IOException("설치 도중 다른 프로그램이 파일을 변경했습니다: " + change.Path);
                    // Persist intent before mutation for crash recovery; restore accepts before/after hashes.
                    change.Applied = true; SaveManifest(backup, manifest);
                    if (plan[change.Path] == null) File.Delete(path);
                    else AtomicWrite(path, plan[change.Path]);
                    if (FileHash(path) != change.AfterHash) throw new IOException("설치 검증 실패: " + change.Path);
                    if (++written == failAfter) throw new IOException("TEST: forced failure");
                }
                manifest.Status = "Installed"; SaveManifest(backup, manifest);
            }
            catch (Exception installError)
            {
                try { RestoreCore(backup, manifest); }
                catch (Exception restoreError) { throw new IOException("설치 및 자동 복원 실패. 백업 보존: " + backup + "\n" + restoreError.Message, installError); }
                throw new IOException("설치를 중단하고 변경된 파일을 자동 복원했습니다.\n" + installError.Message, installError);
            }
            return label + "\n\n게임: " + game + "\n작업 백업: " + backup + "\n\n기존 인원 설정/다른 모드/세이브는 변경하지 않았습니다.\n실험적 호환 수정판이며 온라인 20명 실전 검증은 미완료입니다.";
        }
        private static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), "mp-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void SaveManifest(string backup, Backup manifest)
        {
            using (var memory = new MemoryStream())
            {
                new XmlSerializer(typeof(Backup)).Serialize(memory, manifest);
                AtomicWrite(SafePath(backup, "manifest.xml"), memory.ToArray());
            }
        }
        internal static string Restore(string backup)
        {
            backup = System.IO.Path.GetFullPath(backup).TrimEnd('\\', '/');
            var parent = Directory.GetParent(backup);
            if (parent == null || parent.Name != BackupFolder) throw new IOException("DinkumModpack-Backups 아래의 작업 백업 폴더를 지정하세요.");
            Backup manifest;
            using (var reader = XmlReader.Create(SafePath(backup, "manifest.xml"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                manifest = (Backup)new XmlSerializer(typeof(Backup)).Deserialize(reader);
            if (!System.IO.Path.GetFullPath(manifest.GamePath).TrimEnd('\\').Equals(parent.Parent.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("백업의 게임 경로가 일치하지 않습니다.");
            RequireEnhancementConversion(manifest.GamePath);
            RestoreCore(backup, manifest);
            return "Makeshift 설치 변경을 복원했습니다. 세이브는 변경하지 않았습니다.\n백업은 남겨두었습니다.\n" + backup;
        }
        internal static string LatestBackup(string game)
        {
            string root = SafePath(game, BackupFolder);
            if (Directory.Exists(root))
                foreach (string directory in Directory.GetDirectories(root, "op-*").OrderByDescending(p => p))
                {
                    string file = SafePath(directory, "manifest.xml");
                    if (!File.Exists(file)) continue;
                    using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    {
                        var backup = (Backup)new XmlSerializer(typeof(Backup)).Deserialize(reader);
                        if (backup.Status != "Restored") return directory;
                    }
                }
            throw new IOException("복원할 Makeshift 설치 백업이 없습니다.");
        }
        private static void RestoreCore(string backup, Backup manifest)
        {
            EnsureGameClosed();
            if (manifest.Status == "Restored") return;
            // Validate the complete restore before modifying any target.
            foreach (var change in manifest.Files.Where(c => c.Applied))
            {
                string relative = change.Path.Replace('\\', '/');
                if (!(relative.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(NoticeFolder + "/", StringComparison.OrdinalIgnoreCase) || Catalog.Mods.Any(m => relative.Equals(m.Config, StringComparison.OrdinalIgnoreCase)) || relative == "winhttp.dll" || relative == "doorstop_config.ini" || relative == ".doorstop_version"))
                    throw new IOException("백업에 설치 대상이 아닌 경로가 있습니다.");
                string current = FileHash(SafePath(manifest.GamePath, change.Path));
                if (current != change.AfterHash && current != change.BeforeHash) throw new IOException("설치 이후 변경된 파일은 덮어쓰지 않습니다: " + change.Path);
                if (change.BeforeHash != null && FileHash(SafePath(backup, "original/" + change.Path)) != change.BeforeHash)
                    throw new IOException("백업 파일 검증 실패: " + change.Path);
            }
            foreach (var change in manifest.Files.Where(c => c.Applied).Reverse())
            {
                string path = SafePath(manifest.GamePath, change.Path);
                if (FileHash(path) == change.BeforeHash) continue;
                if (change.BeforeHash == null) File.Delete(path);
                else AtomicWrite(path, File.ReadAllBytes(SafePath(backup, "original/" + change.Path)));
            }
            manifest.Status = "Restored"; SaveManifest(backup, manifest);
        }
        internal static IEnumerable<string> FindGames()
        {
            var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                    using (var registry = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = registry.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                    {
                        string path = (key?.GetValue("SteamPath") ?? key?.GetValue("InstallPath")) as string;
                        if (!string.IsNullOrEmpty(path)) steamRoots.Add(path);
                    }
            steamRoots.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
            foreach (var steam in steamRoots)
            {
                string vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            return libraries.Select(p => System.IO.Path.GetFullPath(System.IO.Path.Combine(p, "steamapps", "common", "Dinkum")))
                .Where(p => File.Exists(System.IO.Path.Combine(p, "Dinkum.exe"))).Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
