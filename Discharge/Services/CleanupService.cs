using System.IO;

namespace Discharge.Services
{
    /// <summary>
    /// 啟動時清掉更新後不再需要的舊檔案，只刪除下列明確指定的檔案，絕不碰使用者的資料（data.json）：
    ///   1. %TEMP%\Discharge-Update（下載並解壓的更新包）
    ///   2. 資料夾內寫入中斷留下的 data.json.tmp
    /// 刪除失敗（例如檔案還被安裝程式占用）就直接略過，下次啟動再試。
    /// </summary>
    public static class CleanupService
    {
        public static void RunInBackground() => Task.Run(Run);

        public static void Run()
        {
            UpdateService.TryDeleteDirectory(UpdateService.DownloadFolder);
            try { File.Delete(DataStore.DataFile + ".tmp"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 被占用，下次再清 */ }
        }
    }
}
