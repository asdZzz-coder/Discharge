using System.IO;
using Discharge.Services;

namespace Discharge.Tests
{
    public class UpdateServiceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Discharge.Tests." + Guid.NewGuid().ToString("N"));

        public UpdateServiceTests() => Directory.CreateDirectory(_root);

        public void Dispose() => UpdateService.TryDeleteDirectory(_root);

        [Fact]
        public void LauncherMarker_InVersionFolder_IsFoundFromSubfolder()
        {
            // 啟動器的結構：apps\{id}\{版本}\.launcher.json，exe 可能在版本資料夾底下的子資料夾
            var version = Directory.CreateDirectory(Path.Combine(_root, "apps", "discharge", "v1.0.0"));
            File.WriteAllText(Path.Combine(version.FullName, ".launcher.json"), "{}");
            var exeDir = Directory.CreateDirectory(Path.Combine(version.FullName, "Application Files", "Discharge_1_0_0_0"));

            Assert.True(UpdateService.HasLauncherMarker(exeDir.FullName));
        }

        [Fact]
        public void LauncherMarker_Missing_IsNotLauncherManaged()
        {
            var exeDir = Directory.CreateDirectory(Path.Combine(_root, "bin", "Debug"));

            Assert.False(UpdateService.HasLauncherMarker(exeDir.FullName));
        }
    }
}
