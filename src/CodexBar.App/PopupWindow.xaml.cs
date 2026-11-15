using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexBar.App;

public partial class PopupWindow : Window
{
    public PopupWindow()
    {
        this.InitializeComponent();
        this.Deactivated += (_, _) =>
        {
            if (this.HideWhenDeactivated)
            {
                this.LastHidden = DateTime.UtcNow;
                this.Hide();
            }
        };
        this.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                this.Hide();
            }
            else if (e.Key == Key.F5)
            {
                this.RefreshRequested?.Invoke();
            }
        };
    }

    public event Action? RefreshRequested;

    public event Action? SettingsRequested;

    public event Action? SpendRequested;

    public event Action? QuitRequested;

    public bool HideWhenDeactivated { get; set; } = true;

    /// <summary>When the popup last hid itself, so a tray click that caused the hide does not reopen it.</summary>
    public DateTime LastHidden { get; private set; } = DateTime.MinValue;

    public void SetCards(
        IReadOnlyList<ProviderCard> cards,
        string status,
        bool refreshing,
        IReadOnlyList<OutageRow>? outages = null)
    {
        this.Cards.ItemsSource = cards;
        this.Outages.ItemsSource = outages;
        this.OutageCard.Visibility = outages is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        var open = outages?.Count(row => row.IsIncident) ?? 0;
        this.OutageSummary.Text = open == 0 ? "All operational" : open == 1 ? "1 service affected" : $"{open} services affected";
        this.EmptyText.Visibility = cards.Count == 0 && outages is not { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        this.StatusText.Text = status;
        this.RefreshButton.IsEnabled = !refreshing;
        this.RefreshButton.Content = refreshing ? "Refreshing…" : "Refresh";
    }

    /// <summary>Shows the popup above the notification area (bottom-right of the work area).</summary>
    public void ShowNearTray()
    {
        this.Show();
        this.UpdateLayout();
        var area = SystemParameters.WorkArea;
        this.Left = area.Right - this.ActualWidth - 4;
        this.Top = area.Bottom - this.ActualHeight - 4;
        this.Activate();
    }

    /// <summary>Renders the popup to a PNG without showing it on screen; used by <c>--render</c>.</summary>
    public void RenderToPng(string path)
    {
        this.HideWhenDeactivated = false;
        this.ShowActivated = false;
        this.Left = -10000;
        this.Top = -10000;
        this.Show();
        this.UpdateLayout();

        // A screenshot should show every card. Windows caps a window at the screen height, so lay the content
        // out again at its full natural height and draw that instead of the window.
        this.Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var content = (FrameworkElement)this.Content;
        content.Measure(new Size(this.ActualWidth, double.PositiveInfinity));
        var size = new Size(this.ActualWidth, content.DesiredSize.Height);
        content.Arrange(new Rect(size));

        const double scale = 2;
        var bitmap = new RenderTargetBitmap(
            (int)(size.Width * scale),
            (int)(size.Height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        this.Hide();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => this.RefreshRequested?.Invoke();

    private void OnSettings(object sender, RoutedEventArgs e) => this.SettingsRequested?.Invoke();

    private void OnSpend(object sender, RoutedEventArgs e) => this.SpendRequested?.Invoke();

    private void OnQuit(object sender, RoutedEventArgs e) => this.QuitRequested?.Invoke();

    private void OnOutage(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            Shell.OpenUrl(url);
        }
    }

    private void OnStatus(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Tag: string url })
        {
            Shell.OpenUrl(url);
        }
    }

    private void OnDashboard(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            Shell.OpenUrl(url);
        }
    }
}
