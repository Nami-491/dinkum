using System;
using System.Collections.Generic;

namespace MakeshiftFixed
{
    // Independent of Unity: allocations can be tested without opening a save.
    internal static class StorageRules
    {
        internal static bool InScope(int level, int chestHouseX, int chestHouseY, int playerHouseX, int playerHouseY)
        {
            return level == 0 && ((chestHouseX == -1 && chestHouseY == -1) ||
                (playerHouseX >= 0 && playerHouseY >= 0 && chestHouseX == playerHouseX && chestHouseY == playerHouseY));
        }

        internal static int[] Allocate(IList<int> available, int requested)
        {
            if (requested <= 0) throw new ArgumentOutOfRangeException(nameof(requested));
            var result = new int[available.Count];
            int remaining = requested;
            for (int i = 0; i < available.Count && remaining > 0; i++)
            {
                result[i] = Math.Min(remaining, Math.Max(0, available[i]));
                remaining -= result[i];
            }
            return remaining == 0 ? result : null; // Never partially consume insufficient stock.
        }

        internal static void Test()
        {
            var a = Allocate(new[] { 2, 10 }, 5);
            Require(a[0] == 2 && a[1] == 3, "2 + 10 stock / 5 requested must remove exactly 5");
            Require(Allocate(new[] { 2, 2 }, 5) == null, "insufficient stock is not consumed");
            Require(Allocate(new[] { 0, -2, 5 }, 5)[2] == 5, "invalid/empty stock ignored");
            Require(Allocate(new[] { int.MaxValue, int.MaxValue }, 7)[0] == 7, "large stock cannot overflow");
            Require(Allocate(new[] { 1, 1, 1 }, 2)[2] == 0, "tool items count as individual units");
            Require(InScope(0, -1, -1, 10, 20), "outdoor storage included inside a house");
            Require(InScope(0, 10, 20, 10, 20), "current house included");
            Require(!InScope(0, 10, 21, 10, 20), "other house excluded");
            Require(!InScope(1, -1, -1, -1, -1), "mine excluded");
            Require(!InScope(2, -1, -1, -1, -1), "reef excluded");
            Require(!InScope(0, 10, 20, -1, -1), "house excluded while outdoors");
            bool rejected = false;
            try { Allocate(new[] { 1 }, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "invalid request rejected");
            var random = new Random(379);
            for (int n = 0; n < 10000; n++)
            {
                var stocks = new int[random.Next(1, 40)];
                int total = 0;
                for (int i = 0; i < stocks.Length; i++) { stocks[i] = random.Next(0, 100); total += stocks[i]; }
                int request = random.Next(1, 2000);
                var plan = Allocate(stocks, request);
                Require((plan != null) == (total >= request), "availability property");
                if (plan == null) continue;
                int removed = 0;
                for (int i = 0; i < plan.Length; i++)
                {
                    Require(plan[i] >= 0 && plan[i] <= stocks[i], "allocation bounds");
                    removed += plan[i];
                }
                Require(removed == request, "conservation property");
            }
        }
        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
