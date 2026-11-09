namespace Discharge.Models
{
    /// <summary>存在 data.json 的全部資料。</summary>
    public class AppData
    {
        public List<Material> Materials { get; set; } = new();
        public List<Machine> Machines { get; set; } = new();
        public List<FunctionRule> Rules { get; set; } = new();

        /// <summary>機種清單（「機種」頁管理），依名稱排序。</summary>
        public List<string> MachineTypes { get; set; } = new();
    }
}
