using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Discharge.Models;
using Discharge.Services;
using Discharge.Views;
using Microsoft.Win32;

namespace Discharge
{
    public partial class MainWindow : Window
    {
        public const string AppTitle = "Discharge";

        private readonly UpdateService _updater = new();
        private readonly AppData _data;
        private readonly ListCollectionView _machineView;
        private readonly ListCollectionView _materialView;

        // 機台表單編輯的是複本（_draft），按「儲存」才寫回 _data
        private Machine? _editing;                     // 正在編輯的機台；新增中為 null
        private Machine _draft = new();
        private ObservableCollection<MachineItem> _draftItems = new();
        private string _savedSnapshot = "";            // 載入時的內容，用來判斷有沒有沒存的變更
        private bool _suppressSelection;
        private ListCollectionView? _chipView;         // 機台表單上的功能按鈕

        private Material? _editingMaterial;            // 物料資料庫右邊表單正在修改的物料；新增中為 null
        private List<MaterialRuleRow> _materialRuleRows = new();
        private bool _suppressPageTab;

        public MainWindow(AppData data)
        {
            InitializeComponent();
            _data = data;
            Title = _updater.IsRelease ? $"{AppTitle} v{_updater.CurrentVersion}" : $"{AppTitle}（開發版）";
            StatusText.Text = $"版本 {_updater.CurrentVersion}";

            _machineView = new ListCollectionView(_data.Machines)
            {
                Filter = o => MachineRules.Matches((Machine)o, MachineSearchBox.Text),
            };
            _machineView.SortDescriptions.Add(new SortDescription(nameof(Machine.WorkOrder), ListSortDirection.Ascending));
            _machineView.SortDescriptions.Add(new SortDescription(nameof(Machine.Name), ListSortDirection.Ascending));
            MachineList.ItemsSource = _machineView;

            _materialView = new ListCollectionView(_data.Materials)
            {
                Filter = o => MaterialRules.Matches((Material)o, MaterialSearchBox.Text),
            };
            _materialView.SortDescriptions.Add(new SortDescription(nameof(Material.PartNo), ListSortDirection.Ascending));
            MaterialGrid.ItemsSource = _materialView;
            FunctionRules.FillFunctionText(_data.Materials, _data.Rules);

            RulesEditor.Initialize(_data, Persist, text => MessageText.Text = text, OnRulesChanged);
            RulesEditor.ManageTypesRequested += () => TypesTabButton.IsChecked = true;
            TypesEditor.Initialize(_data, Persist, text => MessageText.Text = text, OnTypesChanged);
            LoadMachine(null);
            ClearMaterialForm();
            UpdateCounts();
        }

        /// <summary>規則存檔或刪除後，物料的「帶出的功能」和機台表單上的功能按鈕跟著更新。</summary>
        private void OnRulesChanged()
        {
            RefreshMaterials();
            LoadMaterialRules(_editingMaterial?.PartNo);

            CommitItemEdits();
            if (Snapshot() == _savedSnapshot)
            {
                LoadMachine(_editing); // 規則可能改名，表單沒改過就直接重新載入
                return;
            }
            FormToMachine();
            foreach (var rule in _data.Rules) FunctionRules.RenameFunction(new[] { _draft }, rule.Id, rule.Name);
            ItemGrid.Items.Refresh();
            RebuildMachineTypes();
            RebuildFunctionChips();
            UpdateCounts();
        }

        /// <summary>
        /// 「機種」頁新增、改名、刪除後：規則頁的選單、機台表單的機種和工單功能跟著更新。
        /// 改名時，正在編輯、還沒存的表單上的機種也一起改。
        /// </summary>
        private void OnTypesChanged(string? oldName, string? newName)
        {
            RulesEditor.Reload();
            _machineView.Refresh();

            CommitItemEdits();
            if (Snapshot() == _savedSnapshot)
            {
                LoadMachine(_editing);
                return;
            }
            FormToMachine();
            if (oldName != null && newName != null && FunctionRules.SameName(_draft.MachineType, oldName))
                _draft.MachineType = newName;
            RebuildMachineTypes();
            RebuildFunctionChips();
            UpdateCounts();
        }

        private void RefreshMaterials()
        {
            FunctionRules.FillFunctionText(_data.Materials, _data.Rules);
            _materialView.Refresh();
        }

        /// <summary>物料或規則上的料號變了：規則頁重新查一次名稱、規格。</summary>
        private void AfterMaterialsChanged()
        {
            RefreshMaterials();
            RulesEditor.Reload();
            UpdateCounts();
        }

