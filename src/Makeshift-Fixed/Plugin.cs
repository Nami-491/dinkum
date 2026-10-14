using System;
using System.Linq;
using BepInEx;
#if !LEGACY
using BepInEx.Unity.Mono;
#endif
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;

namespace MakeshiftFixed
{
    [BepInPlugin(Id, "Makeshift - Local Compatibility Fix", "1.0.3")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "Snaitf.Dinkum.Makeshift";
        internal static ManualLogSource Log;
        internal const string LobbyKey = "MakeshiftFixedProtocol";
        internal const string Protocol = "103-1";
        private float nextTick;
        private bool checkedRuntime;

        private void Awake()
        {
            Log = Logger;
            try
            {
                StorageRules.Test();
                Catalog.SetupSerialization();
                new Harmony(Id).PatchAll(typeof(Plugin).Assembly);
                Logger.LogInfo("Makeshift fixed 1.0.3 loaded. Main island; host + clients need this same build. No player-limit changes.");
            }
            catch (Exception e)
            {
                new Harmony(Id).UnpatchSelf();
                enabled = false;
                Logger.LogError("Makeshift disabled after compatibility check failure: " + e);
            }
        }

        private void Update()
        {
            if (!checkedRuntime && Time.realtimeSinceStartup > 8 && Environment.GetCommandLineArgs().Contains("--makeshift-self-test"))
            {
                checkedRuntime = true;
                try
                {
                    Catalog.TestSerialization();
                    foreach (string method in new[] { "openCloseCraftMenu", "populateCraftList", "GetAmountOfItemFromChestsNearby" })
                        StorageRules.Require(Harmony.GetPatchInfo(AccessTools.Method(typeof(CraftingManager), method)).Owners.Contains(Id), method);
                    StorageRules.Require(Harmony.GetPatchInfo(AccessTools.Method(typeof(CharPickUp), "UserCode_CmdTakeItemsFromChestInChest")).Owners.Contains(Id), "server removal hook");
                    Logger.LogInfo("MAKESHIFT SELF-TEST PASS: 10000 allocation cases, house/level scope, bounded network serialization, runtime patch ownership.");
                }
                catch (Exception e) { Logger.LogError("MAKESHIFT SELF-TEST FAIL: " + e); }
            }
            if (Time.realtimeSinceStartup < nextTick) return;
            nextTick = Time.realtimeSinceStartup + 1.5f;
            if (!NetworkClient.active) { Menu.Active = false; Catalog.ClearClient(); return; }
            if (Menu.Active && CraftingManager.manage && CraftingManager.manage.craftMenuOpen)
                Catalog.Refresh();
        }

        internal static CharPickUp Local => NetworkMapSharer.Instance && NetworkMapSharer.Instance.localChar
            ? NetworkMapSharer.Instance.localChar.myPickUp : null;
        internal static bool MainIsland => RealWorldTimeLight.time && !RealWorldTimeLight.time.underGround && !RealWorldTimeLight.time.offIsland;
        internal static bool Allowed(CharPickUp player) => MainIsland && player && player.myEquip && player.myEquip.myPermissions.CheckIfCanOpenChest();

        internal static bool HostSupports()
        {
            if (NetworkServer.active) return true;
            return SteamManager.Initialized && SteamLobby.Instance && SteamLobby.Instance.currentLobby != CSteamID.Nil &&
                SteamMatchmaking.GetLobbyData(SteamLobby.Instance.currentLobby, LobbyKey) == Protocol;
        }
    }

    internal static class Menu
    {
        internal static bool Active, Opening;
        internal static readonly AccessTools.FieldRef<CraftingManager, bool> FromChests =
            AccessTools.FieldRefAccess<CraftingManager, bool>("craftingFromChests");
    }

