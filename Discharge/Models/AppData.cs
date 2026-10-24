namespace Discharge.Models
{
    /// <summary>存在 data.json 的全部資料。</summary>
    public class AppData
    {
        public List<Material> Materials { get; set; } = new();
        public List<Machine> Machines { get; set; } = new();
    }
}
