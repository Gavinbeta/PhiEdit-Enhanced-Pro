using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using PhiEdit.Core.Preview;
using System.Numerics;
using Windows.Foundation;

namespace PhiEdit.App.Services;

/// <summary>
/// 噪域遮罩渲染。对照 rpe-next ui/noise-preview.mjs 的 coverage 合成语义：
/// 同类区域 Union（合并重叠，消除接缝与颜色加深），普通区域与反色区域 Xor（重叠抵消）。
/// 用 Win2D 的 CanvasGeometry.CombineWith 完成集合运算，再用 AlphaMaskEffect 把噪点材质
/// 按 coverage 裁剪，一次性绘制，避免逐块半透明填充造成的叠加变深。
/// </summary>
public sealed class NoiseOverlayRenderer : IDisposable
{
    private CanvasRenderTarget? _material;
    private CanvasRenderTarget? _mask;
    private int _width;
    private int _height;
    private float _dpi;

    private static readonly byte[] Appearance = { 127, 35, 35 };
    private static readonly byte[] Active = { 182, 60, 60 };

    public void Render(ICanvasResourceCreator creator, float dpi,
        IReadOnlyList<(NoiseSample Sample, Vector2[] Points)> entries,
        float width, float height)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var w = Math.Max(1, (int)Math.Ceiling(width));
        var h = Math.Max(1, (int)Math.Ceiling(height));
        EnsureTargets(creator, dpi, w, h);

        CanvasGeometry? normal = null;
        CanvasGeometry? inverted = null;
        foreach (var (sample, points) in entries)
        {
            if (points.Length < 3)
            {
                continue;
            }

            using var path = CanvasPathBuilderCompat.CreateCompat(creator, points);
            var geometry = CanvasGeometry.CreatePath(path);
            if (sample.IsInvert)
            {
                if (inverted is null)
                {
                    inverted = geometry;
                }
                else
                {
                    var merged = inverted.CombineWith(geometry, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                    inverted.Dispose();
                    geometry.Dispose();
                    inverted = merged;
                }
            }
            else
            {
                if (normal is null)
                {
                    normal = geometry;
                }
                else
                {
                    var merged = normal.CombineWith(geometry, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                    normal.Dispose();
                    geometry.Dispose();
                    normal = merged;
                }
            }
        }

        CanvasGeometry? coverage;
        if (normal is not null && inverted is not null)
        {
            coverage = normal.CombineWith(inverted, Matrix3x2.Identity, CanvasGeometryCombine.Xor);
        }
        else
        {
            coverage = normal ?? inverted;
        }

        if (coverage is null)
        {
            normal?.Dispose();
            inverted?.Dispose();
            return;
        }

        using (var maskSession = _mask!.CreateDrawingSession())
        {
            maskSession.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            maskSession.FillGeometry(coverage, Windows.UI.Color.FromArgb(255, 255, 255, 255));
        }

        var mix = entries.Count > 0 ? entries.Average(entry => entry.Sample.ActivationMix) : 0;
        var color = Appearance.Select((v, i) => (byte)Math.Round(v + (Active[i] - v) * mix)).ToArray();
        // 参考 NOISE_AREA_COLORS：appearance alpha 0.4、active alpha 0.667，
        // 默认静止状态约 50% 透明，让背景与音符可透出。
        var alpha = (byte)Math.Round(128 + (170 - 128) * mix);
        using (var materialSession = _material!.CreateDrawingSession())
        {
            materialSession.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            materialSession.FillRectangle(new Rect(0, 0, width, height),
                Windows.UI.Color.FromArgb(alpha, color[0], color[1], color[2]));
            DrawNoiseSpeckles(materialSession, width, height, color, alpha);
        }

        var owned = new HashSet<CanvasGeometry>();
        if (normal is not null) owned.Add(normal);
        if (inverted is not null) owned.Add(inverted);
        if (coverage is not null) owned.Add(coverage);
        foreach (var geometry in owned)
        {
            geometry.Dispose();
        }
    }

    public void Draw(CanvasDrawingSession drawing, float width, float height, float opacity = 1)
    {
        if (_material is null || _mask is null)
        {
            return;
        }

        using var effect = new AlphaMaskEffect { Source = _material, AlphaMask = _mask };
        drawing.DrawImage(effect, new Rect(0, 0, width, height), new Rect(0, 0, width, height), opacity);
    }

    private void EnsureTargets(ICanvasResourceCreator creator, float dpi, int width, int height)
    {
        if (_material is not null && _width == width && _height == height && Math.Abs(_dpi - dpi) < 0.01f)
        {
            return;
        }

        DisposeTargets();
        _width = width;
        _height = height;
        _dpi = dpi;
        _material = new CanvasRenderTarget(creator, width, height, dpi);
        _mask = new CanvasRenderTarget(creator, width, height, dpi);
    }

    private static void DrawNoiseSpeckles(CanvasDrawingSession drawing, float width, float height, byte[] color, byte baseAlpha)
    {
        var random = new Random(20261127);
        var count = (int)(width * height / 240);
        var bright = Windows.UI.Color.FromArgb((byte)Math.Min(255, baseAlpha + 40),
            (byte)Math.Min(255, color[0] + 95),
            (byte)Math.Min(255, color[1] + 75),
            (byte)Math.Min(255, color[2] + 75));
        var dark = Windows.UI.Color.FromArgb((byte)Math.Max(0, baseAlpha - 20),
            (byte)Math.Max(0, color[0] - 45),
            (byte)Math.Max(0, color[1] - 25),
            (byte)Math.Max(0, color[2] - 25));
        for (var index = 0; index < count; index++)
        {
            var x = (float)random.NextDouble() * width;
            var y = (float)random.NextDouble() * height;
            var size = 0.8f + (float)random.NextDouble() * 1.8f;
            drawing.FillRectangle(new Rect(x, y, size, size), random.Next(2) == 0 ? bright : dark);
        }
    }

    private void DisposeTargets()
    {
        _material?.Dispose();
        _mask?.Dispose();
        _material = _mask = null;
    }

    public void Dispose() => DisposeTargets();
}

internal static class CanvasPathBuilderCompat
{
    public static CanvasPathBuilder CreateCompat(ICanvasResourceCreator creator, Vector2[] points)
    {
        var builder = new CanvasPathBuilder(creator);
        builder.BeginFigure(points[0]);
        for (var index = 1; index < points.Length; index++)
        {
            builder.AddLine(points[index]);
        }
        builder.EndFigure(CanvasFigureLoop.Closed);
        return builder;
    }
}
