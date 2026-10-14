using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LocalDinkumMods
{
    internal static class EnhancementCompatibility
    {
        internal const string SchemaKey = "LocalEnhancementRegistrySchema";
        internal const string PreviousSchema = "native2174-enhanced510-v1";
        internal const string Schema = "native2174-enhanced1020-v2";
        internal static string Error;
        internal static void ValidateCatalog(Inventory inv)
        {
            try
            {
                using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("native-catalog.tsv")))
                {
                    int id = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var f = line.Split('\t');
                        if (id >= inv.allItems.Length) throw new InvalidDataException("Native catalog length changed.");
                        var i = inv.allItems[id];
                        if (!i || f[0] != id.ToString() || f[1] != i.itemName || bool.Parse(f[2]) != EnhancementRegistry.Supported(i) || int.Parse(f[3]) != i.fuelMax ||
                            Math.Abs(float.Parse(f[4], CultureInfo.InvariantCulture) - i.weaponDamage) > .001f || Math.Abs(float.Parse(f[5], CultureInfo.InvariantCulture) - i.damagePerAttack) > .001f)
                            throw new InvalidDataException("Native catalog mismatch at ID " + id);
                        id++;
                    }
                    if (id != 2174 || id != inv.allItems.Length) throw new InvalidDataException("Unexpected native item count.");
                }
            }
            catch (Exception e) { Error = e.Message; throw; }
        }
        private static bool Ready()
        {
            if (EnhancementSaveUpgrade.Incomplete) { Error = "Interrupted save migration: recover it from the main menu first."; return false; }
            if (!string.IsNullOrEmpty(EnhancementRegistry.Fingerprint) && string.IsNullOrEmpty(Error)) return true;
            Error = Error ?? "Equipment registry is not initialized. Restart after checking the mod version.";
            Debug.LogError(Error); return false;
        }
        internal static bool ValidateSave(SaveAndLoad saver)
        {
            if (!Ready()) return false;
            try
            {
                if (EnhancementSaveUpgrade.Incomplete) throw new InvalidDataException("Save migration is incomplete. Restart and finish migration before loading.");
                string path = Path.Combine(saver.GetSaveSlotDirectory(), "Player.es3");
                if (!File.Exists(path)) return true; // A newly created character.
                var file = new ES3File(File.ReadAllBytes(path), new ES3Settings(path));
                if (!file.KeyExists(SchemaKey) || (file.Load<string>(SchemaKey) != Schema && file.Load<string>(SchemaKey) != PreviousSchema))
                    throw new InvalidDataException("Old/unmarked save requires verified equipment-ID migration. Do not save or remove this mod.");
                return true;
            }
            catch (Exception e) { Error = e.Message; Debug.LogError(Error); return false; }
        }
        [HarmonyPatch(typeof(SaveAndLoad), "LoadGame"), HarmonyPrefix]
        private static bool Load(SaveAndLoad __instance) => ValidateSave(__instance);
        [HarmonyPatch(typeof(SaveAndLoad), "LoadOverFrames"), HarmonyPrefix]
        private static bool LoadCoroutine(SaveAndLoad __instance, ref IEnumerator __result)
        { if (ValidateSave(__instance)) return true; __result = Empty(); return false; }
        private static IEnumerator Empty() { yield break; }
        [HarmonyPatch(typeof(SaveAndLoad), "LoadInvForMultiplayerConnect"), HarmonyPrefix]
        private static bool LoadMultiplayer(SaveAndLoad __instance) => ValidateSave(__instance);
        [HarmonyPatch(typeof(CharacterCreatorScript), "saveCharSetUp"), HarmonyPrefix]
        private static bool NewCharacter() => Ready();
        [HarmonyPatch(typeof(SaveAndLoad), "SaveGame"), HarmonyPrefix]
        private static bool Save(SaveAndLoad __instance) => ValidateSave(__instance);
        [HarmonyPatch(typeof(SaveAndLoad), "SaveGameOnDisconnect"), HarmonyPrefix]
        private static bool SaveDisconnect(SaveAndLoad __instance) => ValidateSave(__instance);
        [HarmonyPatch(typeof(SaveAndLoad), "SaveInv"), HarmonyPostfix]
        private static void Stamp(ES3Settings ___cacheFileSettings)
        {
            if (Ready()) ES3.Save(SchemaKey, Schema, ___cacheFileSettings);
        }
    }
}
