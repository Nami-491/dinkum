using System;
using System.Text.Json;
using LocalDinkum.Enhancement;

internal static class RulesTests
{
    private static int assertions;
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); assertions++; }
    private static void Reject(Action action, string name)
    { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name); }
    public static void Main()
    {
        Check(EnhancementRules.MaximumLevel == 20, "Maximum level is 20");
        int[] expectedPower = { 100, 103, 106, 110, 116, 124, 134, 148, 166, 190, 220, 262, 312, 370, 436, 510, 592, 682, 780, 886, 1000 };
        int[] expectedDurability = { 100, 105, 110, 118, 128, 142, 160, 184, 216, 258, 320, 394, 480, 578, 688, 810, 944, 1090, 1248, 1418, 1600 };
        for (int level = 0; level <= 20; level++)
        {
            Check(EnhancementRules.AttackOrHarvestPercent(level) == expectedPower[level], "Exact power at +" + level);
            Check(EnhancementRules.MaximumDurabilityPercent(level) == expectedDurability[level], "Exact durability at +" + level);
        }
        // Dinks-only costs; original odds and currency prices remain unchanged.
        var tiers = new[]
        {
            (First: 1, Last: 3, Chance: 9000, Copper: 0, Iron: 0, Dinks: 2000),
            (First: 4, Last: 6, Chance: 7000, Copper: 0, Iron: 0, Dinks: 5000),
            (First: 7, Last: 9, Chance: 4500, Copper: 0, Iron: 0, Dinks: 10000),
            (First: 10, Last: 10, Chance: 2500, Copper: 0, Iron: 0, Dinks: 20000),
            (First: 11, Last: 13, Chance: 2000, Copper: 0, Iron: 0, Dinks: 30000),
            (First: 14, Last: 16, Chance: 1500, Copper: 0, Iron: 0, Dinks: 50000),
            (First: 17, Last: 19, Chance: 1000, Copper: 0, Iron: 0, Dinks: 80000),
            (First: 20, Last: 20, Chance: 500, Copper: 0, Iron: 0, Dinks: 150000)
        };
        foreach (var tier in tiers)
        for (int target = tier.First; target <= tier.Last; target++)
        {
            var cost = EnhancementRules.CostForTarget(target);
            Check(cost.SuccessBasisPoints == tier.Chance && cost.CopperBars == tier.Copper && cost.IronBars == tier.Iron && cost.Dinks == tier.Dinks, "Exact cost/odds for +" + target);
            Check(EnhancementRules.Succeeds(target, tier.Chance - 1), "Last successful roll for +" + target);
            Check(!EnhancementRules.Succeeds(target, tier.Chance), "First failed roll for +" + target);
        }
        for (int level = 0; level < 20; level++)
        {
            int successful = 0;
            for (int roll = 0; roll < 10000; roll++)
            {
                int result = EnhancementRules.ResultLevel(level, roll);
                int failureLevel = level <= 10 ? level : level - 1;
                int expected = roll < EnhancementRules.CostForTarget(level + 1).SuccessBasisPoints ? level + 1 : failureLevel;
                Check(result == expected, "Exact success/failure level; no destruction at +" + level);
                if (result > level) successful++;
            }
            Check(successful == EnhancementRules.CostForTarget(level + 1).SuccessBasisPoints, "Exact odds");
        }
        for (int level = 11; level <= 20; level++)
        {
            Check(EnhancementRules.AttackOrHarvestPercent(level) - EnhancementRules.AttackOrHarvestPercent(level - 1) > EnhancementRules.AttackOrHarvestPercent(level - 1) - EnhancementRules.AttackOrHarvestPercent(level - 2), "High-level power increments strictly grow");
            Check(EnhancementRules.MaximumDurabilityPercent(level) - EnhancementRules.MaximumDurabilityPercent(level - 1) > EnhancementRules.MaximumDurabilityPercent(level - 1) - EnhancementRules.MaximumDurabilityPercent(level - 2), "High-level durability increments strictly grow");
        }
        for (int level = 2; level <= 10; level++)
        {
            Check(EnhancementRules.AttackOrHarvestPercent(level) - EnhancementRules.AttackOrHarvestPercent(level - 1) >= EnhancementRules.AttackOrHarvestPercent(level - 1) - EnhancementRules.AttackOrHarvestPercent(level - 2), "Power increments grow");
            Check(EnhancementRules.MaximumDurabilityPercent(level) - EnhancementRules.MaximumDurabilityPercent(level - 1) >= EnhancementRules.MaximumDurabilityPercent(level - 1) - EnhancementRules.MaximumDurabilityPercent(level - 2), "Durability increments grow");
        }
        Check(EnhancementRules.AttackOrHarvestPercent(10) == 220, "Original +10 power");
        Check(EnhancementRules.Capacity(100, 10) == 320, "Original +10 durability");
        Check(EnhancementRules.AttackOrHarvestPercent(20) == 1000, "Maximum power");
        Check(EnhancementRules.Capacity(100, 20) == 1600, "Maximum durability");
        Check(EnhancementRules.PreserveCondition(50, 100, 320) == 160, "No free repair");
        Check(EnhancementRules.PreserveCondition(1, 394, 320) == 1, "Downgrade cannot destroy one-point equipment by rounding");
        Check(EnhancementRules.PreserveCondition(0, 394, 320) == 0, "Already broken equipment is not repaired");
        var item = new EquipmentState(Guid.NewGuid(), "example-weapon", 9, 129);
        var success = item.ApplyResult(0, 100);
        Check(success.InstanceId == item.InstanceId && success.Level == 10 && success.RemainingDurability == 160, "Success keeps identity and condition");
        Check(ReferenceEquals(item.ApplyResult(9999, 100), item), "Failure leaves equipment unchanged");
        for (int level = 0; level < 20; level++)
        {
            int oldCapacity = expectedDurability[level];
            var equipment = new EquipmentState(Guid.NewGuid(), "example-tool", level, oldCapacity / 3);
            var upgraded = equipment.ApplyResult(0, 100);
            Check(upgraded.InstanceId == equipment.InstanceId && upgraded.BaseEquipment == equipment.BaseEquipment && upgraded.Level == level + 1 && upgraded.RemainingDurability == (int)((long)equipment.RemainingDurability * expectedDurability[level + 1] / oldCapacity), "Upgrade preserves identity and condition at +" + level);
            var failed = equipment.ApplyResult(9999, 100);
            if (level <= 10)
                Check(ReferenceEquals(failed, equipment), "Failure keeps +0 through +10 unchanged");
            else
            {
                Check(failed.InstanceId == equipment.InstanceId && failed.BaseEquipment == equipment.BaseEquipment && failed.Level == level - 1 && failed.Level >= 10, "Downgrade preserves identity and respects +10 floor");
                Check(failed.RemainingDurability == (int)((long)equipment.RemainingDurability * expectedDurability[level - 1] / oldCapacity), "Downgrade preserves condition rounded down");
            }
            Check(equipment.Level == level && equipment.RemainingDurability == oldCapacity / 3, "Original equipment state remains immutable");
        }
        // Domain serialization test, NOT Dinkum save/network/transfer integration.
        var serialized = JsonSerializer.Serialize(success);
        var loaded = JsonSerializer.Deserialize<EquipmentState>(serialized);
        Check(loaded != null && loaded.InstanceId == success.InstanceId && loaded.Level == 10 && loaded.RemainingDurability == 160, "Domain record serialization");
        var maximum = new EquipmentState(Guid.NewGuid(), "maximum-tool", 20, 800);
        var loadedMaximum = JsonSerializer.Deserialize<EquipmentState>(JsonSerializer.Serialize(maximum));
        Check(loadedMaximum != null && loadedMaximum.InstanceId == maximum.InstanceId && loadedMaximum.Level == 20 && loadedMaximum.RemainingDurability == 800, "Maximum-level domain record serialization");
        Reject(() => EnhancementRules.ResultLevel(20, 0), "Max-level successful attempt rejected");
        Reject(() => EnhancementRules.ResultLevel(20, 9999), "Max-level failed attempt rejected");
        Reject(() => maximum.ApplyResult(0, 100), "Max-level equipment attempt rejected");
        Reject(() => EnhancementRules.CostForTarget(0), "Invalid target rejected");
        Reject(() => EnhancementRules.CostForTarget(21), "Above-max target rejected");
        Reject(() => EnhancementRules.AttackOrHarvestPercent(21), "Above-max power rejected");
        Reject(() => EnhancementRules.MaximumDurabilityPercent(-1), "Negative level rejected");
        Reject(() => EnhancementRules.ResultLevel(21, 0), "Above-max current level rejected");
        Reject(() => EnhancementRules.ResultLevel(-1, 0), "Negative current level rejected");
        Reject(() => EnhancementRules.ResultLevel(19, -1), "Negative roll rejected");
        Reject(() => EnhancementRules.ResultLevel(0, 10000), "Invalid roll rejected");
        Reject(() => EnhancementRules.Capacity(int.MaxValue, 10), "Capacity overflow rejected");
        Reject(() => EnhancementRules.Capacity(int.MaxValue, 20), "Maximum capacity overflow rejected");
        Reject(() => item.ApplyResult(9999, 0), "Invalid capacity rejected even on failure");
        Reject(() => new EquipmentState(Guid.NewGuid(), "invalid", 0, 101).ApplyResult(9999, 100), "Over-capacity equipment rejected even on failure");
        Console.WriteLine("PASS: " + assertions + " domain assertions. No game files read or written. This test does not verify Unity or multiplayer runtime integration.");
    }
}
