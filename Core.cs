using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TermLens;

public sealed record ApiSettings(string BaseUrl = "", string ApiKey = "", string Model = "")
{
    public Uri Endpoint()
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UserError("请填写有效的 HTTPS 服务地址；本机服务可使用 http://localhost。地址中不要包含密钥、查询参数或 #。");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            path += path.Length == 0 ? "/v1/chat/completions" : "/chat/completions";
        return new UriBuilder(uri) { Path = path }.Uri;
    }
    public void Validate()
    {
        _ = Endpoint();
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsControl))
            throw new UserError("请填写有效的 API Key。");
        if (string.IsNullOrWhiteSpace(Model)) throw new UserError("请填写服务商提供的模型名称。");
    }
}

public sealed class UserError(string message) : Exception(message) { }

public static class QueryRules
{
    public const int MaxTerm = 2000;
    public const int MaxContext = 3000;
    public static string Validate(string term, string context)
    {
        term = term.Trim();
        if (term.Length == 0) throw new UserError("请先选择或输入术语、短语或句子。");
        if (term.Length > MaxTerm) throw new UserError($"所选文字过长，请缩小到 {MaxTerm} 字以内。");
        if (context.Length > MaxContext) throw new UserError($"补充上下文最多 {MaxContext} 字。");
        return term;
    }
}

public sealed record Explanation(string Chinese, string English, string Definition, string Analogy, string Usage, string? Plain = null, string Meaning = "", string Ambiguity = "", string Principle = "", string Example = "", string Pitfalls = "")
{
    [JsonIgnore]
    public string CopyText => Plain ?? $"{Chinese}\n{English}\n\n核心定义\n{Definition}"
        + (Principle.Length > 0 ? $"\n\n原理与步骤\n{Principle}" : "")
        + $"\n\n通俗比喻\n{Analogy}"
        + (Example.Length > 0 ? $"\n\n具体例子\n{Example}" : "")
        + $"\n\n实际用途\n{Usage}"
        + (Pitfalls.Length > 0 ? $"\n\n常见误区与局限\n{Pitfalls}" : "")
        + (Meaning.Length > 0 ? $"\n\n当前含义\n{Meaning}" : "") + (Ambiguity.Length > 0 ? $"\n\n其他可能含义\n{Ambiguity}" : "");
}

public static class SettingsStore
{
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLens");
    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private sealed record Stored(string BaseUrl, string ProtectedKey, string Model);
    public static ApiSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) ?? throw new IOException();
            return new(data.BaseUrl, SecretProtection.Unprotect(data.ProtectedKey), data.Model);
        }
        catch { throw new UserError("无法读取已保存的配置。请重新填写并保存；查询密钥只可由保存时的 Windows 用户解密。"); }
    }
    public static void Save(ApiSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(DirectoryPath);
        var data = new Stored(settings.BaseUrl.Trim(), SecretProtection.Protect(settings.ApiKey.Trim()), settings.Model.Trim());
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}

public static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var success = encrypt
                ? CryptProtectData(ref input, "TermLens API Key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
