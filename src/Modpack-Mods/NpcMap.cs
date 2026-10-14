using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
#if !LEGACY
using BepInEx.Unity.Mono;
using KeyboardShortcut = BepInEx.Unity.Mono.Configuration.KeyboardShortcut;
#endif
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocalDinkumMods
{
    internal struct NpcMapRequest : NetworkMessage { public string Session; }
    internal struct NpcMapReply : NetworkMessage { public string Session; public NpcPoint[] Points; }
    internal struct NpcPoint { public int Id; public float X, Z; public bool Indoors; }

    [BepInPlugin("LocalDinkum.NpcMap", "NPC Map - Multiplayer", "1.0.0")]
    public sealed class NpcMap : BaseUnityPlugin
    {
        private const string LobbyKey = "LocalNpcMapV1";
        private static readonly AccessTools.FieldRef<NPCMapAgent, NPCMapAgent.npcDesire> Desire = AccessTools.FieldRefAccess<NPCMapAgent, NPCMapAgent.npcDesire>("desire");
        private static readonly AccessTools.FieldRef<RenderMap, float> Zoom = AccessTools.FieldRefAccess<RenderMap, float>("desiredScale");
        private ConfigEntry<bool> shown;
        private ConfigEntry<KeyboardShortcut> toggle;
        private string serverSession = "", cacheSession = "";
        private NetworkConnection clientConnection;
        private readonly Dictionary<NetworkConnection, float> requestTimes = new Dictionary<NetworkConnection, float>();
        private NpcPoint[] points = new NpcPoint[0];
        private float received = -100, nextRequest, nextNetwork, nextRender, nextWarning;
        private RectTransform layer;
        private readonly Dictionary<int, RectTransform> icons = new Dictionary<int, RectTransform>();
        private readonly Dictionary<int, TextMeshProUGUI> labels = new Dictionary<int, TextMeshProUGUI>();
        private bool serverReady;
        private bool warnedHost;
        private static bool MainIsland => RealWorldTimeLight.time && !RealWorldTimeLight.time.underGround && !RealWorldTimeLight.time.offIsland;
        private void Awake()
        {
            shown = Config.Bind("Display", "Enabled", true, "NPC icons on the full main-island map. Host must also install this mod.");
            toggle = Config.Bind("Keybinds", "ToggleNpcMap", new KeyboardShortcut(KeyCode.F9), "Toggle NPC overlay while the full map is open.");
            SerializeSetup();
            if (Common.TestRequested) TestWire();
            Logger.LogInfo("NPC Map 1.0.0 loaded. Read-only host snapshots, 2-second refresh, 6-second expiry. No networked map objects.");
        }
        private void Update()
        {
            try
            {
                var map = RenderMap.Instance;
                bool open = map && map.mapOpen;
                if (open && !(ChatBox.chat && ChatBox.chat.chatOpen) && toggle.Value.IsDown())
                { shown.Value = !shown.Value; Common.Notify(shown.Value ? "NPC 지도 표시 켜짐" : "NPC 지도 표시 꺼짐"); }
                if (Time.realtimeSinceStartup >= nextNetwork)
                {
                    nextNetwork = Time.realtimeSinceStartup + .25f;
                    NetworkTick(open && shown.Value && MainIsland);
                }
                if (layer) layer.gameObject.SetActive(open && shown.Value && MainIsland && NetworkClient.isConnected && Time.realtimeSinceStartup - received < 6);
                if (open && shown.Value && MainIsland && NetworkClient.isConnected && Time.realtimeSinceStartup >= nextRender)
                { nextRender = Time.realtimeSinceStartup + .15f; Draw(map); }
            }
            catch (Exception e)
            {
                if (layer) layer.gameObject.SetActive(false);
                if (Time.realtimeSinceStartup >= nextWarning) { nextWarning = Time.realtimeSinceStartup + 10; Logger.LogWarning("NPC overlay skipped: " + e); }
            }
        }
        private void NetworkTick(bool wantMap)
        {
            if (NetworkServer.active && !serverReady)
            {
                NetworkServer.RegisterHandler<NpcMapRequest>(ReceiveRequest, true);
                serverSession = Guid.NewGuid().ToString("N"); serverReady = true;
            }
            else if (!NetworkServer.active && serverReady) { serverReady = false; serverSession = ""; requestTimes.Clear(); }
            if (serverReady && SteamManager.Initialized && SteamLobby.Instance && SteamLobby.Instance.currentLobby != CSteamID.Nil && SteamMatchmaking.GetLobbyOwner(SteamLobby.Instance.currentLobby) == SteamUser.GetSteamID())
                if (SteamMatchmaking.GetLobbyData(SteamLobby.Instance.currentLobby, LobbyKey) != serverSession)
                    SteamMatchmaking.SetLobbyData(SteamLobby.Instance.currentLobby, LobbyKey, serverSession);
            if (NetworkClient.connection != clientConnection)
            {
                clientConnection = NetworkClient.connection; points = new NpcPoint[0]; received = -100; nextRequest = 0; warnedHost = false;
                if (clientConnection != null) NetworkClient.RegisterHandler<NpcMapReply>(ReceiveReply, true);
            }
            if (!wantMap || !NetworkClient.isConnected || !NetworkClient.ready || Time.realtimeSinceStartup < nextRequest) return;
            nextRequest = Time.realtimeSinceStartup + 2;
            string session = HostSession();
            if (session.Length != 32 || !Guid.TryParseExact(session, "N", out _))
            {
                received = -100;
                if (!warnedHost) { warnedHost = true; Common.Notify("NPC 전체 위치 표시에는 방장도 NPC Map 모드를 설치해야 합니다."); }
                return;
            }
            cacheSession = session;
            if (NetworkServer.active) { points = Snapshot(); received = Time.realtimeSinceStartup; }
            else NetworkClient.Send(new NpcMapRequest { Session = session });
        }
        private string HostSession() => NetworkServer.active ? serverSession :
            SteamManager.Initialized && SteamLobby.Instance && SteamLobby.Instance.currentLobby != CSteamID.Nil ? SteamMatchmaking.GetLobbyData(SteamLobby.Instance.currentLobby, LobbyKey) : "";
        private void ReceiveRequest(NetworkConnection conn, NpcMapRequest msg)
        {
            if (conn == null || !conn.identity || !serverReady || msg.Session != serverSession) return;
            float now = Time.realtimeSinceStartup;
            if (requestTimes.TryGetValue(conn, out float last) && now - last < 1.5f) return;
            foreach (var key in requestTimes.Keys.Where(k => k == null || !k.identity).ToArray()) requestTimes.Remove(key);
            requestTimes[conn] = now;
            try { conn.Send(new NpcMapReply { Session = serverSession, Points = Snapshot() }); }
            catch (Exception e) { Logger.LogWarning("NPC snapshot unavailable: " + e.Message); }
        }
        private void ReceiveReply(NpcMapReply msg)
        {
            // No UI work inside a network callback.
            if (msg.Session != cacheSession || msg.Session != HostSession()) return;
            points = msg.Points; received = Time.realtimeSinceStartup;
        }
        private static NpcPoint[] Snapshot()
        {
            if (!MainIsland || !NPCManager.manage || !WorldManager.Instance) return new NpcPoint[0];
            var result = new List<NpcPoint>(); var seen = new HashSet<int>();
            foreach (var agent in NPCManager.manage.npcsOnMap)
            {
                if (agent == null || agent.npcId < 0 || agent.npcId >= NPCManager.manage.NPCDetails.Length || agent.currentWorldArea != WorldArea.MAIN_ISLAND || !seen.Add(agent.npcId)) continue;
                var door = Desire(agent)?.lastWalkedInto;
                Vector3 p = door && door.outside ? door.outside.position : agent.activeNPC ? agent.activeNPC.transform.position : agent.currentPosition;
                if (!ValidCoordinate(p.x) || !ValidCoordinate(p.z) || p.y < -50 || !WorldManager.Instance.isPositionOnMap(p)) continue;
                result.Add(new NpcPoint { Id = agent.npcId, X = p.x, Z = p.z, Indoors = door });
                if (result.Count == 128) break;
            }
            return result.ToArray();
        }
        private void Draw(RenderMap map)
        {
            if (!map.mapParent || !NPCManager.manage || map.mapScale <= 0) return;
            if (!layer || layer.parent != map.mapParent)
            {
                if (layer) Destroy(layer.gameObject);
                icons.Clear(); labels.Clear();
                var go = new GameObject("LocalNpcOverlay", typeof(RectTransform)); layer = go.GetComponent<RectTransform>(); layer.SetParent(map.mapParent, false);
                layer.anchorMin = layer.anchorMax = new Vector2(.5f, .5f); layer.sizeDelta = Vector2.zero; layer.localPosition = Vector3.zero;
            }
            bool fresh = Time.realtimeSinceStartup - received < 6;
            layer.gameObject.SetActive(fresh);
            if (!fresh) return;
            var keep = new HashSet<int>();
            foreach (var p in points)
            {
                if (p.Id < 0 || p.Id >= NPCManager.manage.NPCDetails.Length || !NPCManager.manage.NPCDetails[p.Id]) continue;
                keep.Add(p.Id);
                if (!icons.TryGetValue(p.Id, out var icon) || !icon)
                {
                    var go = new GameObject("NPC-" + p.Id, typeof(RectTransform), typeof(Image)); icon = go.GetComponent<RectTransform>(); icon.SetParent(layer, false); icon.sizeDelta = new Vector2(22, 22);
                    var img = go.GetComponent<Image>(); img.raycastTarget = false; img.sprite = NPCManager.manage.NPCDetails[p.Id].GetNPCSprite(p.Id);
                    var name = new GameObject("Name", typeof(RectTransform), typeof(TextMeshProUGUI)); name.transform.SetParent(icon, false);
                    var label = name.GetComponent<TextMeshProUGUI>(); label.fontSize = 10; label.color = Color.white; label.outlineWidth = .2f; label.raycastTarget = false; label.alignment = TextAlignmentOptions.Top;
                    if (Inventory.Instance && Inventory.Instance.InvDescriptionText) label.font = Inventory.Instance.InvDescriptionText.font;
                    label.rectTransform.sizeDelta = new Vector2(110, 28); label.rectTransform.anchoredPosition = new Vector2(0, -22);
                    icons[p.Id] = icon; labels[p.Id] = label;
                }
                icon.gameObject.SetActive(true);
                icon.localPosition = new Vector3(p.X / 2 / map.mapScale, p.Z / 2 / map.mapScale, 0);
                icon.localScale = Vector3.one / Mathf.Max(.1f, Zoom(map));
                labels[p.Id].text = NPCManager.manage.NPCDetails[p.Id].GetNPCName() + (p.Indoors ? " (실내)" : "");
            }
            foreach (var pair in icons) if (pair.Value && !keep.Contains(pair.Key)) pair.Value.gameObject.SetActive(false);
        }
        private static bool ValidCoordinate(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0 && v <= 100000;
        private static void SerializeSetup()
        {
            Writer<NpcMapRequest>.write = (w, m) => w.WriteString(m.Session);
            Reader<NpcMapRequest>.read = r => new NpcMapRequest { Session = r.ReadString() };
            Writer<NpcMapReply>.write = (w, m) => { w.WriteString(m.Session); w.WriteInt(m.Points.Length); foreach (var p in m.Points) { w.WriteInt(p.Id); w.WriteFloat(p.X); w.WriteFloat(p.Z); w.WriteBool(p.Indoors); } };
            Reader<NpcMapReply>.read = r =>
            {
                string session = r.ReadString(); int n = r.ReadInt();
                if (session == null || session.Length != 32 || n < 0 || n > 128 || n > r.Remaining / 13) throw new FormatException("NPC snapshot bounds");
                var values = new NpcPoint[n]; var ids = new HashSet<int>();
                for (int i = 0; i < n; i++)
                {
                    values[i] = new NpcPoint { Id = r.ReadInt(), X = r.ReadFloat(), Z = r.ReadFloat(), Indoors = r.ReadBool() };
                    if (values[i].Id < 0 || values[i].Id > 1024 || !ids.Add(values[i].Id) || !ValidCoordinate(values[i].X) || !ValidCoordinate(values[i].Z)) throw new FormatException("NPC snapshot entry");
                }
                return new NpcMapReply { Session = session, Points = values };
            };
        }
        private static void TestWire()
        {
            string id = Guid.NewGuid().ToString("N"); var w = new NetworkWriter();
            Writer<NpcMapReply>.write(w, new NpcMapReply { Session = id, Points = new[] { new NpcPoint { Id = 2, X = 100, Z = 200, Indoors = true } } });
            var reply = Reader<NpcMapReply>.read(new NetworkReader(w.ToArray()));
            if (reply.Session != id || reply.Points.Length != 1 || reply.Points[0].Z != 200 || !reply.Points[0].Indoors) throw new Exception("NPC wire roundtrip failed");
        }
        private void OnDestroy() { if (layer) Destroy(layer.gameObject); }
    }
}
