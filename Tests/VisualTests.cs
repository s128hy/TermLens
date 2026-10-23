using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TermLens;

internal static partial class Program
{
    private static void IconMargins()
    {
        using var stream = File.OpenRead("app.ico");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        foreach (var size in new[] { 16, 32, 48, 256 }) Assert(decoder.Frames.Any(x => x.PixelWidth == size));
        foreach (var frame in decoder.Frames)
        {
            var image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var width = image.PixelWidth; var height = image.PixelHeight;
            var pixels = new byte[width * height * 4];
            image.CopyPixels(pixels, width * 4, 0);
            var margin = Math.Max(1, (int)(width * 0.05));
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (x < margin || x >= width - margin || y < margin || y >= height - margin)
                        Assert(pixels[(y * width + x) * 4 + 3] == 0);
            Assert(pixels.Where((_, i) => i % 4 == 3).Any(x => x > 0));
        }
    }
    private static void UiCompactLayout(App app, MainWindow main)
    {
        var tabs = (TabControl)main.FindName("Tabs");
        tabs.SelectedIndex = 0;
        var button = (Button)main.FindName("QueryButton");
        var input = (TextBox)main.FindName("TermInput");
        var root = (FrameworkElement)main.FindName("Root");
        foreach (var scale in new[] { 0.8, 1.0, 2.0 })
        {
            app.SetDisplay(new DisplaySettings(scale, false));
            main.ApplyDisplay();
            RenderContent(main, 464, 500, $"artifacts/compact-{scale:0.0}-preview.png");
            var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Assert(button.ActualWidth > 0 && button.ActualHeight > 0);
            Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1);
            input.Text = "测试输入 gradient descent";
            input.SelectAll();
            Equal(input.SelectedText, input.Text);
        }
        app.SetDisplay(new DisplaySettings()); main.ApplyDisplay();
        input.Clear();
        RenderContent(main, 544, 660, "artifacts/main-preview.png");
        RenderContent(main, 464, 500, "artifacts/main-compact-preview.png");
        tabs.SelectedIndex = 1;
        main.UpdateLayout();
        var slider = (Slider)main.FindName("FontScaleSlider");
        slider.ApplyTemplate();
        var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
        slider.Value = 1.4;
        Equal(track.Value, slider.Value);
        slider.Value = 1.0;
        app.SetDisplay(new DisplaySettings()); main.ApplyDisplay();
    }
}
