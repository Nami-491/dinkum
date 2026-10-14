using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;

namespace LocalDinkumMods
{
    internal struct TransferRequest : NetworkMessage
    {
        public string Id, Session;
        public int HouseX, HouseY;
        public int[] Items, Stacks;
    }
    internal struct TransferReply : NetworkMessage { public string Id; public bool[] Accepted; }
    internal static class TransferProtocol
    {
        internal const string Key = "LocalInventoryTransfer";
        internal static string Session = "";
        internal static bool Pending => pending != null;
        private sealed class Transaction
        {
            internal TransferRequest Request;
            internal int[] Slots;
            internal bool[] Returned;
            internal bool[] Accepted;
            internal string Journal, Player;
            internal Inventory Inventory;
            internal NetworkConnection Connection;
            internal float SentAt, NextRetry;
            internal bool Warned;
        }
        private sealed class Receipt { internal NetworkConnection Owner; internal TransferReply Reply; }
        private static readonly Dictionary<string, Receipt> receipts = new Dictionary<string, Receipt>();
        private static readonly Dictionary<NetworkConnection, float> lastRequests = new Dictionary<NetworkConnection, float>();
        private static Transaction pending;
        internal static void Setup()
        {
            Writer<TransferRequest>.write = (w, m) => { w.WriteString(m.Id); w.WriteString(m.Session); w.WriteInt(m.HouseX); w.WriteInt(m.HouseY); w.WriteInt(m.Items.Length); for (int i = 0; i < m.Items.Length; i++) { w.WriteInt(m.Items[i]); w.WriteInt(m.Stacks[i]); } };
            Reader<TransferRequest>.read = r =>
            {
                var m = new TransferRequest { Id = ReadId(r), Session = ReadId(r), HouseX = r.ReadInt(), HouseY = r.ReadInt() };
                int count = r.ReadInt(); if (count < 0 || count > 96 || count > r.Remaining / 8) throw new FormatException("Transfer length");
                m.Items = new int[count]; m.Stacks = new int[count];
                for (int i = 0; i < count; i++) { m.Items[i] = r.ReadInt(); m.Stacks[i] = r.ReadInt(); if (m.Items[i] < 0 || m.Stacks[i] < 0 || m.Stacks[i] > 100000000) throw new FormatException("Transfer item"); }
                return m;
            };
            Writer<TransferReply>.write = (w, m) => { w.WriteString(m.Id); w.WriteInt(m.Accepted.Length); foreach (bool b in m.Accepted) w.WriteBool(b); };
            Reader<TransferReply>.read = r => { string id = ReadId(r); int count = r.ReadInt(); if (count < 0 || count > 96 || count > r.Remaining) throw new FormatException("Receipt length"); var accepted = new bool[count]; for (int i = 0; i < count; i++) accepted[i] = r.ReadBool(); return new TransferReply { Id = id, Accepted = accepted }; };
        }
        private static string ReadId(NetworkReader r) { string value = r.ReadString(); if (value == null || value.Length != 32 || !Guid.TryParseExact(value, "N", out _)) throw new FormatException("Transfer identifier"); return value; }
        internal static void StartServer()
        {
            Session = Guid.NewGuid().ToString("N"); receipts.Clear(); lastRequests.Clear();
            NetworkServer.UnregisterHandler<TransferRequest>(); NetworkServer.RegisterHandler<TransferRequest>(Receive, true); Advertise();
        }
        internal static void StartClient()
        {
            NetworkClient.UnregisterHandler<TransferReply>(); NetworkClient.RegisterHandler<TransferReply>(Reply, true);
        }
        internal static void Advertise()
        {
            if (NetworkServer.active && SteamManager.Initialized && SteamLobby.Instance && SteamLobby.Instance.currentLobby != CSteamID.Nil &&
                SteamMatchmaking.GetLobbyOwner(SteamLobby.Instance.currentLobby) == SteamUser.GetSteamID())
                SteamMatchmaking.SetLobbyData(SteamLobby.Instance.currentLobby, Key, Session);
        }
        internal static string HostSession()
        {
            if (NetworkServer.active) return Session;
            return SteamManager.Initialized && SteamLobby.Instance && SteamLobby.Instance.currentLobby != CSteamID.Nil ? SteamMatchmaking.GetLobbyData(SteamLobby.Instance.currentLobby, Key) : "";
        }
        internal static void Begin()
        {
            if (pending != null) { Common.Notify("이전 상자 전송 결과를 확인 중입니다. 다시 보내지 마세요."); return; }
            var player = NetworkMapSharer.Instance.localChar;
            string session = HostSession();
            if (!Guid.TryParseExact(session, "N", out _)) { Common.Notify("방장에게도 같은 Inventory Management 수정판이 필요합니다."); return; }
            if (!DepositService.Allowed(player)) { Common.Notify("본섬에서 상자 열기 권한이 있어야 합니다."); return; }
            var inv = Inventory.Instance; var slots = new List<int>(); var items = new List<int>(); var stacks = new List<int>();
            int hotbar = AccessTools.FieldRefAccess<Inventory, int>("slotPerRow")(inv);
            for (int i = hotbar; i < inv.invSlots.Length && slots.Count < 96; i++)
            {
                var slot = inv.invSlots[i];
                if (!slot || !DisplayRules.EligibleForAutoMove(i, hotbar, slot.slotUnlocked, SlotLocks.IsLocked(i)) || !Common.Item(slot.itemNo) || slot.isSelectedForGive()) continue;
                var item = slot.itemInSlot;
                if (!item || item.isDeed || item.isABackpack || item == inv.teleCaller || item == inv.copyPad || slot.stack < 0 || slot.stack > 100000000 || (item.checkIfStackable() && slot.stack == 0)) continue;
                slots.Add(i); items.Add(slot.itemNo); stacks.Add(slot.stack);
            }
            if (slots.Count == 0) { Common.Notify("단축바를 제외한 인벤토리에 보낼 아이템이 없습니다."); return; }
            var house = player.myInteract.InsideHouseDetails;
            var tx = new Transaction { Request = new TransferRequest { Id = Guid.NewGuid().ToString("N"), Session = session, HouseX = house?.xPos ?? -1, HouseY = house?.yPos ?? -1, Items = items.ToArray(), Stacks = stacks.ToArray() }, Slots = slots.ToArray(), Returned = new bool[slots.Count], Player = inv.playerName, Inventory = inv, Connection = NetworkClient.connection, SentAt = Time.realtimeSinceStartup, NextRetry = Time.realtimeSinceStartup + 1 };
            string directory = Path.Combine(Paths.ConfigPath, "LocalInventoryTransfers"); Directory.CreateDirectory(directory);
            tx.Journal = Path.Combine(directory, tx.Request.Id + ".txt");
            Journal(tx, "PREPARED: not sent"); // Refuse to remove items if durable diagnostics cannot be written.
            pending = tx;
            int removed = 0;
            try
            {
                for (; removed < slots.Count; removed++) inv.invSlots[slots[removed]].updateSlotContentsAndRefresh(-1, 0);
                Journal(tx, "SENT/ESCROW: awaiting server receipt; do not manually refund without verifying host stock");
            }
            catch
            {
                for (int i = 0; i < slots.Count; i++) inv.invSlots[slots[i]].updateSlotContentsAndRefresh(items[i], stacks[i]);
                pending = null; throw;
            }
            if (NetworkServer.active) Reply(new TransferReply { Id = tx.Request.Id, Accepted = DepositService.Apply(player, tx.Request) });
            else NetworkClient.Send(tx.Request);
        }
        private static void Receive(NetworkConnection connection, TransferRequest request)
        {
            if (connection == null || !connection.identity) return;
            if (receipts.TryGetValue(request.Id, out var stored))
            {
                if (stored.Owner == connection) connection.Send(stored.Reply);
                return;
            }
            if (lastRequests.TryGetValue(connection, out float last) && Time.realtimeSinceStartup - last < .5f) return;
            lastRequests[connection] = Time.realtimeSinceStartup;
            var result = new TransferReply { Id = request.Id, Accepted = new bool[request.Items.Length] };
            if (receipts.Count >= 10000) { connection.Send(result); return; }
            if (request.Session == Session && receipts.Count < 10000)
                result.Accepted = DepositService.Apply(connection.identity.GetComponent<CharMovement>(), request);
            receipts.Add(request.Id, new Receipt { Owner = connection, Reply = result });
            connection.Send(result);
        }
        private static void Reply(TransferReply reply)
        {
            var tx = pending;
            if (tx == null || reply.Id != tx.Request.Id || reply.Accepted.Length != tx.Slots.Length || tx.Accepted != null) return;
            tx.Accepted = reply.Accepted;
            Journal(tx, "RECEIPT: accepted=" + string.Join(",", reply.Accepted.Select(x => x ? "1" : "0")));
            Refund();
        }
        private static void Refund()
        {
            var tx = pending;
            if (tx?.Accepted == null || !Inventory.Instance || Inventory.Instance != tx.Inventory || Inventory.Instance.playerName != tx.Player ||
                NetworkClient.connection != tx.Connection || HostSession() != tx.Request.Session) return;
            var inv = Inventory.Instance;
            for (int i = 0; i < tx.Slots.Length; i++)
            {
                if (tx.Accepted[i] || tx.Returned[i]) continue;
                InventorySlot target = tx.Slots[i] < inv.invSlots.Length && inv.invSlots[tx.Slots[i]].itemNo == -1 ? inv.invSlots[tx.Slots[i]] : null;
                if (!target) target = inv.invSlots.FirstOrDefault(s => s && s.slotUnlocked && s.itemNo == -1);
                if (!target) continue; // Keep escrow, never drop or discard a failed transfer.
                target.updateSlotContentsAndRefresh(tx.Request.Items[i], tx.Request.Stacks[i]); tx.Returned[i] = true;
            }
            if (Enumerable.Range(0, tx.Slots.Length).All(i => tx.Accepted[i] || tx.Returned[i]))
            {
                Journal(tx, "COMPLETE: accepted=" + tx.Accepted.Count(x => x) + "; rejected items returned");
                pending = null; Common.Notify(tx.Accepted.Count(x => x) + "개 슬롯을 근처 상자로 보냈습니다. 보관하지 못한 아이템은 반환했습니다.");
            }
            else if (!tx.Warned) { tx.Warned = true; Common.Notify("돌려받을 아이템을 위해 인벤토리 빈칸을 확보하세요. 전송 기록은 보존되어 있습니다."); }
        }
        internal static void Tick()
        {
            if (pending == null) return;
            var tx = pending;
            if (tx.Accepted != null) { Refund(); return; }
            if (Time.realtimeSinceStartup - tx.SentAt > 10 && !tx.Warned)
            {
                tx.Warned = true; Common.Notify("상자 전송 응답이 지연됩니다. 게임을 종료하지 마세요. 연결이 끊겼다면 전송 기록으로 수량 확인이 필요합니다.");
            }
            if (Time.realtimeSinceStartup > tx.NextRetry && NetworkClient.isConnected && NetworkClient.connection == tx.Connection && HostSession() == tx.Request.Session)
            {
                tx.NextRetry = Time.realtimeSinceStartup + 2;
                if (!NetworkServer.active) NetworkClient.Send(tx.Request); // Same identifier: server replays receipt, never reapplies deposit.
            }
        }
        private static void Journal(Transaction tx, string state)
        {
            var lines = new List<string> { state, "Transaction=" + tx.Request.Id, "Session=" + tx.Request.Session, "Player=" + tx.Player, "No automatic replay or refund after restart. Compare host stock before recovery." };
            for (int i = 0; i < tx.Slots.Length; i++) lines.Add("slot=" + tx.Slots[i] + " item=" + tx.Request.Items[i] + " stack_or_metadata=" + tx.Request.Stacks[i]);
            File.AppendAllLines(tx.Journal, lines, Encoding.UTF8);
        }
        internal static void Test()
        {
            var m = new TransferRequest { Id = Guid.NewGuid().ToString("N"), Session = Guid.NewGuid().ToString("N"), HouseX = 12, HouseY = 34, Items = new[] { 2, 7 }, Stacks = new[] { 5, 90 } };
            var w = new NetworkWriter(); Writer<TransferRequest>.write(w, m); var read = Reader<TransferRequest>.read(new NetworkReader(w.ToArray()));
            if (read.Id != m.Id || read.Session != m.Session || read.HouseX != 12 || !read.Items.SequenceEqual(m.Items) || !read.Stacks.SequenceEqual(m.Stacks)) throw new Exception("Transfer round trip");
            w.Reset(); Writer<TransferReply>.write(w, new TransferReply { Id = m.Id, Accepted = new[] { true, false } });
            var reply = Reader<TransferReply>.read(new NetworkReader(w.ToArray()));
            if (!reply.Accepted[0] || reply.Accepted[1]) throw new Exception("Receipt round trip");
            var simulatedReceipts = new Dictionary<string, int>(); int applied = 0;
            for (int i = 0; i < 20; i++) if (!simulatedReceipts.ContainsKey(m.Id)) simulatedReceipts[m.Id] = ++applied;
            if (applied != 1) throw new Exception("Deduplication invariant");
        }
    }
    [HarmonyPatch(typeof(NetworkMapSharer), "OnStartServer")]
    internal static class TransferServerStart { private static void Postfix() => TransferProtocol.StartServer(); }
    [HarmonyPatch(typeof(NetworkMapSharer), "OnStartClient")]
    internal static class TransferClientStart { private static void Postfix() => TransferProtocol.StartClient(); }
    [HarmonyPatch(typeof(SteamLobby), "OnLobbyEntered")]
    internal static class TransferLobby { private static void Postfix() => TransferProtocol.Advertise(); }
    [HarmonyPatch(typeof(ContainerManager), "UserCode_RpcRefreshOpenedChest")]
    internal static class TransferCacheScope
    {
        internal static int Depth;
        private static void Prefix(ref bool __state)
        {
            __state = RealWorldTimeLight.time && !RealWorldTimeLight.time.underGround && !RealWorldTimeLight.time.offIsland && Guid.TryParseExact(TransferProtocol.HostSession(), "N", out _);
            if (__state) Depth++;
        }
        private static Exception Finalizer(bool __state, Exception __exception) { if (__state) Depth--; return __exception; }
    }
    [HarmonyPatch(typeof(ContainerManager), "checkIfChestIsInsideAndInThisHouse")]
    internal static class TransferExactChest
    {
        private static bool Prefix(HouseDetails house, Chest checkChest, ref bool __result)
        {
            if (TransferCacheScope.Depth == 0) return true;
            __result = checkChest != null && DepositRules.Context(checkChest.insideX, checkChest.insideY, house?.xPos ?? -1, house?.yPos ?? -1);
            return false;
        }
    }
}
