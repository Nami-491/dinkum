using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Mono.Cecil;

namespace MakeshiftSetup
{
    internal sealed class ShortcutItem { internal string Mod, Key, Label, Default; }
    internal static class ShortcutSettings
    {
        internal static readonly ShortcutItem[] Items = {
            new ShortcutItem { Mod="inventory", Key="SortInventory", Label="인벤토리 정렬", Default="F6" },
            new ShortcutItem { Mod="inventory", Key="SendToChests", Label="근처 상자 보내기", Default="F7" },
            new ShortcutItem { Mod="inventory", Key="ToggleSlotLock", Label="가리킨 아이템 잠금/해제", Default="F8" },
            new ShortcutItem { Mod="npc", Key="ToggleNpcMap", Label="NPC 지도 표시 전환", Default="F9" },
            new ShortcutItem { Mod="prices", Key="TogglePrices", Label="판매가격 표시 전환", Default="F10" },
            new ShortcutItem { Mod="enhancement", Key="OpenEnhancement", Label="장비 강화 창", Default="F11" },
            new ShortcutItem { Mod="recovery", Key="Scan", Label="강화 드롭 검색 (방장)", Default="F5" },
            new ShortcutItem { Mod="recovery", Key="Next", Label="다음 검색 결과", Default="F5 + LeftShift" },
            new ShortcutItem { Mod="recovery", Key="Recall", Label="선택 장비 회수 (2회)", Default="F5 + LeftControl" },
            new ShortcutItem { Mod="recovery", Key="Close", Label="검색 닫기 / 회수 취소", Default="F5 + LeftAlt" },
        };
        internal static readonly string[] Keys = new[] { "None" }.Concat(Enumerable.Range(1, 12).Select(i => "F" + i)).Concat(Enumerable.Range('A', 26).Select(i => ((char)i).ToString())).Concat(Enumerable.Range(0, 10).Select(i => "Alpha" + i)).Concat(new[] { "Tab", "BackQuote", "Minus", "Equals", "LeftBracket", "RightBracket", "Backslash", "Semicolon", "Quote", "Comma", "Period", "Slash", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "UpArrow", "DownArrow", "LeftArrow", "RightArrow", "KeypadDivide", "KeypadMultiply", "KeypadPlus", "KeypadMinus", "KeypadPeriod", "KeypadEnter" }).Concat(Enumerable.Range(0, 10).Select(i => "Keypad" + i)).ToArray();
        internal static void Validate(Dictionary<string, string> values)
        {
            var seen = new HashSet<string>();
            foreach (var pair in values)
            {
                if (!Items.Any(i => i.Key == pair.Key)) throw new IOException("알 수 없는 단축키: " + pair.Key);
                var parts = pair.Value.Split('+').Select(p => p.Trim()).ToArray();
                if (!Keys.Contains(parts[0]) || parts.Skip(1).Any(p => !new[] { "LeftControl", "LeftShift", "LeftAlt", "RightControl", "RightShift", "RightAlt" }.Contains(p)) || parts.Distinct().Count() != parts.Length)
                    throw new IOException("단축키 형식 오류: " + pair.Value);
                string key = string.Join("+", parts.OrderBy(p => p));
                if (parts[0] != "None" && !seen.Add(key)) throw new IOException("중복 단축키를 다른 키로 바꿔주세요: " + pair.Value);
            }
        }
        internal static string SetValue(string text, string section, string key, string value)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList(); bool inside = false, foundSection = false, replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string trim = lines[i].Trim();
                if (trim.StartsWith("[") && trim.EndsWith("]"))
                {
                    if (inside && !replaced) { lines.Insert(i++, key + " = " + value); replaced = true; }
                    inside = trim == "[" + section + "]"; foundSection |= inside;
                }
                else if (inside && trim.Contains("=") && trim.Split('=')[0].Trim() == key)
                { if (!replaced) { lines[i] = key + " = " + value; replaced = true; } else lines[i] = "# duplicate removed: " + lines[i]; }
            }
            if (!foundSection) { lines.Add("[" + section + "]"); lines.Add(key + " = " + value); }
            else if (!replaced) lines.Add(key + " = " + value);
            return string.Join("\r\n", lines);
        }
        internal static Dictionary<string, string> Read(string game)
        {
            var values = Items.ToDictionary(i => i.Key, i => i.Default);
            if (!Directory.Exists(game)) return values;
            foreach (var group in Items.GroupBy(i => i.Mod))
            {
                var mod = Catalog.Mods.Single(m => m.Key == group.Key); string path = Engine.SafePath(game, mod.Config);
                if (!File.Exists(path)) continue;
                bool inside = false;
                foreach (string line in File.ReadAllLines(path))
                {
                    string trim = line.Trim();
                    if (trim.StartsWith("[")) inside = trim == "[Keybinds]";
                    else if (inside && !trim.StartsWith("#") && trim.Contains("="))
                    { int at = trim.IndexOf('='); string key = trim.Substring(0, at).Trim(); if (group.Any(i => i.Key == key)) values[key] = trim.Substring(at + 1).Trim(); }
                }
            }
            return values;
        }
    }
    internal sealed class ShortcutForm : Form
    {
        internal Dictionary<string, string> Values;
        internal ShortcutForm(Dictionary<string, string> values)
        {
            Text = "내 PC 단축키 설정"; ClientSize = new Size(620, 165 + 38 * ShortcutSettings.Items.Length); Font = new Font("맑은 고딕", 10); StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = ShortcutSettings.Items.Length + 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var boxes = new Dictionary<string, ComboBox>(); int row = 0;
            foreach (var item in ShortcutSettings.Items)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                layout.Controls.Add(new Label { Text = item.Label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
                var box = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown }; box.Items.AddRange(ShortcutSettings.Keys); box.Text = values[item.Key];
                boxes[item.Key] = box; layout.Controls.Add(box, 1, row++);
            }
            var help = new Label { Text = "조합키 직접 입력: F7 + LeftControl (왼쪽 Ctrl + F7)\nNone = 사용 안 함. 게임/다른 모드의 키와 겹치지 않게 선택하세요.\n확인 후 메인 화면에서 [선택 모드 적용]을 눌러야 저장됩니다.", Dock = DockStyle.Fill, AutoSize = false };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75)); layout.Controls.Add(help, 0, row++); layout.SetColumnSpan(help, 2);
            var ok = new Button { Text = "확인", Dock = DockStyle.Right, Width = 100 }; layout.Controls.Add(ok, 1, row);
            ok.Click += (s, e) => { try { var result = boxes.ToDictionary(p => p.Key, p => p.Value.Text.Trim()); ShortcutSettings.Validate(result); Values = result; DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(ex.Message); } };
            Controls.Add(layout);
        }
        internal void Render(string file)
        {
            StartPosition = FormStartPosition.Manual; Location = new Point(-10000, -10000); ShowInTaskbar = false;
            Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(file); }
            Close();
        }
    }
    internal static partial class Engine
    {
        private static void RejectOldMorePlayers(string game)
        {
            foreach (var file in PluginFiles(game))
            {
                try
                {
                    using (var m = ModuleDefinition.ReadModule(file))
                    foreach (var a in m.Types.SelectMany(t => t.CustomAttributes))
                        if (a.AttributeType.FullName == "BepInEx.BepInPlugin" && a.ConstructorArguments.Count >= 3 && (string)a.ConstructorArguments[0].Value == "MorePlayers" &&
                            (!Version.TryParse((string)a.ConstructorArguments[2].Value, out var version) || version < new Version(0, 4, 0)))
                            throw new IOException("구버전 MorePlayers 충돌을 발견해 설치를 중단했습니다: " + file + "\n참가자 PC라면 게임 종료 후 해당 DLL을 plugins 밖에 보관하세요. 방장은 검증된 0.4.0이 필요합니다. 자동 삭제하지 않았습니다.");
                }
                catch (BadImageFormatException) { }
            }
        }
    }
}
