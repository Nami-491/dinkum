using System;
using BepInEx;
#if !LEGACY
using BepInEx.Unity.Mono;
#endif
using HarmonyLib;
using UnityEngine;

namespace LocalDinkumMods
{
    [BepInPlugin(Id, "Fancy Feast - Local Fix", "1.0.6")]
    public sealed class FancyFeast : BaseUnityPlugin
    {
        internal const string Id = "Snaitf.Dinkum.FancyFeast";
        internal static bool Tick;
        private void Awake()
        {
            Tick = Config.Bind("Options", "Show Tick", true, "Show a tick when the favourite food is in inventory.").Value;
            new Harmony(Id).PatchAll(typeof(FancyFeast).Assembly);
            Logger.LogInfo("Fancy Feast local 1.0.6 loaded (client-side display only).");
            if (Common.TestRequested) Common.CheckPatches(Logger, Id, AccessTools.Method(typeof(ConversationManager), "FillNameBoxDetails"));
        }
    }
    [HarmonyPatch(typeof(ConversationManager), "FillNameBoxDetails")]
    internal static class FavouriteFood
    {
        private static void Postfix(ConversationManager __instance, NPCAI targetNPC, TMPro.TextMeshProUGUI ___npcNameText)
        {
            if (!targetNPC || !NPCManager.manage || !Inventory.Instance || !___npcNameText) return;
            var identity = targetNPC.GetComponent<NPCIdentity>();
            if (!identity || identity.NPCNo < 0 || identity.NPCNo >= NPCManager.manage.NPCDetails.Length) return;
            var details = NPCManager.manage.NPCDetails[identity.NPCNo];
            if (details == null || !details.favouriteFood) return;
            var food = details.favouriteFood;
            string foodName = food.getInvItemName(1);
            if (string.IsNullOrEmpty(foodName) || ___npcNameText.text.Contains(foodName)) return;
            bool present = false;
            if (FancyFeast.Tick && Inventory.Instance.invSlots != null)
                foreach (var slot in Inventory.Instance.invSlots)
                    if (slot && slot.itemNo == food.getItemId() && slot.stack > 0) { present = true; break; }
            ___npcNameText.text += "<size=75%> (" + foodName + (present ? " ✓" : "") + ")</size>";
        }
    }
}
