using Discharge.Models;

namespace Discharge.Services
{
    /// <summary>功能規則：工單選功能時帶出對應的料，取消時扣回去。</summary>
    public static class FunctionRules
    {
        /// <summary>Excel 用這兩個字元分隔功能和數量，名稱裡不能有。</summary>
        private static readonly char[] ReservedChars = { '、', '×' };

        public static bool SameName(string a, string b) =>
            string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>檢查規則能不能存：名稱必填且不重複，數量要大於 0。可以存就回傳 null。</summary>
        public static string? Validate(FunctionRule rule, IEnumerable<FunctionRule> all)
        {
            if (rule.Name.Length == 0) return "請輸入功能名稱。";
            if (rule.Name.IndexOfAny(ReservedChars) >= 0) return "功能名稱不能有「、」或「×」。";
            if (all.Any(r => r.Id != rule.Id && SameName(r.Name, rule.Name))) return $"已經有「{rule.Name}」這條規則了，功能名稱不能重複，請改一個名稱。";
            if (rule.MachineTypes.FirstOrDefault(t => ValidateMachineType(t) != null) is { } badType) return ValidateMachineType(badType);
            var bad = rule.Items.FirstOrDefault(i => i.Quantity <= 0);
            return bad == null ? null : $"料號 {bad.PartNo} 的數量要大於 0。";
        }

        // ---------- 複製、找重複 ----------

        /// <summary>複製規則用的名稱：「原名（複製）」，已經有了就「原名（複製 2）」、「（複製 3）」…</summary>
        public static string CopyName(string name, IEnumerable<FunctionRule> all)
        {
            var names = all.Select(r => r.Name).ToList();
            var candidate = $"{name}（複製）";
            for (var n = 2; names.Any(x => SameName(x, candidate)); n++) candidate = $"{name}（複製 {n}）";
            return candidate;
        }

        /// <summary>兩條規則帶出的料一樣：料號相同（不分大小寫）、每個料號的數量也相同，順序不拘。</summary>
        public static bool SameItems(FunctionRule a, FunctionRule b)
        {
            static Dictionary<string, decimal> Totals(FunctionRule rule)
            {
                var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in rule.Items)
                    totals[item.PartNo.Trim()] = totals.GetValueOrDefault(item.PartNo.Trim()) + item.Quantity;
                return totals;
            }
            var x = Totals(a);
            var y = Totals(b);
            return x.Count == y.Count && x.All(p => y.TryGetValue(p.Key, out var q) && q == p.Value);
        }

        /// <summary>其他規則裡，帶出的料和這條完全一樣的（沒有料的不算）。</summary>
        public static List<FunctionRule> FindSameItems(FunctionRule rule, IEnumerable<FunctionRule> all) =>
            rule.Items.Count == 0 ? new()
                : all.Where(r => r.Id != rule.Id && SameItems(r, rule))
                     .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>機種名稱：不能空白，也不能有「、」（Excel 用它分隔）。可以用就回傳 null。</summary>
        public static string? ValidateMachineType(string type)
        {
            if (type.Trim().Length == 0) return "請輸入機種名稱。";
            return type.Contains('、') ? "機種名稱不能有「、」。" : null;
        }

        /// <summary>搜尋功能名稱、適用機種和規則上的料號。</summary>
        public static bool Matches(FunctionRule rule, string? query) =>
            TextRules.Matches(query, new[] { rule.Name }.Concat(rule.MachineTypes).Concat(rule.Items.Select(i => i.PartNo)).ToArray());

        // ---------- 機種 ----------

        /// <summary>
        /// 這個機種能不能用這條規則：規則沒設機種的所有機種都能用；
        /// 機台還沒選機種時，只能用沒設機種的規則。
        /// </summary>
        public static bool IsAvailable(FunctionRule rule, string machineType) =>
            rule.MachineTypes.Count == 0
            || machineType.Trim().Length > 0 && rule.MachineTypes.Any(t => SameName(t, machineType));

        // ---------- 工單選功能 ----------

        /// <summary>
        /// 選了一個功能：把規則上的料加進表單，料號已經有的把數量加上去。
        /// 資料庫找不到的料號不加，回傳在 Missing。
        /// </summary>
        public static (int Added, int Merged, List<string> Missing) AddFunction(
            IList<MachineItem> items, IList<MachineFunction> functions, FunctionRule rule, IEnumerable<Material> materials)
        {
            int added = 0, merged = 0;
            var missing = new List<string>();
            if (functions.Any(f => f.RuleId == rule.Id)) return (added, merged, missing);

            functions.Add(new MachineFunction { RuleId = rule.Id, Name = rule.Name });
            foreach (var ruleItem in rule.Items)
            {
                var item = items.FirstOrDefault(i => MaterialRules.SamePartNo(i.PartNo, ruleItem.PartNo));
                if (item != null)
                {
                    item.Quantity += ruleItem.Quantity;
                    merged++;
                }
                else
                {
                    var material = materials.FirstOrDefault(m => MaterialRules.SamePartNo(m.PartNo, ruleItem.PartNo));
                    if (material == null)
                    {
                        missing.Add(ruleItem.PartNo);
                        continue;
                    }
                    item = new MachineItem
                    {
                        MaterialId = material.Id,
                        PartNo = material.PartNo,
                        Name = material.Name,
                        Spec = material.Spec,
                        Quantity = ruleItem.Quantity,
                    };
                    items.Add(item);
                    added++;
                }
                item.Sources.Add(new ItemSource { RuleId = rule.Id, RuleName = rule.Name, Quantity = ruleItem.Quantity });
            }
            return (added, merged, missing);
        }

