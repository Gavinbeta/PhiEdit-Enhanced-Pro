using System.IO.Compression;
using System.Text;
using PhiEdit.Core.IO;
using Xunit;

namespace PhiEdit.Core.Tests;

public class ChartAssetStoreTests
{
    [Fact]
    public void Resolve_And_FindBackground_FollowReferencePriorities()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "Resources/chart/info.txt", "Picture: bg.png\nSong: song.ogg");
            Write(zip, "Resources/chart/chart.json", "{}");
            Write(zip, "Resources/chart/bg.png", new byte[] { 1, 2, 3 });
        }

        var store = ChartAssetStore.FromZip(stream.ToArray());
        Assert.NotNull(store.Resolve("bg.png"));
        Assert.Equal("Resources/chart/bg.png", store.FindBackground(string.Empty));
    }

    [Fact]
    public void FindChart_UsesInfoTxtChartField()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "info.txt", "Chart: chart.json\nPicture: bg.png");
            Write(zip, "chart.json", "{}");
            Write(zip, "other.json", "{}");
            Write(zip, "bg.png", new byte[] { 1 });
        }

        var store = ChartAssetStore.FromZip(stream.ToArray());
        Assert.Equal("chart.json", store.FindChart());
    }

    private static void Write(ZipArchive zip, string name, string text) =>
        Write(zip, name, Encoding.UTF8.GetBytes(text));

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        using var target = entry.Open();
        target.Write(bytes);
    }
}
