using System.IO;
using Discharge.Models;
using Discharge.Services;

namespace Discharge.Tests
{
    public class FunctionRulesTests
    {
        private static readonly List<Material> Materials = new()
        {
            new Material { PartNo = "NFB-100", Name = "無熔絲開關", Spec = "3P 100A" },
            new Material { PartNo = "MC-25", Name = "電磁接觸器", Spec = "25A" },
            new Material { PartNo = "T-01", Name = "端子台", Spec = "10P" },
        };

        private static FunctionRule Rule(string name, params (string PartNo, decimal Quantity)[] items) => new()
        {
            Name = name,
            Items = items.Select(i => new RuleItem { PartNo = i.PartNo, Quantity = i.Quantity }).ToList(),
        };

        [Fact]
        public void Validate_RequiresUniqueNameAndPositiveQuantity()
        {
            var existing = Rule("正逆轉");
            var all = new[] { existing };

            Assert.Equal("請輸入功能名稱。", FunctionRules.Validate(Rule(""), all));
            Assert.NotNull(FunctionRules.Validate(Rule("A、B"), all));
            Assert.NotNull(FunctionRules.Validate(Rule("A×2"), all));
            Assert.NotNull(FunctionRules.Validate(Rule("正逆轉"), all));
            Assert.Null(FunctionRules.Validate(existing, all)); // 自己不算重複
            Assert.Equal("料號 T-01 的數量要大於 0。", FunctionRules.Validate(Rule("新功能", ("T-01", 0)), all));
            Assert.Null(FunctionRules.Validate(Rule("新功能", ("T-01", 2)), all));
        }

        [Fact]
        public void AddFunction_BringsItemsWithMaterialDetails_AndReportsMissing()
        {
            var items = new List<MachineItem>();
            var functions = new List<MachineFunction>();
            var rule = Rule("正逆轉", ("NFB-100", 1), ("mc-25", 2), ("不存在", 1));

            var (added, merged, missing) = FunctionRules.AddFunction(items, functions, rule, Materials);

            Assert.Equal((2, 0), (added, merged));
            Assert.Equal("不存在", Assert.Single(missing));
            Assert.Equal("正逆轉", Assert.Single(functions).Name);
            var mc = items.Single(i => i.PartNo == "MC-25"); // 用資料庫的寫法
            Assert.Equal(("電磁接觸器", "25A", 2m, "正逆轉"), (mc.Name, mc.Spec, mc.Quantity, mc.FunctionText));
        }

        [Fact]
        public void AddFunction_SameRuleTwice_DoesNotDouble()
        {
            var items = new List<MachineItem>();
            var functions = new List<MachineFunction>();
            var rule = Rule("正逆轉", ("T-01", 2));

            FunctionRules.AddFunction(items, functions, rule, Materials);
            FunctionRules.AddFunction(items, functions, rule, Materials);

            Assert.Equal(2m, Assert.Single(items).Quantity);
            Assert.Single(functions);
        }

        [Fact]
        public void TwoFunctionsSharingAPart_AddUp_AndRemoveOnlyTheirOwnShare()
        {
            var items = new List<MachineItem>();
            var functions = new List<MachineFunction>();
            var a = Rule("A", ("T-01", 2), ("NFB-100", 1));
            var b = Rule("B", ("T-01", 3));

            FunctionRules.AddFunction(items, functions, a, Materials);
            var (added, merged, _) = FunctionRules.AddFunction(items, functions, b, Materials);

            Assert.Equal((0, 1), (added, merged));
            var terminal = items.Single(i => i.PartNo == "T-01");
            Assert.Equal((5m, "A、B"), (terminal.Quantity, terminal.FunctionText));

            var removed = FunctionRules.RemoveFunction(items, functions, a.Id);

            Assert.Equal(1, removed);                    // NFB-100 只有 A 帶，整項移除
            Assert.Equal((3m, "B"), (terminal.Quantity, terminal.FunctionText));
            Assert.Equal("B", Assert.Single(functions).Name);

            Assert.Equal(1, FunctionRules.RemoveFunction(items, functions, b.Id));
            Assert.Empty(items);
        }

        [Fact]
        public void RemoveFunction_KeepsManualQuantityAndManualItems()
        {
            var manual = new MachineItem { PartNo = "MC-25", Quantity = 4 };
            var items = new List<MachineItem> { manual };
            var functions = new List<MachineFunction>();
            var rule = Rule("A", ("T-01", 2), ("MC-25", 1));

            FunctionRules.AddFunction(items, functions, rule, Materials);
            items.Single(i => i.PartNo == "T-01").Quantity = 5; // 使用者手動改多
            FunctionRules.RemoveFunction(items, functions, rule.Id);

            Assert.Equal(4m, manual.Quantity);
            Assert.Equal(3m, items.Single(i => i.PartNo == "T-01").Quantity);
        }

