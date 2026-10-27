using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;

namespace Markify.LogoGen;

/// <summary>
/// 生成 Markify 的占位图标（SVG / PNG / ICO）。
///
/// 为什么用程序生成而不是手搓图片：
///   1. 一份几何定义同时输出 SVG（矢量、可读、好改）和 ICO（多尺寸位图），不会出现两份源文件互相不同步；
///   2. 换真实 Logo 时只要替换本目录的几何定义或直接换掉输出文件，不必重新导出各尺寸；
///   3. 尺寸和留白可复现，不依赖手工导出时的心情。
///
/// 设计说明（占位用，非最终品牌）：
///   深靛蓝圆角方块 + 白色"M"字形折线，右上角一枚琥珀色圆点作强调色。
///   M 用粗折线而非字体绘制，保证在 16×16 下仍能辨认。
/// </summary>
internal static class Program
{
    // 品牌色（与 App.axaml 的 chrome 配色同族，但不依赖它们，独立可辨识）
    private static readonly SKColor BackgroundTop = new(0x2B, 0x3A, 0x67);    // 深靛蓝
    private static readonly SKColor BackgroundBottom = new(0x1B, 0x24, 0x40); // 更深，形成轻微渐变
    private static readonly SKColor Foreground = SKColors.White;
    private static readonly SKColor Accent = new(0xFF, 0xB3, 0x3D);           // 琥珀色圆点

    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    private static int Main(string[] args)
    {
        var outputDir = args.Length > 0
            ? args[0]
            : Path.Combine("src", "Markify", "Assets");

        Directory.CreateDirectory(outputDir);

        var svgPath = Path.Combine(outputDir, "markify-logo.svg");
        var icoPath = Path.Combine(outputDir, "markify-logo.ico");
        var pngPath = Path.Combine(outputDir, "markify-logo.png");

        File.WriteAllText(svgPath, BuildSvg(), new UTF8Encoding(false));
        Console.WriteLine($"svg  {svgPath}  ({new FileInfo(svgPath).Length} bytes)");

        // 1024 的主图，用于文档、README 和安装包素材
        using (var large = Render(1024))
        {
            using var data = large.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(pngPath, data.ToArray());
            Console.WriteLine($"png  {pngPath}  ({new FileInfo(pngPath).Length} bytes)");
        }

        WriteIco(icoPath, IconSizes);
        Console.WriteLine($"ico  {icoPath}  ({new FileInfo(icoPath).Length} bytes)");

        return 0;
    }

    /// <summary>按给定边长绘制图标。所有坐标按边长比例换算，因此任意尺寸都保持同一构图。</summary>
    private static SKBitmap Render(int size)
    {
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);

        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var s = size / 256f;   // 以 256 为设计栅格

        // 圆角方块底。半径取 22%，接近 iOS/macOS 的 squircle 观感，
        // 但纯圆角在小尺寸下更稳，不会因抗锯齿糊成一团。
        var inset = 6f * s;
        var rect = new SKRect(inset, inset, size - inset, size - inset);
        var radius = 56f * s;

        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(0, rect.Top),
                   new SKPoint(0, rect.Bottom),
                   [BackgroundTop, BackgroundBottom],
                   [0f, 1f],
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
        {
            canvas.DrawRoundRect(rect, radius, radius, paint);
        }

        // "M" 字形折线。用折线而不是文字：避免依赖字体，且 16px 下笔画不会糊。
        // 坐标以 256 栅格给出，再按比例缩放。
        // 几何要点：
        //   - 左右竖笔同起同止（top=86 / bottom=170），视觉上才是一个字；
        //   - 中段尖角下探到 140，与基线留出 30 的差，M 才有纵向比例；
        //   - 笔画宽度 30，配合 round join，小尺寸下不会糊成一团。
        var strokeWidth = 30f * s;
        var points = new[]
        {
            new SKPoint(76f * s, 170f * s),    // 左下
            new SKPoint(76f * s, 86f * s),     // 左上
            new SKPoint(128f * s, 140f * s),   // 中下（V 底）
            new SKPoint(180f * s, 86f * s),    // 右上
            new SKPoint(180f * s, 170f * s),   // 右下
        };

        using (var path = new SKPath())
        {
            path.MoveTo(points[0]);
            for (var i = 1; i < points.Length; i++)
                path.LineTo(points[i]);

            using var paint = new SKPaint
            {
                Color = Foreground,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = strokeWidth,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            canvas.DrawPath(path, paint);
        }

        // 强调色圆点：呼应"标记/标注"的语义，也给纯色图标一个记忆点。
        // 放在右上角外侧，与 M 的右竖笔顶部形成呼应，避免压住笔画。
        // 小于等于 32px 时略放大并加底色光晕，否则会糊进白笔画里。
        var dotRadius = Math.Max(2.4f, 19f * s);
        var dotCenter = new SKPoint(190f * s, 66f * s);

        using (var dotPaint = new SKPaint { Color = Accent, IsAntialias = true })
        {
            // 小尺寸先描一圈底色，让圆点从白色笔画中分离出来
            if (size <= 32)
            {
                using var halo = new SKPaint { Color = BackgroundBottom, IsAntialias = true };
                canvas.DrawCircle(dotCenter, dotRadius + Math.Max(1f, 2.5f * s), halo);
            }

            canvas.DrawCircle(dotCenter, dotRadius, dotPaint);
        }

        canvas.Flush();
        return bitmap;
    }

