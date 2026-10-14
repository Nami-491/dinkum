using System;

namespace LocalDinkumMods
{
    internal static class DepositRules
    {
        internal static int Destination(int[] ids, int[] stacks, int item, int amount, bool stackable)
        {
            if (ids == null || stacks == null || ids.Length != stacks.Length || item < 0 || amount < 0) return -1;
            bool matching = false;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == item)
                {
                    matching = true;
                    if (stackable && amount > 0 && stacks[i] >= 0 && (long)stacks[i] + amount <= int.MaxValue) return i;
                }
            if (matching) for (int i = 0; i < ids.Length; i++) if (ids[i] == -1) return i;
            return -1;
        }
        internal static bool Context(int chestX, int chestY, int houseX, int houseY) => chestX == houseX && chestY == houseY;
        internal static void Test()
        {
            Require(Destination(new[] { 7, -1 }, new[] { 2, 0 }, 7, 3, true) == 0, "stack matching item");
            Require(Destination(new[] { 7, -1 }, new[] { 20, 0 }, 7, 40, false) == 1, "tool durability not combined");
            Require(Destination(new[] { 7 }, new[] { 20 }, 7, 40, false) == -1, "full chest does not discard tool");
            Require(Destination(new[] { 8, -1 }, new[] { 5, 0 }, 7, 3, true) == -1, "unmatched item remains in inventory");
            Require(Destination(new[] { 7 }, new[] { int.MaxValue }, 7, 1, true) == -1, "overflow rejected");
            Require(!Context(-1, -1, 10, 20) && Context(10, 20, 10, 20), "house boundaries");
            var random = new Random(50);
            for (int n = 0; n < 10000; n++)
            {
                int before = random.Next(0, 1000000), transfer = random.Next(1, 1000000);
                var ids = new[] { 7 }; var stacks = new[] { before };
                int target = Destination(ids, stacks, 7, transfer, true);
                Require(target == 0, "destination property");
                int after = checked(stacks[target] + transfer);
                Require((long)after == (long)before + transfer, "conservation property");
                Require(stacks[0] == before, "planning does not mutate source");
            }
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
