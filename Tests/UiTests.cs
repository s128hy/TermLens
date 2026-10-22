using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TermLens;

internal static partial class Program
{
    private sealed class UiHandler : HttpMessageHandler
    {
        public TaskCompletionSource ReleaseOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string LastContext = "";
        public bool Cancelled;
        public bool Block;
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var term = data.RootElement.GetProperty("term").GetString();
            LastContext = data.RootElement.GetProperty("context").GetString()!;
            if (Block || term == "旧查询")
                return Events(new GatedStream(Event("{\"definition\":\"这是未完成的解释"), Event("\"}" , "stop") + "data: [DONE]\n\n", ReleaseOld.Task, () => Cancelled = true));
            var result = Answer.Replace("随机森林", "新查询结果");
            return JsonResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = result } } } }));
        }
    }
    private static void PumpUntil(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression("condition")] string expression = "")
    {
        var frame = new DispatcherFrame();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || clock.ElapsedMilliseconds > 3000) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!condition()) throw new Exception("Timed out: " + expression);
    }
    private static void UiReplacement(App app, LookupWindow popup, UiHandler handler)
    {
        app.Settings = new("https://example.com/v1", "test-only-key", "test-model");
        popup.Query("旧查询", "", new Native.Point(100, 100));
        try { PumpUntil(() => ((TextBlock)popup.FindName("DefinitionText")).Text.Contains("未完成")); }
        catch (Exception ex) { throw new Exception(ex.Message + $"; visible={popup.IsVisible}; message={((TextBlock)popup.FindName("MessageText")).Text}; title={((TextBlock)popup.FindName("TitleText")).Text}"); }
        Assert(!app.History.Entries.Any(x => x.Term == "旧查询"));
        Assert(!((Button)popup.FindName("CopyButton")).IsEnabled);
        popup.Query("新查询", "", new Native.Point(100, 100));
        PumpUntil(() => ((Button)popup.FindName("CopyButton")).IsEnabled);
        handler.ReleaseOld.TrySetResult();
        PumpUntil(() => handler.Cancelled);
        Equal(((TextBlock)popup.FindName("TitleText")).Text, "新查询结果");
        popup.Dismiss();
    }
    private static void UiContext(App app, LookupWindow popup, UiHandler handler)
    {
        popup.Query("Transformer", "", new Native.Point(100, 100));
        PumpUntil(() => ((Button)popup.FindName("CopyButton")).IsEnabled);
        ((TextBox)popup.FindName("ContextInput")).Text = "The transformer reduces voltage.";
        var click = typeof(LookupWindow).GetMethod("Context_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        click.Invoke(popup, new object[] { popup, new RoutedEventArgs() });
        PumpUntil(() => ((Button)popup.FindName("CopyButton")).IsEnabled);
        Equal(handler.LastContext, "The transformer reduces voltage.");
        handler.Block = true; handler.Cancelled = false;
        handler.ReleaseOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        popup.Query("等待取消", "", new Native.Point(100, 100));
        PumpUntil(() => ((TextBlock)popup.FindName("DefinitionText")).Text.Contains("未完成"));
        var cancel = typeof(LookupWindow).GetMethod("Cancel_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        cancel.Invoke(popup, new object[] { popup, new RoutedEventArgs() });
        PumpUntil(() => handler.Cancelled);
        Equal(((TextBlock)popup.FindName("MessageText")).Text, "查询已取消。");
        Assert(((Button)popup.FindName("RetryButton")).IsEnabled);
        Assert(!((Button)popup.FindName("CopyButton")).IsEnabled);
        Assert(!app.History.Entries.Any(x => x.Term == "等待取消"));
        popup.Dismiss();
    }
    private static void UiPersistent(App app, LookupWindow popup, UiHandler handler)
    {
        handler.Block = true; handler.Cancelled = false;
        handler.ReleaseOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        popup.Query("保持显示", "", new Native.Point(100, 100));
        PumpUntil(() => ((TextBlock)popup.FindName("DefinitionText")).Text.Contains("未完成"));
        ActivateFixture(new System.Windows.Interop.WindowInteropHelper(popup).Handle);
        var other = new Window { Title = "TermLens focus test", Width = 260, Height = 150 };
        try
        {
            other.Show();
            ActivateFixture(new System.Windows.Interop.WindowInteropHelper(other).Handle);
            PumpUntil(() => !popup.IsActive);
            Assert(popup.IsVisible && !handler.Cancelled);
            handler.ReleaseOld.TrySetResult();
            PumpUntil(() => ((Button)popup.FindName("CopyButton")).IsEnabled);
            Assert(popup.IsVisible);
            var close = typeof(LookupWindow).GetMethod("Close_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            close.Invoke(popup, new object[] { popup, new RoutedEventArgs() });
            Assert(!popup.IsVisible);
        }
        finally { other.Close(); popup.Dismiss(); handler.Block = false; handler.ReleaseOld.TrySetResult(); }
    }
    private static void UiHistory(App app, MainWindow main, UiHandler handler)
    {
        app.History.Clear();
        var answer = ExplanationClient.Parse(Answer, "RF") with { Principle = "原理示例", Example = "可复现的小例子", Pitfalls = "局限示例" };
        app.History.Remember("随机森林", "机器学习", answer);
        app.History.Remember("Transformer", "The transformer reduces voltage.", answer);
        main.OpenHistory();
        var list = (ListBox)main.FindName("HistoryList");
        Equal(list.Items.Count, 2);
        ((TextBox)main.FindName("HistoryFilter")).Text = "随机";
        Equal(list.Items.Count, 1);
        var before = handler.Requests;
        var previousSettings = app.Settings;
        app.Settings = new ApiSettings(); // Cached answers must work without a configured service.
        var item = (HistoryEntry)list.Items[0];
        var open = typeof(MainWindow).GetMethod("HistoryOpen_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        open.Invoke(main, new object[] { new Button { Tag = item }, new RoutedEventArgs() });
        var field = typeof(App).GetField("lookup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var saved = (LookupWindow)field.GetValue(app)!;
        Equal(handler.Requests, before);
        Assert(saved.IsVisible && ((Button)saved.FindName("CopyButton")).IsEnabled);
        Equal(((TextBlock)saved.FindName("ExampleText")).Text, "可复现的小例子");
        Assert(((TextBlock)saved.FindName("HistoryNotice")).Text.Contains("历史记录"));
        RenderContent(main, 544, 660, "artifacts/history-preview.png");
        RenderContent(saved, 430, 640, "artifacts/detailed-card-preview.png");
        app.Settings = previousSettings;
        var retry = typeof(LookupWindow).GetMethod("Retry_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        retry.Invoke(saved, new object[] { saved, new RoutedEventArgs() });
        PumpUntil(() => handler.Requests > before && ((Button)saved.FindName("CopyButton")).IsEnabled);
        Equal(app.History.Entries[0].Term, "随机森林");
        saved.Dismiss();
        var clear = typeof(MainWindow).GetMethod("ClearHistory_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        clear.Invoke(main, new object[] { main, new RoutedEventArgs() });
        Equal(list.Items.Count, 0); Equal(app.History.Entries.Count, 0);
    }
}
