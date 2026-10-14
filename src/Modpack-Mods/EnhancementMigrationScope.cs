using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LocalDinkumMods
{
    // Pure selection policy. All other candidate save files still participate
    // in the transaction snapshot and backup but must never be replaced.
    internal static class EnhancementMigrationScope
    {
        internal const int Unknown = 0, Skip = 4;
        internal static bool ModernFile(string relative) => Regex.IsMatch(relative.Replace('\\', '/'),
            @"^Slot[0-9]+/(Player|Container)\.es3(\.(bak|bac|enhancement-backup))?$", RegexOptions.CultureInvariant);
        internal static HashSet<string> Select(string root, string[] files, string[] slots, IDictionary<string, int> modes)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                string full = Path.GetFullPath(file);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("저장 폴더 밖의 파일입니다.");
                relative.Add(file, full.Substring(root.Length + 1).Replace('\\', '/'));
            }
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string slot in slots)
            {
                if (!Regex.IsMatch(slot, @"^Slot[0-9]+$")) throw new InvalidDataException("잘못된 슬롯 이름입니다.");
                if (!modes.TryGetValue(slot, out int mode) || mode == Unknown) throw new InvalidDataException(slot + ": 저장 종류 또는 '이번에는 건너뛰기'를 선택하세요.");
                if (mode == Skip) continue;
                if (mode < 1 || mode > 3) throw new InvalidDataException("알 수 없는 저장 종류입니다.");
                if (!relative.Values.Contains(slot + "/Player.es3", StringComparer.Ordinal))
                    throw new InvalidDataException(slot + ": Player.es3 없는 구형 전용 저장은 아직 지원하지 않습니다. 이 슬롯은 건너뛰고 원본을 보존하세요.");
                foreach (var pair in relative)
                    if (pair.Value.StartsWith(slot + "/", StringComparison.Ordinal) && ModernFile(pair.Value)) result.Add(pair.Key);
            }
            if (result.Count == 0) throw new InvalidDataException("변환할 슬롯을 하나 이상 선택하세요.");
            return result;
        }
    }
}
