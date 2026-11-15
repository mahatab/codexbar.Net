using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexBar.Core;

namespace CodexBar.App;

/// <summary>
/// The notification-area icon: a tiny two-bar usage meter, like the macOS menu bar icon.
/// The top bar is the first quota window of the first provider, the bottom bar the second.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon icon;
    private IntPtr currentHandle;

    public TrayIcon(Action onClick, Action onRefresh, Action onSpend, Action onSettings, Action onQuit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open CodexBar", null, (_, _) => onClick());
        menu.Items.Add("Refresh now", null, (_, _) => onRefresh());
        menu.Items.Add("Usage && spend…", null, (_, _) => onSpend());
        menu.Items.Add("Settings…", null, (_, _) => onSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => onQuit());

        this.icon = new NotifyIcon { Text = "CodexBar", ContextMenuStrip = menu, Visible = true };
        this.icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                onClick();
            }
        };
        this.Update([], "CodexBar", hasError: false);
    }

    /// <param name="letter">Provider initial drawn beside the bars when each provider has its own icon.</param>
    public void Update(IReadOnlyList<double> usedPercents, string tooltip, bool hasError, string? letter = null)
    {
        var handle = Render(usedPercents, Theme.TaskbarIsDark, hasError, letter);
        this.icon.Icon = Icon.FromHandle(handle);
        if (this.currentHandle != IntPtr.Zero)
        {
            DestroyIcon(this.currentHandle);
        }

        this.currentHandle = handle;
        // NotifyIcon rejects tooltips longer than 127 characters.
        this.icon.Text = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip;
    }

    public void Notify(string title, string text, bool warning = true) =>
        this.icon.ShowBalloonTip(8000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        this.icon.Visible = false;
        this.icon.Dispose();
        if (this.currentHandle != IntPtr.Zero)
        {
            DestroyIcon(this.currentHandle);
        }
    }

    /// <summary>Draws the meter at 32×32; Windows scales it down for standard-DPI taskbars.</summary>
    internal static IntPtr Render(IReadOnlyList<double> usedPercents, bool darkTaskbar, bool hasError, string? letter = null)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            var track = darkTaskbar ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0);
            var idle = darkTaskbar ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(220, 0, 0, 0);

            // With a letter, the bars give up their left third so the provider can be told apart at a glance.
            float left = 2;
            if (!string.IsNullOrEmpty(letter))
            {
                using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
                using var ink = new SolidBrush(idle);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                graphics.DrawString(letter[..1].ToUpperInvariant(), font, ink, new RectangleF(-2, 4, 17, 24), format);
                left = 14;
            }

            DrawBar(graphics, new RectangleF(left, 6, 30 - left, 9), usedPercents.Count > 0 ? usedPercents[0] : null, track, idle);
            DrawBar(graphics, new RectangleF(left, 19, 30 - left, 6), usedPercents.Count > 1 ? usedPercents[1] : null, track, idle);

            if (hasError)
            {
                using var dot = new SolidBrush(Color.FromArgb(255, 240, 165, 58));
                graphics.FillEllipse(dot, 22, 0, 10, 10);
            }
        }

        return bitmap.GetHicon();
    }

    private static void DrawBar(Graphics graphics, RectangleF bounds, double? usedPercent, Color track, Color idle)
    {
        using var trackBrush = new SolidBrush(track);
        FillRounded(graphics, trackBrush, bounds);
        if (usedPercent is not { } used)
        {
            return;
        }

        var color = used >= UsageStore.HighUsageThreshold ? Color.FromArgb(255, 239, 91, 91)
            : used >= 70 ? Color.FromArgb(255, 240, 165, 58)
            : idle;
        var width = (float)(bounds.Width * Math.Clamp(used, 0, 100) / 100);
        if (width < 1)
        {
            return;
        }

        using var fill = new SolidBrush(color);
        FillRounded(graphics, fill, bounds with { Width = Math.Max(width, bounds.Height) });
    }

    private static void FillRounded(Graphics graphics, Brush brush, RectangleF bounds)
    {
        var diameter = Math.Min(bounds.Height, bounds.Width);
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90, 180);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 180);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
