using System.IO;
using System.Windows;
using Discharge.Models;
using Discharge.Services;

namespace Discharge.Views
{
    /// <summary>顯示 Excel 裡有什麼，選擇合併或完全以 Excel 為準。</summary>
    public partial class ImportWindow : Window
    {
        private readonly ExcelImport _file;
        private readonly AppData _current;

        public ImportMode Mode => ReplaceOption.IsChecked == true ? ImportMode.Replace : ImportMode.Merge;

        public ImportWindow(string path, ExcelImport file, AppData current)
        {
            InitializeComponent();
            _file = file;
            _current = current;

            FileText.Text = Path.GetFileName(path);

            var parts = new List<string>();
            if (file.Materials != null) parts.Add($"物料 {file.Materials.Count} 項");
            if (file.Machines != null) parts.Add($"機台 {file.Machines.Count} 台");
            if (file.HasItems) parts.Add($"出料明細 {file.ItemCount} 筆");
            if (file.Rules != null) parts.Add($"功能規則 {file.Rules.Count} 條");
            SummaryText.Text = string.Join("　·　", parts);

            var notices = new List<string>();
            if (file.Materials == null) notices.Add("這個檔案沒有物料資料庫，物料不會變動。");
            if (file.Machines == null) notices.Add("這個檔案沒有機台資料，機台表單不會變動。");
            else if (!file.HasItems) notices.Add("這個檔案沒有出料明細，現有表單上的料不會變動。");
            if (file.Rules == null) notices.Add("這個檔案沒有功能規則，規則不會變動。");
            if (file.MergedForms > 0) notices.Add($"有 {file.MergedForms} 張表單的工令和機台名稱重複，會併成一張，相同料號的數量相加。");
            if (notices.Count > 0)
            {
                NoticeText.Text = string.Join("\n", notices);
                NoticeText.Visibility = Visibility.Visible;
            }

            ReplaceText.Text = ReplaceDescription();
        }

        /// <summary>完全以 Excel 為準時，目前的資料會少掉哪些。</summary>
        private string ReplaceDescription()
        {
            var parts = new List<string>();
            if (_file.Materials != null) parts.Add($"現有的 {_current.Materials.Count} 項物料");
            if (_file.Machines != null) parts.Add($"現有的 {_current.Machines.Count} 台機台");
            if (_file.Rules != null) parts.Add($"現有的 {_current.Rules.Count} 條規則");
            return $"{string.Join("、", parts)}換成檔案的內容，檔案裡沒有的會被刪除。";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (Mode == ImportMode.Replace && MessageBox.Show(
                    $"{ReplaceDescription()}\n\n確定要繼續嗎？", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            DialogResult = true;
        }
    }
}
