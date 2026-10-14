using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Mirror;
using UnityEngine;

namespace LocalDinkumMods
{
    internal static class EnhancementConversion
    {
        internal static bool Completed;
        internal static string Status = "";
        internal static string GuardDirectory => Path.Combine(BepInEx.Paths.ConfigPath, "LocalEnhancementState");
        internal static void RequireConversion()
        {
            EnsurePlainDirectory(GuardDirectory);
            EnsurePlainDirectory(EnhancementTransactions.DirectoryPath);
            string flag = Path.Combine(GuardDirectory, "restore-required.flag");
            string marker = Path.Combine(EnhancementTransactions.DirectoryPath, "installed-save-guard.txt");
            if (File.Exists(flag)) RejectLinks(flag);
            if (File.Exists(marker)) RejectLinks(marker);
            EnhancementTransactions.AtomicWrite(flag, Encoding.UTF8.GetBytes("Enhancement registry active. Convert all local saves before removal."));
            EnhancementTransactions.AtomicWrite(marker, Encoding.UTF8.GetBytes("CONVERSION_REQUIRED\n" + (EnhancementRegistry.Fingerprint ?? "registry-initializing")));
        }
        // Explicit main-menu operation only. No save is rewritten at plugin startup.
        // Original files are kept OUTSIDE Slot directories so they cannot be loaded
        // accidentally after removing the registry.
        // Main installs a prefix on character load entry points calling this hook.
        // The flag deliberately stays set until the process is restarted.
        internal static bool CharacterLoadStarted;
        internal static void MarkCharacterLoadStarted() { CharacterLoadStarted = true; }
        internal static bool CanConvert() => !CharacterLoadStarted && !Completed &&
            !NetworkClient.active && !NetworkServer.active && !SaveAndLoad.gameHasCompletelyLoaded &&
            !(SaveLoad.saveOrLoad && SaveLoad.saveOrLoad.isSaving);

