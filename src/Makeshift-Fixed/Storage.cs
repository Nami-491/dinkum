using System;
using System.Collections.Generic;
using UnityEngine;

namespace MakeshiftFixed
{
    internal static class Storage
    {
        private sealed class Slot
        {
            internal Chest Chest;
            internal HouseDetails House;
            internal int Index, Available;
        }

        internal static bool ValidItem(int id) => Inventory.Instance && id >= 0 && id < Inventory.Instance.allItems.Length && Inventory.Instance.allItems[id];
        private static bool UnitItem(int id) => Inventory.Instance.allItems[id].isATool || Inventory.Instance.allItems[id].hasFuel;

        private static IEnumerable<Chest> Eligible(CharPickUp player)
        {
            if (!Plugin.Allowed(player) || !ContainerManager.manage || !WorldManager.Instance) yield break;
            var house = player.myInteract.InsideHouseDetails;
            var visited = new HashSet<string>();
            foreach (var chest in ContainerManager.manage.activeChests)
            {
                if (chest == null || chest.itemIds == null || chest.itemStacks == null || chest.playingLookingInside > 0 ||
                    !StorageRules.InScope(chest.placedInWorldLevel, chest.insideX, chest.insideY, house?.xPos ?? -1, house?.yPos ?? -1)) continue;
                var map = chest.insideX == -1 ? WorldManager.Instance.onTileMap : house?.houseMapOnTile;
                if (map == null || chest.xPos < 0 || chest.yPos < 0 || chest.xPos >= map.GetLength(0) || chest.yPos >= map.GetLength(1)) continue;
                int objectId = map[chest.xPos, chest.yPos];
                if (objectId < 0 || objectId >= WorldManager.Instance.allObjects.Length ||
                    !WorldManager.Instance.allObjects[objectId].tileObjectChest) continue;
                var definition = WorldManager.Instance.allObjects[objectId].tileObjectChest;
                if (definition.isStash || definition.isFishPond || definition.isBugTerrarium || definition.isMannequin ||
                    definition.isToolRack || definition.isDisplayStand || definition.isAutoPlacer || definition.isAutoSorter) continue;
                string key = chest.insideX + ":" + chest.insideY + ":" + chest.xPos + ":" + chest.yPos;
                if (visited.Add(key)) yield return chest;
            }
        }

        internal static Dictionary<int, int> Counts(CharPickUp player)
        {
            var counts = new Dictionary<int, int>();
            foreach (var c in Eligible(player))
                for (int i = 0; i < Math.Min(c.itemIds.Length, c.itemStacks.Length); i++)
                {
                    int id = c.itemIds[i];
                    if (!ValidItem(id)) continue;
                    int units = UnitItem(id) ? 1 : Math.Max(0, c.itemStacks[i]);
                    counts.TryGetValue(id, out int previous);
                    counts[id] = (int)Math.Min(100000000L, (long)previous + units);
                }
            return counts;
        }

        internal static bool Consume(CharPickUp player, int item, int amount)
        {
            if (!ValidItem(item) || amount <= 0 || amount > 100000000) return false;
            bool unit = UnitItem(item);
            var slots = new List<Slot>();
            var available = new List<int>();
            foreach (var c in Eligible(player))
                for (int i = 0; i < Math.Min(c.itemIds.Length, c.itemStacks.Length); i++)
                    if (c.itemIds[i] == item)
                    {
                        int quantity = unit ? 1 : Math.Max(0, c.itemStacks[i]);
                        slots.Add(new Slot { Chest = c, House = c.insideX == -1 ? null : player.myInteract.InsideHouseDetails, Index = i, Available = quantity });
                        available.Add(quantity);
                    }
            var plan = StorageRules.Allocate(available, amount);
            if (plan == null) return false;
            // Plan first, then apply on the server's single Unity thread, without yielding.
            for (int i = 0; i < plan.Length; i++)
            {
                if (plan[i] == 0) continue;
                var slot = slots[i];
                int left = unit ? 0 : slot.Available - plan[i];
                // The game's context predicate accepts indoor chests when house == null.
                // Update the exact planned object, not a second ambiguous coordinate lookup.
                slot.Chest.itemIds[slot.Index] = left == 0 ? -1 : item;
                slot.Chest.itemStacks[slot.Index] = left;
                ContainerManager.manage.RpcRefreshOpenedChest(slot.Chest.xPos, slot.Chest.yPos, slot.Index,
                    slot.Chest.itemIds[slot.Index], left, slot.House?.xPos ?? -1, slot.House?.yPos ?? -1);
            }
            return true;
        }
    }
}
