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
            using var mutex = new Mutex(true, @"Local\Discharge.SingleInstance", out bool isFirst);
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
    }
}