        internal static void ConvertAll()
        {
            if (!CanConvert())
                throw new InvalidOperationException("게임을 저장하고 종료한 뒤 다시 실행하여, 캐릭터를 불러오기 전 첫 화면에서 변환하세요.");
            if (string.IsNullOrEmpty(EnhancementRegistry.Fingerprint)) throw new InvalidOperationException("Equipment registry has not initialized.");
            string root = Path.GetFullPath(Application.persistentDataPath).TrimEnd(Path.DirectorySeparatorChar);
            RejectLinks(root);
            string backup = Path.Combine(EnhancementTransactions.DirectoryPath, "conversion-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            RejectLinks(EnhancementTransactions.DirectoryPath);
            Directory.CreateDirectory(backup); RejectLinks(backup);
            RequireConversion();
            string guard = Path.Combine(EnhancementTransactions.DirectoryPath, "installed-save-guard.txt");
            var files = EnumerateSaveFiles(root);
            var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var verifiedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var verificationLocks = new List<FileStream>();
            int changed = 0;
            try
            {
                // Keep each source read-locked while snapshotting AND parsing it.
                // Backups are those exact snapshot bytes, never a later reread.
                foreach (string path in files)
                {
                    RejectLinks(path);
                    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        byte[] original = ReadBytes(held);
                        originals.Add(path, EnhancementRegistry.Hash(original));
                        if (IsLegacy(path))
                        {
                            object legacy = ReadLegacySnapshot(original);
                            changed += ConvertObject(legacy, new HashSet<object>());
                            using (var output = new MemoryStream()) { new BinaryFormatter().Serialize(output, legacy); replacements[path] = output.ToArray(); }
                        }
                        else
                        {
                            var file = new ES3File(original, new ES3Settings(path));
                            foreach (string key in file.GetKeys())
                            {
                                if (!InventoryKey(key)) continue;
                                object value = file.Load(key);
                                int n = ConvertObject(value, new HashSet<object>());
                                if (n != 0) { SaveTyped(file, key, value); changed += n; }
                            }
                            if (file.KeyExists("LocalItemLocksV1")) file.Save("LocalItemLocksV1", ConvertLocks(file.Load<string>("LocalItemLocksV1")));
                            replacements[path] = EnhancementTransactions.DiskBytes(file);
                        }
                        BackupSnapshot(path, root, backup, original);
                    }
                }
                RequireExactSet(root, files);
                foreach (string path in files) RequireSourceHash(path, originals[path]);
                foreach (var pair in replacements)
                {
                    // Sibling => same volume. No .es3, .dat or .bak filename:
                    // an interrupted staging file can never enter the save scan.
                    string temp = Path.Combine(Path.GetDirectoryName(pair.Key), "ld-enh-stage-" + Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        RejectLinks(Path.GetDirectoryName(pair.Key));
                        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(pair.Value, 0, pair.Value.Length); stream.Flush(true); }
                        RequireExactSet(root, files);
                        RequireSourceHash(pair.Key, originals[pair.Key]);
                        // ReplaceFile's displaced original is preserved on the SAME
                        // volume too: a last-moment atomic cloud replacement must
                        // never be silently discarded between compare and replace.
                        string displaced = Path.Combine(Path.GetDirectoryName(pair.Key), "ld-enh-recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
                        File.Replace(temp, pair.Key, displaced);
                        RejectLinks(displaced);
                        string displacedHash = EnhancementRegistry.Hash(File.ReadAllBytes(displaced));
                        if (displacedHash != originals[pair.Key])
                            throw new IOException("교체 직전 저장이 변경되었습니다. 변경된 원본은 보존했습니다: " + displaced);
                        // Only the known byte-identical displaced copy is removed.
                        File.Delete(displaced);
                        ES3File.cachedFiles.Remove(pair.Key);
                    }
                    finally { if (File.Exists(temp)) { RejectLinks(temp); File.Delete(temp); } }
                }
                RequireExactSet(root, files);
                foreach (string path in files)
                {
                    RejectLinks(path);
                    var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    verificationLocks.Add(held); // Retain through certificate publication.
                    byte[] snapshot = ReadBytes(held);
                    string digest = EnhancementRegistry.Hash(snapshot);
                    if (digest != EnhancementRegistry.Hash(replacements[path])) throw new IOException("변환 후 저장 내용이 외부에서 변경되었습니다: " + path);
                    if (IsLegacy(path))
                    {
                        if (ConvertObject(ReadLegacySnapshot(snapshot), new HashSet<object>()) != 0) throw new InvalidDataException("Legacy conversion verification failed: " + path);
                    }
                    else
                    {
                        var file = new ES3File(snapshot, new ES3Settings(path));
                        foreach (string key in file.GetKeys())
                            if (InventoryKey(key) && ConvertObject(file.Load(key), new HashSet<object>()) != 0) throw new InvalidDataException("Conversion verification failed: " + path);
                        if (file.KeyExists("LocalItemLocksV1"))
                        {
                            string locks = file.Load<string>("LocalItemLocksV1");
                            if (ConvertLocks(locks) != locks) throw new InvalidDataException("Lock conversion verification failed: " + path);
                        }
                    }
                    verifiedHashes.Add(path, digest);
                }
                RequireExactSet(root, files);
                var certificate = new StringBuilder("CONVERTED\n" + EnhancementRegistry.Fingerprint + "\n" + root + "\n");
                foreach (string path in files) certificate.Append(verifiedHashes[path]).Append('\t').Append(path).Append('\n');
                // Publish only hashes of the exact byte snapshots verified above;
                // never hash fresh, potentially replaced content for certification.
                EnhancementTransactions.AtomicWrite(Path.Combine(GuardDirectory, "conversion-certificate.txt"), Encoding.UTF8.GetBytes(certificate.ToString()));
                RequireExactSet(root, files);
                EnhancementTransactions.AtomicWrite(guard, Encoding.UTF8.GetBytes(certificate.ToString()));
                RequireExactSet(root, files);
                string flag = Path.Combine(GuardDirectory, "restore-required.flag");
                if (File.Exists(flag)) { RejectLinks(flag); File.Move(flag, Path.Combine(backup, "restore-required.flag")); }
                Completed = true;
                Status = changed + "개 강화 장비/표시를 일반 장비로 변환했습니다. 게임을 종료한 뒤 설치기에서 복원하세요. 원본 백업: " + backup;
            }
            catch
            {
                // Also invalidate a certificate if a late set-change/flag step
                // fails. Already converted files remain usable; originals remain
                // in the backup. Never auto-overwrite concurrent cloud changes.
                RequireConversion();
                Status = "변환 중단. 제거 가드는 유지됩니다. 원본 백업: " + backup;
                throw;
            }
            finally { foreach (var held in verificationLocks) held.Dispose(); }
        }
        private static void SaveTyped(ES3File file, string key, object value) => typeof(ES3File).GetMethods().Single(m => m.Name == "Save" && m.IsGenericMethodDefinition).MakeGenericMethod(value.GetType()).Invoke(file, new[] { (object)key, value });
        private static bool IsLegacy(string path) => Path.GetFileName(path).IndexOf(".es3", StringComparison.OrdinalIgnoreCase) < 0;
        private static byte[] ReadBytes(FileStream stream)
        { stream.Position = 0; using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); } }
        private static void RequireSourceHash(string path, string expected)
        {
            RejectLinks(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (EnhancementRegistry.Hash(ReadBytes(stream)) != expected) throw new IOException("준비 이후 저장 파일이 변경되었습니다: " + path);
        }
        private static void RejectLinks(string path)
        {
            for (string cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("링크/정션 저장 경로는 변환할 수 없습니다: " + cursor);
        }
        private static void EnsurePlainDirectory(string path)
        {
            var missing = new Stack<string>(); string cursor = Path.GetFullPath(path);
            while (!Directory.Exists(cursor))
            {
                if (File.Exists(cursor)) throw new IOException("디렉터리 대신 파일이 존재합니다: " + cursor);
                missing.Push(cursor); cursor = Path.GetDirectoryName(cursor);
                if (cursor == null) throw new IOException("디렉터리 루트를 확인할 수 없습니다.");
            }
            RejectLinks(cursor);
            while (missing.Count != 0) { cursor = missing.Pop(); Directory.CreateDirectory(cursor); RejectLinks(cursor); }
        }
        private static string[] EnumerateSaveFiles(string root)
        {
            RejectLinks(root);
            var found = new List<string>();
            foreach (string slot in Directory.EnumerateDirectories(root))
            {
                if (!Regex.IsMatch(Path.GetFileName(slot), "^Slot[0-9]+$")) continue;
                var pending = new Stack<string>(); pending.Push(slot);
                while (pending.Count != 0)
                {
                    string directory = pending.Pop(); RejectLinks(directory);
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        RejectLinks(file);
                        if (IsSave(file)) found.Add(Path.GetFullPath(file));
                    }
                    foreach (string child in Directory.EnumerateDirectories(directory)) { RejectLinks(child); pending.Push(child); }
                }
            }
            return found.OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }
        private static void RequireExactSet(string root, string[] expected)
        {
            if (!EnumerateSaveFiles(root).SequenceEqual(expected, StringComparer.Ordinal))
                throw new IOException("변환 도중 저장 파일 집합이 변경되었습니다. 다시 시도하세요.");
        }
        private static void BackupSnapshot(string path, string root, string backup, byte[] bytes)
        {
            string relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string copy = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(copy)); RejectLinks(Path.GetDirectoryName(copy));
            using (var stream = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (EnhancementRegistry.Hash(File.ReadAllBytes(copy)) != EnhancementRegistry.Hash(bytes)) throw new IOException("변환 원본 백업 검증 실패: " + copy);
        }
        internal static bool IsSave(string path)
        {
            string name = Path.GetFileName(path);
            return name.IndexOf(".es3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Regex.IsMatch(name, @"^(playerInfo|npc|drops)\.(dat|bak)$", RegexOptions.IgnoreCase) ||
                (string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Chests", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, @"^chest.*\.(dat|bak)$", RegexOptions.IgnoreCase));
        }
        private sealed class LegacyBinder : SerializationBinder
        {
            private static readonly Dictionary<string, Type> allowed = new[] { typeof(PlayerInv), typeof(ChestSave), typeof(DropSaves), typeof(DropToSave), typeof(NPCInventory), typeof(NPCStatus), typeof(Inventory).Assembly.GetType("NPCsave", true), typeof(int), typeof(bool), typeof(float), typeof(long), typeof(ulong), typeof(string) }.ToDictionary(t => t.FullName, t => t);
            public override Type BindToType(string assemblyName, string typeName)
            {
                bool array = typeName.EndsWith("[]", StringComparison.Ordinal);
                string name = array ? typeName.Substring(0, typeName.Length - 2) : typeName;
                if (!allowed.TryGetValue(name, out var type)) throw new SerializationException("Unexpected legacy save type: " + typeName);
                return array ? type.MakeArrayType() : type;
            }
        }
        private static object ReadLegacy(string path)
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 16 * 1024 * 1024) throw new InvalidDataException("Legacy inventory file size outside limits: " + path);
            return ReadLegacySnapshot(File.ReadAllBytes(path));
        }
        private static object ReadLegacySnapshot(byte[] bytes)
        {
            if (bytes.Length <= 0 || bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Legacy inventory snapshot size outside limits");
            using (var stream = new MemoryStream(bytes, false))
            {
                object value = new BinaryFormatter { Binder = new LegacyBinder() }.Deserialize(stream);
                if (!(value is PlayerInv) && !(value is ChestSave) && !(value is DropSaves) && value.GetType().Name != "NPCsave") throw new SerializationException("Unexpected legacy save root");
                return value;
            }
        }
        private static bool InventoryKey(string key) => key == "playerInfo" || key.StartsWith("stash_", StringComparison.Ordinal) || key == "chests" || key == "chestInfo" || key == "npc" || key == "drops" || key == "signs" || key == "buried";
        private static int ConvertObject(object value, HashSet<object> seen)
        {
            if (value == null || value is string || value.GetType().IsPrimitive || !seen.Add(value)) return 0;
            int count = 0;
            if (value is PlayerInv p)
            {
                count += ConvertPairs(p.itemsInInvSlots, p.stacksInSlots);
                if (p.catalogue != null && p.catalogue.Length > EnhancementRegistry.BaseCount) { Array.Resize(ref p.catalogue, EnhancementRegistry.BaseCount); count++; }
                return count;
            }
            if (value is ChestSave c) return ConvertPairs(c.itemId, c.itemStack);
            if (value is NPCInventory npc) return ConvertPairs(npc.inventorySlots, npc.inventoryStacks);
            if (value is DropToSave d) { if (EnhancementRegistry.Entries.TryGetValue(d.itemId, out var e)) { d.stackId = BaseStack(d.itemId, d.stackId); d.itemId = e.BaseId; return 1; } return 0; }
            if (value is BuriedItem buried) { if (EnhancementRegistry.Entries.TryGetValue(buried.itemId, out var e)) { buried.stackedAmount = BaseStack(buried.itemId, buried.stackedAmount); buried.itemId = e.BaseId; return 1; } return 0; }
            if (value is SignDetails sign)
            {
                // Only exact native rack/display tokens are interpreted. Free text
                // and ItemOnTop tile IDs are never treated as inventory IDs.
                if (sign.signSays == null || !Regex.IsMatch(sign.signSays, @"^<-?\d+> <-?\d+> <-?\d+>$")) return 0;
                sign.signSays = Regex.Replace(sign.signSays, @"<(-?\d+)>", m => { if (int.TryParse(m.Groups[1].Value, out int id) && EnhancementRegistry.Entries.TryGetValue(id, out var e)) { count++; return "<" + e.BaseId + ">"; } return m.Value; }); return count;
            }
            if (value is IEnumerable enumerable) { foreach (var item in enumerable) count += ConvertObject(item, seen); return count; }
            // Only known plain game save classes are traversed; never Unity objects.
            string type = value.GetType().Name;
            if (type == "AllChestSaves" || type == "NPCsave" || type == "DropSaves" || type == "SignSave" || type == "BuriedSave")
                foreach (var field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) count += ConvertObject(field.GetValue(value), seen);
            return count;
        }
        private static int BaseStack(int id, int stack)
        {
            var e = EnhancementRegistry.Describe(id);
            return EnhancementRulesForConversion(stack, Inventory.Instance.allItems[id].fuelMax, Inventory.Instance.allItems[e.BaseId].fuelMax);
        }
        private static int EnhancementRulesForConversion(int stack, int oldMax, int newMax)
        { if (stack < 0 || stack > oldMax) throw new InvalidDataException("Equipment condition outside valid range; conversion refused."); return LocalDinkum.Enhancement.EnhancementRules.PreserveCondition(stack, oldMax, newMax); }
        private static int ConvertPairs(int[] ids, int[] stacks)
        {
            if (ids == null && stacks == null) return 0;
            if (ids == null || stacks == null || ids.Length != stacks.Length) throw new InvalidDataException("Save inventory array mismatch");
            int count = 0;
            for (int i = 0; i < ids.Length; i++) if (EnhancementRegistry.Entries.TryGetValue(ids[i], out var e)) { stacks[i] = BaseStack(ids[i], stacks[i]); ids[i] = e.BaseId; count++; }
            return count;
        }
        private static string ConvertLocks(string value)
        {
            return string.Join("|", (value ?? "").Split('|').Select(record =>
            {
                var fields = record.Split(':');
                if (fields.Length == 4 && int.TryParse(fields[1], out int id) && int.TryParse(fields[2], out int stack) && EnhancementRegistry.Entries.TryGetValue(id, out var e)) { fields[1] = e.BaseId.ToString(); fields[2] = BaseStack(id, stack).ToString(); return string.Join(":", fields); }
                return record;
            }));
        }
        internal static void Test(Action<bool, string> check, string directory)
        {
            int id = EnhancementRegistry.Entries.First().Key;
            int baseId = EnhancementRegistry.Describe(id).BaseId;
            int fuel = Inventory.Instance.allItems[id].fuelMax;
            var chest = new ChestSave { itemId = new[] { id, id }, itemStack = new[] { fuel, fuel / 2 } };
            check(ConvertObject(chest, new HashSet<object>()) == 2 && chest.itemId.All(x => x == baseId) && chest.itemStack[0] == Inventory.Instance.allItems[baseId].fuelMax, "chest conversion");
            var buried = new BuriedSave { buriedByPlayer = new[] { new BuriedItem(id, fuel, 1, 2) } };
            check(ConvertObject(buried, new HashSet<object>()) == 1 && buried.buriedByPlayer[0].itemId == baseId, "buried equipment conversion");
            var drop = new DropToSave { itemId = id, stackId = fuel };
            check(ConvertObject(drop, new HashSet<object>()) == 1 && drop.itemId == baseId, "drop conversion");
            var sign = new SignDetails { signSays = "<" + id + "> <-1> <-1>" };
            check(ConvertObject(sign, new HashSet<object>()) == 1 && sign.signSays == "<" + baseId + "> <-1> <-1>", "tool rack sign conversion");
            var tile = new ItemOnTop();
            tile.itemId = id;
            check(ConvertObject(tile, new HashSet<object>()) == 0 && tile.itemId == id, "tile-object IDs never rewritten");
            var legacy = Path.Combine(directory, "playerInfo.dat");
            using (var output = File.Create(legacy)) new BinaryFormatter().Serialize(output, new PlayerInv { itemsInInvSlots = new[] { id }, stacksInSlots = new[] { fuel } });
            var read = (PlayerInv)ReadLegacy(legacy);
            check(ConvertObject(read, new HashSet<object>()) == 1 && read.itemsInInvSlots[0] == baseId, "legacy player conversion");
            check(IsSave("x/Slot0/Player.es3.bak") && IsSave("x/Slot0/Chests/chest1+2.dat") && !IsSave("x/Slot0/MapSave.dat"), "conversion scope");
            string auditRoot = Path.Combine(directory, "conversion-audit");
            string auditSlot = Path.Combine(auditRoot, "Slot0"); EnsurePlainDirectory(auditSlot);
            string auditSave = Path.Combine(auditSlot, "Player.es3"); File.WriteAllBytes(auditSave, new byte[] { 1, 2, 3 });
            string snapshotPath = Path.Combine(directory, "snapshot-read.es3");
            var snapshotFile = new ES3File(new ES3Settings(snapshotPath), false);
            snapshotFile.Save("snapshot-test", 42);
            File.WriteAllBytes(snapshotPath, EnhancementTransactions.DiskBytes(snapshotFile));
            using (var held = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var parsed = new ES3File(ReadBytes(held), new ES3Settings(snapshotPath));
                check(parsed.Load<int>("snapshot-test") == 42, "encrypted ES3 snapshot parsed while source read-locked");
            }
            string digest = EnhancementRegistry.Hash(new byte[] { 1, 2, 3 });
            var exactSet = EnumerateSaveFiles(auditRoot);
            RequireSourceHash(auditSave, digest); RequireExactSet(auditRoot, exactSet);
            check(exactSet.Length == 1, "safe save enumeration and unchanged hash");
            File.WriteAllBytes(Path.Combine(auditSlot, "ld-enh-stage-test.tmp"), new byte[] { 4 });
            RequireExactSet(auditRoot, exactSet); check(true, "interrupted staging excluded from save set");
            File.WriteAllBytes(auditSave, new byte[] { 9 });
            bool rejected = false; try { RequireSourceHash(auditSave, digest); } catch (IOException) { rejected = true; }
            check(rejected, "concurrent source modification rejected");
            File.WriteAllBytes(Path.Combine(auditSlot, "Container.es3"), new byte[] { 8 });
            rejected = false; try { RequireExactSet(auditRoot, exactSet); } catch (IOException) { rejected = true; }
            check(rejected, "concurrent new save rejected");
        }
    }
}
