using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;

namespace MakeshiftSetup
{
    // Standalone fixture only; not included in the installer project. The real
    // Engine supplies SafePath. All fixture writes stay in a new workspace folder.
    internal static partial class Engine
    {
        internal static string SafePath(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        internal static void CheckGuardFixture(string game) => RequireEnhancementConversion(game);
    }
    internal static class EnhancementGuardTests
    {
        private static int assertions;
        private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); assertions++; }
        private static void Reject(Action action, string name) { try { action(); } catch (IOException) { assertions++; return; } throw new Exception("Expected rejection: " + name); }
        private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        private static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        private static string Certificate(string saves) => "CONVERTED\n" + new string('A', 64) + "\n" + saves + "\n" + string.Join("\n", Engine.EnhancementSaveFiles(saves).OrderBy(x => x, StringComparer.Ordinal).Select(x => Digest(x) + "\t" + x)) + "\n";
        private static int Main()
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "work", "enhancement-guard-tests-" + Guid.NewGuid().ToString("N"));
            string game = Path.Combine(root, "game"), saves = Path.Combine(root, "isolated-product"); Directory.CreateDirectory(game); Directory.CreateDirectory(saves);
            string marker = Engine.SafePath(game, Engine.EnhancementRestoreFlag);
            Engine.CheckGuardFixture(game); Check(true, "never-run installation");
            Put(marker, "CONVERSION_REQUIRED\nregistry-initializing"); Reject(() => Engine.CheckGuardFixture(game), "active registry");
            string player = Path.Combine(saves, "Slot0", "Player.es3"); Put(player, "original");
            string[] included = { "Slot0/Player.es3.bak", "Slot0/Player.es3.enhancement-backup", "Slot1/Container.es3.tmp", "Slot1/NPC.DAT", "Slot0/drops.bak", "Slot0/playerInfo.dat", "Slot0/Chests/chesth1+2_3+4.dat", "Slot0/chests/chest3+4.bak" };
            foreach (string p in included) Put(Path.Combine(saves, p), p);
            string[] excluded = { "Slot0/MapSave.dat", "Slot0/other.dat", "Slot0/chest3+4.dat", "Slot0/Other/chest3+4.dat", "Slot0/photo.png", "slot9/Player.es3", "SlotA/Player.es3", "Outside/Player.es3" };
            foreach (string p in excluded) Put(Path.Combine(saves, p), p);
            Check(Engine.EnhancementSaveFiles(saves).Count == included.Length + 1, "exact legacy and ES3 scope");
            string valid = Certificate(saves); Put(marker, valid); Engine.CheckGuardFixture(game); Check(true, "valid certificate");
            Put(marker, valid.TrimEnd('\n')); Engine.CheckGuardFixture(game); Check(true, "no final newline accepted");
            Put(marker, valid.Replace("\n", "\r\n")); Engine.CheckGuardFixture(game); Check(true, "CRLF accepted");
            string emptyRoot = Path.Combine(root, "empty-product"); Directory.CreateDirectory(emptyRoot);
            Put(marker, "CONVERTED\n" + new string('A', 64) + "\n" + emptyRoot + "\n"); Engine.CheckGuardFixture(game); Check(true, "empty save set accepted"); Put(marker, valid);
            foreach (string path in Engine.EnhancementSaveFiles(saves))
            { string before = File.ReadAllText(path); Put(path, before + "changed"); Reject(() => Engine.CheckGuardFixture(game), "modified file " + Path.GetFileName(path)); Put(path, before); }
            string extra = Path.Combine(saves, "Slot2", "Player.es3"); Put(extra, "cloud imported"); Reject(() => Engine.CheckGuardFixture(game), "new cloud save"); File.Move(extra, Path.Combine(root, "held-cloud-file"));
            string missing = Path.Combine(root, "held-player"); File.Move(player, missing); Reject(() => Engine.CheckGuardFixture(game), "removed save"); File.Move(missing, player);
            foreach (string path in excluded) Put(Path.Combine(saves, path), "unrelated changed"); Engine.CheckGuardFixture(game); Check(true, "unrelated files ignored");
            string first = valid.Split('\n')[3]; Put(marker, valid.TrimEnd('\n') + "\n" + first + "\n"); Reject(() => Engine.CheckGuardFixture(game), "duplicate record");
            Put(marker, valid.Replace(new string('A', 64), new string('a', 64))); Reject(() => Engine.CheckGuardFixture(game), "lowercase registry hash");
            var sortedLines = valid.TrimEnd('\n').Split('\n'); var swapped = (string[])sortedLines.Clone(); string swap = swapped[3]; swapped[3] = swapped[4]; swapped[4] = swap;
            Put(marker, string.Join("\n", swapped)); Reject(() => Engine.CheckGuardFixture(game), "unsorted records");
            Put(marker, valid.Replace(first.Split('\t')[0], "NOT_A_HASH")); Reject(() => Engine.CheckGuardFixture(game), "invalid file digest");
            Put(marker, "CONVERTED\n" + new string('A', 64) + "\n" + saves + "\n" + new string('0', 64) + "\t" + Path.Combine(root, "outside.es3") + "\n"); Reject(() => Engine.CheckGuardFixture(game), "outside-root path");
            Put(marker, valid.Replace(player, Path.Combine(saves, "Slot0", "..", "Slot0", "Player.es3"))); Reject(() => Engine.CheckGuardFixture(game), "traversal alias");
            Put(marker, valid + "\n"); Reject(() => Engine.CheckGuardFixture(game), "extra blank record");
            File.WriteAllBytes(marker, new byte[] { 0xff, 0xfe, 0xfd }); Reject(() => Engine.CheckGuardFixture(game), "invalid UTF8");
            Put(marker, valid); var beforeHashes = Engine.EnhancementSaveFiles(saves).ToDictionary(p => p, Digest); Engine.CheckGuardFixture(game);
            Check(beforeHashes.All(p => Digest(p.Key) == p.Value), "validation does not mutate saves");
            string provisional = Engine.SafePath(game, "BepInEx/config/LocalEnhancementState/restore-required.flag"); Put(provisional, "old active flag");
            Engine.CheckGuardFixture(game); Check(true, "authoritative converted certificate overrides auxiliary marker");
            File.Move(marker, Path.Combine(root, "certificate-held")); Reject(() => Engine.CheckGuardFixture(game), "auxiliary marker without certificate");
            Console.WriteLine("PASS: " + assertions + " enhancement removal guard assertions; fixtures retained at " + root);
            return 0;
        }
    }
}
