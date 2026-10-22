using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TermLens;

public partial class LookupWindow : Window
{
    private readonly App app;
    private CancellationTokenSource? pending;
    private string term = "", context = "";
    private Explanation? explanation;
    private int generation;
    private readonly Stopwatch elapsed = new();
    private readonly DispatcherTimer waitingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool receivedText;
    public LookupWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        waitingTimer.Tick += (_, _) => LoadingText.Text = $"{(receivedText ? "正在生成解释" : "正在等待服务响应")}… 已等待 {(int)elapsed.Elapsed.TotalSeconds} 秒";
        Closing += (_, e) =>
        {
            if (app.Exiting) return;
            e.Cancel = true;
            Dismiss();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
    }
    public async void Query(string selected, string suppliedContext, Native.Point anchor, bool stream = true)
    {
        CancelPending();
        term = selected; context = suppliedContext;
        ContextInput.Text = context;
        ContextExpander.IsExpanded = false;
        var version = ++generation;
        explanation = null;
        TitleText.Text = term.Length > 80 ? term[..80] + "…" : term;
        EnglishText.Text = "计算机 · 入门解释";
        DefinitionText.Text = AnalogyText.Text = UsageText.Text = "";
        Sections.Visibility = Visibility.Collapsed;
        MessageText.Visibility = Visibility.Collapsed;
        HistoryNotice.Text = "";
        ContentScroll.ScrollToTop();
        CopyButton.IsEnabled = false;
        CopyButton.Content = "复制解释";
        RetryButton.IsEnabled = false;
        PlainButton.IsEnabled = false;
        receivedText = false;
        LoadingText.Text = "正在连接… 已等待 0 秒";
        elapsed.Restart();
        waitingTimer.Start();
        LoadingPanel.Visibility = Visibility.Visible;
        Show();
        Native.Place(this, anchor);
        Activate();
        pending = new CancellationTokenSource();
        var cancellation = pending;
        var progress = new ViewProgress(partial => Dispatcher.BeginInvoke(() =>
        {
            if (version != generation || pending != cancellation || cancellation.IsCancellationRequested || !IsVisible) return;
            receivedText = true;
            RenderContent(partial);
        }));
        try
        {
            var result = await app.Client.ExplainAsync(app.Settings, term, context, cancellation.Token, progress, stream);
            if (version != generation || !IsVisible) return;
            Render(result);
            app.History.Remember(term, context, result);
            HistoryNotice.Text = app.History.Warning;
        }
        catch (OperationCanceledException) { }
        catch (UserError ex) { if (version == generation) ShowMessage(ex.Message); }
        catch { if (version == generation) ShowMessage("查询未完成，请重试或检查接口设置。"); }
        finally
        {
            if (version == generation)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                RetryButton.IsEnabled = true;
                PlainButton.IsEnabled = true;
                waitingTimer.Stop();
                elapsed.Stop();
                pending = null;
            }
            cancellation.Dispose();
        }
    }
    internal void Render(Explanation result)
    {
        explanation = result;
        RenderContent(result);
        CopyButton.IsEnabled = true;
    }
    public void ShowSaved(HistoryEntry entry, Native.Point anchor)
    {
        CancelPending();
        term = entry.Term; context = entry.Context;
        ContextInput.Text = context;
        ContextExpander.IsExpanded = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        RetryButton.IsEnabled = PlainButton.IsEnabled = true;
        CopyButton.Content = "复制解释";
        Render(entry.Answer);
        HistoryNotice.Text = "历史记录 · " + entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · 重新查询可获取新解释";
        ContentScroll.ScrollToTop();
        Show(); Native.Place(this, anchor); Activate();
    }
    private sealed class ViewProgress(Action<Explanation> update) : IProgress<Explanation>
    {
        public void Report(Explanation value) => update(value);
    }
    private void RenderContent(Explanation result)
    {
        TitleText.Text = result.Chinese;
        EnglishText.Text = result.English;
        if (result.Plain != null) ShowMessage(result.Plain);
        else
        {
            MessageText.Visibility = Visibility.Collapsed;
            Sections.Visibility = Visibility.Visible;
            DefinitionText.Text = result.Definition;
            AnalogyText.Text = result.Analogy;
            UsageText.Text = result.Usage;
            PrincipleText.Text = result.Principle;
            ExampleText.Text = result.Example;
            PitfallsText.Text = result.Pitfalls;
            PrinciplePanel.Visibility = result.Principle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ExamplePanel.Visibility = result.Example.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            PitfallsPanel.Visibility = result.Pitfalls.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            MeaningText.Text = result.Meaning;
            AmbiguityText.Text = result.Ambiguity;
            AmbiguityText.Visibility = result.Ambiguity.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            MeaningPanel.Visibility = result.Meaning.Length > 0 || result.Ambiguity.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
        Sections.Visibility = Visibility.Collapsed;
    }
    private void CancelPending()
    {
        generation++;
        pending?.Cancel();
        pending = null;
        waitingTimer.Stop();
        elapsed.Stop();
    }
    public void Dismiss()
    {
        CancelPending(); Hide();
        explanation = null;
        term = context = "";
        TitleText.Text = EnglishText.Text = DefinitionText.Text = AnalogyText.Text = UsageText.Text = "";
        MeaningText.Text = AmbiguityText.Text = MessageText.Text = ContextInput.Text = "";
        PrincipleText.Text = ExampleText.Text = PitfallsText.Text = HistoryNotice.Text = "";
        CopyButton.IsEnabled = false;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();
    private void Drag_Header(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBlock or System.Windows.Controls.Border)
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelPending();
        LoadingPanel.Visibility = Visibility.Collapsed;
        RetryButton.IsEnabled = true;
        PlainButton.IsEnabled = true;
        ShowMessage("查询已取消。");
    }
    private void Retry_Click(object sender, RoutedEventArgs e)
        => Requery(true);
    private void Plain_Click(object sender, RoutedEventArgs e)
        => Requery(false);
    private void Context_Click(object sender, RoutedEventArgs e)
        => Requery(true);
    private void Requery(bool stream)
    {
        Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var rect);
        Query(term, ContextInput.Text, new Native.Point(rect.Left - 12, rect.Top - 18), stream);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => app.ShowMain(true);
    private void History_Click(object sender, RoutedEventArgs e) => app.ShowHistory();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(explanation?.CopyText ?? ""); CopyButton.Content = "已复制"; }
        catch { CopyButton.Content = "复制失败，请重试"; }
    }
}
