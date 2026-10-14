using System;
using BepInEx;
#if !LEGACY
using BepInEx.Unity.Mono;
using KeyboardShortcut = BepInEx.Unity.Mono.Configuration.KeyboardShortcut;
#endif
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LocalDinkumMods
{
    [BepInPlugin(Id, "Inventory Management - Individual Item Locks", "0.12.0")]
    public sealed class InventoryManagement : BaseUnityPlugin
    {
        internal const string Id = "dev.TinyResort.InventoryManagement";
        internal static ManualLogSource Log;
        internal static ConfigEntry<int> Range;
        private ConfigEntry<KeyboardShortcut> sortKey, depositKey, lockKey;
        private void Awake()
        {
            enabled = false;
            Log = Logger;
            sortKey = Config.Bind("Keybinds", "SortInventory", new KeyboardShortcut(KeyCode.F6), "Sort unlocked slots. Supports modifiers, e.g. F6 + LeftControl.");
            depositKey = Config.Bind("Keybinds", "SendToChests", new KeyboardShortcut(KeyCode.F7), "Send matching items. Host and clients need this local version.");
            lockKey = Config.Bind("Keybinds", "ToggleSlotLock", new KeyboardShortcut(KeyCode.F8), "Hover an item to lock this individual copy/stack, not its pocket or item type. Manual pocket/drag moves follow the item. Key name preserved for compatibility.");
            SlotLocks.Initialize(Config);
            Range = Config.Bind("General", "RangeTiles", 30, new ConfigDescription("Host-controlled nearby chest range in tiles.", new AcceptableValueRange<int>(1, 100)));
            DepositRules.Test();
            TransferProtocol.Setup();
            var harmony = new Harmony(Id);
            try { harmony.PatchAll(typeof(InventoryManagement).Assembly); }
            catch (Exception e)
            {
                Logger.LogError("Inventory Management disabled: patch binding failed. " + e);
                try { harmony.UnpatchSelf(); } catch (Exception cleanup) { Logger.LogError("Inventory patch cleanup failed: " + cleanup); }
                return;
            }
            enabled = true;
            Logger.LogInfo("Inventory Management 0.12.0 loaded. Individual item locks; native-save-bound metadata; custom shortcuts; host + clients for deposits. Old position locks are not re-applied.");
            if (Common.TestRequested)
            {
                TransferProtocol.Test();
                Common.CheckPatches(Logger, Id, AccessTools.Method(typeof(NetworkMapSharer), "OnStartServer"), AccessTools.Method(typeof(NetworkMapSharer), "OnStartClient"));
                Logger.LogInfo("MODPACK SELF-TEST PASS: 10000 deposit plans, metadata preservation, transfer serialization and receipt deduplication.");
            }
        }
        private void Update()
        {
            try
            {
                TransferProtocol.Tick();
                SlotLocks.Refresh();
                if (!Ready()) return;
                if (lockKey.Value.IsDown()) { SlotLocks.ToggleHovered(); return; }
                if (sortKey.Value.IsDown() && !TransferProtocol.Pending) { Inventory.Instance.SortAllSlots(); return; }
                if (depositKey.Value.IsDown()) TransferProtocol.Begin();
            }
            catch (Exception e) { Logger.LogError(e); Common.Notify("상자 전송을 중단했습니다. 전송 기록과 LogOutput.log를 확인하세요."); }
        }
        internal static bool Ready()
        {
            return !ItemLockBridge.ExternalModalOpen && Inventory.Instance && Inventory.Instance.invOpen && NetworkMapSharer.Instance && NetworkMapSharer.Instance.localChar &&
                !(ChatBox.chat && ChatBox.chat.chatOpen) && !(ChestWindow.chests && ChestWindow.chests.chestWindowOpen) &&
                !(CraftingManager.manage && CraftingManager.manage.craftMenuOpen) &&
                Inventory.Instance.dragSlot && Inventory.Instance.dragSlot.itemNo == -1 &&
                Inventory.Instance.weaponSlot && Inventory.Instance.weaponSlot.itemNo == -1;
        }
    }
}
