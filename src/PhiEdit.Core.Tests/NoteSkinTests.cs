using PhiEdit.Core.Skins;
using Xunit;

namespace PhiEdit.Core.Tests;

public class NoteSkinTests
{
    private static string BuiltinSkinPath =>
        Path.Combine(AppContext.BaseDirectory, "NoteSkins", "builtin-gaofan.zip");

    [Fact]
    public void MiniYaml_ReadsManifestKeys()
    {
        var text = "name: \"高仿官皮新版\"\nauthor: fkh(B站苏八拉稀)\nhitFx: [7, 6]\nholdAtlas: [50, 53]\nhitFxScale: 0.95\n";
        var values = MiniYaml.Parse(text);

        Assert.Equal("高仿官皮新版", values["name"]);
        Assert.Equal("fkh(B站苏八拉稀)", values["author"]);
        Assert.Equal(new[] { 7, 6 }, MiniYaml.ParseIntArray(values["hitFx"]));
        Assert.Equal(new[] { 50, 53 }, MiniYaml.ParseIntArray(values["holdAtlas"]));
        Assert.Equal(0.95, double.Parse(values["hitFxScale"], System.Globalization.CultureInfo.InvariantCulture), 6);
    }

    [Fact]
    public void BuiltinSkin_ParsesWithExpectedLayout()
    {
        Assert.True(File.Exists(BuiltinSkinPath), "内置资源包未随测试复制");

        var skin = NoteSkin.FromZip(File.ReadAllBytes(BuiltinSkinPath), "builtin-gaofan", builtin: true);

        Assert.Equal("高仿官皮新版", skin.Info.Name);
        Assert.Equal(new[] { 7, 6 }, skin.Info.HitFx);
        Assert.Equal(new[] { 50, 53 }, skin.Info.HoldAtlas);
        Assert.Equal(new[] { 50, 153 }, skin.Info.HoldAtlasMH);
        Assert.Equal(9, skin.Images.Count);
        Assert.Equal(1000, skin.Images["click.png"].Width);
        Assert.Equal(203, skin.Images["hold.png"].Height);
    }

    [Fact]
    public void Parse_RejectsPackageWithoutManifest()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["click.png"] = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
        };

        Assert.Throws<FormatException>(() => NoteSkin.Parse(files, "x"));
    }

    [Fact]
    public void ComputeId_IsStableForSameContent()
    {
        var files = new Dictionary<string, byte[]> { ["a.png"] = new byte[] { 1, 2, 3 } };
        Assert.Equal(NoteSkin.ComputeId(files), NoteSkin.ComputeId(files));
    }
}
