using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace OrderMail.Core.Settings;

public enum AiProvider
{
    RuleBased,
    OpenAiCompatible,
}

public enum MailSourceKind
{
    EmlFolder,
    Imap,
}

public sealed class AppSettings
{
    public AiSettings Ai { get; set; } = new();
    public MailSourceSettings Mail { get; set; } = new();

    /// <summary>Results at or above this confidence are registered (or dismissed) without human review.</summary>
    public double AutoRegisterThreshold { get; set; } = 0.8;
}

public sealed class AiSettings
{
    public AiProvider Provider { get; set; } = AiProvider.RuleBased;
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "qwen2.5:7b";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public bool UseJsonSchema { get; set; } = true;
}

public sealed class MailSourceSettings
{
    public MailSourceKind Kind { get; set; } = MailSourceKind.EmlFolder;
    public string EmlFolder { get; set; } = "samples/mails";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public string ImapUser { get; set; } = "";
    public string ImapPassword { get; set; } = "";
    public string ImapFolder { get; set; } = "INBOX";
    public int ImapSinceDays { get; set; } = 30;
    public bool AutoPoll { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;
}

/// <summary>Persists settings as JSON; secrets are encrypted for the current Windows user.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _lock = new();
    private AppSettings _current;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public event Action? Changed;

    /// <summary>Returns a copy; edit it and pass it to <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_lock) return Clone(_current);
        }
    }

    public void Save(AppSettings settings)
    {
        var stored = Clone(settings);
        stored.Ai.ApiKey = SecretProtector.Protect(stored.Ai.ApiKey);
        stored.Mail.ImapPassword = SecretProtector.Protect(stored.Mail.ImapPassword);

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(stored, JsonOptions));
            _current = Clone(settings);
        }
        Changed?.Invoke();
    }

    private static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        settings.Ai.ApiKey = SecretProtector.Unprotect(settings.Ai.ApiKey);
        settings.Mail.ImapPassword = SecretProtector.Unprotect(settings.Mail.ImapPassword);
        return settings;
    }

    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions)!;
}

public static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OrderMail");

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        if (!OperatingSystem.IsWindows()) return PlainPrefix + secret;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(bytes);
    }

    /// <summary>Returns "" when the value was encrypted by another user or machine.</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal)) return stored[PlainPrefix.Length..];
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[DpapiPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
