using System.Text.Json.Serialization;

namespace Discharge.Models
{
    /// <summary>一條功能規則：工單上選了這個功能，就帶出這些料。條數沒有上限。</summary>
    public class FunctionRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public List<RuleItem> Items { get; set; } = new();

        [JsonIgnore]
        public int ItemCount => Items.Count;
    }

    /// <summary>規則帶出的一項料。用料號對應物料資料庫（資料庫改料號時會一起改）。</summary>
    public class RuleItem
    {
        public string PartNo { get; set; } = "";
        public decimal Quantity { get; set; } = 1;
    }

    /// <summary>機台表單選的一個功能。存名稱，規則刪掉後表單上還看得到。</summary>
    public class MachineFunction
    {
        public string RuleId { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>表單上一項料有多少數量是哪個功能帶出來的，取消這個功能時才知道要扣掉多少。</summary>
    public class ItemSource
    {
        public string RuleId { get; set; } = "";
        public string RuleName { get; set; } = "";
        public decimal Quantity { get; set; }
    }
}
