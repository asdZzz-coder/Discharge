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
        private ListCollectionView? _typeView;
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

        /// <param name="rule">要編輯的規則；null 是新增。</param>
        /// <param name="copyFrom">新增時先填入這條規則的內容（複製規則），算是還沒儲存的變更。</param>
        private void LoadRule(FunctionRule? rule, FunctionRule? copyFrom = null)
        {
            _editing = rule;
            _draftId = rule?.Id ?? Guid.NewGuid().ToString("N");
            RuleNameBox.Text = "";
            _draftTypes = new();
            _rows = new ObservableCollection<RuleItemRow>();
            _savedSnapshot = Snapshot(); // 新增時，空白的表單才算「沒有變更」

            var source = rule ?? copyFrom;
            RuleNameBox.Text = rule?.Name ?? (copyFrom == null ? "" : FunctionRules.CopyName(copyFrom.Name, _data.Rules));
            _draftTypes = source?.MachineTypes.ToList() ?? new();
            TypeFilterBox.Text = "";
            RebuildTypeChips();
            _rows = new ObservableCollection<RuleItemRow>((source?.Items ?? new()).Select(ToRow));
            RuleItemGrid.ItemsSource = _rows;
            RuleTitle.Text = rule?.Name ?? (copyFrom == null ? "新增規則" : $"複製「{copyFrom.Name}」");
            RuleActions.Visibility = rule != null ? Visibility.Visible : Visibility.Collapsed;
            if (rule != null) _savedSnapshot = Snapshot();
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

        // ---------- 適用機種：多選下拉選單，數量不限；機種在「機種」頁新增、刪除 ----------

        /// <summary>按了選單裡的「管理機種」。</summary>
        public event Action? ManageTypesRequested;

        /// <summary>機種清單上的，加上這條規則勾了、清單卻沒有的（照理不會有，保險起見也列出來才取消得掉）。</summary>
        private List<string> KnownTypes()
        {
            var types = _data.MachineTypes.ToList();
            foreach (var type in _draftTypes)
                if (!MachineTypeRules.Contains(types, type)) types.Add(type);
            return types.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void RebuildTypeChips()
        {
            var chips = KnownTypes()
                .Select(t => new RuleTypeChip { Name = t, IsSelected = MachineTypeRules.Contains(_draftTypes, t) })
                .ToList();
            _typeView = new ListCollectionView(chips)
            {
                Filter = o => TextRules.Matches(TypeFilterBox.Text, ((RuleTypeChip)o).Name),
            };
            RuleTypeChips.ItemsSource = _typeView;
            UpdateTypeHeader();
        }

        private void UpdateTypeHeader()
        {
            var selected = _draftTypes.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            RuleTypeCountText.Text = selected.Count == 0 ? "（都不選＝所有機種都能用）" : $"已選 {selected.Count} 個";
            RuleTypeSummaryText.Text = selected.Count == 0 ? "所有機種都能用" : string.Join("、", selected);
            RuleTypeSummaryText.Foreground = (System.Windows.Media.Brush)FindResource(selected.Count == 0 ? "SubtleBrush" : "TextBrush");
            ClearTypesButton.IsEnabled = selected.Count > 0;

            RuleTypeEmptyText.Text = _typeView is not { IsEmpty: true } ? ""
                : _data.MachineTypes.Count == 0 && _draftTypes.Count == 0 ? "還沒有機種，按「管理機種」到「機種」頁新增。"
                : "找不到符合的機種";
            RuleTypeEmptyText.Visibility = RuleTypeEmptyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>勾選框顯示出來時也會收到 Checked，已經選了就不用再加。</summary>
        private void RuleType_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: RuleTypeChip chip } box) return;
            var selected = box.IsChecked == true;
            var existing = MachineTypeRules.Find(_draftTypes, chip.Name);
            if (selected && existing == null) _draftTypes.Add(chip.Name);
            else if (!selected && existing != null) _draftTypes.Remove(existing);
            else return;
            UpdateTypeHeader();
            UpdateDirtyMark();
        }

        private void ClearTypes_Click(object sender, RoutedEventArgs e)
        {
            if (_draftTypes.Count == 0) return;
            _draftTypes.Clear();
            RebuildTypeChips();
            UpdateDirtyMark();
        }

        private void ManageTypes_Click(object sender, RoutedEventArgs e)
        {
            RuleTypeDropdown.IsChecked = false;
            ManageTypesRequested?.Invoke();
        }

        private void TypeFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_typeView == null) return; // LoadRule 完成前
            _typeView.Refresh();
            UpdateTypeHeader();
        }

        private void TypeFilterBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            RuleTypeDropdown.IsChecked = false;
            e.Handled = true;
        }

        /// <summary>
        /// 選單打開時，下面的按鈕暫時點不到（在選單外點按鈕關閉時，不會又被同一下點擊打開），
        /// 並把游標放進搜尋框，直接就能打字。
        /// </summary>
        private void RuleTypePopup_Opened(object? sender, EventArgs e)
        {
            RuleTypeDropdown.IsHitTestVisible = false;
            Dispatcher.BeginInvoke(() => TypeFilterBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void RuleTypePopup_Closed(object? sender, EventArgs e)
        {
            TypeFilterBox.Text = "";
            Dispatcher.BeginInvoke(() => RuleTypeDropdown.IsHitTestVisible = true);
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
                if (rule.Name.Length == 0 || error.Contains("名稱") || error.Contains("已經有")) FocusName();
                return false;
            }

            // 帶出的料和別條一模一樣：提醒可能重複了，還是可以存（例如同樣的料，給不同機種用）
            var same = FunctionRules.FindSameItems(rule, _data.Rules);
            if (same.Count > 0)
            {
                var list = string.Join("\n", same.Take(10).Select(r =>
                    $"・{r.Name}（{(r.MachineTypes.Count == 0 ? "所有機種" : string.Join("、", r.MachineTypes))}）"));
                if (same.Count > 10) list += $"\n…還有 {same.Count - 10} 條";
                if (MessageBox.Show($"這條規則帶出的料（{rule.Items.Count} 項，數量也一樣）和下面的規則完全相同，可能重複了：\n\n{list}\n\n還是要儲存嗎？",
                        title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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

        private void FocusName()
        {
            RuleNameBox.Focus();
            RuleNameBox.SelectAll();
        }

        /// <summary>複製目前的規則：開一條新規則，機種和料都一樣，名稱加上「（複製）」，改好再儲存。</summary>
        private void CopyRule_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null || !ConfirmLeave()) return;
            var source = _editing;
            SelectSilently(null);
            LoadRule(null, source);
            FocusName();
            _message($"已複製「{source.Name}」，改好名稱和內容再按「儲存」");
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
