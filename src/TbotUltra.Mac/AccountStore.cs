using System.Text.RegularExpressions;
using TbotUltra.Core.Configuration;

namespace TbotUltra.Mac;

public sealed record AccountEntry(
    string Name,
    string Username,
    string Password,
    string ServerName,
    string ServerUrl,
    bool ManualLogin,
    bool ProxyEnabled,
    string ProxyServer);

/// <summary>
/// Reads and writes the engine's <c>.env</c> account file (same keys the Windows app and
/// <c>EnvAccountProvider</c> use), so accounts stay interchangeable between both front-ends.
/// </summary>
public sealed class AccountStore
{
    private const string AccountsKey = "TBOT_ACCOUNTS";
    private const string ActiveAccountKey = "TBOT_ACTIVE_ACCOUNT";
    private static readonly string[] AccountFields =
        ["USERNAME", "PASSWORD", "SERVER_NAME", "SERVER_URL", "MANUAL_LOGIN", "PROXY_ENABLED", "PROXY_SERVER", "NEVER_USE_OWN_IP"];

    private readonly object _sync = new();
    private readonly string _envPath;

    public AccountStore(string envPath)
    {
        _envPath = envPath;
    }

    public string ActiveAccountName()
    {
        lock (_sync)
        {
            var values = EnvFileParser.ReadValues(_envPath);
            var names = ParseNames(values);
            var active = values.GetValueOrDefault(ActiveAccountKey, string.Empty);
            return names.FirstOrDefault(name => string.Equals(name, active, StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault()
                ?? string.Empty;
        }
    }

    public List<AccountEntry> List()
    {
        lock (_sync)
        {
            var values = EnvFileParser.ReadValues(_envPath);
            return ParseNames(values).Select(name => Read(values, name)).ToList();
        }
    }

    public AccountEntry? Find(string name)
        => List().FirstOrDefault(account => string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates or updates an account. An empty password keeps the stored one.</summary>
    public AccountEntry Save(AccountEntry account, bool setActive)
    {
        var name = NormalizeName(account.Name);
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Account name must contain letters or digits.");
        }

        // No URL means "pick the game world from the lobby at login", like the Windows app's server picker.
        var chooseInLobby = string.IsNullOrWhiteSpace(account.ServerUrl);
        var serverUrl = chooseInLobby ? LobbyWorldSelectionDefaults.ServerUrl : NormalizeServerUrl(account.ServerUrl);
        if (string.IsNullOrWhiteSpace(account.Username))
        {
            throw new InvalidOperationException("Email / username is required.");
        }

        lock (_sync)
        {
            var values = EnvFileParser.ReadValues(_envPath);
            var names = ParseNames(values);
            var prefix = Prefix(name);
            var password = string.IsNullOrEmpty(account.Password)
                ? values.GetValueOrDefault($"{prefix}PASSWORD", string.Empty)
                : account.Password;
            if (!account.ManualLogin && string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("Password is required unless manual login is enabled.");
            }

            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }

            values[AccountsKey] = string.Join(",", names);
            values[$"{prefix}USERNAME"] = account.Username.Trim();
            values[$"{prefix}PASSWORD"] = password;
            var serverChanged = !string.Equals(values.GetValueOrDefault($"{prefix}SERVER_URL"), serverUrl, StringComparison.OrdinalIgnoreCase);
            if (chooseInLobby)
            {
                values[$"{prefix}SERVER_NAME"] = LobbyWorldSelectionDefaults.ServerName;
            }
            else if (serverChanged || string.IsNullOrWhiteSpace(values.GetValueOrDefault($"{prefix}SERVER_NAME")))
            {
                values[$"{prefix}SERVER_NAME"] = new Uri(serverUrl).Host;
            }

            values[$"{prefix}SERVER_URL"] = serverUrl;
            values[$"{prefix}MANUAL_LOGIN"] = account.ManualLogin ? "true" : "false";
            values[$"{prefix}PROXY_ENABLED"] = account.ProxyEnabled ? "true" : "false";
            values[$"{prefix}PROXY_SERVER"] = account.ProxyServer?.Trim() ?? string.Empty;
            if (setActive || string.IsNullOrWhiteSpace(values.GetValueOrDefault(ActiveAccountKey)))
            {
                values[ActiveAccountKey] = name;
            }

            Write(values);
            return Read(values, name);
        }
    }

    public void UpdateServer(string name, string serverName, string serverUrl)
    {
        lock (_sync)
        {
            var values = EnvFileParser.ReadValues(_envPath);
            var normalized = NormalizeServerUrl(serverUrl);
            values[$"{Prefix(name)}SERVER_URL"] = normalized;
            values[$"{Prefix(name)}SERVER_NAME"] = string.IsNullOrWhiteSpace(serverName) ? new Uri(normalized).Host : serverName.Trim();
            Write(values);
        }
    }

    public void SetActive(string name)
    {
        lock (_sync)
        {
            var values = EnvFileParser.ReadValues(_envPath);
            var match = ParseNames(values).FirstOrDefault(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Account '{name}' does not exist.");
            values[ActiveAccountKey] = match;
            Write(values);
        }
    }

    public void Delete(string name)
    {
        lock (_sync)
        {
            var values = EnvFileParser.ReadValues(_envPath);
            var names = ParseNames(values);
            names.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            values[AccountsKey] = string.Join(",", names);
            foreach (var field in AccountFields)
            {
                values.Remove($"{Prefix(name)}{field}");
            }

            if (string.Equals(values.GetValueOrDefault(ActiveAccountKey), name, StringComparison.OrdinalIgnoreCase))
            {
                values[ActiveAccountKey] = names.FirstOrDefault() ?? string.Empty;
            }

            Write(values);
        }
    }

    internal static string NormalizeName(string? raw)
        => Regex.Replace(raw ?? string.Empty, "[^A-Za-z0-9_]", string.Empty).ToLowerInvariant();

    internal static string NormalizeServerUrl(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            throw new InvalidOperationException("Server URL is required (for example https://ts1.x1.europe.travian.com).");
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = $"https://{value}";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"'{raw}' is not a valid https server URL.");
        }

        return $"{uri.Scheme}://{uri.Host}";
    }

    private static string Prefix(string name) => $"TBOT_{name.ToUpperInvariant()}_";

    private static List<string> ParseNames(Dictionary<string, string> values)
        => values.GetValueOrDefault(AccountsKey, string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static AccountEntry Read(Dictionary<string, string> values, string name)
    {
        var prefix = Prefix(name);
        return new AccountEntry(
            name,
            values.GetValueOrDefault($"{prefix}USERNAME", string.Empty),
            values.GetValueOrDefault($"{prefix}PASSWORD", string.Empty),
            values.GetValueOrDefault($"{prefix}SERVER_NAME", string.Empty),
            values.GetValueOrDefault($"{prefix}SERVER_URL", string.Empty),
            string.Equals(values.GetValueOrDefault($"{prefix}MANUAL_LOGIN"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(values.GetValueOrDefault($"{prefix}PROXY_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
            values.GetValueOrDefault($"{prefix}PROXY_SERVER", string.Empty));
    }

    private void Write(Dictionary<string, string> values)
    {
        var lines = values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={EnvFileParser.FormatValue(pair.Value)}");
        var tempPath = $"{_envPath}.tmp";
        File.WriteAllLines(tempPath, lines);
        if (!OperatingSystem.IsWindows())
        {
            // The file holds game credentials; keep it readable by the owner only.
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tempPath, _envPath, overwrite: true);
    }
}
