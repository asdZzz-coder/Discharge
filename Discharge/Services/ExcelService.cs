using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Discharge.Models;

namespace Discharge.Services
{
    public enum ImportMode
    {
        /// <summary>合併：檔案裡有的新增或更新，檔案沒有的保留。</summary>
        Merge,
        /// <summary>完全以 Excel 為準：檔案有的工作表，內容整份換掉。</summary>
        Replace,
    }

    /// <summary>從 Excel 讀到的內容（還沒套用到資料）。工作表不存在時對應的清單是 null。</summary>
    public class ExcelImport
    {
        public List<Material>? Materials { get; set; }
        public List<Machine>? Machines { get; set; }
        public List<FunctionRule>? Rules { get; set; }

        /// <summary>檔案有「出料明細」工作表。沒有的話，既有機台的料不動。</summary>
        public bool HasItems { get; set; }

        /// <summary>「機台」工作表有「功能」欄。舊版匯出的檔案沒有，既有機台選的功能不動。</summary>
        public bool HasFunctions { get; set; }

        /// <summary>檔案裡工令和機台名稱重複、被併成一張的表單數。</summary>
        public int MergedForms { get; set; }

        /// <summary>讀不懂的列。有任何一筆就不匯入，免得只進來一半。</summary>
        public List<string> Errors { get; } = new();

        public bool IsEmpty => Materials == null && Machines == null && Rules == null;
        public int ItemCount => Machines?.Sum(m => m.Items.Count) ?? 0;
    }

    public record ImportResult(int MaterialsAdded, int MaterialsUpdated, int MaterialsSkipped, int MachinesAdded, int MachinesUpdated,
        int RulesAdded = 0, int RulesUpdated = 0);

    /// <summary>
    /// Excel 匯出 / 匯入。四張工作表：「物料資料庫」一列一項物料、「機台」一列一張表單、
    /// 「出料明細」一列一項料，用工令和機台名稱對應回表單、「功能規則」一列一條規則上的一項料。
    /// 匯入時依第一列的標題找欄位，順序可以調換。
    /// </summary>
    public static class ExcelService
    {
        public const string MaterialSheet = "物料資料庫";
        public const string MachineSheet = "機台";
        public const string ItemSheet = "出料明細";
        public const string RuleSheet = "功能規則";

        private const string HPartNo = "料號";
        private const string HName = "名稱";
        private const string HSpec = "規格";
        private const string HNote = "備註";
        private const string HWorkOrder = "工令";
        private const string HMachineName = "機台名稱";
        private const string HQuantity = "數量";
        private const string HCreated = "建立時間";
        private const string HUpdated = "最後儲存";
        private const string HFunction = "功能";
        private const string HSources = "來源功能";

        private const char ListSeparator = '、';
        private const char QuantitySeparator = '×';

        private const string DateFormat = "yyyy/mm/dd hh:mm";
        private const double MaxColumnWidth = 60;

        // ---------- 匯出 ----------

