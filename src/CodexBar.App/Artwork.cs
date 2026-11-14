using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexBar.App;

/// <summary>Generates the project's images from code, so the logo, icon and screenshots never go stale.</summary>
internal static class Artwork
{
    /// <summary>The logo is the tray meter on a dark tile: a teal bar over a shorter amber one.</summary>
    public static void WriteLogo(string pngPath, string icoPath)
    {
        Save(DrawLogo(512), pngPath);

        // An .ico file may hold a PNG directly (Windows Vista and later): a 6-byte header, one 16-byte entry, the PNG.
        using var png = new MemoryStream();
        Encode(DrawLogo(256), png);
        using var ico = new BinaryWriter(File.Create(icoPath));
        ico.Write((short)0);
        ico.Write((short)1);
        ico.Write((short)1);
        ico.Write((byte)0);
        ico.Write((byte)0);
        ico.Write((byte)0);
        ico.Write((byte)0);
        ico.Write((short)1);
        ico.Write((short)32);
        ico.Write((int)png.Length);
        ico.Write(22);
        ico.Write(png.ToArray());
    }

    /// <summary>A strip of enlarged tray icons in their main states, on a taskbar-colored background.</summary>
    public static void WriteTrayStates(string path)
    {
        (IReadOnlyList<double> Bars, bool Error, string? Letter)[] states =
        [
            ([22, 41], false, null),
            ([74, 51], false, null),
            ([93, 60], false, null),
            ([38, 74], true, null),
            ([16, 41], false, "C"),
            ([46, 31], false, "G"),
        ];
        const int cell = 96;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1F)), null, new Rect(0, 0, cell * states.Length, cell));
            for (var index = 0; index < states.Length; index++)
            {
                var handle = TrayIcon.Render(states[index].Bars, darkTaskbar: true, states[index].Error, states[index].Letter);
                var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                context.DrawImage(source, new Rect((index * cell) + 16, 16, 64, 64));
            }
        }

        var bitmap = new RenderTargetBitmap(cell * states.Length, cell, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Save(bitmap, path);
    }

    /// <summary>Renders a normal window (title bar excluded) at its full content height.</summary>
    public static void RenderWindow(Window window, string path)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.Show();
        window.UpdateLayout();

        var content = (FrameworkElement)window.Content;
        if (content is ScrollViewer scroller)
        {
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        var width = content.ActualWidth;
        content.Measure(new Size(width, double.PositiveInfinity));
        var size = new Size(width, content.DesiredSize.Height);
        content.Arrange(new Rect(size));

        // The window background is not part of the content element, so paint it underneath.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(size));
        }

        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Render(content);
        Save(bitmap, path);
        window.Close();
    }

    private static RenderTargetBitmap DrawLogo(int pixels)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Drawn on a 64-unit grid and scaled, so every size has the same proportions.
            context.PushTransform(new ScaleTransform(pixels / 64d, pixels / 64d));
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24)), null, new Rect(2, 2, 60, 60), 14, 14);
            var track = new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x42));
            context.DrawRoundedRectangle(track, null, new Rect(12, 18, 40, 12), 6, 6);
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x16, 0xD3, 0xB4)), null, new Rect(12, 18, 29, 12), 6, 6);
            context.DrawRoundedRectangle(track, null, new Rect(12, 37, 40, 9), 4.5, 4.5);
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xF0, 0xA5, 0x3A)), null, new Rect(12, 37, 18, 9), 4.5, 4.5);
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        using var stream = File.Create(path);
        Encode(bitmap, stream);
    }

    private static void Encode(BitmapSource bitmap, Stream stream)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
