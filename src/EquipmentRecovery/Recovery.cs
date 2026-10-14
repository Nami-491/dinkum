using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
#if !LEGACY
using BepInEx.Unity.Mono;
using KeyboardShortcut = BepInEx.Unity.Mono.Configuration.KeyboardShortcut;
#endif
using Mirror;
using UnityEngine;

namespace LocalDinkumRecovery
{
    [BepInPlugin("LocalDinkum.EquipmentRecovery", "Enhanced Equipment Recovery", "1.2.0")]
    [BepInDependency("LocalDinkum.EquipmentEnhancement", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Recovery : BaseUnityPlugin
    {
        private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
        private ConfigEntry<KeyboardShortcut> scanKey, nextKey, recallKey, closeKey;
        private FieldInfo entriesField, fingerprintField, levelField;
        private readonly List<DroppedItem> results = new List<DroppedItem>();
        private int index;
        private bool visible;
        private string status = "";
        private uint armed;
        private float until;
        private int armedItem, armedStack;
        private Vector3 armedPosition;
        private NetworkIdentity session;
        private float scanAfter;

        private void Awake()
        {
            try
            {
                // Fail closed against untested game updates, before any state changes.
                using (var sha = SHA256.Create())
                using (var input = File.OpenRead(typeof(Inventory).Assembly.Location))
                    if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "") != "5106DA5D624AAEDABB16159A9BDAA9AB3CE4D09EB992BD4759D881DD9D9BEA24")
                        throw new InvalidOperationException("Unsupported game assembly. Recovery disabled.");
                var asm = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "LocalDinkum.EquipmentEnhancement");
                var registry = asm.GetType("LocalDinkumMods.EnhancementRegistry", true);
                entriesField = registry.GetField("Entries", Hidden);
                fingerprintField = registry.GetField("Fingerprint", Hidden);
                levelField = registry.GetNestedType("Entry", BindingFlags.NonPublic).GetField("Level", BindingFlags.Instance | BindingFlags.NonPublic);
                if (entriesField == null || fingerprintField == null || levelField == null) throw new MissingFieldException("Enhancement registry is incompatible.");
                scanKey = Config.Bind("Keybinds", "Scan", new KeyboardShortcut(KeyCode.F5), "Scan live enhanced equipment drops. Read-only.");
                nextKey = Config.Bind("Keybinds", "Next", new KeyboardShortcut(KeyCode.F5, KeyCode.LeftShift), "Select next search result.");
                recallKey = Config.Bind("Keybinds", "Recall", new KeyboardShortcut(KeyCode.F5, KeyCode.LeftControl), "Press twice within 8 seconds to move selected drop to your feet. Host only.");
                closeKey = Config.Bind("Keybinds", "Close", new KeyboardShortcut(KeyCode.F5, KeyCode.LeftAlt), "Hide results and cancel confirmation.");
                Logger.LogInfo("Recovery ready: host-only, existing enhanced drops only. No automatic recovery or save rewrite.");
            }
            catch (Exception e) { enabled = false; Logger.LogError(e); }
        }
        private static CharPickUp Player => NetworkMapSharer.Instance && NetworkMapSharer.Instance.localChar ? NetworkMapSharer.Instance.localChar.myPickUp : null;
        private static bool Ready => NetworkServer.active && NetworkClient.isConnected && NetworkClient.ready && Player && Player.isServer &&
            SaveAndLoad.gameHasCompletelyLoaded && Inventory.Instance && WorldManager.Instance && RealWorldTimeLight.time &&
            !(SaveLoad.saveOrLoad && SaveLoad.saveOrLoad.isSaving) && !(ChatBox.chat && ChatBox.chat.chatOpen) &&
            !Inventory.Instance.invOpen && !Inventory.Instance.menuOpen &&
            !(CraftingManager.manage && CraftingManager.manage.craftMenuOpen) && !(ChestWindow.chests && ChestWindow.chests.chestWindowOpen);
        private int Level(int id)
        {
            var entries = entriesField.GetValue(null) as IDictionary;
            if (string.IsNullOrEmpty(fingerprintField.GetValue(null) as string) || entries == null || !entries.Contains(id)) return 0;
            return (int)levelField.GetValue(entries[id]);
        }
        private bool Live(DroppedItem d)
        {
            if (!d || !d.isServer || d.netId == 0 || d.HasBeenPickedUp() || !NetworkServer.spawned.TryGetValue(d.netId, out var identity) || identity != d.netIdentity) return false;
            int id = d.myItemId;
            return Inventory.Instance && id >= 0 && id < Inventory.Instance.allItems.Length && Inventory.Instance.allItems[id] &&
                RecoveryRules.ValidGear(Level(id), d.stackAmount, Inventory.Instance.allItems[id].fuelMax);
        }
        private static bool SameArea(DroppedItem d)
        {
            var room = Player.myInteract.InsideHouseDetails;
            int map = RealWorldTimeLight.time.underGround ? 1 : RealWorldTimeLight.time.offIsland ? 2 : 0;
            return RecoveryRules.SameArea((int)d.droppedOnMap, map, d.inside != null, d.inside?.xPos ?? -1, d.inside?.yPos ?? -1,
                room != null, room?.xPos ?? -1, room?.yPos ?? -1);
        }
        private void Update()
        {
            try
            {
                if (!Ready) { visible = false; armed = 0; session = null; results.Clear(); return; }
                if (session != Player.netIdentity) { results.Clear(); armed = 0; visible = false; session = Player.netIdentity; }
                if (closeKey.Value.IsDown()) { visible = false; armed = 0; return; }
                if (recallKey.Value.IsDown()) { Recall(); return; }
                if (nextKey.Value.IsDown()) { armed = 0; if (results.Count > 0) { index = (index + 1) % results.Count; visible = true; status = "선택 변경. 본인 장비인지 확인하세요."; } return; }
                if (scanKey.Value.IsDown() && Time.realtimeSinceStartup >= scanAfter) Scan();
                if (armed != 0 && Time.realtimeSinceStartup >= until) { armed = 0; status = "회수 확인 시간이 만료됐습니다."; }
            }
            catch (Exception e) { armed = 0; status = "작업 중단: " + e.Message; Logger.LogError(e); }
        }
        private void Scan()
        {
            scanAfter = Time.realtimeSinceStartup + 1;
            armed = 0; results.Clear(); index = 0; visible = true;
            foreach (var identity in NetworkServer.spawned.Values.ToArray())
            {
                if (!identity) continue;
                var drop = identity.GetComponent<DroppedItem>();
                if (Live(drop)) results.Add(drop);
            }
            results.Sort((a,b) => a.netId.CompareTo(b.netId));
            status = results.Count == 0 ? "현재 로드된 드롭에 강화 장비가 없습니다. 소멸·미로드·상자/매장 아이템은 찾지 못합니다." : "강화 드롭 " + results.Count + "개. 다른 사람 장비도 포함될 수 있습니다.";
            Logger.LogInfo("Recovery scan: " + results.Count + " live enhanced drops. No objects changed.");
        }
        private void Recall()
        {
            visible = true;
            if (results.Count == 0) { status = "먼저 검색 키를 누르세요."; return; }
            var d = results[index];
            if (!Live(d)) { armed = 0; status = "아이템이 변경됐거나 이미 회수됐습니다. 다시 검색하세요."; return; }
            if (!SameArea(d) || !d.gameObject.activeInHierarchy) { armed = 0; status = "해당 월드/건물 안으로 이동한 뒤 다시 검색하세요. 구역 간 이동은 차단합니다."; return; }
            if (!Player.myEquip || !Player.myEquip.myPermissions.CheckIfCanPickUp()) { armed = 0; status = "아이템 줍기 권한이 없습니다."; return; }
            Vector3 destination = Player.transform.position;
            if (!Finite(destination)) throw new InvalidOperationException("Invalid destination");
            // Outdoor retrieval must be on dry ground, not on boats/over water.
            if (d.inside == null)
            {
                int x = Mathf.RoundToInt(destination.x / 2f), y = Mathf.RoundToInt(destination.z / 2f);
                var world = WorldManager.Instance;
                if (!world.isPositionOnMap(x, y) || world.waterMap[x,y] || Math.Abs(destination.y - world.heightMap[x,y]) > 1.5f)
                { armed = 0; status = "물·탈것을 벗어나 평평한 육지에 서 주세요."; return; }
                destination.y = world.heightMap[x,y];
            }
            if (!RecoveryRules.Confirmed(d.netId, armed, Time.realtimeSinceStartup, until,
                d.myItemId == armedItem && d.stackAmount == armedStack && d.desiredPos == armedPosition))
            {
                armed = d.netId; armedItem = d.myItemId; armedStack = d.stackAmount; armedPosition = d.desiredPos;
                until = Time.realtimeSinceStartup + 8;
                status = "이 장비를 발앞으로 이동할까요? 8초 안에 회수 키를 다시 누르세요. 다른 키로 선택 변경/닫기 시 취소.";
                return;
            }
            armed = 0;
            // Log BEFORE any mutation. Logging failure stops the operation.
            string log = Path.Combine(Paths.ConfigPath, "LocalEquipmentRecovery");
            Directory.CreateDirectory(log);
            File.AppendAllText(Path.Combine(log, "recovery.log"), DateTime.UtcNow.ToString("O") + " netId=" + d.netId + " item=" + d.myItemId + " condition=" + d.stackAmount + " map=" + d.droppedOnMap + " from=" + Position(d.desiredPos) + " to=" + Position(destination) + Environment.NewLine);
            // Native SyncVar hook animates/moves the SAME object on host and clients.
            // No spawn, destroy, inventory grant, stack change, or direct save edits.
            d.NetworkdesiredPos = destination;
            status = "선택한 장비를 발앞으로 이동했습니다. 직접 주워 주세요. 정상 저장 전 종료하지 마세요.";
            Logger.LogInfo("Recovery moved existing drop netId=" + d.netId);
        }
        private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        private static string Position(Vector3 v) => v.x.ToString("R", CultureInfo.InvariantCulture) + "," + v.y.ToString("R", CultureInfo.InvariantCulture) + "," + v.z.ToString("R", CultureInfo.InvariantCulture);
        private void OnGUI()
        {
            if (!enabled || !visible || !Ready) return;
            var saved = GUI.skin.font;
            try
            {
                if (Inventory.Instance.InvDescriptionText && Inventory.Instance.InvDescriptionText.font && Inventory.Instance.InvDescriptionText.font.sourceFontFile)
                    GUI.skin.font = Inventory.Instance.InvDescriptionText.font.sourceFontFile;
                string text = "강화 장비 찾기 · 방장 전용\n" + scanKey.Value + " 검색 / " + nextKey.Value + " 다음 / " + recallKey.Value + " 회수(2회) / " + closeKey.Value + " 닫기\n";
                if (results.Count > 0)
                {
                    var d = results[index];
                    if (Live(d)) text += (index + 1) + "/" + results.Count + "  " + Inventory.Instance.allItems[d.myItemId].getInvItemName() + " · 내구도 " + d.stackAmount + "\n" +
                        d.droppedOnMap + (d.inside != null ? " 실내(" + d.inside.xPos + "," + d.inside.yPos + ")" : " 야외") + " · 타일(" + Mathf.RoundToInt(d.desiredPos.x / 2f) + "," + Mathf.RoundToInt(d.desiredPos.z / 2f) + ") · " + (SameArea(d) ? "현재 구역" : "다른 구역") + "\n";
                    else text += "선택 항목이 없어졌습니다. 다시 검색하세요.\n";
                }
                var rect = new Rect(20, 120, Mathf.Min(780, Screen.width - 40), 210);
                GUI.Box(rect, "");
                GUI.Label(new Rect(rect.x + 12, rect.y + 10, rect.width - 24, rect.height - 20), text + status, new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 16 });
                // Read-only overlay: never captures mouse or changes cursor state.
            }
            finally { GUI.skin.font = saved; }
        }
    }
}
