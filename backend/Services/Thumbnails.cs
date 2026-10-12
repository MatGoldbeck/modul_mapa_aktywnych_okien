using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using MapaOkien.Backend.Native;
using SkiaSharp;

namespace MapaOkien.Backend.Services;

/// <summary>
/// Miniatury nodów: PrintWindow (wybrane w teście technicznym – szybsze niż WGC i bez żółtej
/// ramki na Windows 10), skalowanie do 320 px szerokości, zapis jako WebP w folderze thumbs\.
/// </summary>
public sealed class Thumbnails
{
    public const int Width = 320;
    private const int WebpQuality = 75;

    private readonly string _dir;

    public Thumbnails(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(_dir, hash + ".webp");
    }

    public void Delete(string key)
    {
        try { File.Delete(PathFor(key)); }
        catch (IOException) { /* plik w użyciu przez frontend – zostanie nadpisany przy kolejnym zapisie */ }
    }

    /// <summary>
    /// Bitmapa konkretnego okna. Okno zminimalizowane nie daje obrazu – wtedy false, a nod
    /// zachowuje ostatnią zapisaną miniaturę.
    /// </summary>
    public bool CaptureWindow(IntPtr hwnd, string key)
    {
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd)) return false;
        if (!Win32.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0) return false;

        try
        {
            using var full = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                bool ok;
                try { ok = Win32.PrintWindow(hwnd, hdc, Win32.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
                if (!ok) return false;
            }

            int h = Math.Max(1, (int)Math.Round(full.Height * (Width / (double)full.Width)));
            using var thumb = new Bitmap(Width, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, Width, h);
            }
            if (IsBlank(thumb)) return false;

            var data = thumb.LockBits(new Rectangle(0, 0, thumb.Width, thumb.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                // Opaque: część aplikacji zostawia kanał alfa = 0, co dałoby przezroczystą miniaturę.
                var info = new SKImageInfo(thumb.Width, thumb.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var image = SKImage.FromPixelCopy(info, data.Scan0, data.Stride);
                Save(image, key);
            }
            finally { thumb.UnlockBits(data); }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Miniatura okna nieudana: {ex.Message}");
            return false;
        }
    }

    /// <summary>Miniatura karty z rozszerzenia (data URL JPEG z captureVisibleTab).</summary>
    public bool SaveTabImage(string dataUrl, string key)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return false;
        try
        {
            using var source = SKBitmap.Decode(Convert.FromBase64String(dataUrl[(comma + 1)..]));
            if (source == null) return false;
            int h = Math.Max(1, (int)Math.Round(source.Height * (Width / (double)source.Width)));
            using var scaled = source.Resize(new SKImageInfo(Width, h), SKFilterQuality.High);
            using var image = SKImage.FromBitmap(scaled);
            Save(image, key);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Miniatura karty nieudana: {ex.Message}");
            return false;
        }
    }

    private void Save(SKImage image, string key)
    {
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
        var path = PathFor(key);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) encoded.SaveTo(fs);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Nieudane przechwycenie daje zwykle obraz całkiem czarny albo jednolity.</summary>
    private static bool IsBlank(Bitmap bmp)
    {
        var first = bmp.GetPixel(0, 0).ToArgb() & 0xFFFFFF;
        for (int y = 0; y < bmp.Height; y += 6)
        for (int x = 0; x < bmp.Width; x += 6)
            if ((bmp.GetPixel(x, y).ToArgb() & 0xFFFFFF) != first) return false;
        return true;
    }
}
