using System;
using BepInEx;
#if !LEGACY
using BepInEx.Unity.Mono;
using KeyboardShortcut = BepInEx.Unity.Mono.Configuration.KeyboardShortcut;
#endif
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace LocalDinkumMods
{
    [BepInPlugin("LocalDinkum.SalePrices", "Sale Prices - Local Display", "1.0.0")]
    public sealed class SalePrices : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> Shown;
        private ConfigEntry<KeyboardShortcut> toggle;
        private void Awake()
        {
            Shown = Config.Bind("Display", "Enabled", true, "Show base sale prices (no licence/event/vendor bonus).");
            toggle = Config.Bind("Keybinds", "TogglePrices", new KeyboardShortcut(KeyCode.F10), "Toggle while inventory is open.");
            new Harmony("LocalDinkum.SalePrices").PatchAll(typeof(SalePrices).Assembly);
            Logger.LogInfo("Sale Prices 1.0.0 loaded. Display only; base prices excluding bonuses.");
        }
        private void Update()
        {
            if (Inventory.Instance && Inventory.Instance.invOpen && !(ChatBox.chat && ChatBox.chat.chatOpen) && toggle.Value.IsDown())
            { Shown.Value = !Shown.Value; Common.Notify(Shown.Value ? "기본 판매가격 표시 켜짐" : "기본 판매가격 표시 꺼짐"); }
        }
    }
    [HarmonyPatch(typeof(Inventory), "fillHoverDescription")]
    internal static class PriceTooltip
    {
        private const string Marker = "\n<color=#B8860B>[기본 판매가]";
        private static void Postfix(Inventory __instance, InventorySlot rollOverSlot)
        {
            if (!__instance.InvDescriptionText) return;
            var ui = __instance.InvDescriptionText;
            string text = ui.text ?? "";
            int at = text.IndexOf(Marker, StringComparison.Ordinal); if (at >= 0) text = text.Substring(0, at);
            var item = rollOverSlot ? rollOverSlot.itemInSlot : null;
            if (SalePrices.Shown.Value && item && !item.isDeed && item.value > 0)
            {
                long count = item.hasFuel || item.hasColourVariation ? 1 : Math.Max(1, rollOverSlot.stack);
                long total = DisplayRules.BasePrice(item.value, count, item.relic);
                long unit = DisplayRules.BasePrice(item.value, 1, item.relic);
                text += Marker + " " + unit.ToString("N0") + " Dink" + (count > 1 ? " / " + count + "개 " + total.ToString("N0") + " Dink" : "") + "</color>\n<size=75%>상업 면허·판매처·행사 보너스 제외</size>";
            }
            if (ui.text != text) ui.text = text;
        }
    }
}