        private bool Persist()
        {
            try
            {
                DataStore.Save(_data);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show($"存檔失敗：{ex.Message}", AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>更新數量標籤、清單空白時的提示和「尚未儲存」標籤。</summary>
        private void UpdateCounts()
        {
            var machineSearch = !string.IsNullOrWhiteSpace(MachineSearchBox.Text);
            MachineCountText.Text = machineSearch
                ? $"{_machineView.Count} / {_data.Machines.Count} 台"
                : $"{_data.Machines.Count} 台";
            MachineEmptyText.Text = _data.Machines.Count == 0 ? "還沒有機台，\n按「新增機台」開始建立表單"
                : _machineView.Count == 0 ? "找不到符合的機台" : "";

            var materialSearch = !string.IsNullOrWhiteSpace(MaterialSearchBox.Text);
            MaterialCountText.Text = materialSearch
                ? $"{_materialView.Count} / {_data.Materials.Count} 項"
                : $"{_data.Materials.Count} 項";
            MaterialEmptyText.Text = _data.Materials.Count == 0 ? "資料庫還沒有物料，\n在右邊填好料號、名稱後按「新增」，或從 Excel 複製後按「從剪貼簿貼上」"
                : _materialView.Count == 0 ? "找不到符合的物料" : "";

            ItemCountText.Text = $"{_draftItems.Count} 項";
            ItemEmptyPanel.Visibility = _draftItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateDirtyMark();
        }

        private void PageTab_Checked(object sender, RoutedEventArgs e)
        {
            if (RulesPage == null || _suppressPageTab || sender is not RadioButton { Tag: string page }) return; // InitializeComponent 期間

            // 離開規則頁前先存好，別的頁面才看得到最新的規則
            if (RulesPage.Visibility == Visibility.Visible && page != "2" && !RulesEditor.ConfirmLeave())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _suppressPageTab = true;
                    RulesTabButton.IsChecked = true;
                    _suppressPageTab = false;
                });
                return;
            }
            MachinesPage.Visibility = page == "0" ? Visibility.Visible : Visibility.Collapsed;
            MaterialsPage.Visibility = page == "1" ? Visibility.Visible : Visibility.Collapsed;
            RulesPage.Visibility = page == "2" ? Visibility.Visible : Visibility.Collapsed;
            TypesPage.Visibility = page == "3" ? Visibility.Visible : Visibility.Collapsed;
            if (page == "3") TypesEditor.Reload(); // 規則、表單存過之後，用到的數量會變
        }

        private void ManageRules_Click(object sender, RoutedEventArgs e) => RulesTabButton.IsChecked = true;

        private void ManageTypes_Click(object sender, RoutedEventArgs e)
        {
            MachineTypeDropdown.IsChecked = false;
            TypesTabButton.IsChecked = true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
            if (MachinesPage.IsVisible) SaveMachine();
            else if (RulesPage.IsVisible) RulesEditor.Save();
            else return;
            e.Handled = true;
        }

