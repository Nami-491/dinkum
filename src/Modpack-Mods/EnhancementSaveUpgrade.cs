using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace LocalDinkumMods
{
    // Explicit, offline-at-main-menu migration. No startup or character-load writes.
    internal static class EnhancementSaveUpgrade
    {
        internal enum Mode { Unknown, OldEnhanced, NativeOnly, Current }
        private static readonly Dictionary<string, Mode> modes = new Dictionary<string, Mode>();
        private static bool open, confirm, finished;
        private static int page;
        private static string status = "각 캐릭터의 저장 종류를 직접 선택하세요. 모르면 변환하지 마세요.";
        private static string Root => Path.GetFullPath(Application.persistentDataPath).TrimEnd(Path.DirectorySeparatorChar);
        private static string Flag => Path.Combine(Root, "LocalEnhancementMigration.pending");
        internal static bool Incomplete => File.Exists(Flag) || Directory.Exists(Flag);
        private static bool Safe => !finished && EnhancementConversion.CanConvert() && !string.IsNullOrEmpty(EnhancementRegistry.Fingerprint);
        private static string Label(Mode m) => m == Mode.OldEnhanced ? "구 강화 1.0 / 업데이트 후 저장 없음" : m == Mode.NativeOnly ? "강화 모드 사용 안 한 저장" : m == Mode.Current ? "이미 1.1/1.2로 변환한 저장" : "저장 종류 선택 필요";
        internal static bool Draw()
        {
            if (!Inventory.Instance || (!EnhancementConversion.CanConvert() && !finished)) return false;
            var oldFont = GUI.skin.font;
            try
            {
                if (Inventory.Instance.InvDescriptionText && Inventory.Instance.InvDescriptionText.font && Inventory.Instance.InvDescriptionText.font.sourceFontFile) GUI.skin.font = Inventory.Instance.InvDescriptionText.font.sourceFontFile;
                if (GUI.Button(new Rect(20, 20, 285, 42), "참가자 세이브 호환성 설정")) { open = !open; confirm = false; }
                if (!open && !Incomplete) return false;
                var rect = new Rect(Math.Max(10, Screen.width / 2 - 360), 75, 720, Math.Min(560, Screen.height - 85));
                GUI.Box(rect, "강화 1.2 / +20 — 캐릭터를 고르기 전에 한 번 설정");
                GUILayout.BeginArea(new Rect(rect.x + 16, rect.y + 32, rect.width - 32, rect.height - 48));
                GUILayout.Label("본인 PC의 전체 Slot 저장만 처리합니다. 원본은 별도 백업합니다.\n구 강화 사용 후 이번 업데이트에서 정상 저장했다면 자동 변환하지 마세요.\nSteam Cloud 동기화·파일 복사·다른 게임 실행 중에는 진행하지 마세요.");
                if (Incomplete)
                {
                    GUILayout.Label("이전 변환이 중단됐습니다. 먼저 교체 전 원본으로 복구해야 합니다.");
                    if (GUILayout.Button("중단된 변환 복구 (검증된 파일만 원래대로)"))
                        try { Rollback(); status = "원본 복구 완료. 게임을 재시작한 뒤 저장 종류를 다시 선택하세요."; finished = true; }
                        catch (Exception e) { status = e.Message; Debug.LogError(e); }
                }
                else
                {
                    string[] slots;
                    try { slots = Directory.GetDirectories(Root).Where(p => Regex.IsMatch(Path.GetFileName(p), "^Slot[0-9]+$")).OrderBy(p => p, StringComparer.Ordinal).ToArray(); }
                    catch (Exception e) { slots = new string[0]; status = e.Message; }
                    page = Math.Max(0, Math.Min(page, Math.Max(0, (slots.Length - 1) / 4)));
                    foreach (string slot in slots.Skip(page * 4).Take(4))
                    {
                        string key = Path.GetFileName(slot);
                        if (!modes.ContainsKey(key)) modes[key] = Mode.Unknown;
                        GUILayout.BeginHorizontal(); GUILayout.Label(key, GUILayout.Width(85));
                        if (GUILayout.Button(Label(modes[key]))) { modes[key] = (Mode)(((int)modes[key] + 1) % 4); confirm = false; }
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("이전")) { page = Math.Max(0, page - 1); confirm = false; }
                    GUILayout.Label((page + 1) + " 페이지 / " + slots.Length + "개 캐릭터");
                    if (GUILayout.Button("다음")) { page++; confirm = false; }
                    GUILayout.EndHorizontal();
                    GUILayout.Label("구 강화: 기존 장비 번호 +38 변환. 미사용: 번호 유지.\n이미 변환: 번호 유지·검사만. 모든 Slot의 종류를 확인하세요.");
                    if (Safe && GUILayout.Button(confirm ? "확인 — 선택한 종류가 맞습니다. 백업·변환 실행" : "선택 확인 및 변환 준비"))
                    {
                        if (!confirm) confirm = true;
                        else try { Migrate(modes); finished = true; status = "완료. 게임을 종료했다가 다시 실행하세요. 강화 단계·내구도는 유지됩니다."; }
                        catch (Exception e) { status = "중단: " + e.Message; Debug.LogError(e); }
                    }
                }
                GUILayout.Label(status);
                if (GUILayout.Button("닫기")) { open = false; confirm = false; }
                GUILayout.EndArea(); return true;
            }
            finally { GUI.skin.font = oldFont; }
        }
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); }
        private static void Plain(string path)
        {
            for (string p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
                if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("링크 경로는 지원하지 않습니다: " + p);
        }
        private static string[] Files()
        {
            Plain(Root); var files = new List<string>();
            foreach (string slot in Directory.GetDirectories(Root).Where(p => Regex.IsMatch(Path.GetFileName(p), "^Slot[0-9]+$")))
            {
                var pending = new Stack<string>(); pending.Push(slot);
                while (pending.Count != 0)
                {
                    string dir = pending.Pop(); Plain(dir);
                    foreach (string child in Directory.GetDirectories(dir)) { Plain(child); pending.Push(child); }
                    foreach (string file in Directory.GetFiles(dir)) { Plain(file); if (EnhancementConversion.IsSave(file)) files.Add(file); }
                }
            }
            Check(files.Count <= 10000, "저장 파일 수 제한 초과"); return files.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }
        private static void SaveTyped(ES3File file, string key, object value) => typeof(ES3File).GetMethods().Single(m => m.Name == "Save" && m.IsGenericMethodDefinition).MakeGenericMethod(value.GetType()).Invoke(file, new[] { (object)key, value });
        private static string Raw(object value)
        {
            var file = new ES3File(false); SaveTyped(file, "v", value); file.settings.postprocessRawCachedData = false;
            return Convert.ToBase64String(file.LoadRawBytes());
        }
        private static bool ItemKey(string key) => key == "playerInfo" || key.StartsWith("stash_", StringComparison.Ordinal) || new[] { "chests", "chestInfo", "npc", "drops", "signs", "buried" }.Contains(key);
        private static int Map(int id, Mode mode, bool reverse)
        {
            Check(id >= -1, "알 수 없는 음수 아이템 번호");
            if (mode == Mode.OldEnhanced)
            {
                if (reverse) { Check(id < 2684 && (id < 2136 || id >= 2174), "역변환 번호 오류"); return id >= 2174 ? id - 38 : id; }
                Check(id < 2646, "구 강화 범위 밖의 아이템입니다. 종류 선택을 확인하세요."); return id >= 2136 ? id + 38 : id;
            }
            Check(id < (mode == Mode.NativeOnly ? 2174 : 3194), "선택한 저장 종류 범위 밖 아이템입니다."); return id;
        }
        private static int Pairs(int[] ids, int[] stacks, Mode mode, bool reverse)
        {
            if (ids == null && stacks == null) return 0;
            Check(ids != null && stacks != null && ids.Length == stacks.Length, "아이템 배열 손상"); int count = 0;
            for (int n = 0; n < ids.Length; n++)
            {
                int mapped = Map(ids[n], mode, reverse), current = reverse ? ids[n] : mapped;
                if (current >= 2174) Check(stacks[n] >= 0 && stacks[n] <= Inventory.Instance.allItems[current].fuelMax, "강화 장비 내구도 범위 오류");
                if (mapped != ids[n]) count++; ids[n] = mapped;
            }
            return count;
        }
        private static void Worn(Mode mode, bool reverse, params int[] ids)
        {
            // Clothing never belongs to the generated equipment registry.
            int limit = mode == Mode.OldEnhanced && !reverse ? 2136 : 2174;
            foreach (int id in ids) Check(id >= -1 && id < limit, "장비칸에 예상 밖 강화 아이템이 있습니다.");
        }
        private static int Transform(object value, Mode mode, bool reverse, HashSet<object> seen)
        {
            if (value == null || value is string || value.GetType().IsPrimitive || !seen.Add(value)) return 0;
            if (value is PlayerInv p)
            {
                int n = Pairs(p.itemsInInvSlots, p.stacksInSlots, mode, reverse);
                Worn(mode, reverse, p.head, p.face, p.face2, p.body, p.pants, p.shoes);
                if (p.catalogue != null)
                {
                    Check(p.catalogue.Length <= (mode == Mode.OldEnhanced ? reverse ? 2684 : 2646 : mode == Mode.NativeOnly ? 2174 : 3194), "카탈로그 범위 오류");
                    if (mode == Mode.OldEnhanced && p.catalogue.Length > 2136)
                    {
                        int from = reverse ? 2174 : 2136, to = reverse ? 2136 : 2174;
                        Check(p.catalogue.Length >= from, "카탈로그 구조 오류");
                        var next = new bool[p.catalogue.Length + (reverse ? -38 : 38)]; Array.Copy(p.catalogue, next, 2136);
                        Array.Copy(p.catalogue, from, next, to, p.catalogue.Length - from); p.catalogue = next;
                    }
                }
                return n;
            }
            if (value is ChestSave c) return Pairs(c.itemId, c.itemStack, mode, reverse);
            // NPCsave also contains relationship/dialogue records. These contain
            // no item IDs; preserve them verbatim instead of rejecting a valid save.
            if (value is NPCStatus) return 0;
            if (value is NPCInventory npc)
            {
                Worn(mode, reverse, npc.onHeadId, npc.onFaceId, npc.shirtId, npc.pantsId, npc.shoesId);
                return Pairs(npc.inventorySlots, npc.inventoryStacks, mode, reverse);
            }
            if (value is DropToSave drop) { int[] ids = { drop.itemId }, stacks = { drop.stackId }; int n = Pairs(ids, stacks, mode, reverse); drop.itemId = ids[0]; return n; }
            if (value is BuriedItem buried) { int[] ids = { buried.itemId }, stacks = { buried.stackedAmount }; int n = Pairs(ids, stacks, mode, reverse); buried.itemId = ids[0]; return n; }
            if (value is SignDetails sign)
            {
                int n = 0; if (sign.signSays == null || !Regex.IsMatch(sign.signSays, @"^<-?\d+> <-?\d+> <-?\d+>$")) return 0;
                sign.signSays = Regex.Replace(sign.signSays, @"<(-?\d+)>", m => { int id = int.Parse(m.Groups[1].Value), next = Map(id, mode, reverse); if (id != next) n++; return "<" + next + ">"; }); return n;
            }
            int count = 0;
            if (value is IEnumerable list) { foreach (object item in list) count += Transform(item, mode, reverse, seen); return count; }
            Check(new[] { "AllChestSaves", "NPCsave", "DropSaves", "SignSave", "BuriedSave" }.Contains(value.GetType().Name), "알 수 없는 저장 객체: " + value.GetType().Name);
            foreach (var field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) count += Transform(field.GetValue(value), mode, reverse, seen);
            return count;
        }
        private static string Locks(string value, Mode mode, bool reverse) => string.Join("|", value.Split('|').Select(record =>
        {
            if (record.Length == 0) return record; var f = record.Split(':');
            Check(f.Length == 4 && int.TryParse(f[1], out _) && int.TryParse(f[2], out _) && Guid.TryParseExact(f[3], "N", out _), "아이템 잠금 기록 오류");
            f[1] = Map(int.Parse(f[1]), mode, reverse).ToString(); return string.Join(":", f);
        }));
        internal static byte[] Prepare(byte[] bytes, string path, Mode requested)
        {
            Check(Path.GetFileName(path).IndexOf(".es3", StringComparison.OrdinalIgnoreCase) >= 0, "구 .dat 세이브는 별도 확인이 필요합니다. 원본은 변경하지 않았습니다.");
            var input = new ES3File(bytes, new ES3Settings(path)); var output = EnhancementTransactions.CloneFile(input);
            Mode mode = requested;
            if (input.KeyExists(EnhancementCompatibility.SchemaKey))
            {
                var schema = input.Load<string>(EnhancementCompatibility.SchemaKey);
                Check(schema == EnhancementCompatibility.Schema || schema == EnhancementCompatibility.PreviousSchema, "알 수 없는 저장 버전"); mode = Mode.Current;
            }
            Check(mode == Mode.OldEnhanced || mode == Mode.NativeOnly || mode == Mode.Current, "저장 종류를 선택하세요.");
            foreach (string key in input.GetKeys())
            {
                if (!ItemKey(key) && key != "LocalItemLocksV1") continue;
                object value = input.Load(key); string before = Raw(value);
                if (key == "LocalItemLocksV1") value = Locks((string)value, mode, false); else Transform(value, mode, false, new HashSet<object>());
                SaveTyped(output, key, value);
                var verify = EnhancementTransactions.CloneFile(output); object restored = verify.Load(key);
                if (key == "LocalItemLocksV1") restored = Locks((string)restored, mode, true); else Transform(restored, mode, true, new HashSet<object>());
                Check(Raw(restored) == before, "변환 검증 실패: " + key);
            }
            output.Save(EnhancementCompatibility.SchemaKey, EnhancementCompatibility.Schema);
            byte[] disk = EnhancementTransactions.DiskBytes(output); var check = new ES3File(disk, new ES3Settings(path));
            Check(check.GetKeys().OrderBy(k => k).SequenceEqual(output.GetKeys().OrderBy(k => k)), "저장 키 누락");
            foreach (string key in output.GetKeys()) Check(Raw(check.Load(key)) == Raw(output.Load(key)), "디스크 직렬화 검증 실패: " + key);
            return disk;
        }
        private sealed class Change { internal string Relative, Before, After; }
        private static string SafeTarget(string relative)
        {
            Check(!Path.IsPathRooted(relative) && !relative.Contains(':') && !relative.Split('/', '\\').Any(p => p == ".." || p == ".") && Regex.IsMatch(relative, @"^Slot[0-9]+[\\/]"), "잘못된 복구 경로");
            string path = Path.GetFullPath(Path.Combine(Root, relative)); Check(path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && EnhancementConversion.IsSave(path), "복구 범위 오류"); Plain(path); return path;
        }
        private static string Digest(string path) { Plain(path); return EnhancementRegistry.Hash(File.ReadAllBytes(path)); }
        private static void Replace(string path, byte[] bytes, string expected, string displaced)
        {
            Plain(path); Check(Digest(path) == expected, "동기화 등으로 저장이 변경됐습니다: " + Path.GetFileName(path));
            string stage = Path.Combine(Path.GetDirectoryName(path), "ld-upgrade-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                Check(Digest(path) == expected, "교체 직전 저장 변경 감지"); File.Replace(stage, path, displaced);
                Check(Digest(displaced) == expected, "동시 교체 감지. 변경된 원본을 보존했습니다: " + displaced);
                Check(Digest(path) == EnhancementRegistry.Hash(bytes), "교체 파일 검증 실패"); ES3File.cachedFiles.Remove(path);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
        internal static void Migrate(IDictionary<string, Mode> selections)
        {
            Check(Safe && !Incomplete, "캐릭터를 고르기 전 첫 화면에서 실행하세요. 중단된 작업은 먼저 복구하세요.");
            Check(!Directory.Exists(EnhancementTransactions.DirectoryPath) || !Directory.GetFiles(EnhancementTransactions.DirectoryPath, "pending-slot*.txt").Any(), "미완료 강화 요청이 있습니다. 먼저 확인해야 합니다.");
            string[] paths = Files(); Check(paths.Length > 0, "변환할 저장이 없습니다. 새 캐릭터는 변환 없이 생성할 수 있습니다.");
            var prepared = new Dictionary<string, byte[]>(); var plan = new List<Change>();
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            string backup = Path.Combine(Root, "LocalEnhancementMigrationBackups", name); Plain(backup); Directory.CreateDirectory(backup);
            long total = 0;
            foreach (string path in paths)
            {
                Check(new FileInfo(path).Length <= 64 * 1024 * 1024, "저장 파일 크기 제한 초과");
                byte[] bytes; using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var memory = new MemoryStream()) { held.CopyTo(memory); bytes = memory.ToArray(); }
                total += bytes.Length; Check(total <= 256L * 1024 * 1024, "전체 저장 크기 제한 초과");
                string relative = path.Substring(Root.Length + 1), slot = relative.Split('/', '\\')[0];
                Check(selections.TryGetValue(slot, out var mode) && mode != Mode.Unknown, slot + " 저장 종류를 선택하세요.");
                byte[] next = Prepare(bytes, path, mode); prepared.Add(path, next);
                string original = Path.Combine(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(original));
                using (var stream = new FileStream(original, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                var change = new Change { Relative = relative, Before = EnhancementRegistry.Hash(bytes), After = EnhancementRegistry.Hash(next) };
                Check(Digest(original) == change.Before, "백업 검증 실패"); plan.Add(change);
            }
            Check(Files().SequenceEqual(paths), "변환 준비 중 저장 파일 집합 변경");
            foreach (var p in plan) Check(Digest(SafeTarget(p.Relative)) == p.Before, "변환 준비 중 저장 변경");
            string manifest = string.Join("\n", plan.Select(p => p.Before + "\t" + p.After + "\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Relative))));
            EnhancementTransactions.AtomicWrite(Path.Combine(backup, "manifest.txt"), Encoding.UTF8.GetBytes(manifest));
            Plain(Flag); using (var flag = new FileStream(Flag, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { byte[] data = Encoding.ASCII.GetBytes(name); flag.Write(data, 0, data.Length); flag.Flush(true); }
            try
            {
                foreach (var p in plan)
                {
                    Check(Files().SequenceEqual(paths), "교체 중 저장 파일 집합 변경");
                    string path = SafeTarget(p.Relative); Replace(path, prepared[path], p.Before, Path.Combine(backup, "displaced-" + Guid.NewGuid().ToString("N") + ".bin"));
                }
                Check(Files().SequenceEqual(paths), "완료 검사 중 저장 파일 집합 변경");
                foreach (var p in plan) Check(Digest(SafeTarget(p.Relative)) == p.After, "완료 검사 중 저장 변경");
                // Keep the recovery journal until the installed-save guard is
                // durable too. A failed guard write must still allow rollback.
                EnhancementConversion.RequireConversion();
                Check(File.ReadAllText(Flag) == name, "변환 작업 표시 변경"); File.Delete(Flag);
                EnhancementCompatibility.Error = null;
                status = "변환 완료. 게임을 재시작하세요. 백업: " + backup;
            }
            catch { try { Rollback(); } catch (Exception e) { Debug.LogError("Migration rollback blocked; marker retained: " + e); } throw; }
        }
        internal static void Rollback()
        {
            Check(EnhancementConversion.CanConvert(), "첫 화면에서만 복구할 수 있습니다."); Plain(Flag);
            string name = File.ReadAllText(Flag); Check(Regex.IsMatch(name, @"^\d{8}-\d{6}-[a-f0-9]{32}$"), "변환 표시 손상");
            string backup = Path.Combine(Root, "LocalEnhancementMigrationBackups", name); Plain(backup);
            string manifest = Path.Combine(backup, "manifest.txt"); Plain(manifest); Check(new FileInfo(manifest).Length < 4 * 1024 * 1024, "복구 목록 크기 오류");
            var plan = File.ReadAllLines(manifest).Select(line =>
            {
                var f = line.Split('\t'); Check(f.Length == 3 && Regex.IsMatch(f[0], "^[A-F0-9]{64}$") && Regex.IsMatch(f[1], "^[A-F0-9]{64}$"), "복구 목록 손상");
                return new Change { Before = f[0], After = f[1], Relative = Encoding.UTF8.GetString(Convert.FromBase64String(f[2])) };
            }).ToArray();
            Check(plan.Length > 0 && plan.Length <= 10000 && plan.Select(p => SafeTarget(p.Relative)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.Length, "복구 목록 중복/누락");
            var expectedPaths = plan.Select(p => SafeTarget(p.Relative)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Check(Files().SequenceEqual(expectedPaths), "복구 전 저장 파일 집합 변경. 동기화를 멈추고 확인하세요.");
            foreach (var p in plan) { string now = Digest(SafeTarget(p.Relative)); Check(now == p.Before || now == p.After, "변환 이후 외부에서 변경된 파일입니다. 자동 복구 중단: " + p.Relative); Check(Digest(Path.Combine(backup, p.Relative)) == p.Before, "복구 백업 손상"); }
            foreach (var p in plan)
            {
                Check(Files().SequenceEqual(expectedPaths), "복구 중 저장 파일 집합 변경");
                if (Digest(SafeTarget(p.Relative)) == p.After) Replace(SafeTarget(p.Relative), File.ReadAllBytes(Path.Combine(backup, p.Relative)), p.After, Path.Combine(backup, "rollback-" + Guid.NewGuid().ToString("N") + ".bin"));
            }
            foreach (var p in plan) Check(Digest(SafeTarget(p.Relative)) == p.Before, "복구 후 검증 실패");
            Check(Files().SequenceEqual(expectedPaths), "복구 완료 검사 중 저장 파일 집합 변경");
            Check(File.ReadAllText(Flag) == name, "복구 중 표시 변경"); File.Delete(Flag);
        }
    }
}
