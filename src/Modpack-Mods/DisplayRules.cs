using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalDinkumMods
{
    internal static class DisplayRules
    {
        internal static HashSet<int> ParseLocks(string text) => new HashSet<int>((text ?? "").Split(',').Select(s => int.TryParse(s, out int i) ? i : -1).Where(i => i >= 0 && i < 256));
        internal static long BasePrice(int value, long count, bool relic) => ((long)Math.Max(0, value) * Math.Max(1, Math.Min(int.MaxValue, count))) / (relic ? 4 : 1);
        internal static bool EligibleForAutoMove(int index, int hotbar, bool available, bool locked) => index >= hotbar && available && !locked;
        internal static void Test()
        {
            if (!ParseLocks("1,2,2,-1,abc,255,256").SetEquals(new[] { 1, 2, 255 })) throw new Exception("lock parsing");
            if (BasePrice(101, 3, true) != 75 || BasePrice(int.MaxValue, int.MaxValue, false) != 4611686014132420609L) throw new Exception("price overflow/rounding");
            var random = new Random(91);
            for (int round = 0; round < 1000; round++)
            {
                var before = Enumerable.Range(0, 44).Select(i => random.Next(-1, 100)).ToArray();
                var copy = (int[])before.Clone(); var locks = new HashSet<int>(Enumerable.Range(0, 44).Where(i => random.Next(4) == 0));
                var movable = Enumerable.Range(0, 44).Where(i => EligibleForAutoMove(i, 11, true, locks.Contains(i))).ToArray();
                var values = movable.Select(i => copy[i]).OrderBy(x => x).ToArray();
                for (int i = 0; i < movable.Length; i++) copy[movable[i]] = values[i];
                if (!before.OrderBy(x => x).SequenceEqual(copy.OrderBy(x => x)) || Enumerable.Range(0, 44).Any(i => (i < 11 || locks.Contains(i)) && copy[i] != before[i])) throw new Exception("locked sort conservation");
            }
        }
    }
}
