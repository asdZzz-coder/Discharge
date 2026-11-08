using System.IO;
using System.Windows;
using Discharge.Models;
using Discharge.Services;

namespace Discharge
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 只允許開一個視窗，避免兩個視窗互相覆蓋對方存的資料
            using var mutex = new Mutex(true, SingleInstanceName(), out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show($"{MainWindow.AppTitle}已經在執行中。", MainWindow.AppTitle);
                return;
            }

            AppData data;
            try
            {
                data = DataStore.Load();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // 讀不到就不開，免得之後存檔把原本的資料蓋掉
                MessageBox.Show($"讀取資料失敗，程式先關閉，原本的資料不會被改動。\n\n{ex.Message}",
                    MainWindow.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run(new MainWindow(data));
        }

        /// <summary>
        /// 同一個資料資料夾只能開一個視窗。用 DISCHARGE_DATA_DIR 指定測試資料夾時另外算一個，
        /// 這樣使用者開著正式版時，測試仍能用測試資料啟動，兩邊也不會寫到同一份資料。
        /// </summary>
        private static string SingleInstanceName()
        {
            const string name = @"Local\Discharge.SingleInstance";
            var custom = Environment.GetEnvironmentVariable("DISCHARGE_DATA_DIR");
            if (string.IsNullOrWhiteSpace(custom)) return name;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(custom).ToUpperInvariant())))[..16];
            return name + "." + hash;
        }
    }
}
