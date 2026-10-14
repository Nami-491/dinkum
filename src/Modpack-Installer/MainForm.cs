using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MakeshiftSetup
{
    internal sealed class MainForm : Form
    {
        private readonly TextBox path = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckedListBox mods = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        private readonly TextBox details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(244, 247, 249), BorderStyle = BorderStyle.FixedSingle };
        private readonly TextBox log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        private readonly Button apply = new Button { Text = "선택 모드 적용", AutoSize = true, Height = 38, Padding = new Padding(12, 4, 12, 4) };
        private readonly Button restore = new Button { Text = "적용 전 초기 상태로 복원", AutoSize = true, Height = 38, Padding = new Padding(12, 4, 12, 4) };
        private readonly Button browse = new Button { Text = "폴더 선택", AutoSize = true };
        private readonly Button shortcuts = new Button { Text = "단축키 설정", AutoSize = true, Height = 38, Padding = new Padding(8, 4, 8, 4) };
        private System.Collections.Generic.Dictionary<string, string> shortcutValues;
        private bool busy;
        internal MainForm()
        {
            Text = "Dinkum 통합 모드 관리자 · 4.2 참가자 업데이트 / +20";
            Font = new Font("맑은 고딕", 10f); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(880, 810); MinimumSize = new Size(820, 780); StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 10 };
            foreach (int height in new[] { 43, 33, 32, 208, 94, 70, 48, 35, 0, 28 }) layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
            var title = new Label { Text = "참가자 업데이트 · 기존 장비를 유지하며 +20까지", Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), Dock = DockStyle.Fill };
            layout.Controls.Add(title, 0, 0);
            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(path); pathRow.Controls.Add(browse); layout.Controls.Add(pathRow, 0, 1);
            layout.Controls.Add(new Label { Text = "설치할 모드 선택  ·  선택하지 않은 모드는 변경하지 않습니다", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
            foreach (var mod in Catalog.Mods) mods.Items.Add(mod, mod.DefaultSelected);
            mods.ItemHeight = 28;
            layout.Controls.Add(mods, 0, 3); layout.Controls.Add(details, 0, 4);
            layout.Controls.Add(new Label { Text = "참가자는 기본 선택 7개를 적용하세요. 회수 기능은 방장만 선택합니다.\n구 강화 사용자는 게임 첫 화면에서 [참가자 세이브 호환성 설정]을 완료하세요.\n+11 이상 장비 실패: 1단계 하락(+10 하한) / 파괴 없음 / 전원 같은 버전 필수", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(26, 84, 105), TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            buttons.Controls.Add(apply); buttons.Controls.Add(shortcuts); buttons.Controls.Add(restore); layout.Controls.Add(buttons, 0, 6);
            layout.Controls.Add(new Label { Text = "초기 상태 = 이 통합팩 최초 적용 직전의 모드/설정. 순정 게임·세이브 롤백이 아닙니다.", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(120, 70, 10) }, 0, 7);
            layout.Controls.Add(log, 0, 8);
            layout.Controls.Add(new Label { Text = "비공식 실험적 수정판 · 20명 온라인 실전 미검증 · 생물 묶음은 복원 전에 분리", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 9);
            Controls.Add(layout);
            mods.SelectedIndexChanged += (s, e) => details.Text = (mods.SelectedItem as Mod)?.Description ?? "모드를 선택하면 적용 범위와 주의사항을 볼 수 있습니다.";
            mods.SelectedIndex = 2;
            shortcuts.Click += (s, e) =>
            {
                try { using (var dialog = new ShortcutForm(shortcutValues ?? ShortcutSettings.Read(path.Text))) if (dialog.ShowDialog(this) == DialogResult.OK) { shortcutValues = dialog.Values; log.Text = "단축키 선택 완료. 선택한 모드에만 적용됩니다. [선택 모드 적용]을 눌러 저장하세요."; } }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            };
            path.TextChanged += (s, e) => shortcutValues = null;
            browse.Click += (s, e) => { using (var dialog = new FolderBrowserDialog { Description = "Dinkum.exe가 있는 게임 폴더", ShowNewFolderButton = false }) if (dialog.ShowDialog() == DialogResult.OK) { path.Text = dialog.SelectedPath; RefreshStatus(); } };
            apply.Click += async (s, e) =>
            {
                var selected = mods.CheckedItems.Cast<Mod>().Select(m => m.Key).ToArray();
                if (selected.Length == 0) { MessageBox.Show("설치할 모드를 선택하세요."); return; }
                string game = path.Text;
                string enhancementWarning = selected.Contains("enhancement") ? "\n\n장비 강화: 방장·참가자 전원 동일 버전 설치 필수.\n강화 실행 시 성공·실패 모두 딩크만 소모됩니다.\n제거 전 게임 첫 화면에서 일반 장비 변환이 필요합니다." : "";
                enhancementWarning += "\n\n현재 설치된 게임 빌드 25568182 전용입니다.\n기존 강화 저장은 게임 첫 화면에서 종류를 선택한 뒤 백업·변환합니다.\n업데이트 후 정상 저장한 구 강화 파일은 자동 변환하지 마세요.\n+11 이상 장비의 실패는 1단계 하락합니다(+10 하한).";
                if (selected.Contains("recovery")) enhancementWarning += "\n\n장비 찾기는 방장 전용입니다. 원격 멀티 회수 동기화는 미검증입니다.";
                if (MessageBox.Show("선택한 모드를 적용하고 변경 파일을 백업합니다.\n게임을 종료했는지 확인하세요.\n\n네트워크 전송 중에는 게임을 종료하지 마세요.\n테스트 후보이므로 복사한 섬에서 먼저 확인하세요." + enhancementWarning, "선택 모드 적용", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                var bindings = shortcutValues;
                await Run(() => Engine.InstallSelection(game, selected, bindings));
            };
            restore.Click += async (s, e) =>
            {
                if (MessageBox.Show("통합팩 최초 적용 직전의 관리 대상 모드·설정을 복원합니다.\n현재 파일도 백업하므로 되돌릴 수 있습니다.\n\n강화 사용 시 게임 첫 화면의 [강화 모드 제거 준비]를 먼저 완료하세요.\n변환 확인 전 복원은 차단됩니다. 강화 수치는 제거됩니다.\n생물 묶음도 먼저 1마리씩 분리하세요.\n설치기는 세이브 진행 상황을 되돌리거나 아이템을 변환하지 않습니다.\n처음 설치한 로더도 복원 시 제거될 수 있습니다.", "초기 상태 복원", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                string game = path.Text; await Run(() => Engine.RestoreInitial(game));
            };
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; MessageBox.Show("파일 작업이 끝난 뒤 닫아주세요."); } };
            try { path.Text = Engine.FindGames().FirstOrDefault() ?? ""; RefreshStatus(); }
            catch (Exception e) { log.Text = "게임 폴더를 직접 선택하세요.\r\n" + e.Message; }
        }
        private void RefreshStatus()
        {
            string baseline = !string.IsNullOrWhiteSpace(path.Text) && Directory.Exists(path.Text) ? Engine.ActiveBaseline(path.Text) : null;
            log.Text = "참가자는 기본 선택으로 [선택 모드 적용]을 누르세요.\r\n게임 첫 화면 → 참가자 세이브 호환성 설정 → 캐릭터별 종류 선택 → 백업·변환 → 재시작.\r\n강화 1.1 변환 완료 PC는 재변환 없이 사용 가능합니다.\r\n" +
                (baseline == null ? "첫 적용 시 초기 상태 백업을 만듭니다." : "초기 복원 지점: " + baseline) +
                "\r\n로더가 없는 PC에서는 공식 로더를 다운로드하므로 인터넷이 필요합니다.";
        }
        private async Task Run(Func<string> operation)
        {
            busy = true; apply.Enabled = restore.Enabled = browse.Enabled = mods.Enabled = path.Enabled = shortcuts.Enabled = false;
            log.Text = "검사·백업·파일 작업 중입니다. 창을 닫지 마세요…";
            try { log.Text = (await Task.Run(operation)).Replace("\n", "\r\n"); }
            catch (Exception e) { log.Text = "작업 중단:\r\n" + e.Message + "\r\n\r\n권한 오류일 때만 관리자 권한으로 다시 실행하세요. 보안 프로그램을 끄지 마세요."; }
            finally { busy = false; apply.Enabled = restore.Enabled = browse.Enabled = mods.Enabled = path.Enabled = shortcuts.Enabled = true; }
        }
        internal void Render(string destination)
        {
            StartPosition = FormStartPosition.Manual; Location = new Point(-10000, -10000); ShowInTaskbar = false;
            Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(destination); }
            Close();
        }
    }
}
