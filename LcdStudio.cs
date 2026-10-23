using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThgOptimization;

/// <summary>Resolução e formato de uma tela.</summary>
public sealed record LcdTarget(string Key, string Label, int Width, int Height, bool Round);

/// <summary>Valores dos sensores desenhados por cima da imagem.</summary>
public sealed record LcdSensors(float? CpuTemp, float? GpuTemp, float? CpuLoad, float? GpuLoad);

public sealed class LcdOverlayOptions
{
    public bool CpuTemp { get; set; } = true;
    public bool GpuTemp { get; set; } = true;
    public bool CpuLoad { get; set; }
    public bool GpuLoad { get; set; }
    public bool Clock { get; set; }
    public bool Fill { get; set; } = true;
}

/// <summary>
/// Monta o quadro de uma tela LCD: imagem ou GIF redimensionado e cortado para a resolução exata (preencher ou caber),
/// máscara redonda nas telas redondas e os sensores por cima. Gera também os formatos que as telas pedem (JPEG e RGB565),
/// para o envio pelo protocolo de cada fabricante.
/// </summary>
public sealed class LcdStudio
{
    public static readonly LcdTarget[] Targets =
    {
        new("round480", "Redonda 480×480 (TURZX 2,1\"/2,8\", Lian Li HydroShift Circle)", 480, 480, true),
        new("square480", "Quadrada 480×480 (Galahad II LCD, HydroShift)", 480, 480, false),
        new("lianli400", "400×400 (Lian Li UNI FAN TL / SL LCD)", 400, 400, false),
        new("turing35", "320×480 (Turing 3,5\")", 320, 480, false),
        new("turing5", "800×480 (Turing 5\")", 800, 480, false),
        new("bar88", "480×1920 (8,8\" vertical)", 480, 1920, false),
    };

    private readonly List<(BitmapSource Frame, TimeSpan Delay)> _frames = new();
    private int _index;
    private DateTime _shownAt;

    public bool HasMedia => _frames.Count > 0;
    public bool Animated => _frames.Count > 1;