    [HarmonyPatch(typeof(CraftingManager), "openCloseCraftMenu")]
    internal static class OpenMenu
    {
        private static void Prefix(bool isMenuOpen, CraftingManager.CraftingMenuType optionsType)
        {
            int type = (int)optionsType;
            Menu.Opening = true;
            Menu.Active = isMenuOpen && (type == 1 || type == 2 || type == 10 || type == 11 || type == 14) &&
                Plugin.Allowed(Plugin.Local) && Plugin.HostSupports();
            Catalog.ClearClient();
            if (Menu.Active) Catalog.Refresh();
        }
        private static Exception Finalizer(Exception __exception) { Menu.Opening = false; return __exception; }
    }

    // The original menu remains intact. Set the flag immediately before it builds the recipe list.
    [HarmonyPatch(typeof(CraftingManager), "populateCraftList")]
    internal static class PopulateMenu
    {
        private static void Prefix(CraftingManager __instance)
        {
            if (Menu.Opening && Menu.Active) Menu.FromChests(__instance) = true;
        }
    }

    [HarmonyPatch(typeof(CraftingManager), "GetAmountOfItemFromChestsNearby")]
    internal static class CountItems
    {
        private static bool Prefix(int itemId, ref int __result)
        {
            if (!Menu.Active) return true;
            __result = Plugin.Allowed(Plugin.Local) ? Catalog.Count(itemId) : 0;
            return false;
        }
    }

    [HarmonyPatch(typeof(CharPickUp), "UserCode_CmdTakeItemsFromChestInChest")]
    internal static class ConsumeItems
    {
        private static bool Prefix(CharPickUp __instance, int itemId, int amountToRemove, int houseX, int houseY)
        {
            // Non-participating clients retain the base game's nearby-chest behavior.
            if (!Catalog.IsParticipant(__instance)) return true;
            if (!Plugin.Allowed(__instance)) return false;
            var inside = __instance.myInteract.InsideHouseDetails;
            if (houseX != (inside?.xPos ?? -1) || houseY != (inside?.yPos ?? -1))
            {
                Plugin.Log.LogWarning("Ignored stale crafting request after house transition.");
                return false;
            }
            if (!Storage.Consume(__instance, itemId, amountToRemove))
                Plugin.Log.LogWarning("Craft storage changed/insufficient; no storage partially consumed. Avoid simultaneous crafting from the same materials.");
            Catalog.InvalidateHost();
            return false;
        }
    }

    [HarmonyPatch(typeof(NetworkMapSharer), "OnStartServer")]
    internal static class ServerStarted
    {
        private static void Postfix() { Catalog.StartServer(); }
    }
    [HarmonyPatch(typeof(NetworkMapSharer), "OnStartClient")]
    internal static class ClientStarted
    {
        private static void Postfix() { Catalog.StartClient(); }
    }
    [HarmonyPatch(typeof(SteamLobby), "OnLobbyEntered")]
    internal static class LobbyEntered
    {
        private static void Postfix(SteamLobby __instance)
        {
            if (SteamManager.Initialized && SteamMatchmaking.GetLobbyOwner(__instance.currentLobby) == SteamUser.GetSteamID())
                SteamMatchmaking.SetLobbyData(__instance.currentLobby, Plugin.LobbyKey, Plugin.Protocol);
        }
    }

    // Scope the corrected cache lookup to chest-update RPCs from a compatible host.
    // Other base-game chest operations are not globally replaced.
    [HarmonyPatch(typeof(ContainerManager), "UserCode_RpcRefreshOpenedChest")]
    internal static class ChestUpdateScope
    {
        internal static int Depth;
        private static void Prefix(ref bool __state)
        {
            __state = Plugin.MainIsland && Plugin.HostSupports();
            if (__state) Depth++;
        }
        private static Exception Finalizer(bool __state, Exception __exception)
        {
            if (__state) Depth--;
            return __exception;
        }
    }
    [HarmonyPatch(typeof(ContainerManager), "checkIfChestIsInsideAndInThisHouse")]
    internal static class ExactChestContext
    {
        private static bool Prefix(HouseDetails house, Chest checkChest, ref bool __result)
        {
            if (ChestUpdateScope.Depth == 0) return true;
            __result = checkChest != null && checkChest.insideX == (house?.xPos ?? -1) && checkChest.insideY == (house?.yPos ?? -1);
            return false;
        }
    }
}
