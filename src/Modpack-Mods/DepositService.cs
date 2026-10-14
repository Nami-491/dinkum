using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalDinkumMods
{
    internal static class DepositService
    {
        internal static bool Allowed(CharMovement player) => player && player.myEquip && player.myEquip.myPermissions.CheckIfCanOpenChest() &&
            RealWorldTimeLight.time && !RealWorldTimeLight.time.underGround && !RealWorldTimeLight.time.offIsland && ContainerManager.manage && WorldManager.Instance;

        internal static bool[] Apply(CharMovement player, TransferRequest request)
        {
            var result = new bool[request.Items.Length];
            if (!Allowed(player)) return result;
            var house = player.myInteract.InsideHouseDetails;
            if (request.HouseX != (house?.xPos ?? -1) || request.HouseY != (house?.yPos ?? -1)) return result;
            var map = house == null ? WorldManager.Instance.onTileMap : house.houseMapOnTile;
            if (map == null) return result;
            int px = Mathf.RoundToInt(player.transform.position.x / 2f), py = Mathf.RoundToInt(player.transform.position.z / 2f);
            var chests = new List<Chest>(); var seen = new HashSet<string>();
            foreach (var c in ContainerManager.manage.activeChests)
            {
                if (c == null || c.placedInWorldLevel != 0 || c.playingLookingInside > 0 || c.itemIds == null || c.itemStacks == null || c.itemIds.Length != c.itemStacks.Length ||
                    !DepositRules.Context(c.insideX, c.insideY, house?.xPos ?? -1, house?.yPos ?? -1) ||
                    c.xPos < 0 || c.yPos < 0 || c.xPos >= map.GetLength(0) || c.yPos >= map.GetLength(1)) continue;
                if (house == null && (Math.Abs(c.xPos - px) > InventoryManagement.Range.Value || Math.Abs(c.yPos - py) > InventoryManagement.Range.Value)) continue;
                int obj = map[c.xPos, c.yPos];
                if (obj < 0 || obj >= WorldManager.Instance.allObjects.Length) continue;
                var type = WorldManager.Instance.allObjects[obj].tileObjectChest;
                if (!type || type.isStash || type.isAutoSorter || type.isAutoPlacer || type.isMannequin || type.isToolRack || type.isDisplayStand || type.isFishPond || type.isBugTerrarium) continue;
                if (seen.Add(c.insideX + ":" + c.insideY + ":" + c.xPos + ":" + c.yPos)) chests.Add(c);
            }
            for (int i = 0; i < request.Items.Length; i++)
            {
                int itemId = request.Items[i], amount = request.Stacks[i];
                if (!Common.Item(itemId) || amount < 0 || amount > 100000000) continue;
                var item = Inventory.Instance.allItems[itemId];
                if (item.isDeed || item.isABackpack || item == Inventory.Instance.teleCaller || item == Inventory.Instance.copyPad) continue;
                foreach (var c in chests)
                {
                    int slot = DepositRules.Destination(c.itemIds, c.itemStacks, itemId, amount, item.checkIfStackable());
                    if (slot < 0) continue;
                    int after = item.checkIfStackable() && c.itemIds[slot] == itemId ? checked(c.itemStacks[slot] + amount) : amount;
                    c.itemIds[slot] = itemId; c.itemStacks[slot] = after; result[i] = true;
                    try { ContainerManager.manage.RpcRefreshOpenedChest(c.xPos, c.yPos, slot, itemId, after, c.insideX, c.insideY); }
                    catch (Exception e) { InventoryManagement.Log.LogWarning("Chest committed; refresh failed: " + e.Message); }
                    break;
                }
            }
            return result;
        }
    }
}
