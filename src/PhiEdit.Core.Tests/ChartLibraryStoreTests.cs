using PhiEdit.Core.Charts;
using Xunit;

namespace PhiEdit.Core.Tests;

public sealed class ChartLibraryStoreTests
{
    [Fact]
    public void Load_EmptyLibrary_HasNoItems()
    {
        var root = Path.Combine(Path.GetTempPath(), "phiedit-lib-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ChartLibraryStore(root);
            store.Load();
            Assert.Empty(store.Items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureSeeded_CopiesBuiltinChartsOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "phiedit-lib-test-" + Guid.NewGuid().ToString("N"));
        var builtin = Path.Combine(Path.GetTempPath(), "phiedit-builtin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(builtin);
            File.WriteAllText(Path.Combine(builtin, "demo.json"), """
                { "formatVersion": 3, "META": { "name": "示例曲" }, "judgeLineList": [] }
                """);

            var store = new ChartLibraryStore(root);
            store.Load();
            Assert.Equal(1, store.EnsureSeeded(builtin));
            Assert.Single(store.Items);
            var item = store.Items[0];
            Assert.Equal(ChartLibrarySource.Builtin, item.Source);
            Assert.True(File.Exists(store.GetPath(item)));

            // 再次调用不应重复登记。
            Assert.Equal(0, store.EnsureSeeded(builtin));
            Assert.Single(store.Items);

            // 重新加载后条目仍在（持久化生效）。
            var reloaded = new ChartLibraryStore(root);
            reloaded.Load();
            Assert.Single(reloaded.Items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(builtin, recursive: true);
        }
    }

    [Fact]
    public void Import_RegistersChartAndExportCopiesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "phiedit-lib-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(Path.GetTempPath(), "phiedit-src-" + Guid.NewGuid().ToString("N") + ".json");
        var target = Path.Combine(Path.GetTempPath(), "phiedit-out-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(source, """
                { "formatVersion": 3, "META": { "name": "导入曲" }, "judgeLineList": [] }
                """);

            var store = new ChartLibraryStore(root);
            store.Load();
            var item = store.Import(source);
            Assert.Equal(ChartLibrarySource.Imported, item.Source);
            Assert.Single(store.Items);

            store.Export(item, target);
            Assert.True(File.Exists(target));
            Assert.Contains("导入曲", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(source);
            File.Delete(target);
        }
    }
}
