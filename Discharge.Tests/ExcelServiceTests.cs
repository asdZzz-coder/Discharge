using System.IO;
using ClosedXML.Excel;
using Discharge.Models;
using Discharge.Services;

namespace Discharge.Tests
{
    public class ExcelServiceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "Discharge.Tests." + Guid.NewGuid().ToString("N"));

        public ExcelServiceTests() => Directory.CreateDirectory(_dir);

        public void Dispose() => UpdateService.TryDeleteDirectory(_dir);

        private string PathOf(string name) => Path.Combine(_dir, name);

        private static AppData Sample() => new()
        {
            Materials =
            {
                new Material { PartNo = "NFB-100", Name = "無熔絲開關", Spec = "3P 100A", Note = "常用" },
                new Material { PartNo = "007", Name = "端子", Spec = "=A1" },
            },
            Machines =
            {
                new Machine
                {
                    WorkOrder = "W001",
                    Name = "配電盤",
                    CreatedAt = new DateTime(2026, 1, 2, 3, 4, 0),
                    UpdatedAt = new DateTime(2026, 2, 3, 4, 5, 0),
                    Items =
                    {
                        new MachineItem { PartNo = "NFB-100", Name = "無熔絲開關", Spec = "3P 100A", Quantity = 2.5m, Note = "急" },
                        new MachineItem { PartNo = "007", Name = "端子", Quantity = 10 },
                    },
                },
                new Machine { WorkOrder = "W002", Name = "空的表單" },
            },
        };

        /// <summary>用指定內容建一個 Excel，每張工作表的第一列是標題。</summary>
        private string MakeWorkbook(params (string Sheet, object[][] Rows)[] sheets)
        {
            var path = PathOf(Guid.NewGuid().ToString("N") + ".xlsx");
            using var wb = new XLWorkbook();
            foreach (var (sheet, rows) in sheets)
            {
                var ws = wb.Worksheets.Add(sheet);
                for (var r = 0; r < rows.Length; r++)
                    for (var c = 0; c < rows[r].Length; c++)
                        ws.Cell(r + 1, c + 1).Value = rows[r][c] switch
                        {
                            double d => d,
                            string s => s,
                            _ => "",
                        };
            }
            wb.SaveAs(path);
            return path;
        }

        [Fact]
        public void ExportThenImport_IntoEmptyData_RestoresEverything()
        {
            var path = PathOf("a.xlsx");
            ExcelService.Export(Sample(), path);

            var file = ExcelService.Read(path);
            Assert.Empty(file.Errors);
            var target = new AppData();
            var result = ExcelService.Apply(target, file, ImportMode.Merge);

            Assert.Equal(new ImportResult(2, 0, 0, 2, 0), result);
            var terminal = target.Materials.Single(m => m.PartNo == "007"); // 開頭的 0 還在
            Assert.Equal("=A1", terminal.Spec);                             // 不會被當成公式
            var nfb = target.Materials.Single(m => m.PartNo == "NFB-100");
            Assert.Equal(("無熔絲開關", "3P 100A", "常用"), (nfb.Name, nfb.Spec, nfb.Note));

            var machine = target.Machines.Single(m => m.WorkOrder == "W001");
            Assert.Equal("配電盤", machine.Name);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 0), machine.CreatedAt);
            Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 0), machine.UpdatedAt);
            var item = machine.Items.Single(i => i.PartNo == "NFB-100");
            Assert.Equal((2.5m, "急", nfb.Id), (item.Quantity, item.Note, item.MaterialId));
            Assert.Empty(target.Machines.Single(m => m.WorkOrder == "W002").Items);
        }

        [Fact]
        public void Merge_KeepsWhatFileDoesNotHave_AndFileWinsForSameForm()
        {
            var path = PathOf("a.xlsx");
            ExcelService.Export(Sample(), path);

            var target = new AppData
            {
                Materials = { new Material { PartNo = "OLD-1", Name = "只有本機有" }, new Material { PartNo = "nfb-100", Name = "舊名稱", Spec = "舊規格" } },
                Machines =
                {
                    new Machine { WorkOrder = "W999", Name = "只有本機有", Items = { new MachineItem { PartNo = "OLD-1", Quantity = 1 } } },
                    new Machine { WorkOrder = "w001", Name = "配電盤", CreatedAt = new DateTime(2020, 1, 1), Items = { new MachineItem { PartNo = "OLD-1", Quantity = 9 } } },
                },
            };
            var keptId = target.Machines[1].Id;

            var result = ExcelService.Apply(target, ExcelService.Read(path), ImportMode.Merge);

            Assert.Equal(new ImportResult(1, 1, 0, 1, 1), result);
            Assert.Contains(target.Materials, m => m.PartNo == "OLD-1");
            Assert.Equal("無熔絲開關", target.Materials.Single(m => m.PartNo == "nfb-100").Name);
            Assert.Single(target.Machines.Single(m => m.WorkOrder == "W999").Items);

            var same = target.Machines.Single(m => m.Id == keptId);
            Assert.Equal(new DateTime(2020, 1, 1), same.CreatedAt);                    // 建立時間保留
            Assert.Equal(new[] { "007", "NFB-100" }, same.Items.Select(i => i.PartNo).OrderBy(p => p)); // 料以檔案為準
        }

        [Fact]
        public void Replace_RemovesWhatFileDoesNotHave()
        {
            var path = PathOf("a.xlsx");
            ExcelService.Export(Sample(), path);
            var target = new AppData
            {
                Materials = { new Material { PartNo = "OLD-1", Name = "舊的" } },
                Machines = { new Machine { WorkOrder = "W999", Name = "舊的" } },
            };

            ExcelService.Apply(target, ExcelService.Read(path), ImportMode.Replace);

            Assert.DoesNotContain(target.Materials, m => m.PartNo == "OLD-1");
            Assert.DoesNotContain(target.Machines, m => m.WorkOrder == "W999");
            Assert.Equal(2, target.Materials.Count);
            Assert.Equal(2, target.Machines.Count);
        }

        [Fact]
        public void Replace_WithOnlyMaterialSheet_KeepsMachines()
        {
            var path = MakeWorkbook((ExcelService.MaterialSheet, new[]
            {
                new object[] { "料號", "名稱" },
                new object[] { "A001", "斷路器" },
            }));
            var target = Sample();

            var file = ExcelService.Read(path);
            Assert.Null(file.Machines);
            ExcelService.Apply(target, file, ImportMode.Replace);

            Assert.Equal("A001", target.Materials.Single().PartNo);
            Assert.Equal(2, target.Machines.Count);
            Assert.Equal(2, target.Machines.Single(m => m.WorkOrder == "W001").Items.Count);
            Assert.All(target.Machines.Single(m => m.WorkOrder == "W001").Items, i => Assert.Equal("", i.MaterialId));
        }

        [Fact]
        public void Replace_WithoutItemSheet_KeepsItemsOfSameForm()
        {
            var path = MakeWorkbook((ExcelService.MachineSheet, new[]
            {
                new object[] { "工令", "機台名稱" },
                new object[] { "W001", "配電盤" },
            }));
            var target = Sample();

            ExcelService.Apply(target, ExcelService.Read(path), ImportMode.Replace);

            Assert.Equal(2, target.Machines.Single().Items.Count); // W002 不在檔案裡被移除，W001 的料留著
        }

        [Fact]
        public void Read_ColumnsInAnyOrder_NumericPartNo_AndFormOnlyInItemSheet()
        {
            var path = MakeWorkbook((ExcelService.ItemSheet, new[]
            {
                new object[] { "數量", "料號", "機台名稱", "工令" },
                new object[] { 3d, 1001d, "配電盤", "W001" },
                new object[] { "2", 1001d, "配電盤", "W001" }, // 同一項料加總
            }));
            var target = new AppData { Materials = { new Material { PartNo = "1001", Name = "斷路器", Spec = "3P" } } };

            var file = ExcelService.Read(path);
            Assert.Empty(file.Errors);
            ExcelService.Apply(target, file, ImportMode.Merge);

            var item = target.Machines.Single().Items.Single();
            Assert.Equal(("1001", 5m), (item.PartNo, item.Quantity));
            Assert.Equal(("斷路器", "3P"), (item.Name, item.Spec)); // 空白的從資料庫補
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("")]
        public void Read_BadQuantity_IsAnError(string quantity)
        {
            var path = MakeWorkbook((ExcelService.ItemSheet, new[]
            {
                new object[] { "工令", "機台名稱", "料號", "數量" },
                new object[] { "W001", "配電盤", "A001", quantity },
            }));

            var error = Assert.Single(ExcelService.Read(path).Errors);
            Assert.Contains("第 2 列", error);
        }

        [Fact]
        public void Read_MissingColumns_And_HalfFilledRows_AreErrors()
        {
            var path = MakeWorkbook(
                (ExcelService.MachineSheet, new[]
                {
                    new object[] { "工令", "機台名稱" },
                    new object[] { "W001", "" },
                }),
                (ExcelService.ItemSheet, new[] { new object[] { "工令", "料號" } }));

            var errors = ExcelService.Read(path).Errors;

            Assert.Contains(errors, e => e.Contains("第 2 列"));
            Assert.Contains(errors, e => e.Contains("機台名稱") && e.Contains("數量"));
        }

        [Fact]
        public void Read_DuplicateForms_AreMergedAndCounted()
        {
            var path = PathOf("a.xlsx");
            var data = new AppData
            {
                Machines =
                {
                    new Machine { WorkOrder = "W001", Name = "A", Items = { new MachineItem { PartNo = "X", Quantity = 1 } } },
                    new Machine { WorkOrder = "w001", Name = "a", Items = { new MachineItem { PartNo = "X", Quantity = 2 }, new MachineItem { PartNo = "Y", Quantity = 1 } } },
                },
            };
            ExcelService.Export(data, path);

            var file = ExcelService.Read(path);

            Assert.Equal(1, file.MergedForms);
            var form = file.Machines!.Single();
            Assert.Equal(3m, form.Items.Single(i => i.PartNo == "X").Quantity);
            Assert.Equal(2, form.Items.Count);
        }

        [Fact]
        public void Read_OtherWorkbook_FirstSheetWithPartNoHeader_IsMaterialList()
        {
            var path = MakeWorkbook(("Sheet1", new[]
            {
                new object[] { "品名", "品號" },
                new object[] { "斷路器", "A001" },
            }));

            var file = ExcelService.Read(path);

            Assert.Null(file.Machines);
            Assert.Equal(("A001", "斷路器"), (file.Materials!.Single().PartNo, file.Materials!.Single().Name));
        }

        [Fact]
        public void Read_UnrelatedWorkbook_IsEmpty()
        {
            var path = MakeWorkbook(("Sheet1", new[] { new object[] { "姓名", "電話" }, new object[] { "王小明", "0912" } }));

            var file = ExcelService.Read(path);

            Assert.True(file.IsEmpty);
            Assert.Empty(file.Errors);
        }

        [Fact]
        public void Read_MaterialSheetWithoutHeader_IsAnError()
        {
            var path = MakeWorkbook((ExcelService.MaterialSheet, new[] { new object[] { "A001", "斷路器" } }));

            var file = ExcelService.Read(path);

            Assert.Null(file.Materials);
            Assert.Single(file.Errors);
        }

        private static AppData SampleWithRules()
        {
            var data = new AppData
            {
                Materials =
                {
                    new Material { PartNo = "NFB-100", Name = "無熔絲開關", Spec = "3P 100A" },
                    new Material { PartNo = "T-01", Name = "端子台", Spec = "10P" },
                },
                Rules =
                {
                    new FunctionRule { Name = "正逆轉", Items = { new RuleItem { PartNo = "NFB-100", Quantity = 2.5m }, new RuleItem { PartNo = "T-01", Quantity = 4 } } },
                    new FunctionRule { Name = "變頻", Items = { new RuleItem { PartNo = "T-01", Quantity = 1 } } },
                    new FunctionRule { Name = "還沒設定料" },
                },
            };
            var machine = new Machine { WorkOrder = "W001", Name = "配電盤" };
            FunctionRules.AddFunction(machine.Items, machine.Functions, data.Rules[0], data.Materials);
            FunctionRules.AddFunction(machine.Items, machine.Functions, data.Rules[1], data.Materials);
            data.Machines.Add(machine);
            return data;
        }

        [Fact]
        public void ExportThenImport_RestoresRulesAndFunctions_AndCancelStillWorks()
        {
            var path = PathOf("a.xlsx");
            ExcelService.Export(SampleWithRules(), path);

            var file = ExcelService.Read(path);
            Assert.Empty(file.Errors);
            var target = new AppData();
            var result = ExcelService.Apply(target, file, ImportMode.Merge);

            Assert.Equal((3, 0), (result.RulesAdded, result.RulesUpdated));
            var rule = target.Rules.Single(r => r.Name == "正逆轉");
            Assert.Equal(2.5m, rule.Items.Single(i => i.PartNo == "NFB-100").Quantity);
            Assert.Empty(target.Rules.Single(r => r.Name == "還沒設定料").Items);

            var machine = target.Machines.Single();
            Assert.Equal(new[] { "正逆轉", "變頻" }, machine.Functions.Select(f => f.Name));
            Assert.Equal(rule.Id, machine.Functions[0].RuleId);
            var terminal = machine.Items.Single(i => i.PartNo == "T-01");
            Assert.Equal((5m, "正逆轉、變頻"), (terminal.Quantity, terminal.FunctionText));

            // 匯入後取消功能，一樣能正確扣回去
            FunctionRules.RemoveFunction(machine.Items, machine.Functions, rule.Id);
            Assert.Equal(1m, machine.Items.Single().Quantity);
            Assert.Equal("變頻", machine.Functions.Single().Name);
        }

        [Fact]
        public void Merge_SameRuleName_KeepsIdAndReplacesItems_OtherRulesStay()
        {
            var path = MakeWorkbook((ExcelService.RuleSheet, new[]
            {
                new object[] { "功能", "料號", "數量" },
                new object[] { "A", "X", 2d },
                new object[] { "A", "x", 3d },   // 同一項料加總
                new object[] { "新規則", "", "" },
            }));
            var keep = new FunctionRule { Name = "只有本機有" };
            var same = new FunctionRule { Name = "a", Items = { new RuleItem { PartNo = "OLD", Quantity = 9 } } };
            var target = new AppData { Rules = { keep, same } };

            var result = ExcelService.Apply(target, ExcelService.Read(path), ImportMode.Merge);

            Assert.Equal((1, 1), (result.RulesAdded, result.RulesUpdated));
            Assert.Contains(keep, target.Rules);
            Assert.Same(same, target.Rules.Single(r => r.Id == same.Id));
            Assert.Equal(("X", 5m), (same.Items.Single().PartNo, same.Items.Single().Quantity));
        }

        [Fact]
        public void Replace_RemovesRulesNotInFile_ButWithoutRuleSheetKeepsThem()
        {
            var rulesFile = MakeWorkbook((ExcelService.RuleSheet, new[] { new object[] { "功能", "料號", "數量" }, new object[] { "A", "X", 1d } }));
            var materialsFile = MakeWorkbook((ExcelService.MaterialSheet, new[] { new object[] { "料號", "名稱" }, new object[] { "X", "料" } }));

            var target = new AppData { Rules = { new FunctionRule { Name = "舊" } } };
            ExcelService.Apply(target, ExcelService.Read(materialsFile), ImportMode.Replace);
            Assert.Equal("舊", target.Rules.Single().Name);

            ExcelService.Apply(target, ExcelService.Read(rulesFile), ImportMode.Replace);
            Assert.Equal("A", target.Rules.Single().Name);
        }

        [Fact]
        public void Read_OldExportWithoutFunctionColumns_StillWorks_AndKeepsFormFunctions()
        {
            var path = MakeWorkbook(
                (ExcelService.MachineSheet, new[] { new object[] { "工令", "機台名稱" }, new object[] { "W001", "A" } }),
                (ExcelService.ItemSheet, new[]
                {
                    new object[] { "工令", "機台名稱", "料號", "數量" },
                    new object[] { "W001", "A", "X", 1d },
                }));
            var target = new AppData { Machines = { new Machine { WorkOrder = "W001", Name = "A", Functions = { new MachineFunction { RuleId = "r1", Name = "舊功能" } } } } };

            var file = ExcelService.Read(path);
            Assert.Empty(file.Errors);
            Assert.False(file.HasFunctions);
            ExcelService.Apply(target, file, ImportMode.Merge);

            Assert.Equal("舊功能", target.Machines.Single().Functions.Single().Name);
            Assert.Empty(target.Machines.Single().Items.Single().Sources);
        }

        [Theory]
        [InlineData("功能A")]       // 沒有數量
        [InlineData("功能A×abc")]   // 數量不是數字
        [InlineData("×2")]          // 沒有名稱
        public void Read_BadSources_IsAnError(string sources)
        {
            var path = MakeWorkbook((ExcelService.ItemSheet, new[]
            {
                new object[] { "工令", "機台名稱", "料號", "數量", "來源功能" },
                new object[] { "W001", "A", "X", 1d, sources },
            }));

            Assert.Contains("來源功能", Assert.Single(ExcelService.Read(path).Errors));
        }

        [Theory]
        [InlineData("A", "X", "abc")]  // 數量錯
        [InlineData("", "X", "1")]     // 沒寫功能
        [InlineData("A、B", "X", "1")] // 名稱有保留字元
        public void Read_BadRuleRows_AreErrors(string name, string partNo, string quantity)
        {
            var path = MakeWorkbook((ExcelService.RuleSheet, new[]
            {
                new object[] { "功能", "料號", "數量" },
                new object[] { name, partNo, quantity },
            }));

            Assert.Contains("第 2 列", Assert.Single(ExcelService.Read(path).Errors));
        }

        [Fact]
        public void Merge_SkipsNewMaterialsWithoutName()
        {
            var path = MakeWorkbook((ExcelService.MaterialSheet, new[]
            {
                new object[] { "料號", "名稱" },
                new object[] { "A001", "" },
            }));
            var target = new AppData();

            var result = ExcelService.Apply(target, ExcelService.Read(path), ImportMode.Merge);

            Assert.Equal(1, result.MaterialsSkipped);
            Assert.Empty(target.Materials);
        }
    }
}
