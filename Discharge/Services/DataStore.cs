using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Discharge.Models;

namespace Discharge.Services
{
    /// <summary>
    /// 資料存在 %AppData%\Discharge\data.json（一般的 JSON 檔，可以直接備份）。
    /// 設定環境變數 DISCHARGE_DATA_DIR 可以改用別的資料夾（測試用，不會動到正式資料）。
    /// </summary>
    public static class DataStore
    {
        public static string DataDirectory { get; } = ResolveDirectory();

        public static string DataFile => Path.Combine(DataDirectory, "data.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), // 中文直接存成文字，打開檔案看得懂
        };

        private static string ResolveDirectory()
        {
            var custom = Environment.GetEnvironmentVariable("DISCHARGE_DATA_DIR");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Discharge")
                : Path.GetFullPath(custom);
        }

        public static AppData Load() => Load(DataFile);

        /// <summary>讀取資料；檔案不存在時回傳空的資料，格式錯誤時丟出 InvalidDataException（不要蓋掉原本的檔案）。</summary>
        public static AppData Load(string file)
        {
            if (!File.Exists(file)) return new AppData();
            try
            {
                var data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(file), Options) ?? new AppData();
                data.Materials ??= new();
                data.Machines ??= new();
                data.Rules ??= new();
                foreach (var m in data.Machines)
                {
                    m.Items ??= new();
                    m.Functions ??= new();
                    foreach (var item in m.Items) item.Sources ??= new();
                }
                foreach (var r in data.Rules)
                {
                    r.Items ??= new();
                    r.MachineTypes ??= new();
                }
                foreach (var m in data.Machines) m.MachineType ??= "";
                return data;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"資料檔格式錯誤：{file}\n{ex.Message}", ex);
            }
        }

        public static void Save(AppData data) => Save(data, DataFile);

        /// <summary>先寫到 .tmp 再換掉正式檔，寫到一半中斷也不會弄壞原本的資料。</summary>
        public static void Save(AppData data, string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
            File.Move(tmp, file, overwrite: true);
        }

        /// <summary>匯入前備份目前的資料檔，回傳備份檔的路徑；還沒有資料檔就回傳 null。</summary>
        public static string? BackupBeforeImport()
        {
            if (!File.Exists(DataFile)) return null;
            var backup = Path.Combine(DataDirectory, $"data.before-import-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(DataFile, backup, overwrite: true);
            return backup;
        }

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        /// <summary>完整複製一份（編輯機台表單時用複本，按「儲存」才寫回）。</summary>
        public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value), Options)!;
    }
}
