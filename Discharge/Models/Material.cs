using System.Text.Json.Serialization;

namespace Discharge.Models
{
    /// <summary>物料資料庫裡的一項物料。</summary>
    public class Material
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string PartNo { get; set; } = "";
        public string Name { get; set; } = "";
        public string Spec { get; set; } = "";
        public string Note { get; set; } = "";

        /// <summary>表格上「功能」欄：哪些規則會帶出這項料（由規則算出來，不存檔）。</summary>
        [JsonIgnore]
        public string FunctionText { get; set; } = "";
    }
}
