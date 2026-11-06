using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Discharge.Models;
using Discharge.Services;

namespace Discharge.Views
{
    /// <summary>從物料資料庫勾選好幾項料，一次加進機台表單。</summary>
    public partial class MaterialPickerWindow : Window
    {
        private readonly List<PickRow> _rows;
        private readonly ListCollectionView _view;
        private readonly string _target;

        /// <summary>按「加入」後的結果：勾選的物料和數量。</summary>
        public List<(Material Material, decimal Quantity)> Picks { get; } = new();

        /// <param name="target">要加進哪裡，顯示用（表單、規則）。</param>
        public MaterialPickerWindow(IEnumerable<Material> materials, IEnumerable<MachineItem> inForm, string target = "表單")
        {
            InitializeComponent();
            _target = target;
            OkText.Text = $"加入{target}";

            // 已經有的料號和數量，列表上標示「已在表單」
            var existing = inForm
                .GroupBy(i => i.PartNo.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.OrdinalIgnoreCase);
            _rows = materials
                .OrderBy(m => m.PartNo, StringComparer.OrdinalIgnoreCase)
                .Select(m => new PickRow(m, existing.GetValueOrDefault(m.PartNo.Trim()), target))
                .ToList();
            foreach (var row in _rows) row.PropertyChanged += (_, _) => UpdateSummary();

            _view = new ListCollectionView(_rows)
            {
                Filter = o => o is PickRow r
                    && (OnlySelectedBox.IsChecked != true || r.Selected)
                    && MaterialRules.Matches(r.Material, SearchBox.Text),
            };
            RowsGrid.ItemsSource = _view;
            UpdateSummary();
            Loaded += (_, _) => SearchBox.Focus();
        }

        private void UpdateSummary()
        {
            var selected = _rows.Count(r => r.Selected);
            var shown = _view?.Count ?? _rows.Count;
            SelectedCountText.Text = $"已勾選 {selected} 項";
            SummaryText.Text = $"顯示 {shown} / 共 {_rows.Count} 項";
            EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            OkButton.IsEnabled = selected > 0;
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_view == null) return; // InitializeComponent 期間
            RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            _view.Refresh();
            UpdateSummary();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _view.Cast<PickRow>().ToList()) row.Selected = true;
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.Selected = false;
            if (OnlySelectedBox.IsChecked == true) _view.Refresh();
            UpdateSummary();
        }

        /// <summary>選取好幾列後按空白鍵：一次勾選（全部已勾選時改成取消）。</summary>
        private void RowsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || e.OriginalSource is not DataGridCell) return;
            var rows = RowsGrid.SelectedItems.OfType<PickRow>().ToList();
            if (rows.Count == 0) return;
            var value = !rows.All(r => r.Selected);
            foreach (var row in rows) row.Selected = value;
            e.Handled = true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var picked = _rows.Where(r => r.Selected).ToList();
            if (picked.Count == 0) return;

            var bad = picked.FirstOrDefault(r => r.Quantity <= 0);
            if (bad != null)
            {
                MessageBox.Show($"料號 {bad.Material.PartNo} 的數量要大於 0。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var again = picked.Where(r => r.InForm > 0).ToList();
            if (again.Count > 0)
            {
                var names = string.Join("\n", again.Take(10).Select(r => $"{r.Material.PartNo} {r.Material.Name}")) + (again.Count > 10 ? "\n…" : "");
                if (MessageBox.Show($"下面 {again.Count} 項已經在{_target}上，加入後會把數量加上去：\n\n{names}\n\n要繼續嗎？",
                        Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }

            Picks.AddRange(picked.Select(r => (r.Material, r.Quantity)));
            DialogResult = true;
        }
    }

    /// <summary>選料視窗的一列。</summary>
    public class PickRow : INotifyPropertyChanged
    {
        private readonly string _target;

        public PickRow(Material material, decimal inForm, string target)
        {
            Material = material;
            InForm = inForm;
            _target = target;
        }

        public Material Material { get; }

        /// <summary>已經有的數量（0 表示還沒有）。</summary>
        public decimal InForm { get; }

        public string Status => InForm > 0 ? $"已在{_target}（{InForm:0.##}）" : "";

        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; OnChanged(); } }
        }

        private decimal _quantity = 1;
        /// <summary>數量；改了數量就自動勾選。</summary>
        public decimal Quantity
        {
            get => _quantity;
            set
            {
                if (_quantity == value) return;
                _quantity = value;
                OnChanged();
                if (value > 0) Selected = true;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