    /// <summary>
    /// 写多尺寸 ICO。ICO 允许内嵌 PNG（Vista+ 支持），
    /// 这里 256 用 PNG、小尺寸用 BMP(DIB)，兼容性最好。
    /// </summary>
    private static void WriteIco(string path, IReadOnlyList<int> sizes)
    {
        var images = new List<(int Size, byte[] Data, bool IsPng)>();

        foreach (var size in sizes)
        {
            using var bitmap = Render(size);
            var isPng = size >= 256;

            byte[] payload;
            if (isPng)
            {
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                payload = data.ToArray();
            }
            else
            {
                payload = ToDib(bitmap);
            }

            images.Add((size, payload, isPng));
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)images.Count);

        var offset = 6 + images.Count * 16;
        foreach (var (size, data, _) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));   // 0 表示 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // palette
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // planes
            writer.Write((ushort)32);         // bpp
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data, _) in images)
            writer.Write(data);
    }

    /// <summary>把位图转成 ICO 内嵌的 DIB（32bpp BGRA + AND 掩码），底部行优先。</summary>
    private static byte[] ToDib(SKBitmap bitmap)
    {
        var size = bitmap.Width;
        var pixels = bitmap.Pixels;   // 从上到下

        // BITMAPINFOHEADER(40) + 像素 + AND 掩码（每行按 4 字节对齐）
        var maskStride = ((size + 31) / 32) * 4;
        var headerSize = 40;
        var pixelBytes = size * size * 4;
        var maskBytes = maskStride * size;

        using var ms = new MemoryStream(headerSize + pixelBytes + maskBytes);
        using var w = new BinaryWriter(ms);

        w.Write(40);                 // biSize
        w.Write(size);               // biWidth
        w.Write(size * 2);           // biHeight = XOR + AND 高度之和
        w.Write((ushort)1);          // biPlanes
        w.Write((ushort)32);         // biBitCount
        w.Write(0);                  // BI_RGB
        w.Write(pixelBytes + maskBytes);
        w.Write(0); w.Write(0);      // 分辨率
        w.Write(0); w.Write(0);      // 调色板

        // DIB 行序为自下而上
        for (var y = size - 1; y >= 0; y--)
        {
            for (var x = 0; x < size; x++)
            {
                var c = pixels[y * size + x];
                w.Write(c.Blue);
                w.Write(c.Green);
                w.Write(c.Red);
                w.Write(c.Alpha);
            }
        }

        // AND 掩码：全 0 = 使用 alpha 通道（现代 Windows 会忽略它，但格式要求必须存在）
        w.Write(new byte[maskBytes]);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 输出等价几何的 SVG。手写而不是自动转换，是为了让 SVG 保持简短可读、便于后来替换设计。
    /// 数值与 <see cref="Render"/> 保持同一套 256 栅格坐标。
    /// </summary>
    private static string BuildSvg()
    {
        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<!-- Markify placeholder logo. Replace with the final brand asset when available. -->""");
        sb.AppendLine("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256" role="img" aria-label="Markify">""");
        sb.AppendLine("""  <defs>""");
        sb.AppendLine($"""    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">""");
        sb.AppendLine($"""      <stop offset="0" stop-color="#{BackgroundTop.Red:X2}{BackgroundTop.Green:X2}{BackgroundTop.Blue:X2}"/>""");
        sb.AppendLine($"""      <stop offset="1" stop-color="#{BackgroundBottom.Red:X2}{BackgroundBottom.Green:X2}{BackgroundBottom.Blue:X2}"/>""");
        sb.AppendLine("""    </linearGradient>""");
        sb.AppendLine("""  </defs>""");
        sb.AppendLine($"""  <rect x="{F(6)}" y="{F(6)}" width="{F(244)}" height="{F(244)}" rx="{F(56)}" fill="url(#bg)"/>""");
        sb.AppendLine($"""  <path d="M {F(76)} {F(170)} L {F(76)} {F(86)} L {F(128)} {F(140)} L {F(180)} {F(86)} L {F(180)} {F(170)}" """);
        sb.AppendLine($"""        fill="none" stroke="#{Foreground.Red:X2}{Foreground.Green:X2}{Foreground.Blue:X2}" stroke-width="{F(30)}" stroke-linecap="round" stroke-linejoin="round"/>""");
        sb.AppendLine($"""  <circle cx="{F(190)}" cy="{F(66)}" r="{F(19)}" fill="#{Accent.Red:X2}{Accent.Green:X2}{Accent.Blue:X2}"/>""");
        sb.AppendLine("</svg>");

        return sb.ToString();
    }
}
