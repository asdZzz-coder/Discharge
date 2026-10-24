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
    }
}
