using System.Text.Json.Serialization;

namespace Discharge.Models
{
    /// <summary>一張機台表單：工令、機台名稱、選的功能，以及帶出來或另外選的料。</summary>
    public class Machine
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string WorkOrder { get; set; } = "";
        public string Name { get; set; } = "";
        public List<MachineFunction> Functions { get; set; } = new();
        public List<MachineItem> Items { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        [JsonIgnore]
        public int ItemCount => Items.Count;
    }

    /// <summary>
    /// 機台表單上的一項料。帶料時把料號、名稱、規格複製過來，
    /// 之後資料庫修改或刪除這項物料、或規則改了，已經建好的表單不會跟著變。
    /// </summary>
    public class MachineItem
    {
        public string MaterialId { get; set; } = "";
        public string PartNo { get; set; } = "";
        public string Name { get; set; } = "";
        public string Spec { get; set; } = "";
        public decimal Quantity { get; set; } = 1;
        public string Note { get; set; } = "";
        public List<ItemSource> Sources { get; set; } = new();

        /// <summary>表格上「來源功能」欄。</summary>
        [JsonIgnore]
        public string FunctionText => string.Join("、", Sources.Select(s => s.RuleName));
    }
}
