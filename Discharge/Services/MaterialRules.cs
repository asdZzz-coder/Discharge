using Discharge.Models;

namespace Discharge.Services
{
    public static class MaterialRules
    {
        public static bool SamePartNo(string a, string b) =>
            string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>檢查物料能不能存：料號、名稱必填，料號不能和別項重複（不分大小寫）。可以存就回傳 null。</summary>
        public static string? Validate(Material m, IEnumerable<Material> all)
        {
            if (m.PartNo.Length == 0) return "請輸入料號。";
            if (m.Name.Length == 0) return "請輸入名稱。";
            var dup = all.FirstOrDefault(x => x.Id != m.Id && SamePartNo(x.PartNo, m.PartNo));
            return dup == null ? null : $"料號 {m.PartNo} 已經有了（{dup.Name}）。";
        }

        /// <summary>搜尋料號、名稱、規格、備註。</summary>
        public static bool Matches(Material m, string? query) =>
            TextRules.Matches(query, m.PartNo, m.Name, m.Spec, m.Note);

        /// <summary>分欄篩選：料號、名稱、規格各自比對自己的欄位，空白的不限，全部符合才算。</summary>
        public static bool MatchesFields(Material m, string? partNo, string? name, string? spec) =>
            TextRules.Matches(partNo, m.PartNo) && TextRules.Matches(name, m.Name) && TextRules.Matches(spec, m.Spec);

        private static readonly string[][] HeaderNames =
        {
            new[] { "料號", "品號", "PartNo" },
            new[] { "名稱", "品名", "Name" },
            new[] { "規格", "Spec" },
            new[] { "備註", "Note" },
        };

        /// <summary>
        /// 解析從 Excel 複製的表格（Tab 分隔）。第一列有「料號 / 品號」標題時依標題找欄位（名稱 / 品名、規格、備註，順序不拘），
        /// 沒有標題時依序當成 料號、名稱、規格、備註。料號空白的列略過。
        /// </summary>
        public static List<Material> ParseTable(string text)
        {
            var rows = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Where(line => line.Trim().Length > 0)
                .Select(line => line.Split('\t').Select(c => TextRules.Clean(c.Trim().Trim('"'))).ToArray())
                .ToList();
            return ParseRows(rows);
        }

        /// <summary>這一列是標題列（有「料號 / 品號」）。</summary>
        public static bool IsHeaderRow(string[] row) => FindColumn(row, HeaderNames[0]) >= 0;

        private static int FindColumn(string[] header, string[] names) =>
            Array.FindIndex(header, h => names.Any(n => h.Contains(n, StringComparison.OrdinalIgnoreCase)));

        /// <summary>已經切好欄位的表格轉成物料，規則同 ParseTable。</summary>
        public static List<Material> ParseRows(List<string[]> source)
        {
            var rows = source.ToList();
            if (rows.Count == 0) return new();

            int[] cols = { 0, 1, 2, 3 };
            var header = rows[0];
            var found = HeaderNames.Select(names => FindColumn(header, names)).ToArray();
            if (found[0] >= 0)
            {
                cols = found;
                rows.RemoveAt(0);
            }

            static string Cell(string[] row, int i) => i >= 0 && i < row.Length ? row[i] : "";
            return rows
                .Select(r => new Material { PartNo = Cell(r, cols[0]), Name = Cell(r, cols[1]), Spec = Cell(r, cols[2]), Note = Cell(r, cols[3]) })
                .Where(m => m.PartNo.Length > 0)
                .ToList();
        }

        /// <summary>
        /// 把 incoming 合併進 target：料號相同的更新名稱、規格、備註（新的是空白就保留原本的），沒有的新增。
        /// 新的料號沒有名稱就不加入，算在 Skipped。
        /// </summary>
        public static (int Added, int Updated, int Skipped) Merge(List<Material> target, IEnumerable<Material> incoming)
        {
            int added = 0, updated = 0, skipped = 0;
            foreach (var m in incoming)
            {
                var existing = target.FirstOrDefault(x => SamePartNo(x.PartNo, m.PartNo));
                if (existing == null)
                {
                    if (m.Name.Length == 0) { skipped++; continue; }
                    target.Add(new Material { PartNo = m.PartNo, Name = m.Name, Spec = m.Spec, Note = m.Note });
                    added++;
                    continue;
                }
                if (m.Name.Length > 0) existing.Name = m.Name;
                if (m.Spec.Length > 0) existing.Spec = m.Spec;
                if (m.Note.Length > 0) existing.Note = m.Note;
                updated++;
            }
            return (added, updated, skipped);
        }
    }
}
