using System;
using System.Collections.Generic;

namespace LocalDinkumMods
{
    // A token denotes ONE item/stack, never an item type or a permanent pocket.
    internal sealed class ItemLockToken
    {
        internal readonly Guid Identity;
        internal readonly int ItemId;
        internal ItemLockToken(int itemId) : this(Guid.NewGuid(), itemId) { }
        internal ItemLockToken(Guid identity, int itemId)
        { if (identity == Guid.Empty || itemId < 0) throw new ArgumentException(); Identity = identity; ItemId = itemId; }
        internal ItemLockToken Split() => new ItemLockToken(ItemId);
    }
    internal static class ItemLockRules
    {
        internal static ItemLockToken Keep(ItemLockToken token, int itemId) => token != null && itemId >= 0 && token.ItemId == itemId ? token : null;
        internal static ItemLockToken Merge(ItemLockToken destination, ItemLockToken source, int itemId) => Keep(destination, itemId) ?? Keep(source, itemId);
        internal static string Encode(int[] ids, int[] stacks, Func<int, ItemLockToken> get)
        {
            if (ids == null || stacks == null || ids.Length != stacks.Length || ids.Length > 256) throw new ArgumentException();
            var records = new List<string>();
            for (int i = 0; i < ids.Length; i++)
            {
                var token = Keep(get(i), ids[i]);
                if (token != null) records.Add(i + ":" + ids[i] + ":" + stacks[i] + ":" + token.Identity.ToString("N"));
            }
            return string.Join("|", records);
        }
        internal static Dictionary<int, ItemLockToken> Decode(string text, int[] ids, int[] stacks)
        {
            var result = new Dictionary<int, ItemLockToken>();
            if (text == null || text.Length > 20000 || ids == null || stacks == null || ids.Length != stacks.Length || ids.Length > 256) return result;
            var identities = new HashSet<Guid>();
            foreach (var record in text.Split('|'))
            {
                var fields = record.Split(':');
                if (fields.Length != 4 || !int.TryParse(fields[0], out int slot) || !int.TryParse(fields[1], out int id) || !int.TryParse(fields[2], out int amount) ||
                    !Guid.TryParseExact(fields[3], "N", out Guid identity) || identity == Guid.Empty || slot < 0 || slot >= ids.Length || id < 0 || amount < 0 ||
                    ids[slot] != id || stacks[slot] != amount || result.ContainsKey(slot) || !identities.Add(identity)) continue;
                result.Add(slot, new ItemLockToken(identity, id));
            }
            return result;
        }
        internal static void Test()
        {
            var a = new ItemLockToken(17); var b = new ItemLockToken(17);
            if (a.Identity == b.Identity || Keep(a, 18) != null || Keep(a, -1) != null || Keep(a, 17) != a) throw new Exception("Individual item identity");
            var split = a.Split();
            if (split.ItemId != a.ItemId || split.Identity == a.Identity || Merge(null, a, 17) != a || Merge(b, a, 17) != b) throw new Exception("Split/merge rules");
            var ids = new[] { 17, 17, -1 }; var amounts = new[] { 120, 120, 0 };
            string encoded = Encode(ids, amounts, i => i == 1 ? a : null);
            var decoded = Decode(encoded, ids, amounts);
            if (decoded.Count != 1 || decoded.ContainsKey(0) || decoded[1].Identity != a.Identity) throw new Exception("Identical copies remain distinct on save/load");
            if (Decode(encoded, new[] { 17, 18, -1 }, amounts).Count != 0 || Decode(encoded, ids, new[] { 120, 119, 0 }).Count != 0) throw new Exception("Stale records rejected");
            if (Decode(encoded + "|" + encoded, ids, amounts).Count != 1 || Decode(new string('x', 20001), ids, amounts).Count != 0) throw new Exception("Record bounds");
        }
    }
}
