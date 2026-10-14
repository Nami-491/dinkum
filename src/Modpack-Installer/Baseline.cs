using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace MakeshiftSetup
{
    internal static partial class Engine
    {
        private static Backup ReadBackup(string directory)
        {
            using (var reader = XmlReader.Create(SafePath(directory, "manifest.xml"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                return (Backup)new XmlSerializer(typeof(Backup)).Deserialize(reader);
        }
        internal static string ActiveBaseline(string game)
        {
            string root = SafePath(game, BackupFolder);
            if (!Directory.Exists(root)) return null;
            foreach (string directory in Directory.GetDirectories(root, "initial-*").OrderByDescending(p => p))
                if (File.Exists(SafePath(directory, "manifest.xml")) && ReadBackup(directory).Status == "Baseline") return directory;
            return null;
        }
        private static void EnsureBaseline(string game, Dictionary<string, byte[]> plan)
        {
            var paths = new HashSet<string>(plan.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var mod in Catalog.Mods) { paths.Add(mod.Target); paths.Add(mod.Config); }
            foreach (var file in PluginFiles(game)) if (Catalog.Mods.Any(m => Matches(file, m))) paths.Add(file.Substring(game.Length + 1).Replace('\\', '/'));
            string active = ActiveBaseline(game);
            string root = active ?? SafePath(game, BackupFolder + "/initial-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var manifest = active == null ? new Backup { GamePath = game, Status = "Baseline" } : ReadBackup(active);
            Directory.CreateDirectory(root);
            foreach (string relative in paths)
            {
                if (manifest.Files.Any(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))) continue;
                string file = SafePath(game, relative), hash = FileHash(file);
                if (hash != null)
                {
                    string copy = SafePath(root, "original/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(copy)); File.Copy(file, copy, false);
                    if (FileHash(copy) != hash) throw new IOException("초기 백업 검증 실패: " + relative);
                }
                manifest.Files.Add(new Change { Path = relative, BeforeHash = hash });
            }
            SaveManifest(root, manifest);
        }
        internal static string RestoreInitial(string game)
        {
            game = Path.GetFullPath(game).TrimEnd('\\', '/'); EnsureGameClosed();
            RequireEnhancementConversion(game);
            string root = ActiveBaseline(game);
            if (root == null) throw new IOException("통합팩 적용 전 초기 백업이 없습니다.");
            var initial = ReadBackup(root);
            if (!initial.GamePath.Equals(game, StringComparison.OrdinalIgnoreCase)) throw new IOException("초기 백업 경로 불일치");
            bool removesLoader = initial.Files.Any(f => f.Path == "winhttp.dll" && f.BeforeHash == null);
            if (removesLoader && PluginFiles(game).Any(f => !Catalog.Mods.Any(m => Matches(f, m))))
                throw new IOException("초기 설치 이후 다른 모드가 추가되어 로더 제거가 영향을 줄 수 있습니다. 초기 복원을 중단했습니다. 다른 모드 보존 방침을 먼저 확인하세요.");
            var plan = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in PluginFiles(game)) if (Catalog.Mods.Any(m => Matches(file, m))) plan[file.Substring(game.Length + 1).Replace('\\', '/')] = null;
            foreach (var change in initial.Files)
            {
                if (change.BeforeHash == null) { plan[change.Path] = null; continue; }
                string original = SafePath(root, "original/" + change.Path);
                if (FileHash(original) != change.BeforeHash) throw new IOException("초기 백업 손상: " + change.Path);
                plan[change.Path] = File.ReadAllBytes(original);
            }
            // ExecutePlan backs up today's files before restoring older ones. Nothing is irrecoverably deleted.
            string result = ExecutePlan(game, plan, "통합팩 적용 직전의 초기 모드/설정 상태로 복원했습니다.");
            initial.Status = "Restored"; SaveManifest(root, initial);
            return result + "\n\n초기 백업과 복원 직전 작업 백업을 모두 보존했습니다.\n세이브의 진행 상황과 생물 묶음은 되돌리거나 변환하지 않았습니다.";
        }
    }
}
