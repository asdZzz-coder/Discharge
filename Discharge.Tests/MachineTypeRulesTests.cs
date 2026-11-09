using System.IO;
using ClosedXML.Excel;
using Discharge.Models;
using Discharge.Services;

namespace Discharge.Tests
{
    public class MachineTypeRulesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "Discharge.Tests." + Guid.NewGuid().ToString("N"));

        public MachineTypeRulesTests() => Directory.CreateDirectory(_dir);

        public void Dispose() => UpdateService.TryDeleteDirectory(_dir);

        private static AppData Sample() => new()
        {
            MachineTypes = { "A型", "B型", "C型" },
            Rules =
            {
                new FunctionRule { Name = "正逆轉", MachineTypes = { "A型", "B型" } },
                new FunctionRule { Name = "變頻", MachineTypes = { "B型" } },
                new FunctionRule { Name = "斷路保護" },
            },
            Machines =
            {
                new Machine { WorkOrder = "W1", Name = "一號", MachineType = "B型" },
                new Machine { WorkOrder = "W2", Name = "二號", MachineType = "A型" },
            },
        };

        [Fact]
        public void Validate_RejectsBlankSeparatorAndDuplicates_ButRenameMayKeepItsOwnName()
        {
            var types = new[] { "A型", "B型" };
            Assert.NotNull(MachineTypeRules.Validate(types, " "));
            Assert.NotNull(MachineTypeRules.Validate(types, "A、B"));
            Assert.Equal("已經有「a型」這個機種了。", MachineTypeRules.Validate(types, "a型"));
            Assert.Null(MachineTypeRules.Validate(types, "C型"));
            Assert.Null(MachineTypeRules.Validate(types, "a型", except: "A型")); // 只改大小寫
            Assert.NotNull(MachineTypeRules.Validate(types, "B型", except: "A型"));
        }

        [Fact]
        public void Add_HasNoLimit_AndKeepsListSorted()
        {
            var data = new AppData();
            for (var i = 200; i >= 1; i--) MachineTypeRules.Add(data, $"機種{i:000}");
            Assert.Equal(200, data.MachineTypes.Count);
            Assert.Equal("機種001", data.MachineTypes[0]);
            Assert.Equal("機種200", data.MachineTypes[^1]);
        }

        [Fact]
        public void Rename_ChangesListRulesAndMachines()
        {
            var data = Sample();
            MachineTypeRules.Rename(data, "B型", "Z型");

            Assert.Equal(new[] { "A型", "C型", "Z型" }, data.MachineTypes);
            Assert.Equal(new[] { "A型", "Z型" }, data.Rules[0].MachineTypes);
            Assert.Equal(new[] { "Z型" }, data.Rules[1].MachineTypes);
            Assert.Empty(data.Rules[2].MachineTypes);
            Assert.Equal("Z型", data.Machines[0].MachineType);
            Assert.Equal("A型", data.Machines[1].MachineType);
        }

        [Fact]
        public void Remove_TakesTypeOffRules_ReportsRulesThatBecomeUniversal_AndKeepsMachineText()
        {
            var data = Sample();
            var (rules, machines) = MachineTypeRules.Usage(data, "b型");
            Assert.Equal(new[] { "正逆轉", "變頻" }, rules.Select(r => r.Name));
            Assert.Equal("W1", machines.Single().WorkOrder);
            Assert.Equal("變頻", MachineTypeRules.OnlyThisType(data, "B型").Single().Name);

            MachineTypeRules.Remove(data, "B型");

            Assert.Equal(new[] { "A型", "C型" }, data.MachineTypes);
            Assert.Equal(new[] { "A型" }, data.Rules[0].MachineTypes);
            Assert.Empty(data.Rules[1].MachineTypes);
            Assert.Equal("B型", data.Machines[0].MachineType); // 表單上的文字保留，畫面標示已刪除
        }

        [Fact]
        public void Load_OldDataWithoutList_BuildsItFromRulesAndMachines()
        {
            var file = Path.Combine(_dir, "data.json");
            File.WriteAllText(file, """
                { "Rules": [ { "Name": "正逆轉", "MachineTypes": ["B型", " A型 "] } ],
                  "Machines": [ { "WorkOrder": "W1", "Name": "M", "MachineType": "C型" }, { "WorkOrder": "W2", "Name": "N", "MachineType": "a型" } ] }
                """);
            Assert.Equal(new[] { "A型", "B型", "C型" }, DataStore.Load(file).MachineTypes);
        }

        [Fact]
        public void Load_WithList_DoesNotBringBackDeletedTypesFromMachines_ButAddsTypesRulesUse()
        {
            var file = Path.Combine(_dir, "data.json");
            File.WriteAllText(file, """
                { "MachineTypes": ["A型"],
                  "Rules": [ { "Name": "變頻", "MachineTypes": ["B型"] } ],
                  "Machines": [ { "WorkOrder": "W1", "Name": "M", "MachineType": "已刪除" } ] }
                """);
            Assert.Equal(new[] { "A型", "B型" }, DataStore.Load(file).MachineTypes);

            File.WriteAllText(file, """{ "MachineTypes": [], "Machines": [ { "WorkOrder": "W1", "Name": "M", "MachineType": "X" } ] }""");
            Assert.Empty(DataStore.Load(file).MachineTypes); // 全部刪掉了也不要再長回來
        }

        [Fact]
        public void SaveThenLoad_KeepsList()
        {
            var file = Path.Combine(_dir, "data.json");
            DataStore.Save(Sample(), file);
            Assert.Equal(new[] { "A型", "B型", "C型" }, DataStore.Load(file).MachineTypes);
        }

        [Fact]
        public void Excel_ExportThenImport_KeepsUnusedTypes()
        {
            var path = Path.Combine(_dir, "a.xlsx");
            ExcelService.Export(Sample(), path); // C型 沒有規則或表單用到，要靠「機種」工作表帶回來

            var file = ExcelService.Read(path);
            Assert.Equal(new[] { "A型", "B型", "C型" }, file.MachineTypes);
            var target = new AppData();
            var result = ExcelService.Apply(target, file, ImportMode.Merge);
            Assert.Equal(new[] { "A型", "B型", "C型" }, target.MachineTypes);
            Assert.Equal(3, result.TypesAdded);
        }

        [Fact]
        public void Excel_Merge_AddsTypes_Replace_KeepsOnlyFileTypesAndTypesRulesUse()
        {
            var path = Path.Combine(_dir, "types.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add(ExcelService.TypeSheet);
                ws.Cell(1, 1).Value = "機種";
                ws.Cell(2, 1).Value = "D型";
                ws.Cell(3, 1).Value = "d型"; // 重複
                ws.Cell(4, 1).Value = "A型";
                wb.SaveAs(path);
            }

            var merged = Sample();
            ExcelService.Apply(merged, ExcelService.Read(path), ImportMode.Merge);
            Assert.Equal(new[] { "A型", "B型", "C型", "D型" }, merged.MachineTypes);

            var replaced = Sample();
            ExcelService.Apply(replaced, ExcelService.Read(path), ImportMode.Replace);
            Assert.Equal(new[] { "A型", "B型", "D型" }, replaced.MachineTypes); // B型 規則還在用，留著；C型 沒人用，拿掉
        }

        [Fact]
        public void Excel_OldFileWithoutTypeSheet_AddsTypesFromImportedMachines()
        {
            var path = Path.Combine(_dir, "old.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add(ExcelService.MachineSheet);
                ws.Cell(1, 1).Value = "工令";
                ws.Cell(1, 2).Value = "機台名稱";
                ws.Cell(1, 3).Value = "機種";
                ws.Cell(2, 1).Value = "W9";
                ws.Cell(2, 2).Value = "九號";
                ws.Cell(2, 3).Value = "E型";
                wb.SaveAs(path);
            }

            var file = ExcelService.Read(path);
            Assert.Null(file.MachineTypes);
            var target = new AppData();
            ExcelService.Apply(target, file, ImportMode.Merge);
            Assert.Equal(new[] { "E型" }, target.MachineTypes);
        }

        [Fact]
        public void Excel_TypeWithSeparator_IsAnError()
        {
            var path = Path.Combine(_dir, "bad.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add(ExcelService.TypeSheet);
                ws.Cell(1, 1).Value = "機種";
                ws.Cell(2, 1).Value = "A、B";
                wb.SaveAs(path);
            }
            Assert.Contains("第 2 列", Assert.Single(ExcelService.Read(path).Errors));
        }
    }
}
