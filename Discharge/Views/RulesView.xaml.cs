using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Discharge.Models;
using Discharge.Services;

namespace Discharge.Views
{
    /// <summary>
    /// 「功能規則」頁：新增、修改、刪除規則，條數沒有上限。
    /// 編輯的是複本，按「儲存」才寫回資料（和機台表單相同）。
    /// </summary>
    public partial class RulesView : UserControl
    {
        private AppData _data = new();
        private Func<bool> _persist = () => true;
        private Action<string> _message = _ => { };
        private Action _changed = () => { };
        private ListCollectionView? _view;

        private FunctionRule? _editing;                // 正在編輯的規則；新增中為 null
        private string _draftId = Guid.NewGuid().ToString("N");
        private ObservableCollection<RuleItemRow> _rows = new();
        private List<string> _draftTypes = new();
        private string _savedSnapshot = "";
        private bool _suppressSelection;

        public RulesView()
        {
            InitializeComponent();
        }

        /// <param name="persist">存檔；失敗時回傳 false。</param>
        /// <param name="message">顯示在狀態列的訊息。</param>
        /// <param name="changed">規則存檔後通知主視窗更新。</param>
        public void Initialize(AppData data, Func<bool> persist, Action<string> message, Action changed)
        {
            _data = data;
            _persist = persist;
            _message = message;
            _changed = changed;

            _view = new ListCollectionView(_data.Rules)
            {
                Filter = o => FunctionRules.Matches((FunctionRule)o, RuleSearchBox.Text),
            };
            _view.SortDescriptions.Add(new SortDescription(nameof(FunctionRule.Name), ListSortDirection.Ascending));
            RuleList.ItemsSource = _view;
            LoadRule(null);
        }

        /// <summary>資料整份換掉（匯入 Excel）之後重新載入，正在看的規則還在就留在那一條。</summary>
        public void Reload()
        {
            var current = _data.Rules.FirstOrDefault(r => r.Id == _editing?.Id);
            _view?.Refresh();
            SelectSilently(current);
            LoadRule(current);
        }

        private void UpdateCounts()
        {
            if (_view == null) return;
            RuleCountText.Text = string.IsNullOrWhiteSpace(RuleSearchBox.Text)
                ? $"{_data.Rules.Count} 條"
                : $"{_view.Count} / {_data.Rules.Count} 條";
            RuleEmptyText.Text = _data.Rules.Count == 0 ? "還沒有規則，\n按「新增規則」建立第一條"
                : _view.Count == 0 ? "找不到符合的規則" : "";
            RuleItemCountText.Text = $"{_rows.Count} 項";
            RuleItemEmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDirtyMark();
        }

        private void LoadRule(FunctionRule? rule)
        {
            _editing = rule;
            _draftId = rule?.Id ?? Guid.NewGuid().ToString("N");
            RuleNameBox.Text = rule?.Name ?? "";
            _draftTypes = rule?.MachineTypes.ToList() ?? new();
            NewTypeBox.Text = "";
            RebuildTypeChips();
            _rows = new ObservableCollection<RuleItemRow>((rule?.Items ?? new()).Select(ToRow));
            RuleItemGrid.ItemsSource = _rows;
            RuleTitle.Text = rule == null ? "新增規則" : rule.Name;
            DeleteRuleButton.Visibility = rule != null ? Visibility.Visible : Visibility.Collapsed;
            _savedSnapshot = Snapshot();
            UpdateCounts();
        }

        private RuleItemRow ToRow(RuleItem item)
        {
            var material = _data.Materials.FirstOrDefault(m => MaterialRules.SamePartNo(m.PartNo, item.PartNo));
            return new RuleItemRow
            {
                PartNo = item.PartNo,
                Name = material?.Name ?? "（資料庫沒有這個料號，不會帶出）",
                Spec = material?.Spec ?? "",
                Quantity = item.Quantity,
            };
        }