        public static void Export(AppData data, string path)
        {
            using var wb = new XLWorkbook();

            var materials = StartSheet(wb, MaterialSheet, HPartNo, HName, HSpec, HNote);
            AsText(materials, 1);
            var row = 2;
            foreach (var m in data.Materials.OrderBy(m => m.PartNo, StringComparer.OrdinalIgnoreCase))
            {
                materials.Cell(row, 1).Value = m.PartNo;
                materials.Cell(row, 2).Value = m.Name;
                materials.Cell(row, 3).Value = m.Spec;
                materials.Cell(row, 4).Value = m.Note;
                row++;
            }

            var ordered = data.Machines
                .OrderBy(m => m.WorkOrder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var machines = StartSheet(wb, MachineSheet, HWorkOrder, HMachineName, HCreated, HUpdated, HFunction);
            AsText(machines, 1);
            row = 2;
            foreach (var m in ordered)
            {
                machines.Cell(row, 1).Value = m.WorkOrder;
                machines.Cell(row, 2).Value = m.Name;
                machines.Cell(row, 3).Value = m.CreatedAt;
                machines.Cell(row, 4).Value = m.UpdatedAt;
                machines.Cell(row, 5).Value = string.Join(ListSeparator, m.Functions.Select(f => f.Name));
                machines.Range(row, 3, row, 4).Style.DateFormat.Format = DateFormat;
                row++;
            }

            var items = StartSheet(wb, ItemSheet, HWorkOrder, HMachineName, HPartNo, HName, HSpec, HQuantity, HNote, HSources);
            AsText(items, 1);
            AsText(items, 3);
            row = 2;
            foreach (var m in ordered)
            {
                foreach (var item in m.Items)
                {
                    items.Cell(row, 1).Value = m.WorkOrder;
                    items.Cell(row, 2).Value = m.Name;
                    items.Cell(row, 3).Value = item.PartNo;
                    items.Cell(row, 4).Value = item.Name;
                    items.Cell(row, 5).Value = item.Spec;
                    items.Cell(row, 6).Value = item.Quantity;
                    items.Cell(row, 7).Value = item.Note;
                    items.Cell(row, 8).Value = FormatSources(item.Sources);
                    row++;
                }
            }

            var rules = StartSheet(wb, RuleSheet, HFunction, HPartNo, HQuantity);
            AsText(rules, 2);
            row = 2;
            foreach (var rule in data.Rules.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (rule.Items.Count == 0)
                {
                    rules.Cell(row++, 1).Value = rule.Name; // 還沒設定料的規則也要留著
                    continue;
                }
                foreach (var item in rule.Items)
                {
                    rules.Cell(row, 1).Value = rule.Name;
                    rules.Cell(row, 2).Value = item.PartNo;
                    rules.Cell(row, 3).Value = item.Quantity;
                    row++;
                }
            }

            foreach (var ws in wb.Worksheets) FitColumns(ws);
            wb.SaveAs(path);
        }

        private static IXLWorksheet StartSheet(XLWorkbook wb, string name, params string[] headers)
        {
            var ws = wb.Worksheets.Add(name);
            for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
            var header = ws.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8F3EE");
            ws.SheetView.FreezeRows(1);
            return ws;
        }

        /// <summary>料號、工令設成文字格式，之後在 Excel 手動打 007 才不會變成 7。</summary>
        private static void AsText(IXLWorksheet ws, int column) => ws.Column(column).Style.NumberFormat.Format = "@";

        private static void FitColumns(IXLWorksheet ws)
        {
            ws.Columns().AdjustToContents();
            foreach (var column in ws.ColumnsUsed())
                if (column.Width > MaxColumnWidth) column.Width = MaxColumnWidth;
        }

        // ---------- 匯入：讀檔 ----------

        /// <summary>讀取 Excel（Excel 開著這個檔案也讀得到），不會改動任何資料。</summary>
        public static ExcelImport Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(stream);
            var result = new ExcelImport();

            wb.TryGetWorksheet(MachineSheet, out var machineSheet);
            wb.TryGetWorksheet(ItemSheet, out var itemSheet);
            wb.TryGetWorksheet(RuleSheet, out var ruleSheet);
            if (!wb.TryGetWorksheet(MaterialSheet, out var materialSheet) && machineSheet == null && itemSheet == null && ruleSheet == null)
            {
                // 不是這個程式匯出的檔案：第一張工作表有「料號」標題，就當成物料清單
                var first = wb.Worksheets.FirstOrDefault();
                if (first != null && MaterialRules.IsHeaderRow(RowTexts(first).FirstOrDefault().Cells ?? Array.Empty<string>()))
                    materialSheet = first;
            }

            if (materialSheet != null) ReadMaterials(materialSheet, result);
            if (machineSheet != null || itemSheet != null) ReadMachines(machineSheet, itemSheet, result);
            if (ruleSheet != null) ReadRules(ruleSheet, result);
            return result;
        }

        private static void ReadRules(IXLWorksheet ws, ExcelImport result)
        {
            var columns = HeaderColumns(ws);
            if (!Require(ws, columns, result, HFunction, HPartNo, HQuantity)) return;

            var rules = new List<FunctionRule>();
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                var name = Text(row, columns, HFunction);
                var partNo = Text(row, columns, HPartNo);
                var quantityText = Text(row, columns, HQuantity);
                if (name.Length == 0 && partNo.Length == 0 && quantityText.Length == 0) continue;

                var where = $"「{ws.Name}」第 {row.RowNumber()} 列";
                if (FunctionRules.Validate(new FunctionRule { Name = name }, Array.Empty<FunctionRule>()) is { } nameError)
                {
                    result.Errors.Add($"{where}：{nameError}");
                    continue;
                }
                var rule = rules.FirstOrDefault(r => FunctionRules.SameName(r.Name, name));
                if (rule == null) rules.Add(rule = new FunctionRule { Name = name });
                if (partNo.Length == 0) continue; // 還沒設定料的規則

                if (!TryQuantity(row.Cell(columns[HQuantity]), out var quantity) || quantity <= 0)
                {
                    result.Errors.Add($"{where}：數量要是大於 0 的數字（目前是「{quantityText}」）。");
                    continue;
                }
                var existing = rule.Items.FirstOrDefault(i => MaterialRules.SamePartNo(i.PartNo, partNo));
                if (existing != null) existing.Quantity += quantity;
                else rule.Items.Add(new RuleItem { PartNo = partNo, Quantity = quantity });
            }
            result.Rules = rules;
        }

