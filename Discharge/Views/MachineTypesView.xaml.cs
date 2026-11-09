using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Discharge.Models;
using Discharge.Services;

namespace Discharge.Views
{
    /// <summary>
    /// 「機種」頁：新增、改名、刪除機種，數量沒有上限。按了就存，不用另外按儲存。
    /// </summary>
    public partial class MachineTypesView : UserControl
    {
        private AppData _data = new();
        private Func<bool> _persist = () => true;
        private Action<string> _message = _ => { };
        private Action<string?, string?> _changed = (_, _) => { };
        private string? _selected;
        private bool _suppressSelection;

        public MachineTypesView()
        {
            InitializeComponent();
        }

        /// <param name="persist">存檔；失敗時回傳 false。</param>
        /// <param name="message">顯示在狀態列的訊息。</param>
        /// <param name="changed">機種變了之後通知主視窗（改名時帶原本和新的名稱）。</param>
        public void Initialize(AppData data, Func<bool> persist, Action<string> message, Action<string?, string?> changed)
        {
            _data = data;
            _persist = persist;
            _message = message;
            _changed = changed;
            Reload();
        }

        /// <summary>規則、表單或整份資料變了之後重新整理（用到的數量會變）。</summary>
        public void Reload()
        {
            if (_selected != null) _selected = MachineTypeRules.Find(_data.MachineTypes, _selected);
            var rows = _data.MachineTypes
                .Where(t => TextRules.Matches(TypeSearchBox.Text, t))
                .Select(t =>
                {
                    var (rules, machines) = MachineTypeRules.Usage(_data, t);
                    return new MachineTypeRow
                    {
                        Name = t,
                        UsageText = rules.Count == 0 && machines.Count == 0 ? "還沒有用到"
                            : $"{rules.Count} 條規則　·　{machines.Count} 張表單",
                    };
                })
                .ToList();

            _suppressSelection = true;
            TypeList.ItemsSource = rows;
            TypeList.SelectedItem = rows.FirstOrDefault(r => _selected != null && FunctionRules.SameName(r.Name, _selected));
            _suppressSelection = false;

            TypeCountText.Text = string.IsNullOrWhiteSpace(TypeSearchBox.Text)
                ? $"{_data.MachineTypes.Count} 個"
                : $"{rows.Count} / {_data.MachineTypes.Count} 個";
            TypeEmptyText.Text = _data.MachineTypes.Count == 0 ? "還沒有機種，\n在右邊打名稱按「新增」"
                : rows.Count == 0 ? "找不到符合的機種" : "";
            ShowSelected();
        }

        private void ShowSelected()
        {
            var has = _selected != null;
            SelectedPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            NoSelectionText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!has) return;

            SelectedTitle.Text = _selected;
            RenameBox.Text = _selected;
            var (rules, machines) = MachineTypeRules.Usage(_data, _selected!);
            RuleUsageText.Text = rules.Count == 0 ? "規則：沒有"
                : $"規則（{rules.Count} 條）：{string.Join("、", rules.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}";
            MachineUsageText.Text = machines.Count == 0 ? "機台表單：沒有"
                : $"機台表單（{machines.Count} 張）：{string.Join("、", machines.Select(m => $"{m.WorkOrder} {m.Name}").Take(20))}{(machines.Count > 20 ? "…" : "")}";
        }

        private void TypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection || TypeList.SelectedItem is not MachineTypeRow row) return;
            _selected = row.Name;
            ShowSelected();
        }

        private void TypeSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TypeList != null) Reload();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var name = TextRules.Clean(NewTypeNameBox.Text);
            if (MachineTypeRules.Validate(_data.MachineTypes, name) is { } error)
            {
                MessageBox.Show(error, "新增機種", MessageBoxButton.OK, MessageBoxImage.Warning);
                NewTypeNameBox.Focus();
                return;
            }
            MachineTypeRules.Add(_data, name);
            if (!_persist()) return;

            NewTypeNameBox.Text = "";
            _selected = name;
            TypeSearchBox.Text = ""; // 搜尋中也看得到剛加的
            Reload();
            _changed(null, null);
            _message($"已新增機種「{name}」");
            NewTypeNameBox.Focus();
        }

        private void NewTypeNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            Add_Click(sender, e);
            e.Handled = true;
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            const string title = "機種改名";
            var oldName = _selected;
            var name = TextRules.Clean(RenameBox.Text);
            if (name == oldName) return;
            if (MachineTypeRules.Validate(_data.MachineTypes, name, except: oldName) is { } error)
            {
                MessageBox.Show(error, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                RenameBox.Focus();
                return;
            }

            var (rules, machines) = MachineTypeRules.Usage(_data, oldName);
            MachineTypeRules.Rename(_data, oldName, name);
            if (!_persist()) return;

            _selected = name;
            Reload();
            _changed(oldName, name);
            _message($"機種「{oldName}」改名為「{name}」（{rules.Count} 條規則、{machines.Count} 張表單一起改）");
        }

        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            Rename_Click(sender, e);
            e.Handled = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            var name = _selected;
            var (rules, machines) = MachineTypeRules.Usage(_data, name);
            var onlyThis = MachineTypeRules.OnlyThisType(_data, name);

            var lines = new List<string> { $"確定要刪除機種「{name}」嗎？" };
            if (rules.Count > 0) lines.Add($"\n・{rules.Count} 條規則的適用機種會拿掉「{name}」。");
            if (onlyThis.Count > 0)
                lines.Add($"・其中 {onlyThis.Count} 條只適用這個機種，刪除後沒有設定機種，會變成所有機種都能用：\n　{string.Join("、", onlyThis.Select(r => r.Name))}");
            if (machines.Count > 0)
                lines.Add($"・{machines.Count} 張機台表單選了這個機種，表單上的機種會留著並標示「已刪除的機種」，可以再改選別的。");
            if (rules.Count == 0 && machines.Count == 0) lines.Add("\n沒有規則或機台表單用到這個機種。");
            if (MessageBox.Show(string.Join("\n", lines), "刪除機種", MessageBoxButton.YesNo,
                    onlyThis.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            MachineTypeRules.Remove(_data, name);
            if (!_persist()) return;
            _selected = null;
            Reload();
            _changed(null, null);
            _message($"已刪除機種「{name}」");
        }
    }

    /// <summary>「機種」頁清單的一列。</summary>
    public class MachineTypeRow
    {
        public string Name { get; set; } = "";
        public string UsageText { get; set; } = "";
    }
}