        [Fact]
        public void RemoveFunction_UsesRecordedQuantity_EvenIfRuleChangedOrDeleted()
        {
            var items = new List<MachineItem>();
            var functions = new List<MachineFunction>();
            var rule = Rule("A", ("T-01", 2));
            FunctionRules.AddFunction(items, functions, rule, Materials);

            rule.Items[0].Quantity = 99;
            FunctionRules.RemoveFunction(items, functions, rule.Id);

            Assert.Empty(items);
            Assert.Empty(functions);
        }

        [Fact]
        public void RemoveFunction_QuantityLoweredByHand_RemovesRowInsteadOfGoingNegative()
        {
            var items = new List<MachineItem>();
            var functions = new List<MachineFunction>();
            var rule = Rule("A", ("T-01", 5));
            FunctionRules.AddFunction(items, functions, rule, Materials);
            items[0].Quantity = 1;

            FunctionRules.RemoveFunction(items, functions, rule.Id);

            Assert.Empty(items);
        }

        [Fact]
        public void SetRulesOfPart_AddsUpdatesAndRemoves()
        {
            var a = Rule("A", ("T-01", 1));
            var b = Rule("B", ("t-01", 2));
            var c = Rule("C");
            var rules = new[] { a, b, c };

            Assert.Equal(new[] { a.Id, b.Id }, FunctionRules.RulesOfPart(rules, "T-01").Keys);

            FunctionRules.SetRulesOfPart(rules, "T-01", new Dictionary<string, decimal> { [a.Id] = 7, [c.Id] = 3 });

            Assert.Equal(7m, a.Items.Single().Quantity);
            Assert.Empty(b.Items);
            Assert.Equal(("T-01", 3m), (c.Items.Single().PartNo, c.Items.Single().Quantity));
        }

        [Fact]
        public void RenamePart_And_RemoveParts_FollowTheDatabase()
        {
            var a = Rule("A", ("T-01", 1), ("MC-25", 1));
            var b = Rule("B", ("MC-25", 1));
            var rules = new[] { a, b };

            FunctionRules.RenamePart(rules, "t-01", "T-02");
            Assert.Contains(a.Items, i => i.PartNo == "T-02");

            Assert.Equal(2, FunctionRules.CountRulesUsing(rules, new[] { "MC-25" }));
            Assert.Equal(2, FunctionRules.RemoveParts(rules, new[] { "MC-25" }));
            Assert.Empty(b.Items);
            Assert.Equal("T-02", a.Items.Single().PartNo);
        }

        [Fact]
        public void RenameFunction_UpdatesNamesOnForms_NotQuantities()
        {
            var items = new List<MachineItem>();
            var functions = new List<MachineFunction>();
            var rule = Rule("舊名", ("T-01", 2));
            FunctionRules.AddFunction(items, functions, rule, Materials);
            var machine = new Machine { Items = items, Functions = functions };

            FunctionRules.RenameFunction(new[] { machine }, rule.Id, "新名");

            Assert.Equal("新名", functions.Single().Name);
            Assert.Equal(("新名", 2m), (items.Single().FunctionText, items.Single().Quantity));
        }

        [Fact]
        public void FillFunctionText_ListsRulesSortedByName()
        {
            var materials = new List<Material> { new() { PartNo = "T-01" }, new() { PartNo = "X" } };

            FunctionRules.FillFunctionText(materials, new[] { Rule("B", ("t-01", 1)), Rule("A", ("T-01", 1)) });

            Assert.Equal("A、B", materials[0].FunctionText);
            Assert.Equal("", materials[1].FunctionText);
        }

        [Fact]
        public void IsAvailable_RuleWithoutTypes_IsForEveryMachine_TypedRuleOnlyForItsTypes()
        {
            var common = Rule("通用");
            var typed = Rule("正逆轉");
            typed.MachineTypes.AddRange(new[] { "A型", "B型" });

            Assert.True(FunctionRules.IsAvailable(common, ""));
            Assert.True(FunctionRules.IsAvailable(common, "C型"));
            Assert.True(FunctionRules.IsAvailable(typed, "a型"));   // 不分大小寫
            Assert.True(FunctionRules.IsAvailable(typed, " B型 "));
            Assert.False(FunctionRules.IsAvailable(typed, "C型"));
            Assert.False(FunctionRules.IsAvailable(typed, ""));     // 還沒選機種只看得到通用的
        }

        [Fact]
        public void Validate_RejectsBadMachineTypeNames()
        {
            var rule = Rule("正逆轉");
            rule.MachineTypes.Add("A、B");
            Assert.NotNull(FunctionRules.Validate(rule, new[] { rule }));

            rule.MachineTypes[0] = " ";
            Assert.NotNull(FunctionRules.Validate(rule, new[] { rule }));

            rule.MachineTypes[0] = "A型";
            Assert.Null(FunctionRules.Validate(rule, new[] { rule }));
        }

