using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MakeshiftSetup
{
    internal static partial class Engine
    {
        internal const string EnhancementRestoreFlag = "BepInEx/config/LocalEquipmentEnhancement/installed-save-guard.txt";
        private const string LegacyEnhancementRestoreFlag = "BepInEx/config/LocalEnhancementState/restore-required.flag";
        private static void RequireEnhancementConversion(string game)
        {
            string marker = SafePath(game, EnhancementRestoreFlag);
            if (!File.Exists(marker))
            {
                if (Directory.Exists(marker) || File.Exists(SafePath(game, LegacyEnhancementRestoreFlag)) || Directory.Exists(SafePath(game, LegacyEnhancementRestoreFlag)))
                    throw ConversionRequired("변환 인증서가 없거나 안전 표시 파일이 남아 있습니다.");
                return; // Never-run installations and the old display-only preview have no marker.
            }
            try { ValidateEnhancementCertificate(marker); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException)
            { throw ConversionRequired(error.Message); }
        }
        private static IOException ConversionRequired(string detail) => new IOException("강화 장비 변환 확인에 실패하여 복원을 중단했습니다.\n" + detail + "\n강화 모드의 [강화 모드 제거 준비]로 이 PC의 전체 저장 파일을 변환한 뒤 게임을 종료하고 다시 시도하세요.\n다른 참가자는 자신의 PC에서 별도로 변환해야 합니다. 인증서/모드 DLL을 수동으로 삭제하지 마세요. 이 검사는 파일을 변경하지 않습니다.");

        // No save deserialization or mutation: verify the runtime's exact file set
        // and byte hashes. An imported/cloud-restored file invalidates certification.
        internal static void ValidateEnhancementCertificate(string certificatePath)
        {
            RejectEnhancementLinks(certificatePath);
            var info = new FileInfo(certificatePath);
            if (info.Length <= 0 || info.Length > 16 * 1024 * 1024) throw new IOException("변환 인증서 크기가 올바르지 않습니다.");
            byte[] bytes = File.ReadAllBytes(certificatePath);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException e) { throw new IOException("변환 인증서 UTF-8 오류", e); }
            if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = text.Substring(1);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int length = lines.Length;
            if (length > 0 && lines[length - 1].Length == 0) length--;
            if (length < 3 || lines[0] != "CONVERTED" || !EnhancementDigest(lines[1])) throw new IOException("전체 장비 변환이 완료되지 않았거나 인증서 형식이 잘못되었습니다.");
            if (length > 100003) throw new IOException("변환 인증서 파일 수가 제한을 초과했습니다.");
            string root = EnhancementAbsolutePath(lines[2]);
            if (!Directory.Exists(root) || string.Equals(root.TrimEnd('\\'), Path.GetPathRoot(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new IOException("변환된 저장 루트를 확인할 수 없습니다.");
            RejectEnhancementLinks(root);
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string previous = null;
            for (int i = 3; i < length; i++)
            {
                string[] fields = lines[i].Split('\t');
                if (fields.Length != 2 || !EnhancementDigest(fields[0])) throw new IOException("인증서 파일 기록 형식 오류");
                string path = EnhancementAbsolutePath(fields[1]);
                if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("인증서에 저장 루트 밖의 경로가 있습니다.");
                if (previous != null && StringComparer.Ordinal.Compare(previous, path) >= 0) throw new IOException("인증서 경로 순서/중복 오류");
                if (expected.ContainsKey(path)) throw new IOException("인증서에 중복 경로가 있습니다.");
                expected.Add(path, fields[0]); previous = path;
            }
            var actual = EnhancementSaveFiles(root);
            if (!actual.SetEquals(expected.Keys)) throw new IOException("변환 이후 저장 파일이 추가·삭제·이동되었습니다. 다시 변환하세요.");
            foreach (var pair in expected)
            {
                RejectEnhancementLinks(pair.Key);
                using (var stream = new FileStream(pair.Key, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != pair.Value) throw new IOException("변환 이후 저장 파일 내용이 변경되었습니다: " + Path.GetFileName(pair.Key));
            }
            // Detect directory/marker changes during validation. Callers should
            // invoke this immediately before executing an effective destructive plan.
            if (!EnhancementSaveFiles(root).SetEquals(actual) || !File.ReadAllBytes(certificatePath).SequenceEqual(bytes)) throw new IOException("검사 중 저장 파일 또는 인증서가 변경되었습니다. 다시 시도하세요.");
        }
        private static bool EnhancementDigest(string value) => value != null && Regex.IsMatch(value, "\\A[0-9A-F]{64}\\z", RegexOptions.CultureInvariant);
        private static string EnhancementAbsolutePath(string value)
        {
            // Local Windows absolute paths only. Reject device/UNC paths, ADS,
            // traversal and Win32 trailing-dot/space aliases before touching files.
            if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, "\\A[A-Za-z]:\\\\", RegexOptions.CultureInvariant) || value.IndexOf(':', 2) >= 0 || value.IndexOfAny(new[] { '\r', '\n', '\t', '\0', '/' }) >= 0)
                throw new IOException("인증서에 올바르지 않은 로컬 절대 경로가 있습니다.");
            foreach (string part in value.Substring(3).Split('\\'))
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)) throw new IOException("인증서 경로 별칭/상대 경로는 허용하지 않습니다.");
            string full = Path.GetFullPath(value);
            if (!full.Equals(value, StringComparison.OrdinalIgnoreCase)) throw new IOException("인증서 경로가 정규화되지 않았습니다.");
            return full;
        }
        private static void RejectEnhancementLinks(string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("저장 파일/인증서의 링크·정션 경로는 지원하지 않습니다.");
        }
        internal static HashSet<string> EnhancementSaveFiles(string root)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string slot in Directory.EnumerateDirectories(root))
            {
                if (!Regex.IsMatch(Path.GetFileName(slot), "\\ASlot[0-9]+\\z", RegexOptions.CultureInvariant)) continue;
                var pending = new Stack<string>(); pending.Push(slot);
                while (pending.Count != 0)
                {
                    string directory = pending.Pop(); RejectEnhancementLinks(directory);
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        RejectEnhancementLinks(file);
                        if (IsEnhancementSaveFile(file)) { found.Add(Path.GetFullPath(file)); if (found.Count > 100000) throw new IOException("저장 파일 수가 제한을 초과했습니다."); }
                    }
                    foreach (string child in Directory.EnumerateDirectories(directory)) pending.Push(child);
                }
            }
            return found;
        }
        internal static bool IsEnhancementSaveFile(string path)
        {
            string name = Path.GetFileName(path);
            return name.IndexOf(".es3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Regex.IsMatch(name, "\\A(playerInfo|npc|drops)\\.(dat|bak)\\z", RegexOptions.IgnoreCase) ||
                (string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Chests", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, "\\Achest.*\\.(dat|bak)\\z", RegexOptions.IgnoreCase));
        }
    }
}
