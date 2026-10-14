using System;

namespace LocalDinkum.Enhancement
{
    // Pure rules shared by the game adapter and exhaustive domain tests.
    public static class EnhancementRules
    {
        public const int MaximumLevel = 20;
        // Levels 0-10 retain their original values. Beyond +10, each increment grows
        // by 8 percentage points for power and 12 for durability, ending at 1000/1600.
        private static readonly int[] PowerPercent = { 100, 103, 106, 110, 116, 124, 134, 148, 166, 190, 220, 262, 312, 370, 436, 510, 592, 682, 780, 886, 1000 };
        private static readonly int[] DurabilityPercent = { 100, 105, 110, 118, 128, 142, 160, 184, 216, 258, 320, 394, 480, 578, 688, 810, 944, 1090, 1248, 1418, 1600 };

        public static int AttackOrHarvestPercent(int level) { CheckLevel(level); return PowerPercent[level]; }
        public static int MaximumDurabilityPercent(int level) { CheckLevel(level); return DurabilityPercent[level]; }
        public static AttemptCost CostForTarget(int target)
        {
            if (target < 1 || target > MaximumLevel) throw new ArgumentOutOfRangeException(nameof(target));
            if (target <= 3) return new AttemptCost(9000, 0, 0, 2000);
            if (target <= 6) return new AttemptCost(7000, 0, 0, 5000);
            if (target <= 9) return new AttemptCost(4500, 0, 0, 10000);
            if (target == 10) return new AttemptCost(2500, 0, 0, 20000);
            if (target <= 13) return new AttemptCost(2000, 0, 0, 30000);
            if (target <= 16) return new AttemptCost(1500, 0, 0, 50000);
            if (target <= 19) return new AttemptCost(1000, 0, 0, 80000);
            return new AttemptCost(500, 0, 0, 150000);
        }
        public static bool Succeeds(int target, int serverRoll)
        {
            if (serverRoll < 0 || serverRoll >= 10000) throw new ArgumentOutOfRangeException(nameof(serverRoll));
            return serverRoll < CostForTarget(target).SuccessBasisPoints;
        }
        public static int ResultLevel(int current, int serverRoll)
        {
            CheckLevel(current);
            if (current == MaximumLevel) throw new InvalidOperationException("Already at maximum; do not charge a cost.");
            return Succeeds(current + 1, serverRoll) ? current + 1 : (current <= 10 ? current : Math.Max(10, current - 1));
        }
        public static int Capacity(int baseCapacity, int level)
        {
            if (baseCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(baseCapacity));
            long result = (long)baseCapacity * MaximumDurabilityPercent(level) / 100;
            if (result > int.MaxValue) throw new OverflowException("Enhanced durability exceeds game integer range.");
            return (int)result;
        }
        public static int PreserveCondition(int currentRemaining, int oldCapacity, int newCapacity)
        {
            if (oldCapacity <= 0 || newCapacity <= 0 || currentRemaining < 0 || currentRemaining > oldCapacity) throw new ArgumentOutOfRangeException();
            // Preserve condition ratio. A downgrade must not destroy a tool which
            // still had one durability point merely due to integer rounding.
            return Math.Max(currentRemaining > 0 ? 1 : 0, (int)((long)currentRemaining * newCapacity / oldCapacity));
        }
        private static void CheckLevel(int level)
        { if (level < 0 || level > MaximumLevel) throw new ArgumentOutOfRangeException(nameof(level)); }
    }
    public sealed class AttemptCost
    {
        public int SuccessBasisPoints { get; }
        public int CopperBars { get; }
        public int IronBars { get; }
        public int Dinks { get; }
        internal AttemptCost(int chance, int copper, int iron, int dinks)
        { SuccessBasisPoints = chance; CopperBars = copper; IronBars = iron; Dinks = dinks; }
    }
    // Ownership/location deliberately do not determine the level. The future game adapter
    // must preserve this identity through every storage/drop/transfer/save operation.
    public sealed class EquipmentState
    {
        public Guid InstanceId { get; }
        public string BaseEquipment { get; }
        public int Level { get; }
        public int RemainingDurability { get; }
        public EquipmentState(Guid instanceId, string baseEquipment, int level, int remainingDurability)
        {
            if (instanceId == Guid.Empty || string.IsNullOrWhiteSpace(baseEquipment) || remainingDurability < 0) throw new ArgumentException("Invalid equipment state.");
            EnhancementRules.AttackOrHarvestPercent(level);
            InstanceId = instanceId; BaseEquipment = baseEquipment; Level = level; RemainingDurability = remainingDurability;
        }
        public EquipmentState ApplyResult(int serverRoll, int baseCapacity)
        {
            int oldCapacity = EnhancementRules.Capacity(baseCapacity, Level);
            if (RemainingDurability > oldCapacity) throw new ArgumentException("Remaining durability exceeds capacity.");
            int next = EnhancementRules.ResultLevel(Level, serverRoll);
            if (next == Level) return this;
            int condition = EnhancementRules.PreserveCondition(RemainingDurability, oldCapacity, EnhancementRules.Capacity(baseCapacity, next));
            return new EquipmentState(InstanceId, BaseEquipment, next, condition);
        }
    }
}
