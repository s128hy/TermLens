using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TermLens;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void NotifyWinEvent(uint evt, IntPtr window, int objectId, int childId);
    private static int passed, failed;
    private static readonly List<string> report = new();
    private const string Answer = """{"chinese":"随机森林","english":"Random Forest","definition":"把多棵决策树的判断结合起来，用于分类或预测的机器学习方法。","analogy":"像请多位各有经验的评委一起投票，汇总意见通常比只问一位更稳妥；但评委之间也可能有相同的偏差。","usage":"例如识别垃圾邮件，或根据房屋特征预测房价。"}""";
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        report.Add("Browser fixtures use guest mode; explicitly exposed accessibility=" + (Environment.GetEnvironmentVariable("TERMLENS_TEST_EXPOSE_ACCESSIBILITY") == "1"));
        if (args.Length > 0 && args[0] == "--capture-probe") { CaptureTests(); return failed == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--wps-probe") return WpsProbe();
        if (args.Length > 0 && args[0] == "--browser-probe")
        {
            Test("Chrome", () => BrowserTest(FindBrowser("chrome"), "chrome"));
            return failed == 0 ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "--pdf-probe")
        {
            if (args.Length > 1 && args[1] == "chrome")
                Test("Chrome PDF：读取实际 PDF 选词", () => BrowserTest(FindBrowser("chrome"), "chrome-pdf", true));
            else Test("Edge PDF：读取实际 PDF 选词", () => BrowserTest(FindBrowser("edge"), "edge-pdf", true));
            return failed == 0 ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "--fixture")
        {
            var form = new System.Windows.Forms.Form { Text = "TermLens selection test fixture", Width = 480, Height = 180 };
            var box = new System.Windows.Forms.TextBox { Text = "随机森林 gradient descent", Dock = System.Windows.Forms.DockStyle.Fill, Multiline = true };
            form.Controls.Add(box);
            form.Shown += (_, _) => { ActivateFixture(form.Handle); form.Activate(); box.Focus(); box.Select(0, args.Length > 1 ? (args[1] == "sentence" ? box.Text.Length : 0) : 4); };
            using var changeTimer = new System.Windows.Forms.Timer { Interval = 600 };
            if (args.Length > 1 && args[1] == "churn")
            {
                form.TopMost = true;
                var count = 0;
                changeTimer.Tick += (_, _) =>
                {
                    if (++count > 20) { changeTimer.Stop(); return; }
                    box.Select(0, count % 2 == 0 ? 4 : box.Text.Length);
                    NotifyWinEvent(0x8014, box.Handle, -4, 0);
                };
                changeTimer.Start();
            }
            System.Windows.Forms.Application.Run(form);
            return 0;
        }
        Test("地址：主机自动补 /v1", () => Equal(new ApiSettings("https://example.com").Endpoint().AbsoluteUri, "https://example.com/v1/chat/completions"));
        Test("地址：保留服务商自定义路径", () => Equal(new ApiSettings("https://example.com/api/v3/").Endpoint().AbsoluteUri, "https://example.com/api/v3/chat/completions"));
        Test("地址：接受完整端点", () => Equal(new ApiSettings("https://example.com/v1/chat/completions").Endpoint().AbsoluteUri, "https://example.com/v1/chat/completions"));
        Test("地址：允许本机 HTTP", () => Equal(new ApiSettings("http://127.0.0.1:3456/v1").Endpoint().Scheme, "http"));
        Test("地址：拒绝远程明文 HTTP", () => Throws<UserError>(() => new ApiSettings("http://example.com/v1").Endpoint()));
        Test("地址：拒绝带查询密钥的地址", () => Throws<UserError>(() => new ApiSettings("https://example.com/v1?key=secret").Endpoint()));
        Test("配置：空密钥与空模型被拒绝", () => { Throws<UserError>(() => new ApiSettings("https://example.com", "", "model").Validate()); Throws<UserError>(() => new ApiSettings("https://example.com", "key", "").Validate()); });
        Test("输入：空词与超长词被拒绝", () => { Throws<UserError>(() => QueryRules.Validate("  ", "")); Throws<UserError>(() => QueryRules.Validate(new string('a', QueryRules.MaxTerm + 1), "")); });
        Test("输入：上下文长度限制", () => Throws<UserError>(() => QueryRules.Validate("RF", new string('x', 3001))));
        Test("解析：结构化解释", () => { var answer = ExplanationClient.Parse(Answer, "RF"); Equal(answer.English, "Random Forest"); Assert(answer.CopyText.Contains("核心定义")); });
        Test("解析：兼容代码围栏", () => Equal(ExplanationClient.Parse("```json\n" + Answer + "\n```", "RF").Chinese, "随机森林"));
        Test("解析：非结构化回答保留原文", () => Equal(ExplanationClient.Parse("通俗的原文解释", "RF").Plain, "通俗的原文解释"));
        Test("解析：空回答被拒绝", () => Throws<UserError>(() => ExplanationClient.Parse(" ", "RF")));
        Test("密钥：Windows 加密往返且非明文", () => { var encrypted = SecretProtection.Protect("local-test-secret-测试"); Assert(!encrypted.Contains("local-test-secret")); Equal(SecretProtection.Unprotect(encrypted), "local-test-secret-测试"); });
        HistoryTests();
        ApiTests().GetAwaiter().GetResult();
        UpgradeApiTests().GetAwaiter().GetResult();
        if (Array.IndexOf(args, "--selection") >= 0)
        {
            Test("Windows UIA：读取外部程序的中文选词", SelectionTest);
            Test("Chrome：读取网页中的中文划词", () => BrowserTest(FindBrowser("chrome"), "chrome"));
            Test("Edge：读取网页中的中文划词", () => BrowserTest(FindBrowser("edge"), "edge"));
        }
        Test("界面：主窗口和解释卡片渲染", Render);
        report.Add($"\nPassed: {passed}; Failed: {failed}");
        Directory.CreateDirectory("artifacts");
        File.WriteAllLines("artifacts/test-results.txt", report, Encoding.UTF8);
        Console.WriteLine(report[^1]);
        return failed == 0 ? 0 : 1;
    }
    private static async Task ApiTests()
    {
        var settings = new ApiSettings("https://example.com/v1", "test-only-key", "test-model");
        await TestAsync("API：请求字段、鉴权、上下文和响应解析", async () =>
        {
            using var client = new ExplanationClient(new FakeHandler(async (request, token) =>
            {
                Equal(request.RequestUri!.AbsoluteUri, "https://example.com/v1/chat/completions");
                Equal(request.Headers.Authorization!.ToString(), "Bearer test-only-key");
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Equal(json.RootElement.GetProperty("model").GetString(), "test-model");
                var messages = json.RootElement.GetProperty("messages");
                Equal(messages.GetArrayLength(), 2);
                using var user = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                Equal(user.RootElement.GetProperty("term").GetString(), "随机森林");
                Equal(user.RootElement.GetProperty("context").GetString(), "分类算法");
                return JsonResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Answer } } } }));
            }));
            var result = await client.ExplainAsync(settings, "随机森林", "分类算法", CancellationToken.None);
            Equal(result.English, "Random Forest");
        });
        foreach (var status in new[] { 401, 403, 404, 429, 500, 302 })
        {
            await TestAsync($"API：HTTP {status} 错误有用户提示且不泄露响应体", async () =>
            {
                using var client = new ExplanationClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("server-secret-should-not-leak") })));
                try { await client.ExplainAsync(settings, "RF", "", CancellationToken.None); throw new Exception("Expected error"); }
                catch (UserError ex) { Assert(!ex.Message.Contains("server-secret")); }
            });
        }
        foreach (var body in new[] { "not JSON", """{"choices":[]}""", """{"choices":[{"message":{"content":null}}]}""" })
        {
            await TestAsync("API：无效响应不会显示成功", async () =>
            {
                using var client = new ExplanationClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(body))));
                await ThrowsAsync<UserError>(() => client.ExplainAsync(settings, "RF", "", CancellationToken.None));
            });
        }
        await TestAsync("API：用户取消会终止等待", async () =>
        {
            using var client = new ExplanationClient(new FakeHandler(async (_, token) => { await Task.Delay(10000, token); return JsonResponse("{}"); }));
            using var cts = new CancellationTokenSource(30);
            await ThrowsAsync<OperationCanceledException>(() => client.ExplainAsync(settings, "RF", "", cts.Token));
        });
        await TestAsync("API：网络错误不会泄漏密钥", async () =>
        {
            using var client = new ExplanationClient(new FakeHandler((_, _) => throw new HttpRequestException("test-only-key")));
            try { await client.ExplainAsync(settings, "RF", "", CancellationToken.None); throw new Exception("Expected error"); }
            catch (UserError ex) { Assert(!ex.Message.Contains("test-only-key")); }
        });
    }
    private static void Render()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var uiHandler = new UiHandler();
        var app = new App(new ExplanationClient(uiHandler), desktopIntegration: false);
        app.InitializeComponent();
        var main = new MainWindow(app);
        var tabs = (System.Windows.Controls.TabControl)main.FindName("Tabs");
        tabs.SelectedIndex = 0;
        RenderContent(main, 544, 660, "artifacts/main-preview.png");
        RenderContent(main, 464, 500, "artifacts/main-compact-preview.png");
        tabs.SelectedIndex = 1;
        RenderContent(main, 544, 660, "artifacts/settings-preview.png");
        var popup = new LookupWindow(app);
        var render = typeof(LookupWindow).GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, new[] { typeof(Explanation) })!;
        render.Invoke(popup, new object[] { ExplanationClient.Parse(Answer, "RF") with { Meaning = "机器学习领域：集成学习算法", Ambiguity = "此处按机器学习术语解释；若原文语境不同，可补充原句确认。" } });
        RenderContent(popup, 430, 640, "artifacts/card-preview.png");
        ((System.Windows.Controls.Expander)popup.FindName("ContextExpander")).IsExpanded = true;
        RenderContent(popup, 430, 640, "artifacts/context-preview.png");
        Test("界面：新查询替换旧查询，忽略延迟进度", () => UiReplacement(app, popup, uiHandler));
        Test("界面：补充上下文重新查询并取消", () => UiContext(app, popup, uiHandler));
        popup.Dismiss();
        Test("界面：按需创建卡片，关闭后清空内容，随后可以重新查询", () =>
        {
            uiHandler.Block = false;
            var field = typeof(App).GetField("lookup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert(field.GetValue(app) == null);
            app.Lookup("随机森林", "test context", new Native.Point(100, 100));
            var lazyPopup = (LookupWindow)field.GetValue(app)!;
            PumpUntil(() => ((System.Windows.Controls.Button)lazyPopup.FindName("CopyButton")).IsEnabled);
            lazyPopup.Dismiss();
            foreach (var name in new[] { "TitleText", "DefinitionText", "MessageText" })
                Equal(((System.Windows.Controls.TextBlock)lazyPopup.FindName(name)).Text, "");
            Equal(((System.Windows.Controls.TextBox)lazyPopup.FindName("ContextInput")).Text, "");
            app.Lookup("梯度下降", "", new Native.Point(100, 100));
            PumpUntil(() => ((System.Windows.Controls.Button)lazyPopup.FindName("CopyButton")).IsEnabled);
            Assert(ReferenceEquals(lazyPopup, field.GetValue(app)));
            lazyPopup.Dismiss();
        });
        Test("界面：失焦不关闭、不取消查询，手动关闭仍有效", () => UiPersistent(app, popup, uiHandler));
        Test("界面：历史筛选、离线回看和清空", () => UiHistory(app, main, uiHandler));
        SynchronizationContext.SetSynchronizationContext(null);
    }
    private static void RenderContent(Window window, int width, int height, string path)
    {
        var element = (FrameworkElement)window.Content;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        encoder.Save(output);
    }
    private static void SelectionTest()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--fixture") { UseShellExecute = false })!;
        try
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) break;
                if (process.HasExited) throw new Exception("Selection fixture exited unexpectedly");
                Thread.Sleep(100);
            }
            Thread.Sleep(300);
            var handle = process.MainWindowHandle;
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
            Native.GetWindowRect(handle, out var rect);
            string? result = null;
            for (var attempt = 0; attempt < 3 && result == null; attempt++)
            {
                result = Task.Run(() => DesktopSelection.ReadAt(new Native.Point(rect.Left + 60, rect.Top + 70), handle)).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (result == null) Thread.Sleep(250);
            }
            if (result == null)
            {
                var diagnosis = Task.Run(() =>
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(handle);
                    var children = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    var lines = new List<string>();
                    foreach (System.Windows.Automation.AutomationElement child in children)
                        if (child.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var pattern))
                            lines.Add($"pid={child.Current.ProcessId}, selected=" + string.Join("|", Array.ConvertAll(((System.Windows.Automation.TextPattern)pattern).GetSelection(), x => x.GetText(30))));
                    return string.Join(";", lines);
                }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                throw new Exception("Fixture selection unavailable: " + diagnosis);
            }
            Equal(result, "随机森林");
        }
        finally { if (!process.HasExited) process.Kill(); }
    }
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Log("PASS " + name); }
        catch (Exception ex) { failed++; Log("FAIL " + name + ": " + ex.Message); }
    }
    private static async Task TestAsync(string name, Func<Task> test)
    {
        try { await test(); passed++; Log("PASS " + name); }
        catch (Exception ex) { failed++; Log("FAIL " + name + ": " + ex.Message); }
    }
    private static void Log(string line) { report.Add(line); Console.WriteLine(line); }
    private static void Assert(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression("condition")] string expression = "") { if (!condition) throw new Exception("Assertion failed: " + expression); }
    private static void Equal<T>(T actual, T expected) { if (!Equals(actual, expected)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token);
    }
}
