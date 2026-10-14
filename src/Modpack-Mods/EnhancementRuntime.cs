using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using HarmonyLib;
using LocalDinkum.Enhancement;
using Mirror;
using UnityEngine;

namespace LocalDinkumMods
{
    // IDs are appended in base-ID order, ten variants per supported item. Every
    // participant verifies the complete registry fingerprint before becoming ready.
    internal static class EnhancementRegistry
    {
        internal const string Version = "1.2.0";
        internal const string GameHash = "5106DA5D624AAEDABB16159A9BDAA9AB3CE4D09EB992BD4759D881DD9D9BEA24";
        internal sealed class Entry { internal int BaseId, Level; }
        internal static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        private static readonly Dictionary<int, int> first = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> upper = new Dictionary<int, int>();
        internal static string Fingerprint;
        internal static int BaseCount;
        private static Inventory initialized;
        internal static bool Harvest(InventoryItem i) => i.myAnimType == InventoryItem.typeOfAnimation.Axe || i.myAnimType == InventoryItem.typeOfAnimation.Pickaxe;
        internal static bool Weapon(InventoryItem i) => i.itemPrefab && i.itemPrefab.GetComponent<MeleeAttacks>() && i.itemPrefab.GetComponent<MeleeAttacks>().isWeapon;
        internal static bool Supported(InventoryItem i) => i && i.hasFuel && i.fuelMax > 0 && !i.hasColourVariation && !i.isPowerTool && (Harvest(i) || Weapon(i));
        internal static Entry Describe(int id) => Entries.TryGetValue(id, out var e) ? e : new Entry { BaseId = id, Level = 0 };
        internal static int Id(int baseId, int level)
        {
            if (level < 0 || level > EnhancementRules.MaximumLevel) throw new ArgumentOutOfRangeException(nameof(level));
            return level == 0 ? baseId : level <= 10 ? first[baseId] + level - 1 : upper[baseId] + level - 11;
        }
        internal static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", ""); }
        internal static void CheckGame()
        {
            if (Hash(File.ReadAllBytes(typeof(Inventory).Assembly.Location)) != GameHash)
                throw new InvalidOperationException("Enhancement requires the tested Assembly-CSharp hash. No patches installed.");
        }
        internal static void Initialize(Inventory inventory)
        {
            if (initialized == inventory) return;
            Entries.Clear(); first.Clear(); upper.Clear(); Fingerprint = null;
            EnhancementCompatibility.ValidateCatalog(inventory);
            var items = inventory.allItems.ToList(); BaseCount = items.Count;
            var manifest = new StringBuilder(Version + "|" + GameHash + "|" + BaseCount);
            // Preserve every existing +1..+10 ID. Higher tiers are appended only
            // AFTER all 510 old variants, never interleaved with existing equipment.
            for (int startLevel = 1; startLevel <= EnhancementRules.MaximumLevel; startLevel += 10)
            for (int id = 0; id < BaseCount; id++)
            {
                var original = items[id];
                if (startLevel == 1) manifest.Append('|').Append(id).Append(':').Append(original ? original.itemName : "NULL");
                if (!Supported(original)) continue;
                (startLevel == 1 ? first : upper).Add(id, items.Count);
                for (int level = startLevel; level < startLevel + 10; level++)
                {
                    // Do not instantiate the asset GameObject: that can run unrelated
                    // Awake methods. Copy only InventoryItem's serialized definition.
                    var go = new GameObject("LocalEnhancement_" + id + "_" + level);
                    go.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(go);
                    var variant = go.AddComponent<InventoryItem>();
                    foreach (var field in typeof(InventoryItem).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        field.SetValue(variant, field.GetValue(original));
                    variant.itemName = original.itemName + " +" + level;
                    variant.craftable = null; variant.isRequestable = false; variant.canBeUsedInShops = false;
                    variant.showInCreativeMenu = false;
                    variant.fuelMax = EnhancementRules.Capacity(original.fuelMax, level);
                    if (Weapon(original)) variant.weaponDamage = original.weaponDamage * EnhancementRules.AttackOrHarvestPercent(level) / 100f;
                    if (Harvest(original)) variant.damagePerAttack = original.damagePerAttack * EnhancementRules.AttackOrHarvestPercent(level) / 100f;
                    variant.setItemId(items.Count);
                    Entries.Add(items.Count, new Entry { BaseId = id, Level = level });
                    manifest.Append('|').Append(items.Count).Append(':').Append(id).Append(':').Append(level).Append(':').Append(variant.fuelMax);
                    items.Add(variant);
                }
            }
            if (first.Count == 0) throw new InvalidOperationException("No supported equipment assets found.");
            EnhancementConversion.RequireConversion();
            inventory.allItems = items.ToArray(); initialized = inventory;
            Fingerprint = Hash(Encoding.UTF8.GetBytes(manifest.ToString()));
        }
        [HarmonyPatch(typeof(Inventory), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void OnInventory(Inventory __instance) => Initialize(__instance);
        [HarmonyPatch(typeof(InventoryItem), "getInvItemName"), HarmonyPrefix]
        private static bool Name(InventoryItem __instance, ref string __result)
        {
            if (!Inventory.Instance) return true;
            int id = Inventory.Instance.getInvItemId(__instance);
            if (!Entries.TryGetValue(id, out var entry)) return true;
            __result = Inventory.Instance.allItems[entry.BaseId].getInvItemName() + " +" + entry.Level; return false;
        }
    }

    internal struct EnhancementHello : NetworkMessage { public string Fingerprint; }
    internal struct EnhancementWelcome : NetworkMessage { public string Fingerprint; }
    internal struct EnhancementRequest : NetworkMessage { public string Id; public int Item, Stack; }
    internal struct EnhancementResult : NetworkMessage { public string Id; public int Roll; }
    internal static class EnhancementNetwork
    {
        internal static bool Accepted;
        private static NetworkConnection client;
        private static CustomNetworkManager deferred;
        private static float connectedAt;
        private static readonly HashSet<NetworkConnection> peers = new HashSet<NetworkConnection>();
        private static readonly Dictionary<NetworkConnection, float> last = new Dictionary<NetworkConnection, float>();
        private static bool resume;
        internal static void Setup()
        {
            Writer<EnhancementHello>.write = (w, m) => w.WriteString(m.Fingerprint);
            Reader<EnhancementHello>.read = r => new EnhancementHello { Fingerprint = Bounded(r, 64) };
            Writer<EnhancementWelcome>.write = (w, m) => w.WriteString(m.Fingerprint);
            Reader<EnhancementWelcome>.read = r => new EnhancementWelcome { Fingerprint = Bounded(r, 64) };
            Writer<EnhancementRequest>.write = (w, m) => { w.WriteString(m.Id); w.WriteInt(m.Item); w.WriteInt(m.Stack); };
            Reader<EnhancementRequest>.read = r => new EnhancementRequest { Id = ReadId(r), Item = r.ReadInt(), Stack = r.ReadInt() };
            Writer<EnhancementResult>.write = (w, m) => { w.WriteString(m.Id); w.WriteInt(m.Roll); };
            Reader<EnhancementResult>.read = r => { var m = new EnhancementResult { Id = ReadId(r), Roll = r.ReadInt() }; if (m.Roll < -1 || m.Roll >= 10000) throw new FormatException("Invalid roll"); return m; };
        }
        private static string Bounded(NetworkReader r, int size) { var v = r.ReadString(); if (v == null || v.Length != size) throw new FormatException("Enhancement message length"); return v; }
        private static string ReadId(NetworkReader r) { var id = Bounded(r, 32); if (!Guid.TryParseExact(id, "N", out _)) throw new FormatException("Transaction ID"); return id; }
        [HarmonyPatch(typeof(NetworkServer), "Listen"), HarmonyPostfix]
        private static void Server()
        {
            peers.Clear(); last.Clear();
            NetworkServer.RegisterHandler<EnhancementHello>((c, m) =>
            {
                if (string.IsNullOrEmpty(EnhancementRegistry.Fingerprint) || m.Fingerprint != EnhancementRegistry.Fingerprint) { c.Disconnect(); return; }
                peers.Add(c); c.Send(new EnhancementWelcome { Fingerprint = EnhancementRegistry.Fingerprint });
            }, true);
            NetworkServer.RegisterHandler<EnhancementRequest>((connection, request) =>
            {
                try { Receive(connection, request); }
                catch (Exception error) { Debug.LogError("Enhancement host receipt failed; no client costs charged: " + error); connection.Send(new EnhancementResult { Id = request.Id, Roll = -1 }); }
            }, true);
        }
        [HarmonyPatch(typeof(CustomNetworkManager), "OnClientConnect"), HarmonyPrefix]
        private static bool Connect(CustomNetworkManager __instance)
        {
            EnhancementConversion.MarkCharacterLoadStarted();
            if (resume) return true;
            Accepted = false; client = NetworkClient.connection; deferred = __instance; connectedAt = Time.realtimeSinceStartup;
            NetworkClient.RegisterHandler<EnhancementWelcome>(Welcome, true);
            NetworkClient.RegisterHandler<EnhancementResult>(result =>
            {
                try { EnhancementTransactions.Receive(result); }
                catch (Exception error) { EnhancementTransactions.Status = "강화 기록 처리 실패: 비용 확정 상태를 확인 중입니다."; Debug.LogError("Enhancement result handler retained transaction: " + error); }
            }, true);
            if (string.IsNullOrEmpty(EnhancementRegistry.Fingerprint)) { NetworkClient.Disconnect(); return false; }
            NetworkClient.Send(new EnhancementHello { Fingerprint = EnhancementRegistry.Fingerprint });
            return false; // Never load the player's variant IDs or send Ready before the handshake.
        }
        private static void Welcome(EnhancementWelcome m)
        {
            if (m.Fingerprint != EnhancementRegistry.Fingerprint || client != NetworkClient.connection || !deferred) { NetworkClient.Disconnect(); return; }
            Accepted = true; resume = true;
            try { var manager = deferred; deferred = null; manager.OnClientConnect(); }
            finally { resume = false; }
        }
        [HarmonyPatch(typeof(NetworkServer), "SetClientReady"), HarmonyPrefix]
        private static bool Ready(NetworkConnection conn) { if (peers.Contains(conn)) return true; conn.Disconnect(); return false; }
        [HarmonyPatch(typeof(NetworkServer), "AddPlayerForConnection", new[] { typeof(NetworkConnection), typeof(GameObject) }), HarmonyPrefix]
        private static bool Spawn(NetworkConnection conn, ref bool __result) { if (peers.Contains(conn)) return true; conn.Disconnect(); __result = false; return false; }
        private static void Receive(NetworkConnection conn, EnhancementRequest req)
        {
            if (!peers.Contains(conn) || !conn.isReady || !conn.identity || !Common.Item(req.Item) || !EnhancementRegistry.Supported(Inventory.Instance.allItems[req.Item])) return;
            if (last.TryGetValue(conn, out var t) && Time.realtimeSinceStartup - t < .5f) return;
            last[conn] = Time.realtimeSinceStartup;
            if (EnhancementRegistry.Describe(req.Item).Level >= EnhancementRules.MaximumLevel || req.Stack < 1 || req.Stack > Inventory.Instance.allItems[req.Item].fuelMax) return;
            // Receipt is durable before transmission. Retries, including reconnects,
            // cannot draw a new random result for the same transaction.
            var path = Path.Combine(EnhancementTransactions.DirectoryPath, "receipts", req.Id + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string owner = conn.address ?? "local";
            string signature = EnhancementRegistry.Hash(Encoding.UTF8.GetBytes(owner + "|" + req.Item + "|" + req.Stack));
            int roll;
            if (File.Exists(path))
            {
                var parts = File.ReadAllLines(path);
                if (parts.Length != 2 || parts[0] != signature || !int.TryParse(parts[1], out roll) || roll < 0 || roll >= 10000) return;
            }
            else
            {
                if (Directory.EnumerateFiles(Path.GetDirectoryName(path), "*.txt").Take(10001).Count() >= 10000) throw new IOException("Host enhancement receipt limit reached; retain receipts for recovery.");
                using (var rng = RandomNumberGenerator.Create()) { var bytes = new byte[4]; uint n; do { rng.GetBytes(bytes); n = BitConverter.ToUInt32(bytes, 0); } while (n >= uint.MaxValue - uint.MaxValue % 10000); roll = (int)(n % 10000); }
                EnhancementTransactions.AtomicWrite(path, Encoding.UTF8.GetBytes(signature + "\n" + roll));
            }
            conn.Send(new EnhancementResult { Id = req.Id, Roll = roll });
        }
        internal static void Tick()
        {
            if (!NetworkClient.isConnected || client != NetworkClient.connection) { Accepted = false; deferred = null; }
            if (deferred && Time.realtimeSinceStartup - connectedAt > 15) { deferred = null; NetworkClient.Disconnect(); Common.Notify("같은 강화 모드 버전이 필요합니다. 접속을 중단했습니다."); }
        }
    }

    internal static class EnhancementTransactions
    {
        internal static string DirectoryPath => Path.Combine(Paths.ConfigPath, "LocalEquipmentEnhancement");
        internal static bool Pending => pending != null;
        internal static bool Fatal;
        internal static string Status = "";
        private sealed class PendingAttempt
        {
            internal string Id, Path, Host;
            internal int Slot, Item, Stack, Wallet;
            internal int[] Items, Stacks;
            internal Inventory Inventory;
        }
        private static PendingAttempt pending;
        private static float retryAt;
        private static Type Bridge => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("LocalDinkumMods.ItemLockBridge", false)).FirstOrDefault(t => t != null);
        internal static bool TransferPending => (bool?)Bridge?.GetProperty("TransferPending")?.GetValue(null, null) == true;
        internal static void SetModal(bool value) => Bridge?.GetProperty("ExternalModalOpen")?.SetValue(null, value, null);
        private static string Capture(int slot) => (string)Bridge?.GetMethod("Capture")?.Invoke(null, new object[] { slot }) ?? "";
        private static void Restore(int slot, string token) => Bridge?.GetMethod("Restore")?.Invoke(null, new object[] { slot, token });
        internal static int Material(string name)
        {
            var ids = Inventory.Instance.allItems.Select((item, id) => new { item, id }).Where(x => x.item && x.item.itemName == name && !x.item.hasFuel).ToArray();
            if (ids.Length != 1) throw new InvalidOperationException("Material asset is not uniquely identifiable: " + name);
            return ids[0].id;
        }
        internal static int Count(int id) => Inventory.Instance.invSlots.Where(s => s && s.slotUnlocked && s.itemNo == id).Sum(s => s.stack);
        internal static void Begin(InventorySlot selected)
        {
            if (Pending || Fatal || TransferPending || EnhancementConversion.Completed || !EnhancementNetwork.Accepted) return;
            var inv = Inventory.Instance;
            int slot = Array.IndexOf(inv.invSlots, selected);
            if (slot < 0 || !selected.slotUnlocked || selected.stack <= 0) throw new InvalidOperationException("Invalid selected equipment.");
            var entry = EnhancementRegistry.Describe(selected.itemNo);
            var cost = EnhancementRules.CostForTarget(entry.Level + 1);
            if (inv.wallet < cost.Dinks) { Status = "딩크가 부족합니다."; return; }
            string save = PlayerPath();
            if (!File.Exists(save)) throw new InvalidOperationException("Save the character once before enhancing equipment.");
            string journal = Path.Combine(DirectoryPath, "pending-slot" + SaveLoad.saveOrLoad.currentSaveSlotNo() + ".txt");
            if (File.Exists(journal)) throw new InvalidOperationException("Unresolved enhancement journal. Reconnect to its host and reopen the enhancement window.");
            Directory.CreateDirectory(DirectoryPath);
            var tx = new PendingAttempt { Id = Guid.NewGuid().ToString("N"), Path = journal, Host = NetworkManager.singleton.networkAddress ?? "", Slot = slot, Item = selected.itemNo, Stack = selected.stack, Wallet = inv.wallet, Items = inv.invSlots.Select(s => s.itemNo).ToArray(), Stacks = inv.invSlots.Select(s => s.stack).ToArray(), Inventory = inv };
            // No items are taken into escrow. Inputs stay intact until an atomic
            // playerInfo + receipt disk commit can be made on the client thread.
            AtomicWrite(journal, Encoding.UTF8.GetBytes(string.Join("\n", tx.Id, tx.Host, tx.Slot, tx.Item, tx.Stack, tx.Wallet, string.Join(",", tx.Items), string.Join(",", tx.Stacks))));
            pending = tx; retryAt = 0; Status = "방장 결과 확인 중… 딩크는 아직 차감되지 않았습니다.";
        }
        internal static string PlayerPath() => Path.Combine(Application.persistentDataPath, "Slot" + SaveLoad.saveOrLoad.currentSaveSlotNo(), "Player.es3");
        internal static void Tick()
        {
            if (pending != null && (!NetworkClient.isConnected || pending.Inventory != Inventory.Instance)) { pending = null; SetModal(false); Status = "미완료 요청은 보존했습니다. 재접속 후 강화 창을 열면 복구합니다."; }
            if (pending == null || Fatal || !EnhancementNetwork.Accepted || !NetworkClient.ready || pending.Inventory != Inventory.Instance) return;
            if (Time.realtimeSinceStartup < retryAt) return;
            retryAt = Time.realtimeSinceStartup + 2;
            NetworkClient.Send(new EnhancementRequest { Id = pending.Id, Item = pending.Item, Stack = pending.Stack });
        }
        internal static void Cancel()
        {
            if (pending == null || Fatal) return;
            Archive(pending, "user-cancelled-before-commit");
            Status = "확정 전 요청을 취소했습니다. 딩크는 소모하지 않았습니다.";
        }
        internal static void Receive(EnhancementResult result)
        {
            var tx = pending;
            if (tx == null || tx.Id != result.Id || tx.Inventory != Inventory.Instance || !EnhancementNetwork.Accepted || Fatal) return;
            if (result.Roll == -1) { Status = "방장이 결과를 저장하지 못했습니다. 비용 차감 없이 재시도합니다."; return; }
            var inv = Inventory.Instance;
            if (SaveLoad.saveOrLoad.isSaving) return;
            if (tx.Wallet != inv.wallet || !tx.Items.SequenceEqual(inv.invSlots.Select(s => s.itemNo)) || !tx.Stacks.SequenceEqual(inv.invSlots.Select(s => s.stack)))
            { Status = "대기 중 인벤토리가 변경되어 취소했습니다. 비용은 차감되지 않았습니다."; Archive(tx, "cancelled"); return; }
            bool committed = false;
            try
            {
                var entry = EnhancementRegistry.Describe(tx.Item); int level = EnhancementRules.ResultLevel(entry.Level, result.Roll);
                var quote = EnhancementRules.CostForTarget(entry.Level + 1);
                var player = new PlayerInv(); player.SaveInv();
                // Build a complete replacement off to the side: cost failure never
                // partially changes slots, money, or the current equipment.
                if (player.money < quote.Dinks) throw new InvalidOperationException("Insufficient funds");
                player.money -= quote.Dinks;
                int nextId = EnhancementRegistry.Id(entry.BaseId, level);
                int nextStack = EnhancementRules.PreserveCondition(tx.Stack, inv.allItems[tx.Item].fuelMax, inv.allItems[nextId].fuelMax);
                player.itemsInInvSlots[tx.Slot] = nextId; player.stacksInSlots[tx.Slot] = nextStack;
                string path = PlayerPath();
                var disk = new ES3File(path);
                if (disk.Load<string>("LocalEnhancementLastTransactionV1", "") == tx.Id) { Archive(tx, "already-committed"); return; }
                // Merge the current cache first to retain other mods' same-file keys.
                if (ES3File.cachedFiles.TryGetValue(path, out var cached)) disk = CloneFile(cached);
                var locks = Enumerable.Range(0, inv.invSlots.Length).Select(Capture).ToArray();
                if (Bridge != null)
                    disk.Save("LocalItemLocksV1", string.Join("|", Enumerable.Range(0, locks.Length).Where(i => locks[i].Length == 32 && player.itemsInInvSlots[i] >= 0).Select(i => i + ":" + player.itemsInInvSlots[i] + ":" + player.stacksInSlots[i] + ":" + locks[i])));
                disk.Save("playerInfo", player); disk.Save("LocalEnhancementLastTransactionV1", tx.Id);
                disk.Save(EnhancementCompatibility.SchemaKey, EnhancementCompatibility.Schema);
                AtomicWrite(Path.Combine(DirectoryPath, "installed-save-guard.txt"), Encoding.UTF8.GetBytes("CONVERSION_REQUIRED\n" + EnhancementRegistry.Fingerprint));
                AtomicWrite(path, DiskBytes(disk)); committed = true;
                ES3File.cachedFiles[path] = disk;
                for (int i = 0; i < inv.invSlots.Length; i++)
                    if (inv.invSlots[i].itemNo != player.itemsInInvSlots[i] || inv.invSlots[i].stack != player.stacksInSlots[i])
                        inv.invSlots[i].updateSlotContentsAndRefresh(player.itemsInInvSlots[i], player.stacksInSlots[i]);
                for (int i = 0; i < locks.Length; i++) Restore(i, locks[i]);
                inv.changeWalletToLoad(player.money);
                inv.equipNewSelectedSlot();
                // Save lock identity after the replacement without overwriting any
                // earlier same-file data. Main mod hook will refresh on normal save.
                Archive(tx, "complete");
                Status = level > entry.Level ? "강화 성공! +" + level : level < entry.Level ? "강화 실패: +" + entry.Level + " → +" + level + " 하락 (파괴 없음·딩크 소모)" : "강화 실패: +" + level + " 유지 (딩크 소모)";
                Common.Notify(Status);
            }
            catch (Exception error)
            {
                if (committed) { Fatal = true; Status = "저장은 완료됐지만 화면 반영에 실패했습니다. 추가 저장 없이 게임을 종료하고 다시 실행하세요."; }
                else Status = "강화 저장 실패 — 아이템과 돈은 변경하지 않았습니다. " + error.Message;
                Debug.LogError("Enhancement transaction: " + error);
            }
        }
        private static void Archive(PendingAttempt tx, string suffix)
        {
            File.Move(tx.Path, tx.Path + "." + tx.Id + "." + suffix); pending = null;
        }
        internal static void Recover()
        {
            if (!EnhancementNetwork.Accepted || !SaveLoad.saveOrLoad) return;
            if (pending != null && pending.Inventory == Inventory.Instance) return;
            pending = null;
            string path = Path.Combine(DirectoryPath, "pending-slot" + SaveLoad.saveOrLoad.currentSaveSlotNo() + ".txt");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            if (lines.Length != 8 || !Guid.TryParseExact(lines[0], "N", out _)) throw new InvalidDataException("Invalid enhancement journal; retained for recovery.");
            var tx = new PendingAttempt { Id = lines[0], Path = path, Host = lines[1], Slot = int.Parse(lines[2]), Item = int.Parse(lines[3]), Stack = int.Parse(lines[4]), Wallet = int.Parse(lines[5]), Items = lines[6].Split(',').Select(int.Parse).ToArray(), Stacks = lines[7].Split(',').Select(int.Parse).ToArray(), Inventory = Inventory.Instance };
            var disk = new ES3File(PlayerPath());
            if (disk.Load<string>("LocalEnhancementLastTransactionV1", "") == tx.Id) { Archive(tx, "recovered-committed"); Status = "이미 저장된 강화 결과를 복구했습니다."; return; }
            if (tx.Host != (NetworkManager.singleton.networkAddress ?? "")) { Status = "이전 강화를 완료하려면 같은 방장에게 다시 접속하세요."; return; }
            pending = tx; retryAt = 0;
        }
        internal static void AtomicWrite(string target, byte[] bytes)
        {
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(target)) File.Replace(temporary, target, target + ".enhancement-backup"); else File.Move(temporary, target);
        }
        internal static ES3File CloneFile(ES3File source)
        {
            var saved = source.settings;
            var raw = (ES3Settings)saved.Clone();
            raw.encryptionType = ES3.EncryptionType.None; raw.compressionType = ES3.CompressionType.None;
            raw.postprocessRawCachedData = false;
            try
            {
                source.settings = raw;
                var copy = new ES3File(source.LoadRawBytes(), raw);
                copy.settings = (ES3Settings)saved.Clone();
                return copy;
            }
            finally { source.settings = saved; }
        }
        internal static byte[] DiskBytes(ES3File source)
        {
            var saved = source.settings;
            var disk = (ES3Settings)saved.Clone();
            disk.postprocessRawCachedData = true;
            try { source.settings = disk; return source.LoadRawBytes(); }
            finally { source.settings = saved; }
        }
        [HarmonyPatch(typeof(SaveAndLoad), "SaveGame"), HarmonyPrefix]
        private static bool BlockFatalSave() => !Fatal;
        [HarmonyPatch(typeof(SaveAndLoad), "SaveGameOnDisconnect"), HarmonyPrefix]
        private static bool BlockFatalDisconnectSave() => !Fatal;
        [HarmonyPatch(typeof(Inventory), "Update"), HarmonyPrefix]
        private static bool BlockFatalInput() => !Fatal && !EquipmentEnhancement.ModalOpen && !Pending;
        [HarmonyPatch(typeof(Inventory), "moveCursor"), HarmonyPrefix]
        private static bool BlockUnderlyingCursorInput() => !Fatal && !EquipmentEnhancement.ModalOpen && !Pending;
        [HarmonyPatch(typeof(SaveAndLoad), "LoadOverFrames"), HarmonyPrefix]
        private static void MarkLoadStarted() => EnhancementConversion.MarkCharacterLoadStarted();
        [HarmonyPatch(typeof(CharacterCreatorScript), "saveCharSetUp"), HarmonyPrefix]
        private static void MarkNewCharacterStarted() => EnhancementConversion.MarkCharacterLoadStarted();
    }

    internal static class EnhancementSelfTest
    {
        private static bool done;
        internal static void Tick()
        {
            bool isolatedFlag = ((Application.productName == "DKT921" && new DirectoryInfo(Paths.GameRootPath).Name == "runtime-isolated-20260921") || (Application.productName == "DKT014" && new DirectoryInfo(Paths.GameRootPath).Name == "runtime-compat-20261014")) && File.Exists(Path.Combine(Paths.GameRootPath, "enhancement-self-test.flag"));
            if (done || !(Environment.GetCommandLineArgs().Contains("--local-enhancement-self-test") || isolatedFlag) || !Inventory.Instance || string.IsNullOrEmpty(EnhancementRegistry.Fingerprint) || Time.realtimeSinceStartup < 5) return;
            done = true;
            int count = 0;
            Action<bool, string> check = (ok, message) => { if (!ok) throw new Exception("Enhancement self-test: " + message); count++; };
            try
            {
                var inv = Inventory.Instance;
                check(Enumerable.Range(1, EnhancementRules.MaximumLevel).All(level => EnhancementRules.CostForTarget(level).CopperBars == 0 && EnhancementRules.CostForTarget(level).IronBars == 0), "all tiers cost Dinks only");
                foreach (var pair in EnhancementRegistry.Entries)
                {
                    var item = inv.allItems[pair.Key]; var original = inv.allItems[pair.Value.BaseId];
                    check(item != original, "shared definition mutated");
                    check(item.fuelMax == EnhancementRules.Capacity(original.fuelMax, pair.Value.Level), "capacity");
                    check(item.craftable == null && !item.isRequestable, "recipe isolation");
                    check(inv.getInvItemId(item) == pair.Key, "ID cache");
                    check(item.getInvItemName().EndsWith("+" + pair.Value.Level), "localized variant name");
                    if (EnhancementRegistry.Weapon(original)) check(Math.Abs(item.weaponDamage - original.weaponDamage * EnhancementRules.AttackOrHarvestPercent(pair.Value.Level) / 100f) < .001, "weapon stat");
                    if (EnhancementRegistry.Harvest(original)) check(Math.Abs(item.damagePerAttack - original.damagePerAttack * EnhancementRules.AttackOrHarvestPercent(pair.Value.Level) / 100f) < .001, "harvest stat");
                }
                var sample = EnhancementRegistry.Entries.First();
                var saved = new PlayerInv { itemsInInvSlots = new[] { sample.Key, sample.Key }, stacksInSlots = new[] { 15, 30 }, money = 12345 };
                var file = new ES3File(false); file.Save("playerInfo", saved); file.Save("LocalItemLocksV1", "1:" + sample.Key + ":30:" + Guid.NewGuid().ToString("N")); file.Save("UnrelatedModKey", "must survive");
                var roundtrip = EnhancementTransactions.CloneFile(file);
                var player = roundtrip.Load<PlayerInv>("playerInfo");
                check(player.itemsInInvSlots.SequenceEqual(saved.itemsInInvSlots) && player.stacksInSlots.SequenceEqual(saved.stacksInSlots) && player.money == saved.money, "ES3 equipment and wallet roundtrip");
                check(roundtrip.Load<string>("LocalItemLocksV1") == file.Load<string>("LocalItemLocksV1") && roundtrip.Load<string>("UnrelatedModKey") == "must survive", "ES3 unrelated-key preservation");
                var request = new EnhancementRequest { Id = Guid.NewGuid().ToString("N"), Item = sample.Key, Stack = 15 };
                var writer = new NetworkWriter(); Writer<EnhancementRequest>.write(writer, request);
                var read = Reader<EnhancementRequest>.read(new NetworkReader(writer.ToArray()));
                check(read.Id == request.Id && read.Item == request.Item && read.Stack == request.Stack, "request wire");
                foreach (int roll in new[] { -1, 0, 8999, 9999 }) { writer.Reset(); Writer<EnhancementResult>.write(writer, new EnhancementResult { Id = request.Id, Roll = roll }); var result = Reader<EnhancementResult>.read(new NetworkReader(writer.ToArray())); check(result.Id == request.Id && result.Roll == roll, "result wire"); }
                var testDir = Path.Combine(EnhancementTransactions.DirectoryPath, "isolated-self-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(testDir);
                var path = Path.Combine(testDir, "transaction.es3");
                EnhancementTransactions.AtomicWrite(path, EnhancementTransactions.DiskBytes(file));
                roundtrip.Save("playerInfo", new PlayerInv { money = 500 });
                EnhancementTransactions.AtomicWrite(path, EnhancementTransactions.DiskBytes(roundtrip));
                check(new ES3File(path).Load<PlayerInv>("playerInfo").money == 500, "atomic replacement");
                check(new ES3File(path + ".enhancement-backup").Load<PlayerInv>("playerInfo").money == 12345, "recoverable prior snapshot");
                EnhancementConversion.Test(check, testDir);
                Debug.Log("ENHANCEMENT_SELF_TEST_PASS checks=" + count + " variants=" + EnhancementRegistry.Entries.Count + " registry=" + EnhancementRegistry.Fingerprint + " files=" + testDir + " (no player/world save touched)");
            }
            catch (Exception e) { Debug.LogError("ENHANCEMENT_SELF_TEST_FAIL " + e); }
        }
    }
}
