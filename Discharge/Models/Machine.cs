using System.Text.Json.Serialization;

namespace Discharge.Models
{
    /// <summary>一張機台表單：工令、機台名稱，以及從物料資料庫選出來的料。</summary>
    public class Machine
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string WorkOrder { get; set; } = "";
        public string Name { get; set; } = "";
        public List<MachineItem> Items { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        [JsonIgnore]
        public int ItemCount => Items.Count;
    }

    /// <summary>
    /// 機台表單上的一項料。選料時把料號、名稱、規格複製過來，
    /// 之後資料庫修改或刪除這項物料，已經建好的表單不會跟著變。
    /// </summary>
    public class MachineItem
    {
        public string MaterialId { get; set; } = "";
        public string PartNo { get; set; } = "";
        public string Name { get; set; } = "";
        public string Spec { get; set; } = "";
        public decimal Quantity { get; set; } = 1;
        public string Note { get; set; } = "";
    }
}
