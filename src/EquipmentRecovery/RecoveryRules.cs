using System;

namespace LocalDinkumRecovery
{
    internal static class RecoveryRules
    {
        // Different world levels or interiors must never be joined by relocation.
        internal static bool SameArea(int map, int currentMap, bool indoor, int x, int y, bool playerIndoor, int px, int py)
            => map == currentMap && indoor == playerIndoor && (!indoor || (x == px && y == py));
        internal static bool ValidGear(int level, int condition, int maximum)
            => level >= 1 && level <= 20 && maximum > 0 && condition > 0 && condition <= maximum;
        internal static bool Confirmed(uint selected, uint armed, double now, double until, bool unchanged)
            => selected != 0 && selected == armed && !double.IsNaN(now) && now < until && unchanged;
    }
}
