using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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

        private Material? _editingMaterial;            // 物料資料庫右邊表單正在修改的物料；新增中為 null

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

            LoadMachine(null);
            ClearMaterialForm();
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
            if (MachinesPage == null || sender is not RadioButton { Tag: string page }) return; // InitializeComponent 期間
            MachinesPage.Visibility = page == "0" ? Visibility.Visible : Visibility.Collapsed;
            MaterialsPage.Visibility = page == "1" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && MachinesPage.IsVisible)
            {
                SaveMachine();
                e.Handled = true;
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!ConfirmLeaveMachine()) e.Cancel = true;
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
                ? "填好工令和機台名稱，再從資料庫選料，最後按「儲存」。"
                : $"建立於 {m.CreatedAt:yyyy/MM/dd HH:mm}　·　最後儲存 {m.UpdatedAt:yyyy/MM/dd HH:mm}";
            DeleteMachineButton.Visibility = m != null ? Visibility.Visible : Visibility.Collapsed;
            _savedSnapshot = Snapshot();
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
            _data.Materials.Add(m);
            Persist();
            _materialView.Refresh();
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
            _editingMaterial.PartNo = m.PartNo;
            _editingMaterial.Name = m.Name;
            _editingMaterial.Spec = m.Spec;
            _editingMaterial.Note = m.Note;
            Persist();
            _materialView.Refresh();
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
            if (MessageBox.Show($"確定要從資料庫刪除 {selected.Count} 項物料嗎？\n\n{names}\n\n已經建好的機台表單不受影響。",
                    "刪除物料", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var m in selected) _data.Materials.Remove(m);
            Persist();
            _materialView.Refresh();
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
            _materialView.Refresh();
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
            if (!ConfirmLeaveMachine()) return; // 匯出的是存好的資料
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
            MessageText.Text = $"已匯出 {_data.Materials.Count} 項物料、{_data.Machines.Count} 台機台到 {dlg.FileName}";
            MessageBox.Show($"匯出完成：{_data.Materials.Count} 項物料、{_data.Machines.Count} 台機台。", title);
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            const string title = "匯入 Excel";
            if (!ConfirmLeaveMachine()) return;
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
                MessageBox.Show($"這個檔案裡找不到可以匯入的資料。\n\n要有「{ExcelService.MaterialSheet}」「{ExcelService.MachineSheet}」「{ExcelService.ItemSheet}」工作表，" +
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

            var message = $"物料：新增 {result.MaterialsAdded} 項、更新 {result.MaterialsUpdated} 項\n機台：新增 {result.MachinesAdded} 台、更新 {result.MachinesUpdated} 台";
            if (result.MaterialsSkipped > 0) message += $"\n有 {result.MaterialsSkipped} 項新物料沒有名稱，沒有加入。";
            if (backup != null) message += $"\n\n匯入前的資料備份在：\n{backup}";
            MessageText.Text = $"匯入完成：物料 {_data.Materials.Count} 項、機台 {_data.Machines.Count} 台";
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
            _materialView.Refresh();
            _machineView.Refresh();
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
                if (!ConfirmLeaveMachine()) return;
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
