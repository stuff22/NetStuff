using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (UiOptions.Size is { } sz) { Width = sz.W; Height = sz.H; }
        if (UiOptions.Snapshot != null) Opened += async (_, _) => await SnapshotAsync(UiOptions.Snapshot, UiOptions.SnapDelay);
    }

    /// <summary>Screenshot des Fensters speichern und beenden (automatisierte Tests).</summary>
    private async Task SnapshotAsync(string file, int delaySeconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (UiOptions.Report && Content is MainView mv) mv.AddToReport();
            var size = new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bmp = new RenderTargetBitmap(size);
            bmp.Render(this);
            bmp.Save(file);
        });
        Close();
    }
}
