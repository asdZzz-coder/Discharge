using Discharge.Models;

namespace Discharge.Services
{
    /// <summary>
    /// 機種清單：在「機種」頁新增、改名、刪除，規則的適用機種和機台表單的機種都從這裡選。
    /// 名稱不分大小寫，不能重複。
    /// </summary>
    public static class MachineTypeRules
    {
        /// <summary>依名稱排序（不分大小寫）。</summary>
        public static void Sort(List<string> types) => types.Sort(StringComparer.OrdinalIgnoreCase);

        public static bool Contains(IEnumerable<string> types, string name) => types.Any(t => FunctionRules.SameName(t, name));

        /// <summary>清單上的寫法（大小寫可能不同）；清單沒有就回傳 null。</summary>
        public static string? Find(IEnumerable<string> types, string name) => types.FirstOrDefault(t => FunctionRules.SameName(t, name));

        /// <summary>
        /// 舊版資料沒有機種清單，從規則和表單用過的機種建立。
        /// 之後每次都補上規則用到、清單卻沒有的（例如從 Excel 匯入的規則），去掉空白和重複。
        /// </summary>
        public static void Normalize(AppData data, bool seedFromMachines)
        {
            var source = data.MachineTypes
                .Concat(data.Rules.SelectMany(r => r.MachineTypes))
                .Concat(seedFromMachines ? data.Machines.Select(m => m.MachineType) : Enumerable.Empty<string>());
            var types = new List<string>();
            foreach (var type in source)
            {
                var name = type?.Trim() ?? "";
                if (name.Length > 0 && !Contains(types, name)) types.Add(name);
            }
            Sort(types);
            data.MachineTypes = types;
        }

        /// <summary>新增或改名時檢查名稱；可以用就回傳 null。改名時 except 是原本的名稱。</summary>
        public static string? Validate(IEnumerable<string> types, string name, string? except = null)
        {
            if (FunctionRules.ValidateMachineType(name) is { } error) return error;
            return types.Any(t => FunctionRules.SameName(t, name) && (except == null || !FunctionRules.SameName(t, except)))
                ? $"已經有「{name.Trim()}」這個機種了。"
                : null;
        }

        public static void Add(AppData data, string name)
        {
            data.MachineTypes.Add(name.Trim());
            Sort(data.MachineTypes);
        }

        /// <summary>改名：清單、規則的適用機種、機台表單上的機種一起改。</summary>
        public static void Rename(AppData data, string oldName, string newName)
        {
            newName = newName.Trim();
            var index = data.MachineTypes.FindIndex(t => FunctionRules.SameName(t, oldName));
            if (index >= 0) data.MachineTypes[index] = newName;
            Sort(data.MachineTypes);

            foreach (var rule in data.Rules)
            {
                var at = rule.MachineTypes.FindIndex(t => FunctionRules.SameName(t, oldName));
                if (at < 0) continue;
                rule.MachineTypes[at] = newName;
                rule.MachineTypes = rule.MachineTypes
                    .Where((t, i) => rule.MachineTypes.FindIndex(x => FunctionRules.SameName(x, t)) == i)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            foreach (var machine in data.Machines.Where(m => FunctionRules.SameName(m.MachineType, oldName)))
                machine.MachineType = newName;
        }

        /// <summary>用到這個機種的規則和機台表單。</summary>
        public static (List<FunctionRule> Rules, List<Machine> Machines) Usage(AppData data, string name) => (
            data.Rules.Where(r => Contains(r.MachineTypes, name)).ToList(),
            data.Machines.Where(m => FunctionRules.SameName(m.MachineType, name)).ToList());

        /// <summary>
        /// 只適用這個機種的規則：刪掉這個機種後就沒有設定機種，會變成所有機種都能用。
        /// </summary>
        public static List<FunctionRule> OnlyThisType(AppData data, string name) =>
            data.Rules.Where(r => r.MachineTypes.Count > 0 && r.MachineTypes.All(t => FunctionRules.SameName(t, name))).ToList();

        /// <summary>
        /// 刪除：從清單和規則的適用機種拿掉。機台表單上的機種保留原本的文字（和規則刪掉時一樣，表單不跟著變），
        /// 表單上會標「已刪除的機種」，可以改選別的。
        /// </summary>
        public static void Remove(AppData data, string name)
        {
            data.MachineTypes.RemoveAll(t => FunctionRules.SameName(t, name));
            foreach (var rule in data.Rules) rule.MachineTypes.RemoveAll(t => FunctionRules.SameName(t, name));
        }
    }
}
