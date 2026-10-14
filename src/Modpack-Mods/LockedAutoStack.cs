using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace LocalDinkumMods
{
    // Scope filtering to the original chest quick-stack button only. Crafting, eating,
    // selling and manual movement must still be able to use a locked item.
    [HarmonyPatch(typeof(ChestWindow), "autoStackIntoChest")]
    internal static class LockedAutoStack
    {
        private static bool Prefix() => !TransferProtocol.Pending && !ItemLockBridge.ExternalModalOpen;
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList(); int counts = 0, removals = 0;
            var count = AccessTools.Method(typeof(Inventory), "getAmountOfItemInAllSlots");
            var remove = AccessTools.Method(typeof(Inventory), "removeAmountOfItem");
            foreach (var instruction in code)
            {
                if (instruction.Calls(count)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(LockedAutoStack), nameof(Count)); counts++; }
                if (instruction.Calls(remove)) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(LockedAutoStack), nameof(Remove)); removals++; }
            }
            if (counts != 2 || removals != 2) throw new InvalidOperationException("Unsupported chest quick-stack IL; item lock filter not installed.");
            return code;
        }
        private static int Count(Inventory inventory, int id)
        {
            long count = 0;
            for (int i = 0; i < inventory.invSlots.Length; i++)
            {
                var slot = inventory.invSlots[i];
                if (slot && slot.slotUnlocked && slot.itemNo == id && slot.stack > 0 && !SlotLocks.IsLocked(i)) count += slot.stack;
            }
            int largestDestination = 0;
            if (ChestWindow.chests) foreach (var slot in ChestWindow.chests.chestSlots) if (slot && slot.itemNo == id) largestDestination = Math.Max(largestDestination, slot.stack);
            return (int)Math.Min(count, (long)int.MaxValue - largestDestination);
        }
        private static void Remove(Inventory inventory, int id, int amount)
        {
            for (int i = 0; i < inventory.invSlots.Length && amount > 0; i++)
            {
                var slot = inventory.invSlots[i];
                if (!slot || !slot.slotUnlocked || slot.itemNo != id || slot.stack <= 0 || SlotLocks.IsLocked(i)) continue;
                int used = Math.Min(amount, slot.stack); amount -= used;
                int remaining = slot.stack - used;
                slot.updateSlotContentsAndRefresh(remaining == 0 ? -1 : id, remaining);
            }
            if (amount != 0) throw new InvalidOperationException("Chest quick-stack source changed unexpectedly.");
        }
    }
}
