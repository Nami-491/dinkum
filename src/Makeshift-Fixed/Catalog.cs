using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;

namespace MakeshiftFixed
{
    internal struct CatalogRequest : NetworkMessage { public int Sequence; }
    internal struct CatalogReply : NetworkMessage
    {
        public int Sequence, HouseX, HouseY;
        public Dictionary<int, int> Counts;
    }

    internal static class Catalog
    {
        private const int MaxEntries = 8192;
        private static readonly Dictionary<NetworkConnection, float> Participants = new Dictionary<NetworkConnection, float>();
        private static Dictionary<int, int> cached = new Dictionary<int, int>();
        private static int sequence, houseX = int.MinValue, houseY = int.MinValue;
        private static float receivedAt = -100, hostReadAt = -100;
        internal static void SetupSerialization()
        {
            Writer<CatalogRequest>.write = (w, m) => w.WriteInt(m.Sequence);
            Reader<CatalogRequest>.read = r => new CatalogRequest { Sequence = r.ReadInt() };
            Writer<CatalogReply>.write = (w, m) =>
            {
                w.WriteInt(m.Sequence); w.WriteInt(m.HouseX); w.WriteInt(m.HouseY);
                int count = Math.Min(m.Counts?.Count ?? 0, MaxEntries);
                w.WriteInt(count);
                if (m.Counts != null)
                    foreach (var pair in m.Counts.Take(count)) { w.WriteInt(pair.Key); w.WriteInt(pair.Value); }
            };
            Reader<CatalogReply>.read = r =>
            {
                var m = new CatalogReply { Sequence = r.ReadInt(), HouseX = r.ReadInt(), HouseY = r.ReadInt(), Counts = new Dictionary<int, int>() };
                int count = r.ReadInt();
                if (count < 0 || count > MaxEntries || count > r.Remaining / 8) throw new FormatException("Invalid Makeshift catalog length");
                for (int i = 0; i < count; i++)
                {
                    int id = r.ReadInt(), amount = r.ReadInt();
                    if (id < 0 || amount < 0 || amount > 100000000 || m.Counts.ContainsKey(id)) throw new FormatException("Invalid Makeshift catalog entry");
                    m.Counts.Add(id, amount);
                }
                return m;
            };
        }
        internal static void StartServer()
        {
            Participants.Clear();
            NetworkServer.UnregisterHandler<CatalogRequest>();
            NetworkServer.RegisterHandler<CatalogRequest>(OnRequest, true);
            Plugin.Log.LogInfo("Makeshift server catalog ready (authenticated, rate limited).");
        }
        internal static void StartClient()
        {
            ClearClient();
            NetworkClient.UnregisterHandler<CatalogReply>();
            NetworkClient.RegisterHandler<CatalogReply>(OnReply, true);
        }
        private static void OnRequest(NetworkConnection connection, CatalogRequest request)
        {
            if (connection == null || !connection.identity) return;
            if (Participants.TryGetValue(connection, out float last) && Time.realtimeSinceStartup - last < 1f) return;
            var player = connection.identity.GetComponent<CharPickUp>();
            if (!Plugin.Allowed(player)) return;
            Participants[connection] = Time.realtimeSinceStartup;
            var inside = player.myInteract.InsideHouseDetails;
            connection.Send(new CatalogReply { Sequence = request.Sequence, HouseX = inside?.xPos ?? -1, HouseY = inside?.yPos ?? -1, Counts = Storage.Counts(player) });
        }
        private static void OnReply(CatalogReply reply)
        {
            if (reply.Sequence != sequence || !Menu.Active || !Plugin.Allowed(Plugin.Local)) return;
            var inside = Plugin.Local.myInteract.InsideHouseDetails;
            if (reply.HouseX != (inside?.xPos ?? -1) || reply.HouseY != (inside?.yPos ?? -1)) return;
            cached = reply.Counts;
            houseX = reply.HouseX; houseY = reply.HouseY; receivedAt = Time.realtimeSinceStartup;
            if (CraftingManager.manage && CraftingManager.manage.craftMenuOpen) CraftingManager.manage.RefreshIfCraftingFromChest();
        }
        internal static void Refresh()
        {
            if (!Plugin.Allowed(Plugin.Local) || !Plugin.HostSupports()) return;
            if (NetworkServer.active) { ReadHost(); return; }
            if (NetworkClient.isConnected) NetworkClient.Send(new CatalogRequest { Sequence = ++sequence });
        }
        private static void ReadHost()
        {
            var inside = Plugin.Local.myInteract.InsideHouseDetails;
            houseX = inside?.xPos ?? -1; houseY = inside?.yPos ?? -1;
            cached = Storage.Counts(Plugin.Local);
            hostReadAt = receivedAt = Time.realtimeSinceStartup;
        }
        internal static int Count(int item)
        {
            var local = Plugin.Local;
            if (!Plugin.Allowed(local)) return 0;
            var inside = local.myInteract.InsideHouseDetails;
            bool contextChanged = houseX != (inside?.xPos ?? -1) || houseY != (inside?.yPos ?? -1);
            if (NetworkServer.active && (contextChanged || Time.realtimeSinceStartup - hostReadAt > .25f)) ReadHost();
            else if (contextChanged || Time.realtimeSinceStartup - receivedAt > 5f) return 0;
            return cached.TryGetValue(item, out int count) ? count : 0;
        }
        internal static bool IsParticipant(CharPickUp player)
        {
            if (!NetworkServer.active) return false;
            if (player == Plugin.Local) return Menu.Active;
            return player.connectionToClient != null && Participants.ContainsKey(player.connectionToClient);
        }
        internal static void ClearClient() { cached.Clear(); houseX = houseY = int.MinValue; receivedAt = hostReadAt = -100; sequence++; }
        internal static void InvalidateHost() { hostReadAt = -100; }

        internal static void TestSerialization()
        {
            var w = new NetworkWriter();
            var expected = new CatalogReply { Sequence = 7, HouseX = 10, HouseY = 20, Counts = new Dictionary<int, int> { { 2, 5 }, { 7, 11 } } };
            Writer<CatalogReply>.write(w, expected);
            var actual = Reader<CatalogReply>.read(new NetworkReader(w.ToArray()));
            StorageRules.Require(actual.Sequence == 7 && actual.HouseX == 10 && actual.HouseY == 20 && actual.Counts[2] == 5 && actual.Counts[7] == 11, "catalog round trip");
            w.Reset(); w.WriteInt(1); w.WriteInt(-1); w.WriteInt(-1); w.WriteInt(int.MaxValue);
            bool rejected = false;
            try { Reader<CatalogReply>.read(new NetworkReader(w.ToArray())); } catch (FormatException) { rejected = true; }
            StorageRules.Require(rejected, "malformed catalog rejected before allocation");
        }
    }
}
