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

        /// <summary>目前的機種不能用，只是這張表單已經選了。取消後就不會再出現。</summary>
        public bool NotAvailable { get; set; }

        public string Display => RuleDeleted ? $"{Name}（規則已刪除）"
            : NotAvailable ? $"{Name}（這個機種不適用）"
            : Name;
    }

    /// <summary>機種下拉選單的一個選項；Value 空白是「未指定」。</summary>
    public class MachineTypeOption
    {
        public string Value { get; set; } = "";

        /// <summary>表單選的機種已經在「機種」頁刪掉了。</summary>
        public bool Deleted { get; set; }

        public string Display => Value.Length == 0 ? "（未指定）" : Deleted ? $"{Value}（已刪除的機種）" : Value;
    }

    /// <summary>規則頁「適用機種」下拉選單的一個選項。</summary>
    public class RuleTypeChip
    {
        public string Name { get; set; } = "";
        public bool IsSelected { get; set; }
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
