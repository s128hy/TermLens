using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace TermLens;

public partial class App : Application
{
    private Mutex? instance;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? pauseItem;
    private DesktopSelection? selection;
    private SelectionBadge? badge;
    private LookupWindow? lookup;
    private MainWindow? main;
    private IntPtr handle;
    private bool paused;
    private bool resolvingSelection;
    private readonly bool desktopIntegration;
    public bool Exiting { get; private set; }
    public ApiSettings Settings { get; set; } = new();
    public ExplanationClient Client { get; }
    public HistoryStore History { get; }
    public App() : this(new ExplanationClient()) { }
    public App(ExplanationClient client, bool desktopIntegration = true, HistoryStore? history = null)
    {
        Client = client; this.desktopIntegration = desktopIntegration;
        History = history ?? new HistoryStore(desktopIntegration ? HistoryStore.DefaultPath : null);
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!desktopIntegration) return;
        instance = new Mutex(true, @"Local\TermLens.Desktop.v1", out var first);
        if (!first)
        {
            MessageBox.Show("术语镜已在运行。请双击右下角托盘中的术语镜图标打开。", "术语镜");
            Shutdown();
            return;
        }
        string? warning = null;
        try { Settings = SettingsStore.Load(); }
        catch (UserError ex) { warning = ex.Message; }
        main = new MainWindow(this);
        MainWindow = main;
        handle = new WindowInteropHelper(main).EnsureHandle();
        HwndSource.FromHwnd(handle)?.AddHook(OnMessage);
        if (!Native.RegisterHotKey(handle, 1, 0x4003, 0x44))
            warning = "Ctrl + Alt + D 已被其他程序占用；划词和手动查询仍可使用。";
        try
        {
            selection = new DesktopSelection(Dispatcher);
            selection.Selected += selected =>
            {
                if (resolvingSelection) return;
                if (badge == null) { badge = new SelectionBadge(); badge.Chosen += LookupSelection; }
                badge.Present(selected);
            };
            selection.MouseDown += point =>
            {
                if (badge != null && !Native.Contains(badge, point)) badge.Dismiss();
            };
        }
        catch (UserError ex) { warning = ex.Message; }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开术语镜", null, (_, _) => ShowMain());
        menu.Items.Add("查询历史", null, (_, _) => ShowHistory());
        menu.Items.Add("查询剪贴板文字  Ctrl+Alt+D", null, (_, _) => ClipboardQuery());
        pauseItem = new Forms.ToolStripMenuItem("暂停划词", null, (_, _) => TogglePause());
        menu.Items.Add(pauseItem);
        menu.Items.Add("连接与设置", null, (_, _) => ShowMain(true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        tray = new Forms.NotifyIcon
        {
            Text = "术语镜 · 划词解释",
            Icon = CreateIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ShowMain();
        main.Show();
        if (warning != null) main.SetStatus(warning);
    }
    private static System.Drawing.Icon CreateIcon()
    {
        using var stream = GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        using var icon = new System.Drawing.Icon(stream, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }
    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0312 && w.ToInt32() == 1) { handled = true; ClipboardQuery(); }
        return IntPtr.Zero;
    }
    private void ClipboardQuery()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            text = QueryRules.Validate(text, "");
            Native.GetCursorPos(out var point);
            Lookup(text, "", point);
        }
        catch (UserError ex) { ShowMain(); main?.SetStatus(ex.Message); }
        catch { ShowMain(); main?.SetStatus("剪贴板暂时不可用，请重新复制文字后再试。"); }
    }
    public void Lookup(string term, string context, Native.Point anchor)
    {
        badge?.Dismiss();
        try { Settings.Validate(); QueryRules.Validate(term, context); }
        catch (UserError ex) { ShowMain(true); main?.SetStatus(ex.Message); return; }
        (lookup ??= new LookupWindow(this)).Query(term, context, anchor);
    }
    private async void LookupSelection(Selection selected)
    {
        if (resolvingSelection) return;
        resolvingSelection = true;
        try
        {
            Settings.Validate();
            var text = await DesktopSelection.ResolveAsync(selected);
            Lookup(text, "", selected.Anchor);
        }
        catch (UserError ex) { ShowMain(); main?.SetStatus(ex.Message); }
        catch { ShowMain(); main?.SetStatus("选区读取失败，请手动复制文字后按 Ctrl+Alt+D 查询。"); }
        finally { resolvingSelection = false; }
    }
    public void ShowMain(bool settings = false)
    {
        main?.Show();
        if (main != null) { main.WindowState = WindowState.Normal; if (settings) main.OpenSettings(); main.Activate(); }
    }
    public void ShowHistory()
    {
        ShowMain();
        main?.OpenHistory();
    }
    public void ShowHistoryEntry(HistoryEntry entry)
    {
        Native.GetCursorPos(out var anchor);
        (lookup ??= new LookupWindow(this)).ShowSaved(entry, anchor);
    }
    public void TogglePause()
    {
        paused = !paused;
        if (selection != null) selection.Paused = paused;
        badge?.Dismiss();
        main?.SetPaused(paused);
        if (pauseItem != null) pauseItem.Text = paused ? "恢复划词" : "暂停划词";
        if (tray != null) tray.Text = paused ? "术语镜 · 划词已暂停" : "术语镜 · 划词解释";
    }
    private void ExitApp() { Exiting = true; Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        if (!desktopIntegration) { Client.Dispose(); base.OnExit(e); return; }
        selection?.Dispose();
        lookup?.Dismiss();
        Native.UnregisterHotKey(handle, 1);
        if (tray != null) { tray.Visible = false; tray.Icon?.Dispose(); tray.Dispose(); }
        Client.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
