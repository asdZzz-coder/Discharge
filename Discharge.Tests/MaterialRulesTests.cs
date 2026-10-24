using Discharge.Models;
using Discharge.Services;

namespace Discharge.Tests
{
    public class MaterialRulesTests
    {
        [Fact]
        public void Validate_RequiresPartNoAndName()
        {
            Assert.Equal("請輸入料號。", MaterialRules.Validate(new Material { Name = "斷路器" }, []));
            Assert.Equal("請輸入名稱。", MaterialRules.Validate(new Material { PartNo = "A001" }, []));
            Assert.Null(MaterialRules.Validate(new Material { PartNo = "A001", Name = "斷路器" }, []));
        }

        [Fact]
        public void Validate_RejectsDuplicatePartNo_IgnoringCaseButNotItself()
        {
            var existing = new Material { PartNo = "nfb-100", Name = "無熔絲開關" };
            var all = new List<Material> { existing };

            Assert.NotNull(MaterialRules.Validate(new Material { PartNo = "NFB-100", Name = "另一個" }, all));
            Assert.Null(MaterialRules.Validate(new Material { Id = existing.Id, PartNo = "NFB-100", Name = "改名" }, all));
        }

        [Fact]
        public void Matches_AllKeywordsAcrossFields()
        {
            var m = new Material { PartNo = "NFB-100", Name = "無熔絲開關", Spec = "3P 100A", Note = "常用" };

            Assert.True(MaterialRules.Matches(m, ""));
            Assert.True(MaterialRules.Matches(m, "nfb 100a"));
            Assert.True(MaterialRules.Matches(m, "無熔絲　常用")); // 全形空格
            Assert.False(MaterialRules.Matches(m, "nfb 200a"));
        }

        [Fact]
        public void ParseTable_WithHeader_FindsColumnsByName()
        {
            var text = "品名\t規格\t料號\r\n斷路器\t3P 100A\tA001\r\n\t\t\r\n端子\t\tA002\r\n";

            var items = MaterialRules.ParseTable(text);

            Assert.Equal(2, items.Count);
            Assert.Equal(("A001", "斷路器", "3P 100A", ""), (items[0].PartNo, items[0].Name, items[0].Spec, items[0].Note));
            Assert.Equal(("A002", "端子"), (items[1].PartNo, items[1].Name));
        }

        [Fact]
        public void ParseTable_WithoutHeader_UsesColumnOrder()
        {
            var items = MaterialRules.ParseTable("A001\t斷路器\t3P\t備註一\nA002\t端子");

            Assert.Equal(2, items.Count);
            Assert.Equal(("A001", "斷路器", "3P", "備註一"), (items[0].PartNo, items[0].Name, items[0].Spec, items[0].Note));
            Assert.Equal("", items[1].Spec);
        }

        [Fact]
        public void Merge_UpdatesSamePartNo_KeepsOldValuesWhenNewIsBlank()
        {
            var target = new List<Material> { new() { PartNo = "A001", Name = "舊名稱", Spec = "舊規格", Note = "舊備註" } };
            var incoming = new[]
            {
                new Material { PartNo = "a001", Name = "新名稱" },
                new Material { PartNo = "A002", Name = "端子" },
                new Material { PartNo = "A003" },
            };

            var (added, updated, skipped) = MaterialRules.Merge(target, incoming);

            Assert.Equal((1, 1, 1), (added, updated, skipped));
            Assert.Equal(2, target.Count);
            Assert.Equal(("新名稱", "舊規格", "舊備註"), (target[0].Name, target[0].Spec, target[0].Note));
        }
    }
}
