using System;
using System.Collections.Generic;
using BepInEx;
#if !LEGACY
using BepInEx.Unity.Mono;
#endif
using HarmonyLib;
using UnityEngine;

namespace LocalDinkumMods
{
    [BepInPlugin(Id, "Fish In A Barrel - Local Fix", "1.0.2")]
    public sealed class FishInABarrel : BaseUnityPlugin
    {
        internal const string Id = "Snaitf.Dinkum.FishInABarrel";
        internal static bool Stack, Container;
        private Inventory initialized;
        private void Awake()
        {
            Stack = Config.Bind("Options", "canStack", true, "Stack fish, bugs and critters. Split stacks before disabling the mod.").Value;
            Container = Config.Bind("Options", "canContainer", true, "Allow creatures in normal storage; preserve other vanilla restrictions.").Value;
            new Harmony(Id).PatchAll(typeof(FishInABarrel).Assembly);
            Logger.LogInfo("Fish In A Barrel local 1.0.2 loaded. Split creature stacks before removing this mod.");
            if (Common.TestRequested) Common.CheckPatches(Logger, Id, AccessTools.Method(typeof(ChestWindow), "lockBugsAndFishFromChest"),
                AccessTools.Method(typeof(GiveNPC), "donateItemToMuseum"), AccessTools.Method(typeof(GiveNPC), "getDollarValueOfGiveSlots"), AccessTools.Method(typeof(InputMaster), "drop"));
        }
        private void Update()
        {
            if (!Stack || !Inventory.Instance || initialized == Inventory.Instance || Inventory.Instance.allItems == null) return;
            int count = 0;
            foreach (var item in Inventory.Instance.allItems) if (Common.Creature(item)) { item.isStackable = true; count++; }
            if (count == 0) return;
            initialized = Inventory.Instance;
            if (Common.TestRequested) Logger.LogInfo("MODPACK SELF-TEST PASS: creature definitions initialized: " + count);
        }
    }
    [HarmonyPatch(typeof(ChestWindow), "lockBugsAndFishFromChest")]
    internal static class CreatureContainers
    {
        private static void Postfix(ChestWindow __instance)
        {
            if (!FishInABarrel.Container || !Inventory.Instance || Inventory.Instance.invSlots == null) return;
            foreach (var slot in Inventory.Instance.invSlots)
            {
                if (!slot || !Common.Item(slot.itemNo)) continue;
                var item = Inventory.Instance.allItems[slot.itemNo];
                if (!Common.Creature(item) || item.isDeed || (__instance.isStash && item.isABackpack) ||
                    (Inventory.Instance.teleCaller && slot.itemNo == Inventory.Instance.teleCaller.getItemId()) ||
                    (Inventory.Instance.copyPad && slot.itemNo == Inventory.Instance.copyPad.getItemId())) continue;
                slot.clearDisable();
            }
        }
    }
    [HarmonyPatch(typeof(GiveNPC), "donateItemToMuseum")]
    internal static class DonateOne
    {
        internal sealed class Original { internal InventorySlot Slot; internal int Id, Stack; }
        private static void Prefix(ref List<Original> __state)
        {
            __state = new List<Original>();
            if (!Inventory.Instance || Inventory.Instance.invSlots == null) return;
            var seen = new HashSet<int>();
            foreach (var slot in Inventory.Instance.invSlots)
                if (slot && Common.Item(slot.itemNo) && slot.isSelectedForGive() && seen.Add(slot.itemNo) &&
                    Common.Creature(slot.itemInSlot) && slot.stack > 1)
                    __state.Add(new Original { Slot = slot, Id = slot.itemNo, Stack = slot.stack });
        }
        private static void Postfix(List<Original> __state)
        {
            if (__state == null) return;
            foreach (var saved in __state)
                if (saved.Slot && saved.Slot.itemNo == -1 && saved.Slot.stack == 0)
                    saved.Slot.updateSlotContentsAndRefresh(saved.Id, saved.Stack - 1);
        }
    }
    [HarmonyPatch(typeof(GiveNPC), "getDollarValueOfGiveSlots")]
    internal static class FishTickets
    {
        private static void Postfix(ref int ___raffleTicketAmount)
        {
            if (!TownEventManager.manage || !TownEventManager.manage.IsJohnsAnniversary() || !Inventory.Instance) return;
            long extra = 0;
            foreach (var slot in Inventory.Instance.invSlots)
                if (slot && slot.itemNo >= 0 && slot.itemInSlot && slot.itemInSlot.fish && slot.isSelectedForGive())
                    extra += Math.Max(0, (slot.getGiveAmount() == 0 ? slot.stack : Math.Min(slot.stack, slot.getGiveAmount())) - 1);
            ___raffleTicketAmount = (int)Math.Min(int.MaxValue, ___raffleTicketAmount + extra);
        }
    }
    // Prevent the vanilla exhibit action from deleting an entire creature stack.
    [HarmonyPatch(typeof(InputMaster), "drop")]
    internal static class ExhibitStackGuard
    {
        private static void Postfix(ref bool __result)
        {
            if (!__result || !FishInABarrel.Stack || !Inventory.Instance || !NetworkMapSharer.Instance || !NetworkMapSharer.Instance.localChar || !WorldManager.Instance) return;
            var inv = Inventory.Instance;
            if (inv.invSlots == null || inv.selectedSlot < 0 || inv.selectedSlot >= inv.invSlots.Length) return;
            var slot = inv.invSlots[inv.selectedSlot];
            if (!slot || slot.stack <= 1 || !Common.Creature(slot.itemInSlot)) return;
            var player = NetworkMapSharer.Instance.localChar;
            if (!player.myInteract || !player.myInteract.tileHighlighter) return;
            Vector3 position = player.myInteract.tileHighlighter.transform.position;
            position.y = player.transform.position.y;
            if ((slot.itemInSlot.fish && WorldManager.Instance.IsFishPondInPos(position)) ||
                (slot.itemInSlot.bug && WorldManager.Instance.IsBugTerrariumInPos(position)))
            {
                __result = false;
                Common.Notify("연못/사육장에 넣기 전에 생물을 1마리씩 분리하세요.");
            }
        }
    }
}