        /// <summary>列出所有機種（其他規則、機台表單用過的，加上這條規則新加的）。</summary>
        private void RebuildTypeChips()
        {
            var types = FunctionRules.AllMachineTypes(_data.Rules, _data.Machines);
            foreach (var type in _draftTypes)
                if (!types.Any(t => FunctionRules.SameName(t, type))) types.Add(type);
            RuleTypeChips.ItemsSource = types
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(t => new RuleTypeChip { Name = t, IsSelected = _draftTypes.Any(d => FunctionRules.SameName(d, t)) })
                .ToList();
        }

        /// <summary>按鈕顯示出來時也會收到 Checked，已經選了就不用再加。</summary>
        private void RuleType_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: RuleTypeChip chip } button) return;
            var selected = button.IsChecked == true;
            var existing = _draftTypes.FirstOrDefault(t => FunctionRules.SameName(t, chip.Name));
            if (selected && existing == null) _draftTypes.Add(chip.Name);
            else if (!selected && existing != null) _draftTypes.Remove(existing);
            else return;
            UpdateDirtyMark();
        }

        private void AddType_Click(object sender, RoutedEventArgs e)
        {
            var name = TextRules.Clean(NewTypeBox.Text);
            if (FunctionRules.ValidateMachineType(name) is { } error)
            {
                MessageBox.Show(error, "加入機種", MessageBoxButton.OK, MessageBoxImage.Warning);
                NewTypeBox.Focus();
                return;
            }
            // 已經有的機種（不分大小寫）就直接選它
            var known = FunctionRules.AllMachineTypes(_data.Rules, _data.Machines).FirstOrDefault(t => FunctionRules.SameName(t, name)) ?? name;
            if (!_draftTypes.Any(t => FunctionRules.SameName(t, known))) _draftTypes.Add(known);
            NewTypeBox.Text = "";
            RebuildTypeChips();
            UpdateDirtyMark();
        }

        private void NewTypeBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            AddType_Click(sender, e);
            e.Handled = true;
        }

        private FunctionRule FormToRule() => new()
        {
            Id = _draftId,
            Name = TextRules.Clean(RuleNameBox.Text),
            MachineTypes = _draftTypes.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
            Items = _rows.Select(r => new RuleItem { PartNo = r.PartNo, Quantity = r.Quantity }).ToList(),
        };

        private string Snapshot() => DataStore.Serialize(FormToRule());

        private void CommitEdits() => RuleItemGrid.CommitEdit(DataGridEditingUnit.Row, true);

        private void UpdateDirtyMark() =>
            RuleUnsavedChip.Visibility = Snapshot() != _savedSnapshot ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>有沒存的變更時先詢問；回傳 false 表示取消。選不儲存就丟掉變更。</summary>
        public bool ConfirmLeave()
        {
            CommitEdits();
            if (Snapshot() == _savedSnapshot) return true;
            var answer = MessageBox.Show("功能規則有變更還沒儲存，要先儲存嗎？", "功能規則",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) return Save();
            if (answer != MessageBoxResult.No) return false;
            LoadRule(_editing);
            return true;
        }

        public bool Save()
        {
            const string title = "儲存規則";
            CommitEdits();
            var rule = FormToRule();
            var error = FunctionRules.Validate(rule, _data.Rules);
            if (error != null)
            {
                MessageBox.Show(error, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                if (rule.Name.Length == 0 || error.Contains("名稱") || error.Contains("已經有")) RuleNameBox.Focus();
                return false;
            }

            var index = _editing == null ? -1 : _data.Rules.IndexOf(_editing);
            if (index < 0) _data.Rules.Add(rule);
            else _data.Rules[index] = rule;
            if (_editing != null && _editing.Name != rule.Name)
                FunctionRules.RenameFunction(_data.Machines, rule.Id, rule.Name);
            if (!_persist()) return false;

            _view?.Refresh();
            SelectSilently(rule);
            LoadRule(rule);
            _changed();
            _message($"已儲存規則「{rule.Name}」（{rule.Items.Count} 項料）");
            return true;
        }

        private void SelectSilently(FunctionRule? rule)
        {
            _suppressSelection = true;
            RuleList.SelectedItem = rule;
            if (rule != null) RuleList.ScrollIntoView(rule);
            _suppressSelection = false;
        }

        private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection) return;
            // 搜尋把目前的規則篩掉時選取會變成空的，這時繼續編輯原本的規則
            if (RuleList.SelectedItem is not FunctionRule target || target == _editing) return;
            if (!ConfirmLeave())
            {
                Dispatcher.BeginInvoke(() => SelectSilently(_editing));
                return;
            }
            SelectSilently(target);
            LoadRule(target);
        }

        private void RuleSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _view?.Refresh();
            UpdateCounts();
        }

        private void RuleName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_savedSnapshot.Length > 0) UpdateDirtyMark(); // LoadRule 完成前不用判斷
        }

        private void RuleItemGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            // 這時新的值還沒寫回去，等編輯結束後再判斷
            Dispatcher.InvokeAsync(UpdateDirtyMark, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void NewRule_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmLeave()) return;
            SelectSilently(null);
            LoadRule(null);
            RuleNameBox.Focus();
        }

        private void Save_Click(object sender, RoutedEventArgs e) => Save();

        private void Revert_Click(object sender, RoutedEventArgs e)
        {
            CommitEdits();
            if (Snapshot() == _savedSnapshot) return;
            if (MessageBox.Show("要放棄還沒儲存的變更嗎？", "還原變更", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            LoadRule(_editing);
            _message("已還原成上次儲存的內容");
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            if (MessageBox.Show($"確定要刪除規則「{_editing.Name}」嗎？\n已經建好的機台表單不受影響，上面的料會留著。",
                    "刪除規則", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var removed = _editing;
            _data.Rules.Remove(removed);
            _persist();
            _view?.Refresh();
            SelectSilently(null);
            LoadRule(null);
            _changed();
            _message($"已刪除規則「{removed.Name}」");
        }

        private void Pick_Click(object sender, RoutedEventArgs e)
        {
            if (_data.Materials.Count == 0)
            {
                MessageBox.Show("物料資料庫還沒有物料，請先到「物料資料庫」新增。", "從資料庫選料");
                return;
            }
            CommitEdits();
            var existing = _rows.Select(r => new MachineItem { PartNo = r.PartNo, Quantity = r.Quantity });
            var picker = new MaterialPickerWindow(_data.Materials, existing, "規則") { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true) return;

            foreach (var (material, quantity) in picker.Picks)
            {
                var row = _rows.FirstOrDefault(r => MaterialRules.SamePartNo(r.PartNo, material.PartNo));
                if (row != null) row.Quantity += quantity;
                else _rows.Add(new RuleItemRow { PartNo = material.PartNo, Name = material.Name, Spec = material.Spec, Quantity = quantity });
            }
            RuleItemGrid.Items.Refresh();
            UpdateCounts();
            _message($"加入 {picker.Picks.Count} 項料（記得儲存）");
        }

        private void RemoveItems_Click(object sender, RoutedEventArgs e)
        {
            CommitEdits();
            var selected = RuleItemGrid.SelectedItems.OfType<RuleItemRow>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("請先在表格上選取要移除的料（按住 Ctrl 或 Shift 可以選好幾項）。", "移除料");
                return;
            }
            foreach (var row in selected) _rows.Remove(row);
            UpdateCounts();
            _message($"移除 {selected.Count} 項料（記得儲存）");
        }
    }

    /// <summary>規則表格的一列，名稱、規格是從資料庫查來顯示用的。</summary>
    public class RuleItemRow
    {
        public string PartNo { get; set; } = "";
        public string Name { get; set; } = "";
        public string Spec { get; set; } = "";
        public decimal Quantity { get; set; } = 1;
    }
}