        [Fact]
        public void Matches_SearchesMachineTypes()
        {
            var rule = Rule("正逆轉");
            rule.MachineTypes.Add("配電盤A型");

            Assert.True(FunctionRules.Matches(rule, "a型"));
            Assert.True(MachineRules.Matches(new Machine { MachineType = "配電盤A型" }, "A型"));
        }

        [Fact]
        public void CopyName_AddsSuffix_AndSkipsNamesInUse()
        {
            var rules = new List<FunctionRule> { Rule("正逆轉") };

            Assert.Equal("正逆轉（複製）", FunctionRules.CopyName("正逆轉", rules));
            rules.Add(Rule("正逆轉（複製）"));
            Assert.Equal("正逆轉（複製 2）", FunctionRules.CopyName("正逆轉", rules));
            rules.Add(Rule("正逆轉（複製 2）"));
            Assert.Equal("正逆轉（複製 3）", FunctionRules.CopyName("正逆轉", rules));
            Assert.Null(FunctionRules.Validate(Rule(FunctionRules.CopyName("正逆轉", rules)), rules));
        }

        [Fact]
        public void FindSameItems_ComparesPartNosAndQuantities_IgnoringOrderAndCase()
        {
            var a = Rule("A", ("T-01", 2), ("MC-25", 1));
            var sameOtherOrder = Rule("B", ("mc-25", 1), (" T-01 ", 2));
            var otherQuantity = Rule("C", ("T-01", 3), ("MC-25", 1));
            var fewer = Rule("D", ("T-01", 2));
            var more = Rule("E", ("T-01", 2), ("MC-25", 1), ("NFB-100", 1));
            var all = new[] { a, sameOtherOrder, otherQuantity, fewer, more };

            Assert.Equal(new[] { "B" }, FunctionRules.FindSameItems(a, all).Select(r => r.Name));
            Assert.Equal(new[] { "A" }, FunctionRules.FindSameItems(sameOtherOrder, all).Select(r => r.Name));
            Assert.Empty(FunctionRules.FindSameItems(otherQuantity, all));
            Assert.Empty(FunctionRules.FindSameItems(more, all));
        }

        [Fact]
        public void FindSameItems_CopyIsReported_RulesWithoutItemsAreNot()
        {
            var original = Rule("正逆轉", ("T-01", 2));
            original.MachineTypes.Add("A型");
            var copy = Rule(FunctionRules.CopyName(original.Name, new[] { original }), ("T-01", 2));
            copy.MachineTypes.Add("B型"); // 機種不同，料一樣還是算重複
            var emptyA = Rule("空的A");
            var emptyB = Rule("空的B");
            var all = new[] { original, copy, emptyA, emptyB };

            Assert.Equal("正逆轉", Assert.Single(FunctionRules.FindSameItems(copy, all)).Name);
            Assert.Empty(FunctionRules.FindSameItems(emptyA, all));
        }

        [Fact]
        public void ManyRules_AreSupported()
        {
            var rules = Enumerable.Range(1, 2000).Select(i => Rule($"功能{i}", ("T-01", 1))).ToList();

            Assert.All(rules.Take(5), r => Assert.Null(FunctionRules.Validate(r, rules)));
            Assert.Equal(2000, FunctionRules.RulesOfPart(rules, "T-01").Count);
        }

        [Fact]
        public void DataStore_OldFileWithoutRules_LoadsWithEmptyLists()
        {
            var file = Path.Combine(Path.GetTempPath(), "Discharge.Tests." + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file, """
                { "Materials": [ { "PartNo": "T-01", "Name": "端子台" } ],
                  "Machines": [ { "WorkOrder": "W1", "Name": "A", "Items": [ { "PartNo": "T-01", "Quantity": 2 } ] } ] }
                """);
            try
            {
                var data = DataStore.Load(file);

                Assert.Empty(data.Rules);
                Assert.Empty(data.Machines[0].Functions);
                Assert.Empty(data.Machines[0].Items[0].Sources);
                Assert.Equal(2m, data.Machines[0].Items[0].Quantity);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void DataStore_RulesAndSources_RoundTrip()
        {
            var file = Path.Combine(Path.GetTempPath(), "Discharge.Tests." + Guid.NewGuid().ToString("N") + ".json");
            var rule = Rule("正逆轉", ("T-01", 2.5m));
            var machine = new Machine { WorkOrder = "W1", Name = "A" };
            FunctionRules.AddFunction(machine.Items, machine.Functions, rule, Materials);
            try
            {
                DataStore.Save(new AppData { Rules = { rule }, Machines = { machine } }, file);
                var data = DataStore.Load(file);

                Assert.Equal(2.5m, data.Rules.Single().Items.Single().Quantity);
                Assert.Equal("正逆轉", data.Machines.Single().Functions.Single().Name);
                Assert.Equal(rule.Id, data.Machines.Single().Items.Single().Sources.Single().RuleId);
                Assert.DoesNotContain("FunctionText", File.ReadAllText(file));
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}
