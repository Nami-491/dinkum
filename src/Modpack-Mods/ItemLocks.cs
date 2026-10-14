using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace LocalDinkumMods
{
    // Optional cross-mod boundary. Equipment enhancement can carry a lock across its
    // deliberate item-id change without coupling ordinary inventory use to that mod.
    public static class ItemLockBridge
    {
        public static bool ExternalModalOpen { get; set; }
        public static bool TransferPending => TransferProtocol.Pending;
        public static string Capture(int index)
        {
            var inv = Inventory.Instance;
            return inv && inv.invSlots != null && index >= 0 && index < inv.invSlots.Length ? SlotLocks.Get(inv.invSlots[index])?.Identity.ToString("N") ?? "" : "";
        }
        public static void Restore(int index, string identity)
        {
            var inv = Inventory.Instance;
            if (!inv || inv.invSlots == null || index < 0 || index >= inv.invSlots.Length || !inv.invSlots[index]) return;
            var slot = inv.invSlots[index];
            SlotLocks.Set(slot, slot.itemNo >= 0 && Guid.TryParseExact(identity, "N", out var id) && id != Guid.Empty ? new ItemLockToken(id, slot.itemNo) : null);
        }
    }
    internal static class SlotLocks
    {
        private static readonly Dictionary<InventorySlot, ItemLockToken> tokens = new Dictionary<InventorySlot, ItemLockToken>();
        private static readonly Dictionary<InventorySlot, TextMeshProUGUI> badges = new Dictionary<InventorySlot, TextMeshProUGUI>();
        private static Inventory inventory;
        private static float next;
        internal static bool HasAny => tokens.Count > 0;
        private static bool Owned(InventorySlot slot)
        {
            var inv = Inventory.Instance;
            return slot && inv && (slot == inv.dragSlot || slot == inv.weaponSlot || (inv.invSlots != null && inv.invSlots.Contains(slot)));
        }
        internal static void Initialize(ConfigFile file) { ItemLockRules.Test(); tokens.Clear(); }
        internal static ItemLockToken Get(InventorySlot slot)
        {
            if (ReferenceEquals(slot, null)) return null;
            if (!slot) { tokens.Remove(slot); return null; }
            if (!Owned(slot)) { tokens.Remove(slot); return null; }
            if (!tokens.TryGetValue(slot, out var token)) return null;
            var valid = ItemLockRules.Keep(token, slot.itemNo);
            if (valid == null) tokens.Remove(slot);
            return valid;
        }
        internal static void Set(InventorySlot slot, ItemLockToken token)
        {
            if (!slot) return;
            if (!Owned(slot)) { tokens.Remove(slot); return; }
            token = ItemLockRules.Keep(token, slot.itemNo);
            if (token == null) tokens.Remove(slot); else tokens[slot] = token;
            next = 0;
        }
        internal static bool IsLocked(int index) => Inventory.Instance && Inventory.Instance.invSlots != null && index >= 0 && index < Inventory.Instance.invSlots.Length && Get(Inventory.Instance.invSlots[index]) != null;
        internal static void ToggleHovered()
        {
            var inv = Inventory.Instance;
            if (!inv || TransferProtocol.Pending) return;
            var slot = inv.cursorRollOver();
            if (!slot && !inv.usingMouse && inv.currentlySelected) slot = inv.currentlySelected.GetComponent<InventorySlot>();
            if (!slot || !inv.invSlots.Contains(slot) || !slot.slotUnlocked || !Common.Item(slot.itemNo)) return;
            bool locked = Get(slot) == null;
            Set(slot, locked ? new ItemLockToken(slot.itemNo) : null);
            Common.Notify(locked ? "이 아이템 잠금: 이동해도 유지 · 자동 정렬/보내기 제외" : "이 아이템 잠금 해제");
        }
        internal static void Refresh()
        {
            var inv = Inventory.Instance;
            if (inventory != inv) { tokens.Clear(); inventory = inv; }
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + .2f;
            foreach (var slot in tokens.Keys.ToArray()) Get(slot);
            foreach (var key in badges.Keys.Where(s => !s).ToArray()) badges.Remove(key);
            if (!inv || inv.invSlots == null) return;
            foreach (var slot in inv.invSlots.Concat(new[] { inv.dragSlot, inv.weaponSlot }))
            {
                if (!slot) continue;
                bool locked = Get(slot) != null;
                if (!badges.TryGetValue(slot, out var badge) || !badge)
                {
                    if (!locked) continue;
                    var go = new GameObject("LocalItemLock", typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(slot.transform, false);
                    badge = go.GetComponent<TextMeshProUGUI>(); badge.text = "L"; badge.fontSize = 14; badge.color = Color.yellow; badge.raycastTarget = false;
                    if (inv.InvDescriptionText) badge.font = inv.InvDescriptionText.font;
                    badge.alignment = TextAlignmentOptions.TopLeft;
                    var r = badge.rectTransform; r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(4, -2); r.sizeDelta = new Vector2(20, 20);
                    badges[slot] = badge;
                }
                badge.gameObject.SetActive(locked);
            }
        }
        internal static string Save()
        {
            var inv = Inventory.Instance;
            return ItemLockRules.Encode(inv.invSlots.Select(s => s.itemNo).ToArray(), inv.invSlots.Select(s => s.stack).ToArray(), i => Get(inv.invSlots[i]));
        }
        internal static void Load(string data)
        {
            tokens.Clear(); inventory = Inventory.Instance;
            var inv = inventory;
            if (!inv || inv.invSlots == null) return;
            foreach (var pair in ItemLockRules.Decode(data, inv.invSlots.Select(s => s.itemNo).ToArray(), inv.invSlots.Select(s => s.stack).ToArray())) Set(inv.invSlots[pair.Key], pair.Value);
        }
    }
    [HarmonyPatch(typeof(Inventory), "swapSlots")]
    internal static class FollowItemSwap
    {
        internal sealed class State { internal InventorySlot A, B; internal int AI, BI, AS, BS; internal ItemLockToken AT, BT; internal bool Applied; }
        private static void Prefix(InventorySlot firstSlot, InventorySlot secondSlot, out State __state)
        {
            __state = null;
            if (!firstSlot || !secondSlot || firstSlot == secondSlot || (secondSlot.equipSlot && !secondSlot.equipSlot.canEquipInThisSlot(firstSlot.itemNo)) || (firstSlot.equipSlot && !firstSlot.equipSlot.canEquipInThisSlot(secondSlot.itemNo))) return;
            __state = new State { A = firstSlot, B = secondSlot, AI = firstSlot.itemNo, BI = secondSlot.itemNo, AS = firstSlot.stack, BS = secondSlot.stack, AT = SlotLocks.Get(firstSlot), BT = SlotLocks.Get(secondSlot) };
        }
        private static void Postfix(State __state)
        {
            var s = __state;
            if (s == null || s.Applied || !s.A || !s.B || s.A.itemNo != s.BI || s.B.itemNo != s.AI || s.A.stack != s.BS || s.B.stack != s.AS) return;
            SlotLocks.Set(s.A, s.BT); SlotLocks.Set(s.B, s.AT);
            s.Applied = true;
        }
        private static Exception Finalizer(State __state, Exception __exception)
        { if (__exception != null && __state != null && (__state.AI != __state.BI || __state.AS != __state.BS)) { try { Postfix(__state); } catch (Exception e) { InventoryManagement.Log.LogWarning("Lock swap reconciliation skipped: " + e.Message); } } return __exception; }
    }
    [HarmonyPatch(typeof(Inventory), "splitSlot")]
    internal static class FollowItemSplit
    {
        private static void Prefix(Inventory __instance, InventorySlot slotToSplit, out FollowItemSwap.State __state)
        {
            var drag = __instance.dragSlot;
            __state = slotToSplit && drag && Common.Item(slotToSplit.itemNo) && slotToSplit.itemInSlot.checkIfStackable() && !slotToSplit.equipSlot
                ? new FollowItemSwap.State { A = slotToSplit, B = drag, AI = slotToSplit.itemNo, BI = drag.itemNo, AS = slotToSplit.stack, BS = drag.stack, AT = SlotLocks.Get(slotToSplit), BT = SlotLocks.Get(drag) } : null;
            if (__state == null && slotToSplit && slotToSplit.itemNo == -1 && drag && Common.Item(drag.itemNo) && drag.itemInSlot.checkIfStackable())
                __state = new FollowItemSwap.State { A = slotToSplit, B = drag, AI = -1, BI = drag.itemNo, AS = 0, BS = drag.stack, BT = SlotLocks.Get(drag) };
        }
        private static void Postfix(FollowItemSwap.State __state)
        {
            var s = __state;
            if (s == null || s.Applied || !s.A || !s.B) return;
            int id = s.AI >= 0 ? s.AI : s.BI;
            if ((s.A.itemNo != id && s.A.itemNo != -1) || (s.B.itemNo != id && s.B.itemNo != -1) || (long)s.A.stack + s.B.stack != (long)s.AS + s.BS || (s.A.stack == s.AS && s.B.stack == s.BS)) return;
            bool fromA = s.A.stack < s.AS;
            var source = fromA ? s.A : s.B; var destination = fromA ? s.B : s.A;
            var sourceToken = fromA ? s.AT : s.BT; var destinationToken = fromA ? s.BT : s.AT;
            // Keep the remaining source's identity. Only the portion entering the other
            // stack inherits a cloned lock when the source still exists.
            var entering = sourceToken != null && source.itemNo == id && source.stack > 0 ? sourceToken.Split() : sourceToken;
            SlotLocks.Set(source, sourceToken);
            SlotLocks.Set(destination, ItemLockRules.Merge(destinationToken, entering, id));
            s.Applied = true;
        }
        private static Exception Finalizer(FollowItemSwap.State __state, Exception __exception)
        { if (__exception != null) { try { Postfix(__state); } catch (Exception e) { InventoryManagement.Log.LogWarning("Lock split reconciliation skipped: " + e.Message); } } return __exception; }
    }
    [HarmonyPatch]
    internal static class FollowItemMerge
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Inventory), "Update");
            yield return AccessTools.Method(typeof(Inventory), "checkIfWindowIsNeeded");
        }
        internal sealed class Before { internal InventorySlot Slot; internal int Id, Amount; internal ItemLockToken Token; internal bool Applied; }
        private static void Prefix(Inventory __instance, out Before[] __state)
        {
            __state = !SlotLocks.HasAny || __instance.invSlots == null ? new Before[0] : __instance.invSlots.Concat(new[] { __instance.dragSlot, __instance.weaponSlot }).Where(s => s).Distinct()
                .Select(s => new Before { Slot = s, Id = s.itemNo, Amount = s.stack, Token = SlotLocks.Get(s) }).ToArray();
        }
        private static void Postfix(Before[] __state)
        {
            if (__state == null || __state.Length == 0 || __state[0].Applied) return;
            foreach (var source in __state.Where(s => s.Token != null && s.Slot && s.Slot.itemNo == -1 && Common.Item(s.Id)))
            {
                bool stackable = Inventory.Instance.allItems[source.Id].checkIfStackable();
                var targets = __state.Where(t => t.Slot && t.Slot != source.Slot && t.Slot.itemNo == source.Id &&
                    ((t.Id == -1 && t.Slot.stack == source.Amount) || (stackable && t.Id == source.Id && (long)t.Slot.stack - t.Amount == source.Amount))).ToArray();
                if (targets.Length == 1) SlotLocks.Set(targets[0].Slot, ItemLockRules.Merge(SlotLocks.Get(targets[0].Slot), source.Token, source.Id));
            }
            __state[0].Applied = true;
        }
        private static Exception Finalizer(Before[] __state, Exception __exception)
        { if (__exception != null) { try { Postfix(__state); } catch (Exception e) { InventoryManagement.Log.LogWarning("Lock move reconciliation skipped: " + e.Message); } } return __exception; }
    }
    [HarmonyPatch(typeof(InventorySlot), "updateSlotContentsAndRefresh")]
    internal static class ClearReplacedItemLock { private static void Postfix(InventorySlot __instance) { SlotLocks.Get(__instance); } }
    [HarmonyPatch(typeof(Inventory), "SortAllSlots")]
    internal static class LockedSort
    {
        private static bool Prefix(Inventory __instance, out List<InventorySlot> __state)
        {
            __state = new List<InventorySlot>();
            if (TransferProtocol.Pending || ItemLockBridge.ExternalModalOpen) return false;
            for (int i = 0; i < __instance.invSlots.Length; i++)
                if (__instance.invSlots[i] && __instance.invSlots[i].slotUnlocked && SlotLocks.IsLocked(i)) { __state.Add(__instance.invSlots[i]); __instance.invSlots[i].slotUnlocked = false; }
            return true;
        }
        private static Exception Finalizer(List<InventorySlot> __state, Exception __exception)
        { if (__state != null) foreach (var slot in __state) if (slot) slot.slotUnlocked = true; return __exception; }
    }
    // Metadata uses the SAME ES3 cache as the native inventory save, so a game rollback
    // also rolls back lock positions. Old config-based position locks are not re-applied.
    [HarmonyPatch(typeof(SaveAndLoad), "SaveInv")]
    internal static class SaveIndividualLocks
    {
        private static void Postfix(SaveAndLoad __instance)
        {
            if (!AccessTools.FieldRefAccess<SaveAndLoad, bool>("savedSucessfully")(__instance)) return;
            try { ES3.Save("LocalItemLocksV1", SlotLocks.Save(), AccessTools.FieldRefAccess<SaveAndLoad, ES3Settings>("cacheFileSettings")(__instance)); }
            catch (Exception e) { AccessTools.FieldRefAccess<SaveAndLoad, bool>("savedSucessfully")(__instance) = false; InventoryManagement.Log.LogError("Item lock save failed; native save not committed: " + e); }
        }
    }
    [HarmonyPatch(typeof(SaveAndLoad), "LoadInv")]
    internal static class LoadIndividualLocks
    {
        private static void Prefix() => SlotLocks.Load("");
        private static void Postfix(SaveAndLoad __instance)
        {
            try
            {
                var settings = AccessTools.FieldRefAccess<SaveAndLoad, ES3Settings>("cacheFileSettings")(__instance);
                SlotLocks.Load(ES3.KeyExists("LocalItemLocksV1", settings) ? ES3.Load<string>("LocalItemLocksV1", settings) : "");
            }
            catch (Exception e) { SlotLocks.Load(""); InventoryManagement.Log.LogWarning("Item lock metadata skipped: " + e.Message); }
        }
    }
}