        /// <summary>
        /// 取消一個功能：扣掉當初帶出來的數量，扣完沒有了就移除那一項。另外手動加的數量會留著。
        /// 依表單上記錄的數量扣，規則後來改過或刪掉也扣得對。回傳移除了幾項。
        /// </summary>
        public static int RemoveFunction(IList<MachineItem> items, IList<MachineFunction> functions, string ruleId)
        {
            foreach (var function in functions.Where(f => f.RuleId == ruleId).ToList()) functions.Remove(function);

            var removed = 0;
            foreach (var item in items.ToList())
            {
                var sources = item.Sources.Where(s => s.RuleId == ruleId).ToList();
                if (sources.Count == 0) continue;
                foreach (var source in sources)
                {
                    item.Quantity -= source.Quantity;
                    item.Sources.Remove(source);
                }
                if (item.Quantity <= 0)
                {
                    items.Remove(item);
                    removed++;
                }
            }
            return removed;
        }

        // ---------- 規則和物料的對應 ----------

        /// <summary>這個料號在哪些規則上（規則編號 → 數量）。</summary>
        public static Dictionary<string, decimal> RulesOfPart(IEnumerable<FunctionRule> rules, string partNo)
        {
            var result = new Dictionary<string, decimal>();
            foreach (var rule in rules)
            {
                var item = rule.Items.FirstOrDefault(i => MaterialRules.SamePartNo(i.PartNo, partNo));
                if (item != null) result[rule.Id] = item.Quantity;
            }
            return result;
        }

        /// <summary>設定這個料號要由哪些功能帶出：有列的加上或改數量，沒列的從規則移除。</summary>
        public static void SetRulesOfPart(IEnumerable<FunctionRule> rules, string partNo, IReadOnlyDictionary<string, decimal> wanted)
        {
            foreach (var rule in rules)
            {
                var item = rule.Items.FirstOrDefault(i => MaterialRules.SamePartNo(i.PartNo, partNo));
                if (!wanted.TryGetValue(rule.Id, out var quantity))
                {
                    if (item != null) rule.Items.Remove(item);
                    continue;
                }
                if (item == null) rule.Items.Add(new RuleItem { PartNo = partNo, Quantity = quantity });
                else item.Quantity = quantity;
            }
        }

        /// <summary>資料庫改料號時，規則上的料號一起改。</summary>
        public static void RenamePart(IEnumerable<FunctionRule> rules, string oldPartNo, string newPartNo)
        {
            foreach (var item in rules.SelectMany(r => r.Items).Where(i => MaterialRules.SamePartNo(i.PartNo, oldPartNo)))
                item.PartNo = newPartNo;
        }

        /// <summary>資料庫刪物料時，從規則移除。回傳有幾條規則受影響。</summary>
        public static int RemoveParts(IEnumerable<FunctionRule> rules, IReadOnlyCollection<string> partNos)
        {
            var affected = 0;
            foreach (var rule in rules)
                if (rule.Items.RemoveAll(i => partNos.Any(p => MaterialRules.SamePartNo(p, i.PartNo))) > 0)
                    affected++;
            return affected;
        }

        /// <summary>有幾條規則用到這些料號。</summary>
        public static int CountRulesUsing(IEnumerable<FunctionRule> rules, IReadOnlyCollection<string> partNos) =>
            rules.Count(r => r.Items.Any(i => partNos.Any(p => MaterialRules.SamePartNo(p, i.PartNo))));

        /// <summary>規則改名時，表單上記錄的功能名稱一起改（料和數量不動）。</summary>
        public static void RenameFunction(IEnumerable<Machine> machines, string ruleId, string newName)
        {
            foreach (var machine in machines)
            {
                foreach (var function in machine.Functions.Where(f => f.RuleId == ruleId)) function.Name = newName;
                foreach (var source in machine.Items.SelectMany(i => i.Sources).Where(s => s.RuleId == ruleId)) source.RuleName = newName;
            }
        }

        /// <summary>算出每項物料的「功能」欄。</summary>
        public static void FillFunctionText(IEnumerable<Material> materials, IEnumerable<FunctionRule> rules)
        {
            var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var item in rule.Items)
                {
                    if (!names.TryGetValue(item.PartNo.Trim(), out var list)) names[item.PartNo.Trim()] = list = new();
                    list.Add(rule.Name);
                }
            }
            foreach (var material in materials)
                material.FunctionText = names.TryGetValue(material.PartNo.Trim(), out var list) ? string.Join("、", list) : "";
        }
    }
}