    /// <summary>Carrega imagem (PNG, JPEG, BMP) ou GIF animado. Arquivos acima de 64 MB são recusados.</summary>
    public void Load(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("Arquivo não encontrado.", path);
        if (fi.Length > 64L << 20) throw new InvalidDataException("Arquivo grande demais (máximo 64 MB).");
        _frames.Clear(); _index = 0;
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (dec is GifBitmapDecoder && dec.Frames.Count > 1)
        {
            // GIF: cada quadro pode ser parcial; compõe sobre o anterior para virar quadros inteiros.
            int w = dec.Frames[0].PixelWidth, h = dec.Frames[0].PixelHeight;
            if ((long)w * h * dec.Frames.Count > 120_000_000) throw new InvalidDataException("GIF grande demais (quadros demais para a memória).");
            BitmapSource? previous = null;
            foreach (var f in dec.Frames.Take(600))
            {
                var meta = f.Metadata as BitmapMetadata;
                int left = Meta(meta, "/imgdesc/Left"), top = Meta(meta, "/imgdesc/Top");
                int delay = Math.Max(20, Meta(meta, "/grctlext/Delay") * 10);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    if (previous != null) dc.DrawImage(previous, new Rect(0, 0, w, h));
                    dc.DrawImage(f, new Rect(left, top, f.PixelWidth, f.PixelHeight));
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv); rtb.Freeze();
                previous = rtb;
                _frames.Add((rtb, TimeSpan.FromMilliseconds(delay)));
            }
        }
        else
        {
            var f = dec.Frames[0];
            f.Freeze();
            _frames.Add((f, TimeSpan.Zero));
        }
        _shownAt = DateTime.Now;
    }

    public void Clear() { _frames.Clear(); _index = 0; }

    private static int Meta(BitmapMetadata? m, string q)
    {
        try { return m?.GetQuery(q) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0; } catch { return 0; }
    }

    /// <summary>Quadro atual (avança o GIF conforme o tempo de cada quadro).</summary>
    private BitmapSource? Current()
    {
        if (_frames.Count == 0) return null;
        if (_frames.Count > 1 && DateTime.Now - _shownAt >= _frames[_index].Delay)
        {
            _index = (_index + 1) % _frames.Count;
            _shownAt = DateTime.Now;
        }
        return _frames[_index].Frame;
    }

    /// <summary>Desenha o quadro final na resolução da tela.</summary>
    public BitmapSource Render(LcdTarget t, LcdOverlayOptions o, LcdSensors s)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var area = new Rect(0, 0, t.Width, t.Height);
            if (t.Round) dc.PushClip(new EllipseGeometry(area));
            dc.DrawRectangle(Brushes.Black, null, area);
            if (Current() is { } img)
            {
                double sx = t.Width / (double)img.PixelWidth, sy = t.Height / (double)img.PixelHeight;
                double k = o.Fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
                double w = img.PixelWidth * k, h = img.PixelHeight * k;
                dc.DrawImage(img, new Rect((t.Width - w) / 2, (t.Height - h) / 2, w, h));
            }
            DrawSensors(dc, t, o, s);
            if (t.Round) dc.Pop();
        }
        var rtb = new RenderTargetBitmap(t.Width, t.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawSensors(DrawingContext dc, LcdTarget t, LcdOverlayOptions o, LcdSensors s)
    {
        var lines = new List<(string Label, string Value)>();
        if (o.CpuTemp) lines.Add(("CPU", s.CpuTemp is float a ? $"{a:0}°" : "--"));
        if (o.GpuTemp) lines.Add(("GPU", s.GpuTemp is float b ? $"{b:0}°" : "--"));
        if (o.CpuLoad) lines.Add(("CPU", s.CpuLoad is float c ? $"{c:0}%" : "--"));
        if (o.GpuLoad) lines.Add(("GPU", s.GpuLoad is float d ? $"{d:0}%" : "--"));
        if (o.Clock) lines.Add(("", DateTime.Now.ToString("HH:mm")));
        if (lines.Count == 0) return;

        double size = Math.Min(t.Width, t.Height) / (lines.Count > 3 ? 7.5 : 6.0);
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var labelFace = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double lineH = size * 1.15, total = lineH * lines.Count;
        double y = (t.Height - total) / 2;
        var shadow = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
        foreach (var (label, value) in lines)
        {
            var v = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.White, 1.0);
            var l = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, labelFace, size * 0.42, Brushes.White, 1.0);
            double totalW = v.Width + (label.Length > 0 ? l.Width + size * 0.2 : 0);
            double x = (t.Width - totalW) / 2;
            dc.DrawRoundedRectangle(shadow, null, new Rect(x - size * 0.25, y, totalW + size * 0.5, lineH * 0.95), size * 0.2, size * 0.2);
            if (label.Length > 0) { dc.DrawText(l, new Point(x, y + (v.Height - l.Height) * 0.75)); x += l.Width + size * 0.2; }
            dc.DrawText(v, new Point(x, y));
            y += lineH;
        }
    }

    public static byte[] ToJpeg(BitmapSource frame, int quality = 90)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Gira o quadro em 0/90/180/270 graus (feito na imagem, vale para qualquer tela).</summary>
    public static BitmapSource Rotate(BitmapSource frame, int rotation)
    {
        rotation = ((rotation % 360) + 360) % 360;
        if (rotation == 0) return frame;
        var tb = new TransformedBitmap(frame, new RotateTransform(rotation));
        tb.Freeze();
        return tb;
    }

    /// <summary>BGRA 4 bytes por pixel (telas TURZX revisão C), girada em 0/90/180/270 graus.</summary>
    public static byte[] ToBgra(BitmapSource frame, int rotation)
    {
        BitmapSource src = frame;
        if (rotation % 360 != 0)
        {
            var tb = new TransformedBitmap(frame, new RotateTransform(rotation));
            tb.Freeze();
            src = tb;
        }
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[checked(stride * h)];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255; // opaco
        return px;
    }

    /// <summary>RGB565 little-endian (formato das telas Turing 3,5"/5"): 2 bytes por pixel, tamanho exato largura×altura×2.</summary>
    public static byte[] ToRgb565(BitmapSource frame)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[checked(stride * h)];
        bgra.CopyPixels(px, stride, 0);
        var outBuf = new byte[checked(w * h * 2)];
        for (int i = 0, j = 0; i < px.Length; i += 4, j += 2)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            int v = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
            outBuf[j] = (byte)v; outBuf[j + 1] = (byte)(v >> 8);
        }
        return outBuf;
    }
}