        /// <summary>例如「正逆轉×2、變頻×1」。</summary>
        private static string FormatSources(IEnumerable<ItemSource> sources) => string.Join(ListSeparator,
            sources.Select(s => $"{s.RuleName}{QuantitySeparator}{s.Quantity.ToString("0.##########", CultureInfo.InvariantCulture)}"));

        private static bool TryParseSources(string text, out List<ItemSource> sources)
        {
            sources = new List<ItemSource>();
            foreach (var part in text.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var at = part.LastIndexOf(QuantitySeparator);
                if (at <= 0 || !decimal.TryParse(part[(at + 1)..], NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                    return false;
                sources.Add(new ItemSource { RuleName = part[..at].Trim(), Quantity = quantity });
            }
            return true;
        }

        private static void ReadMaterials(IXLWorksheet ws, ExcelImport result)
        {
            var rows = RowTexts(ws).Select(r => r.Cells).ToList();
            if (rows.Count == 0 || !MaterialRules.IsHeaderRow(rows[0]))
            {
                result.Errors.Add($"「{ws.Name}」工作表的第一列要有「{HPartNo}」標題。");
                return;
            }
            result.Materials = MaterialRules.ParseRows(rows);
        }

        private static void ReadMachines(IXLWorksheet? machineSheet, IXLWorksheet? itemSheet, ExcelImport result)
        {
            var forms = new Dictionary<string, Machine>(StringComparer.OrdinalIgnoreCase);

            if (machineSheet != null)
            {
                var columns = HeaderColumns(machineSheet);
                if (!Require(machineSheet, columns, result, HWorkOrder, HMachineName)) return;
                result.HasFunctions = columns.ContainsKey(HFunction);
                foreach (var row in machineSheet.RowsUsed().Skip(1))
                {
                    var workOrder = Text(row, columns, HWorkOrder);
                    var name = Text(row, columns, HMachineName);
                    if (workOrder.Length == 0 && name.Length == 0) continue;
                    if (workOrder.Length == 0 || name.Length == 0)
                    {
                        result.Errors.Add($"「{machineSheet.Name}」第 {row.RowNumber()} 列：工令和機台名稱都要填。");
                        continue;
                    }
                    if (forms.ContainsKey(FormKey(workOrder, name)))
                    {
                        result.MergedForms++;
                        continue;
                    }
                    var machine = new Machine { WorkOrder = workOrder, Name = name };
                    if (Date(row, columns, HCreated) is { } created) machine.CreatedAt = created;
                    if (Date(row, columns, HUpdated) is { } updated) machine.UpdatedAt = updated;
                    foreach (var function in Text(row, columns, HFunction).Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        machine.Functions.Add(new MachineFunction { Name = function });
                    forms[FormKey(workOrder, name)] = machine;
                }
            }

            if (itemSheet != null)
            {
                var columns = HeaderColumns(itemSheet);
                if (!Require(itemSheet, columns, result, HWorkOrder, HMachineName, HPartNo, HQuantity)) return;
                result.HasItems = true;
                foreach (var row in itemSheet.RowsUsed().Skip(1))
                    ReadItem(itemSheet.Name, row, columns, forms, result);
            }

            result.Machines = forms.Values.ToList();
        }

        private static void ReadItem(string sheet, IXLRow row, Dictionary<string, int> columns, Dictionary<string, Machine> forms, ExcelImport result)
        {
            var workOrder = Text(row, columns, HWorkOrder);
            var name = Text(row, columns, HMachineName);
            var partNo = Text(row, columns, HPartNo);
            var quantityText = Text(row, columns, HQuantity);
            if (workOrder.Length == 0 && name.Length == 0 && partNo.Length == 0 && quantityText.Length == 0) return;

            var where = $"「{sheet}」第 {row.RowNumber()} 列";
            if (workOrder.Length == 0 || name.Length == 0)
            {
                result.Errors.Add($"{where}：工令和機台名稱都要填。");
                return;
            }
            if (partNo.Length == 0)
            {
                result.Errors.Add($"{where}：料號要填。");
                return;
            }
            if (!TryQuantity(row.Cell(columns[HQuantity]), out var quantity) || quantity <= 0)
            {
                result.Errors.Add($"{where}：數量要是大於 0 的數字（目前是「{quantityText}」）。");
                return;
            }

            var sourcesText = Text(row, columns, HSources);
            if (!TryParseSources(sourcesText, out var sources))
            {
                result.Errors.Add($"{where}：來源功能要寫成「功能×數量」（目前是「{sourcesText}」）。");
                return;
            }

            // 只有明細、「機台」工作表沒寫的表單也建起來，料才不會不見
            if (!forms.TryGetValue(FormKey(workOrder, name), out var machine))
                forms[FormKey(workOrder, name)] = machine = new Machine { WorkOrder = workOrder, Name = name };

            // 來源功能有寫的，表單也要算選了這個功能，之後才取消得掉
            foreach (var source in sources)
                if (!machine.Functions.Any(f => FunctionRules.SameName(f.Name, source.RuleName)))
                    machine.Functions.Add(new MachineFunction { Name = source.RuleName });

            var existing = machine.Items.FirstOrDefault(i => MaterialRules.SamePartNo(i.PartNo, partNo));
            if (existing != null)
            {
                existing.Quantity += quantity;
                existing.Sources.AddRange(sources);
                return;
            }
            machine.Items.Add(new MachineItem
            {
                PartNo = partNo,
                Name = Text(row, columns, HName),
                Spec = Text(row, columns, HSpec),
                Quantity = quantity,
                Note = Text(row, columns, HNote),
                Sources = sources,
            });
        }

        private static string FormKey(string workOrder, string name) => workOrder.Trim() + "\0" + name.Trim();

        private static Dictionary<string, int> HeaderColumns(IXLWorksheet ws)
        {
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var header = ws.FirstRowUsed();
            if (header == null) return columns;
            foreach (var cell in header.CellsUsed())
                columns.TryAdd(CellText(cell), cell.Address.ColumnNumber);
            return columns;
        }

        private static bool Require(IXLWorksheet ws, Dictionary<string, int> columns, ExcelImport result, params string[] headers)
        {
            var missing = headers.Where(h => !columns.ContainsKey(h)).ToList();
            if (missing.Count == 0) return true;
            result.Errors.Add($"「{ws.Name}」工作表找不到這些欄位：{string.Join("、", missing)}。");
            return false;
        }

        private static string Text(IXLRow row, Dictionary<string, int> columns, string header) =>
            columns.TryGetValue(header, out var column) ? CellText(row.Cell(column)) : "";

        /// <summary>數字料號不要變成 1E+15 這種寫法。</summary>
        private static string CellText(IXLCell cell)
        {
            var value = cell.Value;
            return TextRules.Clean(value.IsNumber
                ? value.GetNumber().ToString("0.##########", CultureInfo.InvariantCulture)
                : cell.GetString());
        }

        private static IEnumerable<(int Row, string[] Cells)> RowTexts(IXLWorksheet ws)
        {
            foreach (var row in ws.RowsUsed())
            {
                var last = row.LastCellUsed()?.Address.ColumnNumber ?? 0;
                yield return (row.RowNumber(), Enumerable.Range(1, last).Select(c => CellText(row.Cell(c))).ToArray());
            }
        }

        private static DateTime? Date(IXLRow row, Dictionary<string, int> columns, string header)
        {
            if (!columns.TryGetValue(header, out var column)) return null;
            var cell = row.Cell(column);
            if (cell.Value.IsDateTime) return cell.Value.GetDateTime();
            return DateTime.TryParse(CellText(cell), out var parsed) ? parsed : null;
        }

        private static bool TryQuantity(IXLCell cell, out decimal quantity)
        {
            quantity = 0;
            var value = cell.Value;
            if (value.IsNumber)
            {
                var number = value.GetNumber();
                if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > 1e15) return false;
                quantity = (decimal)number;
                return true;
            }
            return decimal.TryParse(CellText(cell), NumberStyles.Number, CultureInfo.InvariantCulture, out quantity);
        }

        // ---------- 匯入：套用 ----------

        /// <summary>
        /// 把讀到的內容套用到 target。檔案沒有的工作表，對應的資料不動（「完全以 Excel 為準」也一樣）。
        /// 工令和機台名稱相同就算同一張表單：保留原本的編號和建立時間，料以檔案為準。
        /// </summary>
        public static ImportResult Apply(AppData target, ExcelImport file, ImportMode mode)
        {
            int materialsAdded = 0, materialsUpdated = 0, materialsSkipped = 0, machinesAdded = 0, machinesUpdated = 0, rulesAdded = 0, rulesUpdated = 0;

            if (file.Materials != null)
            {
                if (mode == ImportMode.Replace) target.Materials.Clear();
                (materialsAdded, materialsUpdated, materialsSkipped) = MaterialRules.Merge(target.Materials, file.Materials);
            }

            if (file.Rules != null)
            {
                var touchedRules = new HashSet<FunctionRule>();
                foreach (var imported in file.Rules)
                {
                    var rule = target.Rules.FirstOrDefault(r => FunctionRules.SameName(r.Name, imported.Name));
                    if (rule == null)
                    {
                        rule = new FunctionRule { Name = imported.Name };
                        target.Rules.Add(rule);
                        rulesAdded++;
                    }
                    else
                    {
                        rulesUpdated++;
                    }
                    rule.Items = imported.Items.Select(i => new RuleItem { PartNo = i.PartNo, Quantity = i.Quantity }).ToList();
                    touchedRules.Add(rule);
                }
                if (mode == ImportMode.Replace) target.Rules.RemoveAll(r => !touchedRules.Contains(r));
            }

            // Excel 上只有功能名稱，用名稱對回規則；規則刪掉了就沿用表單原本的編號，都沒有才給新的
            var ruleIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in target.Rules) ruleIds[r.Name.Trim()] = r.Id;
            foreach (var f in target.Machines.SelectMany(m => m.Functions)) ruleIds.TryAdd(f.Name.Trim(), f.RuleId);
            string RuleIdOf(string name)
            {
                if (!ruleIds.TryGetValue(name.Trim(), out var id)) ruleIds[name.Trim()] = id = Guid.NewGuid().ToString("N");
                return id;
            }

            if (file.Machines != null)
            {
                var touched = new HashSet<Machine>();
                foreach (var imported in file.Machines)
                {
                    var existing = target.Machines.FirstOrDefault(m => string.Equals(
                        FormKey(m.WorkOrder, m.Name), FormKey(imported.WorkOrder, imported.Name), StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        existing = new Machine { WorkOrder = imported.WorkOrder, Name = imported.Name, CreatedAt = imported.CreatedAt };
                        target.Machines.Add(existing);
                        machinesAdded++;
                    }
                    else
                    {
                        machinesUpdated++;
                    }

                    existing.UpdatedAt = imported.UpdatedAt;
                    if (file.HasFunctions || file.HasItems && imported.Functions.Count > 0)
                        existing.Functions = imported.Functions.Select(f => new MachineFunction { RuleId = RuleIdOf(f.Name), Name = f.Name }).ToList();
                    if (file.HasItems)
                    {
                        existing.Items = imported.Items.Select(i => CopyItem(i, RuleIdOf)).ToList();
                        FillBlanks(existing.Items, target.Materials);
                    }
                    touched.Add(existing);
                }
                if (mode == ImportMode.Replace) target.Machines.RemoveAll(m => !touched.Contains(m));
            }

            // 物料可能換了編號，重新對應（只動編號，表單上的名稱、規格不變）
            var byPartNo = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in target.Materials) byPartNo.TryAdd(m.PartNo.Trim(), m);
            foreach (var item in target.Machines.SelectMany(m => m.Items))
                item.MaterialId = byPartNo.TryGetValue(item.PartNo.Trim(), out var material) ? material.Id : "";

            return new ImportResult(materialsAdded, materialsUpdated, materialsSkipped, machinesAdded, machinesUpdated, rulesAdded, rulesUpdated);
        }

        private static MachineItem CopyItem(MachineItem i, Func<string, string> ruleIdOf) => new()
        {
            PartNo = i.PartNo,
            Name = i.Name,
            Spec = i.Spec,
            Quantity = i.Quantity,
            Note = i.Note,
            Sources = i.Sources.Select(s => new ItemSource { RuleId = ruleIdOf(s.RuleName), RuleName = s.RuleName, Quantity = s.Quantity }).ToList(),
        };

        /// <summary>明細沒寫名稱、規格時，用資料庫的補上。</summary>
        private static void FillBlanks(List<MachineItem> items, List<Material> materials)
        {
            foreach (var item in items.Where(i => i.Name.Length == 0 || i.Spec.Length == 0))
            {
                var material = materials.FirstOrDefault(m => MaterialRules.SamePartNo(m.PartNo, item.PartNo));
                if (material == null) continue;
                if (item.Name.Length == 0) item.Name = material.Name;
                if (item.Spec.Length == 0) item.Spec = material.Spec;
            }
        }
    }
}
