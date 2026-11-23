using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace OpenStreamMS.Services.Mosaic;

/// <summary>
/// Imagen que pone el Sunshine de una sesión compuesta en el hueco de un miembro que se está
/// (re)iniciando: el logo de OpenStreamMS, el nombre de la sesión y "Reconectando…". Formato que
/// lee Sunshine (parse_waiting_image): "OSWP", ancho y alto en int32, y los píxeles BGRA.
/// </summary>
public static class WaitingImage
{
    public static byte[] Render(int width, int height, string title, string subtitle)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.Clear(Color.FromArgb(13, 13, 26));   // opaca: el degradado con antialias deja los bordes a medias
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height),
                       Color.FromArgb(13, 13, 26), Color.FromArgb(30, 18, 56), 60f))
                g.FillRectangle(bg, 0, 0, width, height);

            int unit = Math.Max(8, Math.Min(width, height));
            int logo = Math.Clamp(unit / 4, 32, 512);
            using (var png = new MemoryStream(OpenStreamMS.Core.Helpers.AppIcon.RenderPng(logo)))
            using (var icon = Image.FromStream(png))
            {
                int lx = (width - logo) / 2, ly = height / 2 - logo + logo / 6;
                g.DrawImage(icon, lx, ly, logo, logo);
            }

            using var titleFont = new Font("Segoe UI Semibold", Math.Max(10f, unit / 22f), GraphicsUnit.Pixel);
            using var subFont = new Font("Segoe UI", Math.Max(9f, unit / 30f), GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.FromArgb(236, 238, 245));
            using var muted = new SolidBrush(Color.FromArgb(160, 168, 190));
            using var center = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            float y = height / 2f + logo / 4f;
            g.DrawString(title, titleFont, white, new RectangleF(0, y, width, titleFont.Size * 1.6f), center);
            g.DrawString(subtitle, subFont, muted, new RectangleF(0, y + titleFont.Size * 1.6f, width, subFont.Size * 1.6f), center);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[12 + width * height * 4];
            "OSWP"u8.CopyTo(bytes);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), width);
            BitConverter.TryWriteBytes(bytes.AsSpan(8), height);
            // Format32bppArgb en memoria ya es B, G, R, A.
            for (int row = 0; row < height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, bytes, 12 + row * width * 4, width * 4);
            return bytes;
        }
        finally { bmp.UnlockBits(data); }
    }
}
