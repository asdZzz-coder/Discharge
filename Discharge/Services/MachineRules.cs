using Discharge.Models;

namespace Discharge.Services
{
    public static class MachineRules
    {
        /// <summary>檢查機台表單能不能存：工令、機台名稱必填，數量要大於 0。可以存就回傳 null。</summary>
        public static string? Validate(Machine m)
        {
            if (m.WorkOrder.Length == 0) return "請輸入工令。";
            if (m.Name.Length == 0) return "請輸入機台名稱。";
            var bad = m.Items.FirstOrDefault(i => i.Quantity <= 0);
            return bad == null ? null : $"料號 {bad.PartNo} 的數量要大於 0。";
        }

        /// <summary>找出工令和機台名稱都一樣的另一張表單（不分大小寫）。</summary>
        public static Machine? FindDuplicate(Machine m, IEnumerable<Machine> all) =>
            all.FirstOrDefault(x => x.Id != m.Id
                && string.Equals(x.WorkOrder.Trim(), m.WorkOrder.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Name.Trim(), m.Name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 把選好的物料加進機台表單：表單上已經有的料號把數量加上去，沒有的新增一項。
        /// 回傳 (新增幾項, 幾項加上數量)。
        /// </summary>
        public static (int Added, int Merged) AddItems(IList<MachineItem> items, IEnumerable<(Material Material, decimal Quantity)> picks)
        {
            int added = 0, merged = 0;
            foreach (var (material, quantity) in picks)
            {
                var existing = items.FirstOrDefault(i => MaterialRules.SamePartNo(i.PartNo, material.PartNo));
                if (existing != null)
                {
                    existing.Quantity += quantity;
                    merged++;
                    continue;
                }
                items.Add(new MachineItem
                {
                    MaterialId = material.Id,
                    PartNo = material.PartNo,
                    Name = material.Name,
                    Spec = material.Spec,
                    Quantity = quantity,
                });
                added++;
            }
            return (added, merged);
        }

        /// <summary>搜尋工令、機台名稱、選的功能，以及表單上料件的料號、名稱、規格。</summary>
        public static bool Matches(Machine m, string? query) =>
            TextRules.Matches(query, new[] { m.WorkOrder, m.Name, m.MachineType }
                .Concat(m.Functions.Select(f => f.Name))
                .Concat(m.Items.SelectMany(i => new[] { i.PartNo, i.Name, i.Spec }))
                .ToArray());
    }
}
