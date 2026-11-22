using PhiEdit.Core.Preview;
using Xunit;

namespace PhiEdit.Core.Tests;

public class PreviewRenderOptionsTests
{
    [Fact]
    public void BackgroundBrightness_ClampsToRange()
    {
        var options = new PreviewRenderOptions();
        options.BackgroundBrightness = 2;
        Assert.Equal(1, options.BackgroundBrightness);
        options.BackgroundBrightness = -1;
        Assert.Equal(0, options.BackgroundBrightness);
    }

    [Fact]
    public void ChangingOption_RaisesPropertyChanged()
    {
        var options = new PreviewRenderOptions();
        var raised = false;
        options.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PreviewRenderOptions.LineColorMode))
            {
                raised = true;
            }
        };

        options.LineColorMode = PreviewLineColorMode.SkinPerfect;
        Assert.True(raised);
        Assert.Equal(PreviewLineColorMode.SkinPerfect, options.LineColorMode);
    }
}
