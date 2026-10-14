using System;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LocalDinkumMods
{
    internal static class Common
    {
        internal static bool Item(int id) => Inventory.Instance && Inventory.Instance.allItems != null && id >= 0 && id < Inventory.Instance.allItems.Length && Inventory.Instance.allItems[id];
        internal static bool Creature(InventoryItem item) => item && (item.fish || item.bug || item.underwaterCreature);
        internal static bool TestRequested => Environment.GetCommandLineArgs().Contains("--modpack-self-test");
        internal static void Notify(string message)
        {
            if (NotificationManager.manage) NotificationManager.manage.createChatNotification(message);
        }
        internal static void CheckPatches(ManualLogSource log, string id, params System.Reflection.MethodBase[] methods)
        {
            foreach (var method in methods)
                if (method == null || Harmony.GetPatchInfo(method)?.Owners.Contains(id) != true) throw new InvalidOperationException("Patch verification failed: " + method);
            log.LogInfo("MODPACK SELF-TEST PASS: " + id + " runtime patch bindings.");
        }
    }
}
