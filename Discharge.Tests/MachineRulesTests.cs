using System.IO;
using Discharge.Models;
using Discharge.Services;

namespace Discharge.Tests
{
    public class MachineRulesTests
    {
        [Fact]
        public void Validate_RequiresWorkOrderNameAndPositiveQuantity()
        {
            Assert.Equal("請輸入工令。", MachineRules.Validate(new Machine { Name = "配電盤" }));
            Assert.Equal("請輸入機台名稱。", MachineRules.Validate(new Machine { WorkOrder = "W001" }));

            var m = new Machine { WorkOrder = "W001", Name = "配電盤", Items = { new MachineItem { PartNo = "A001", Quantity = 0 } } };
            Assert.Equal("料號 A001 的數量要大於 0。", MachineRules.Validate(m));

            m.Items[0].Quantity = 2;
            Assert.Null(MachineRules.Validate(m));
        }

        [Fact]
        public void AddItems_CopiesMaterial_AndAddsQuantityToExistingPartNo()
        {
            var nfb = new Material { PartNo = "NFB-100", Name = "無熔絲開關", Spec = "3P 100A" };
            var terminal = new Material { PartNo = "T-01", Name = "端子" };
            var items = new List<MachineItem> { new() { PartNo = "nfb-100", Name = "無熔絲開關", Quantity = 1 } };

            var (added, merged) = MachineRules.AddItems(items, [(nfb, 2m), (terminal, 10m)]);

            Assert.Equal((1, 1), (added, merged));
            Assert.Equal(3m, items[0].Quantity);
            Assert.Equal(("T-01", "端子", 10m, terminal.Id), (items[1].PartNo, items[1].Name, items[1].Quantity, items[1].MaterialId));
        }

        [Fact]
        public void FindDuplicate_SameWorkOrderAndName()
        {
            var a = new Machine { WorkOrder = "W001", Name = "配電盤 A" };
            var all = new[] { a, new Machine { WorkOrder = "W001", Name = "配電盤 B" } };

            Assert.Same(a, MachineRules.FindDuplicate(new Machine { WorkOrder = "w001", Name = "配電盤 a" }, all));
            Assert.Null(MachineRules.FindDuplicate(new Machine { WorkOrder = "W001", Name = "配電盤 C" }, all));
            Assert.Null(MachineRules.FindDuplicate(a, all));
        }

        [Fact]
        public void Matches_WorkOrderNameOrItems()
        {
            var m = new Machine { WorkOrder = "W001", Name = "配電盤", Items = { new MachineItem { PartNo = "NFB-100", Name = "無熔絲開關" } } };

            Assert.True(MachineRules.Matches(m, "w001"));
            Assert.True(MachineRules.Matches(m, "配電 nfb"));
            Assert.False(MachineRules.Matches(m, "W002"));
        }

        [Fact]
        public void DataStore_SaveAndLoad_RoundTrip()
        {
            var dir = Path.Combine(Path.GetTempPath(), "Discharge.Tests." + Guid.NewGuid().ToString("N"));
            var file = Path.Combine(dir, "data.json");
            try
            {
                Assert.Empty(DataStore.Load(file).Materials); // 沒有檔案時是空的資料

                var data = new AppData();
                data.Materials.Add(new Material { PartNo = "A001", Name = "斷路器", Spec = "3P" });
                data.Machines.Add(new Machine { WorkOrder = "W001", Name = "配電盤", Items = { new MachineItem { PartNo = "A001", Quantity = 2.5m } } });
                DataStore.Save(data, file);

                Assert.Contains("斷路器", File.ReadAllText(file)); // 中文直接存成文字
                Assert.False(File.Exists(file + ".tmp"));

                var loaded = DataStore.Load(file);
                Assert.Equal("斷路器", loaded.Materials.Single().Name);
                Assert.Equal(2.5m, loaded.Machines.Single().Items.Single().Quantity);
            }
            finally
            {
                UpdateService.TryDeleteDirectory(dir);
            }
        }

        [Fact]
        public void DataStore_Load_BrokenFile_Throws()
        {
            var file = Path.Combine(Path.GetTempPath(), "Discharge.Tests." + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file, "{ 壞掉");
            try
            {
                Assert.Throws<InvalidDataException>(() => DataStore.Load(file));
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}
