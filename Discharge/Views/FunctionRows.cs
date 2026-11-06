using System.Globalization;

namespace Discharge.Views
{
    /// <summary>機台表單上的一個功能按鈕。</summary>
    public class FunctionChip
    {
        public string RuleId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsSelected { get; set; }

        /// <summary>規則已經刪掉，只是這張表單當初選過。取消後就不會再出現。</summary>
        public bool RuleDeleted { get; set; }

        public string Display => RuleDeleted ? $"{Name}（規則已刪除）" : Name;
    }

    /// <summary>物料表單上的一列：這項料要不要由這個功能帶出、帶幾個。</summary>
    public class MaterialRuleRow
    {
        public string RuleId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsChecked { get; set; }
        public string QuantityText { get; set; } = "1";

        public bool TryQuantity(out decimal quantity) =>
            decimal.TryParse(QuantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out quantity) && quantity > 0;
    }
}