        private bool ConfirmLeaveAll() => ConfirmLeaveMachine() && RulesEditor.ConfirmLeave();

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!ConfirmLeaveAll()) e.Cancel = true;
        }

        // ---------- 機台表單 ----------

        private void LoadMachine(Machine? m)
        {
            _editing = m;
            _draft = m == null ? new Machine() : DataStore.Clone(m);
            _draftItems = new ObservableCollection<MachineItem>(_draft.Items);
            WorkOrderBox.Text = _draft.WorkOrder;
            MachineNameBox.Text = _draft.Name;
            ItemGrid.ItemsSource = _draftItems;
            FormTitle.Text = m == null ? "新增機台" : $"{m.WorkOrder}　{m.Name}";
            FormSubtitle.Text = m == null
                ? "填好工令和機台名稱，選工單上的功能帶出料，最後按「儲存」。"
                : $"建立於 {m.CreatedAt:yyyy/MM/dd HH:mm}　·　最後儲存 {m.UpdatedAt:yyyy/MM/dd HH:mm}";
            DeleteMachineButton.Visibility = m != null ? Visibility.Visible : Visibility.Collapsed;
            RebuildMachineTypes();
            RebuildFunctionChips();
            _savedSnapshot = Snapshot();
            UpdateCounts();
        }

        // ---------- 機種：決定工單功能能選哪些 ----------

        private void RebuildMachineTypes()
        {
            // 選項是「機種」頁的清單；表單選的機種後來被刪掉的，也列出來並標示
            var options = new List<MachineTypeOption> { new() };
            options.AddRange(_data.MachineTypes.Select(t => new MachineTypeOption { Value = t }));
            var deleted = _draft.MachineType.Length > 0 && !MachineTypeRules.Contains(_data.MachineTypes, _draft.MachineType);
            if (deleted) options.Add(new MachineTypeOption { Value = _draft.MachineType, Deleted = true });

            _suppressSelection = true;
            MachineTypeList.ItemsSource = options;
            MachineTypeList.SelectedItem = options.FirstOrDefault(o => FunctionRules.SameName(o.Value, _draft.MachineType)) ?? options[0];
            _suppressSelection = false;
            MachineTypeHint.Visibility = _data.MachineTypes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MachineTypeText.Text = _draft.MachineType.Length == 0 ? "選擇機種…"
                : deleted ? $"{_draft.MachineType}（已刪除的機種）" : _draft.MachineType;
            MachineTypeText.Foreground = (System.Windows.Media.Brush)FindResource(_draft.MachineType.Length == 0 ? "SubtleBrush" : "TextBrush");
        }

        private void MachineTypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection || MachineTypeList.SelectedItem is not MachineTypeOption option) return;
            MachineTypeDropdown.IsChecked = false;
            if (option.Value == _draft.MachineType) return;

            _draft.MachineType = option.Value;
            RebuildMachineTypes();
            RebuildFunctionChips();
            var unavailable = _draft.Functions.Count(f => _data.Rules.FirstOrDefault(r => r.Id == f.RuleId) is { } rule
                && !FunctionRules.IsAvailable(rule, _draft.MachineType));
            MessageText.Text = unavailable > 0
                ? $"機種改成「{option.Display}」。已選的功能有 {unavailable} 個這個機種不適用，需要的話到工單功能取消（記得儲存）"
                : $"機種改成「{option.Display}」（記得儲存）";
            UpdateCounts();
        }

        /// <summary>
        /// 下拉選單打開時，下面的按鈕暫時點不到：在選單外點按鈕關閉選單時，
        /// 不會又被同一下點擊打開。
        /// </summary>
        private void DropdownPopup_Opened(object? sender, EventArgs e)
        {
            if (sender is Popup { PlacementTarget: UIElement target }) target.IsHitTestVisible = false;
        }

        private void DropdownPopup_Closed(object? sender, EventArgs e)
        {
            if (sender is Popup { PlacementTarget: UIElement target })
                Dispatcher.BeginInvoke(() => target.IsHitTestVisible = true);
        }

        // ---------- 工單功能：選了就帶出料 ----------

        /// <summary>只列出這個機種能用的功能；已經選了但不適用、或規則刪掉的，也列出來才取消得掉。</summary>
        private void RebuildFunctionChips()
        {
            var chips = _data.Rules
                .Where(r => FunctionRules.IsAvailable(r, _draft.MachineType) || _draft.Functions.Any(f => f.RuleId == r.Id))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new FunctionChip
                {
                    RuleId = r.Id,
                    Name = r.Name,
                    IsSelected = _draft.Functions.Any(f => f.RuleId == r.Id),
                    NotAvailable = !FunctionRules.IsAvailable(r, _draft.MachineType),
                })
                .ToList();
            chips.AddRange(_draft.Functions
                .Where(f => _data.Rules.All(r => r.Id != f.RuleId))
                .Select(f => new FunctionChip { RuleId = f.RuleId, Name = f.Name, IsSelected = true, RuleDeleted = true }));

            _chipView = new ListCollectionView(chips)
            {
                Filter = o => TextRules.Matches(FunctionFilterBox.Text, ((FunctionChip)o).Name),
            };
            FunctionChips.ItemsSource = _chipView;
            UpdateFunctionHeader();
        }

        private void UpdateFunctionHeader()
        {
            FunctionCountText.Text = $"已選 {_draft.Functions.Count} 個";
            FunctionSummaryText.Text = _draft.Functions.Count == 0 ? "選擇工單上的功能…" : string.Join("、", _draft.Functions.Select(f => f.Name));
            FunctionSummaryText.Foreground = (System.Windows.Media.Brush)FindResource(_draft.Functions.Count == 0 ? "SubtleBrush" : "TextBrush");

            var hasRules = _data.Rules.Count > 0;
            var anyListed = _chipView is { IsEmpty: false } || _draft.Functions.Count > 0;
            FunctionEmptyText.Text = !hasRules && _draft.Functions.Count == 0 ? "還沒有功能規則，按「管理規則」新增。"
                : _chipView?.Count == 0 && !string.IsNullOrWhiteSpace(FunctionFilterBox.Text) ? "找不到符合的功能"
                : !anyListed && _draft.MachineType.Length == 0 ? "先選機種，才會列出這個機種能用的功能。"
                : !anyListed ? $"「{_draft.MachineType}」沒有能用的功能，到「功能規則」設定適用機種。"
                : "";
            FunctionEmptyText.Visibility = FunctionEmptyText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FunctionFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _chipView?.Refresh();
            if (_chipView != null) UpdateFunctionHeader();
        }

        /// <summary>按鈕顯示出來時也會收到 Checked，表單上已經選了就不用再帶一次。</summary>
        private void FunctionChip_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { DataContext: FunctionChip chip } button) return;
            var selected = button.IsChecked == true;
            if (selected == _draft.Functions.Any(f => f.RuleId == chip.RuleId)) return;
            CommitItemEdits();

            if (selected)
            {
                var rule = _data.Rules.FirstOrDefault(r => r.Id == chip.RuleId);
                if (rule == null) return;
                var (added, merged, missing) = FunctionRules.AddFunction(_draftItems, _draft.Functions, rule, _data.Materials);
                var text = rule.Items.Count == 0
                    ? $"「{rule.Name}」這條規則還沒有設定料"
                    : $"「{rule.Name}」帶出 {added} 項料" + (merged > 0 ? $"，{merged} 項原本就在表單上，已把數量加上去" : "");
                if (missing.Count > 0) text += $"。資料庫沒有這些料號，沒有帶出：{string.Join("、", missing)}";
                MessageText.Text = text + "（記得儲存）";
            }
            else
            {
                var removed = FunctionRules.RemoveFunction(_draftItems, _draft.Functions, chip.RuleId);
                MessageText.Text = $"取消「{chip.Name}」，移除 {removed} 項料（記得儲存）";
                if (chip.RuleDeleted || chip.NotAvailable) Dispatcher.InvokeAsync(RebuildFunctionChips);
            }

            ItemGrid.Items.Refresh();
            UpdateFunctionHeader();
            UpdateCounts();
        }

        /// <summary>把畫面上的內容寫進 _draft（表格正在編輯的那一格要先 CommitItemEdits 才會算進去）。</summary>
        private Machine FormToMachine()
        {
            _draft.WorkOrder = TextRules.Clean(WorkOrderBox.Text);
            _draft.Name = TextRules.Clean(MachineNameBox.Text);
            _draft.Items = _draftItems.ToList();
            return _draft;
        }

        private void CommitItemEdits() => ItemGrid.CommitEdit(DataGridEditingUnit.Row, true);

        private string Snapshot() => DataStore.Serialize(FormToMachine());

        private bool IsMachineDirty
        {
            get
            {
                CommitItemEdits();
                return Snapshot() != _savedSnapshot;
            }
        }

        /// <summary>標題旁的「尚未儲存」。不結束表格的編輯，打字到一半不會被打斷。</summary>
        private void UpdateDirtyMark() =>
            UnsavedChip.Visibility = Snapshot() != _savedSnapshot ? Visibility.Visible : Visibility.Collapsed;

        private void MachineField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_savedSnapshot.Length > 0) UpdateDirtyMark(); // LoadMachine 完成前不用判斷
        }

        private void ItemGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            // 這時新的值還沒寫回去，等編輯結束後再判斷
            Dispatcher.InvokeAsync(UpdateDirtyMark, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>表單有沒存的變更時先詢問；回傳 false 表示使用者取消，留在原本的表單。</summary>
        private bool ConfirmLeaveMachine()
        {
            if (!IsMachineDirty) return true;
            var answer = MessageBox.Show("機台表單有變更還沒儲存，要先儲存嗎？", "機台表單",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return answer == MessageBoxResult.Yes ? SaveMachine() : answer == MessageBoxResult.No;
        }

        private bool SaveMachine()
        {
            const string title = "儲存機台";
            CommitItemEdits();
            var m = FormToMachine();
            var error = MachineRules.Validate(m);
            if (error != null)
            {
                MessageBox.Show(error, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                (m.WorkOrder.Length == 0 ? WorkOrderBox : m.Name.Length == 0 ? MachineNameBox : (Control)ItemGrid).Focus();
                return false;
            }
            var dup = MachineRules.FindDuplicate(m, _data.Machines);
            if (dup != null && MessageBox.Show($"已經有工令 {dup.WorkOrder}、機台名稱 {dup.Name} 的表單，還是要儲存嗎？",
                    title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;

            m.UpdatedAt = DateTime.Now;
            var saved = DataStore.Clone(m);
            var index = _editing == null ? -1 : _data.Machines.IndexOf(_editing);
            if (index < 0) _data.Machines.Add(saved);
            else _data.Machines[index] = saved;
            if (!Persist()) return false;

            _machineView.Refresh();
            SelectMachineSilently(saved);
            LoadMachine(saved);
            MessageText.Text = $"已儲存 {saved.WorkOrder} {saved.Name}（{saved.Items.Count} 項料）";
            return true;
        }

        private void SelectMachineSilently(Machine? m)
        {
            _suppressSelection = true;
            MachineList.SelectedItem = m;
            if (m != null) MachineList.ScrollIntoView(m);
            _suppressSelection = false;
        }

        private void MachineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection) return;
            // 搜尋把目前的機台篩掉時選取會變成空的，這時繼續編輯原本的表單
            if (MachineList.SelectedItem is not Machine target || target == _editing) return;

            if (!ConfirmLeaveMachine())
            {
                Dispatcher.BeginInvoke(() => SelectMachineSilently(_editing));
                return;
            }
            SelectMachineSilently(target);
            LoadMachine(target);
        }

        private void MachineSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_machineView == null) return; // InitializeComponent 期間
            _machineView.Refresh();
            UpdateCounts();
        }

        private void NewMachine_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmLeaveMachine()) return;
            SelectMachineSilently(null);
            LoadMachine(null);
            WorkOrderBox.Focus();
        }

        private void SaveMachine_Click(object sender, RoutedEventArgs e) => SaveMachine();

        private void RevertMachine_Click(object sender, RoutedEventArgs e)
        {
            if (!IsMachineDirty) return;
            if (MessageBox.Show("要放棄還沒儲存的變更嗎？", "還原變更", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            LoadMachine(_editing);
            MessageText.Text = "已還原成上次儲存的內容";
        }

        private void DeleteMachine_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            if (MessageBox.Show($"確定要刪除機台「{_editing.WorkOrder} {_editing.Name}」嗎？\n表單上的 {_editing.Items.Count} 項料也會一起刪除。",
                    "刪除機台", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            var removed = _editing;
            _data.Machines.Remove(removed);
            Persist();
            _machineView.Refresh();
            SelectMachineSilently(null);
            LoadMachine(null);
            MessageText.Text = $"已刪除 {removed.WorkOrder} {removed.Name}";
        }

        private void PickMaterials_Click(object sender, RoutedEventArgs e)
        {
            if (_data.Materials.Count == 0)
            {
                MessageBox.Show("物料資料庫還沒有物料，請先到「物料資料庫」新增。", "從資料庫選料");
                return;
            }
            ItemGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var picker = new MaterialPickerWindow(_data.Materials, _draftItems) { Owner = this };
            if (picker.ShowDialog() != true) return;

            var (added, merged) = MachineRules.AddItems(_draftItems, picker.Picks);
            ItemGrid.Items.Refresh();
            UpdateCounts();
            MessageText.Text = merged > 0
                ? $"加入 {added} 項料，{merged} 項原本就在表單上，已把數量加上去（記得儲存）"
                : $"加入 {added} 項料（記得儲存）";
        }

        private void RemoveItems_Click(object sender, RoutedEventArgs e)
        {
            ItemGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var selected = ItemGrid.SelectedItems.OfType<MachineItem>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("請先在表格上選取要移除的料（按住 Ctrl 或 Shift 可以選好幾項）。", "移除料");
                return;
            }
            foreach (var item in selected) _draftItems.Remove(item);
            UpdateCounts();
            MessageText.Text = $"移除 {selected.Count} 項料（記得儲存）";
        }

        // ---------- 物料資料庫 ----------

        private void MaterialSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_materialView == null) return; // InitializeComponent 期間
            _materialView.Refresh();
            UpdateCounts();
        }

        private void MaterialGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MaterialGrid.SelectedItems.Count == 1 && MaterialGrid.SelectedItem is Material m)
            {
                _editingMaterial = m;
                PartNoBox.Text = m.PartNo;
                MaterialNameBox.Text = m.Name;
                SpecBox.Text = m.Spec;
                MaterialNoteBox.Text = m.Note;
                MaterialFormTitle.Text = $"修改物料：{m.PartNo}";
                UpdateMaterialButton.IsEnabled = true;
                LoadMaterialRules(m.PartNo);
            }
            else
            {
                _editingMaterial = null;
                MaterialFormTitle.Text = "新增物料";
                UpdateMaterialButton.IsEnabled = false;
            }
        }

        private void ClearMaterialForm()
        {
            MaterialGrid.UnselectAll();
            _editingMaterial = null;
            PartNoBox.Text = MaterialNameBox.Text = SpecBox.Text = MaterialNoteBox.Text = "";
            MaterialFormTitle.Text = "新增物料";
            UpdateMaterialButton.IsEnabled = false;
            LoadMaterialRules(null);
        }

        /// <summary>物料表單上「哪些功能會帶出這項料」，partNo 是 null 就全部不勾。</summary>
        private void LoadMaterialRules(string? partNo)
        {
            var current = partNo == null ? new Dictionary<string, decimal>() : FunctionRules.RulesOfPart(_data.Rules, partNo);
            _materialRuleRows = _data.Rules
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new MaterialRuleRow
                {
                    RuleId = r.Id,
                    Name = r.MachineTypes.Count == 0 ? r.Name : $"{r.Name}（{r.MachineTypeText}）",
                    IsChecked = current.ContainsKey(r.Id),
                    QuantityText = current.TryGetValue(r.Id, out var q) ? q.ToString("0.##########", CultureInfo.InvariantCulture) : "1",
                })
                .ToList();
            MaterialRuleList.ItemsSource = _materialRuleRows;
            MaterialRuleEmptyText.Visibility = _materialRuleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MaterialRuleScroll.Visibility = _materialRuleRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>讀表單上勾選的功能和數量；數量不對就提示並回傳 null。</summary>
        private Dictionary<string, decimal>? ReadMaterialRules(string title)
        {
            var wanted = new Dictionary<string, decimal>();
            foreach (var row in _materialRuleRows.Where(r => r.IsChecked))
            {
                if (!row.TryQuantity(out var quantity))
                {
                    MessageBox.Show($"功能「{row.Name}」的數量要是大於 0 的數字。", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
                wanted[row.RuleId] = quantity;
            }
            return wanted;
        }

        private Material FormToMaterial(string id) => new()
        {
            Id = id,
            PartNo = TextRules.Clean(PartNoBox.Text),
            Name = TextRules.Clean(MaterialNameBox.Text),
            Spec = TextRules.Clean(SpecBox.Text),
            Note = TextRules.Clean(MaterialNoteBox.Text),
        };

        private bool ValidateMaterial(Material m, string title)
        {
            var error = MaterialRules.Validate(m, _data.Materials);
            if (error == null) return true;
            MessageBox.Show(error, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            (m.Name.Length == 0 && m.PartNo.Length > 0 ? MaterialNameBox : PartNoBox).Focus();
            return false;
        }

        private void AddMaterial_Click(object sender, RoutedEventArgs e)
        {
            var m = FormToMaterial(Guid.NewGuid().ToString("N"));
            if (!ValidateMaterial(m, "新增物料")) return;
            var wanted = ReadMaterialRules("新增物料");
            if (wanted == null) return;
            _data.Materials.Add(m);
            FunctionRules.SetRulesOfPart(_data.Rules, m.PartNo, wanted);
            Persist();
            AfterMaterialsChanged();
            ClearMaterialForm();
            UpdateCounts();
            PartNoBox.Focus();
            MessageText.Text = $"已新增物料 {m.PartNo} {m.Name}";
        }

        private void UpdateMaterial_Click(object sender, RoutedEventArgs e)
        {
            if (_editingMaterial == null) return;
            var m = FormToMaterial(_editingMaterial.Id);
            if (!ValidateMaterial(m, "修改物料")) return;
            var wanted = ReadMaterialRules("修改物料");
            if (wanted == null) return;
            FunctionRules.RenamePart(_data.Rules, _editingMaterial.PartNo, m.PartNo);
            _editingMaterial.PartNo = m.PartNo;
            _editingMaterial.Name = m.Name;
            _editingMaterial.Spec = m.Spec;
            _editingMaterial.Note = m.Note;
            FunctionRules.SetRulesOfPart(_data.Rules, m.PartNo, wanted);
            Persist();
            AfterMaterialsChanged();
            MaterialFormTitle.Text = $"修改物料：{m.PartNo}";
            MessageText.Text = $"已儲存物料 {m.PartNo} {m.Name}";
        }

        private void ClearMaterialForm_Click(object sender, RoutedEventArgs e)
        {
            ClearMaterialForm();
            PartNoBox.Focus();
        }

        private void DeleteMaterials_Click(object sender, RoutedEventArgs e)
        {
            var selected = MaterialGrid.SelectedItems.OfType<Material>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("請先在清單上選取要刪除的物料（按住 Ctrl 或 Shift 可以選好幾項）。", "刪除物料");
                return;
            }
            var names = string.Join("\n", selected.Take(10).Select(m => $"{m.PartNo} {m.Name}")) + (selected.Count > 10 ? "\n…" : "");
            var partNos = selected.Select(m => m.PartNo).ToList();
            var ruleCount = FunctionRules.CountRulesUsing(_data.Rules, partNos);
            var ruleText = ruleCount > 0 ? $"\n也會從 {ruleCount} 條功能規則上移除。" : "";
            if (MessageBox.Show($"確定要從資料庫刪除 {selected.Count} 項物料嗎？\n\n{names}\n\n已經建好的機台表單不受影響。{ruleText}",
                    "刪除物料", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var m in selected) _data.Materials.Remove(m);
            FunctionRules.RemoveParts(_data.Rules, partNos);
            Persist();
            AfterMaterialsChanged();
            ClearMaterialForm();
            UpdateCounts();
            MessageText.Text = $"已刪除 {selected.Count} 項物料";
        }

        private void PasteMaterials_Click(object sender, RoutedEventArgs e)
        {
            const string title = "從剪貼簿貼上";
            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (ExternalException ex)
            {
                MessageBox.Show($"讀取剪貼簿失敗，請再試一次。\n{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var items = MaterialRules.ParseTable(text);
            if (items.Count == 0)
            {
                MessageBox.Show("剪貼簿裡沒有物料。\n\n請在 Excel 選好範圍按 Ctrl+C 再按這個按鈕。第一列可以是標題（料號、名稱、規格、備註，順序不拘）；" +
                    "沒有標題時依序當成 料號、名稱、規格、備註。", title);
                return;
            }

            var (added, updated, skipped) = MaterialRules.Merge(_data.Materials, items);
            Persist();
            AfterMaterialsChanged();
            UpdateCounts();
            var message = $"新增 {added} 項、更新 {updated} 項物料。";
            if (skipped > 0) message += $"\n有 {skipped} 列沒有名稱，沒有加入。";
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            MessageText.Text = $"從剪貼簿新增 {added} 項、更新 {updated} 項物料";
        }

        // ---------- Excel 匯出 / 匯入 ----------

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            const string title = "匯出 Excel";
            if (!ConfirmLeaveAll()) return; // 匯出的是存好的資料
            var dlg = new SaveFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                FileName = $"Discharge_{DateTime.Now:yyyyMMdd}.xlsx",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                ExcelService.Export(_data, dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"匯出失敗（這個檔案可能正被 Excel 開著）：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            MessageText.Text = $"已匯出 {_data.Materials.Count} 項物料、{_data.Machines.Count} 台機台、{_data.Rules.Count} 條規則到 {dlg.FileName}";
            MessageBox.Show($"匯出完成：{_data.Materials.Count} 項物料、{_data.Machines.Count} 台機台、{_data.Rules.Count} 條規則。", title);
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            const string title = "匯入 Excel";
            if (!ConfirmLeaveAll()) return;
            var dlg = new OpenFileDialog { Filter = "Excel 檔案 (*.xlsx)|*.xlsx" };
            if (dlg.ShowDialog(this) != true) return;

            ExcelImport file;
            try
            {
                file = ExcelService.Read(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"這個檔案讀不出來：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (file.Errors.Count > 0)
            {
                var errors = string.Join("\n", file.Errors.Take(10)) + (file.Errors.Count > 10 ? $"\n…還有 {file.Errors.Count - 10} 筆" : "");
                MessageBox.Show($"檔案有問題，沒有匯入任何資料。改好再匯入一次：\n\n{errors}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (file.IsEmpty)
            {
                MessageBox.Show($"這個檔案裡找不到可以匯入的資料。\n\n要有「{ExcelService.MaterialSheet}」「{ExcelService.MachineSheet}」「{ExcelService.ItemSheet}」「{ExcelService.RuleSheet}」工作表，" +
                    "或第一張工作表的第一列有「料號」標題（可以先按「匯出」產生一份範本）。", title);
                return;
            }

            var window = new ImportWindow(dlg.FileName, file, _data) { Owner = this };
            if (window.ShowDialog() != true) return;

            // 先套用在複本上，備份和存檔都成功才換掉畫面上的資料
            var next = DataStore.Clone(_data);
            var result = ExcelService.Apply(next, file, window.Mode);
            string? backup;
            try
            {
                backup = DataStore.BackupBeforeImport();
                DataStore.Save(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show($"存檔失敗，沒有匯入任何資料：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ReplaceData(next);

            var message = $"物料：新增 {result.MaterialsAdded} 項、更新 {result.MaterialsUpdated} 項\n機台：新增 {result.MachinesAdded} 台、更新 {result.MachinesUpdated} 台\n規則：新增 {result.RulesAdded} 條、更新 {result.RulesUpdated} 條\n機種：新增 {result.TypesAdded} 個";
            if (result.MaterialsSkipped > 0) message += $"\n有 {result.MaterialsSkipped} 項新物料沒有名稱，沒有加入。";
            if (backup != null) message += $"\n\n匯入前的資料備份在：\n{backup}";
            MessageText.Text = $"匯入完成：物料 {_data.Materials.Count} 項、機台 {_data.Machines.Count} 台、規則 {_data.Rules.Count} 條";
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>換成新的資料，重新整理畫面；正在看的機台還在就留在那一台。</summary>
        private void ReplaceData(AppData next)
        {
            var currentId = _editing?.Id;
            _data.Materials.Clear();
            _data.Materials.AddRange(next.Materials);
            _data.Machines.Clear();
            _data.Machines.AddRange(next.Machines);
            _data.Rules.Clear();
            _data.Rules.AddRange(next.Rules);
            _data.MachineTypes.Clear();
            _data.MachineTypes.AddRange(next.MachineTypes);
            RefreshMaterials();
            _machineView.Refresh();
            RulesEditor.Reload();
            TypesEditor.Reload();
            ClearMaterialForm();

            var current = _data.Machines.FirstOrDefault(m => m.Id == currentId);
            SelectMachineSilently(current);
            LoadMachine(current);
        }

        // ---------- 啟動時檢查更新（詢問使用者） ----------

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CleanupService.RunInBackground(); // 清掉更新後遺留的舊檔
            await CheckForUpdateAsync(manual: false);
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            await CheckForUpdateAsync(manual: true);
        }

        private async Task CheckForUpdateAsync(bool manual)
        {
            const string title = "檢查更新";
            if (!_updater.IsRelease)
            {
                if (manual)
                    MessageBox.Show("目前是開發版（非安裝版），無法線上更新。", title);
                return;
            }

            try
            {
                var info = await _updater.CheckAsync();
                if (info == null)
                {
                    if (manual) MessageBox.Show($"目前已是最新版本（{_updater.CurrentVersion}）。", title);
                    return;
                }

                // 從 APP 啟動器開的：新版由啟動器下載，這裡只提醒（啟動時的自動檢查不跳視窗，啟動器自己會標示可更新）
                if (!_updater.IsInstalled)
                {
                    StatusText.Text = $"版本 {_updater.CurrentVersion}（有新版 {info.Version}，請從 APP 啟動器更新）";
                    if (manual)
                        MessageBox.Show($"發現新版本 {info.Version}（目前 {_updater.CurrentVersion}）。\n\n請到「APP 啟動器」按「更新到 v{info.Version}」，更新後從啟動器開啟新版。",
                            "有新版本", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var answer = MessageBox.Show(
                    $"發現新版本 {info.Version}（目前 {_updater.CurrentVersion}）。\n\n是否現在更新？更新完成後程式會自動重新啟動。",
                    "有新版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                if (!ConfirmLeaveAll()) return;
                LoadMachine(_editing); // 選了不儲存：丟掉變更，關閉時就不會再問一次

                StatusText.Text = "下載更新中…";
                await _updater.DownloadAndLaunchAsync(info, p => Dispatcher.Invoke(() => StatusText.Text = $"下載更新中… {p}%"));
                // 安裝程式已啟動，結束本程式讓它能覆蓋檔案；安裝完成後會自動重新開啟
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"版本 {_updater.CurrentVersion}";
                // 啟動時的自動檢查失敗（例如沒網路）不打擾使用者
                if (manual) MessageBox.Show($"檢查更新失敗：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
