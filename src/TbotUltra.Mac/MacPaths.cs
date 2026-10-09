using System.Reflection;

namespace TbotUltra.Mac;

/// <summary>
/// Resolves the data root the engine treats as its "project root". The macOS app bundle is read-only,
/// so accounts, settings, browser state and logs live under Application Support instead.
/// </summary>
public static class MacPaths
{
    public static string ResolveDataRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("TBOT_ROOT");
        var root = !string.IsNullOrWhiteSpace(overridden)
            ? Path.GetFullPath(overridden)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "TbotUltra");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));

        var botConfigPath = Path.Combine(root, "config", "bot.json");
        if (!File.Exists(botConfigPath))
        {
            File.WriteAllText(botConfigPath, ReadEmbeddedText("TbotUltra.Mac.Data.bot.json"));
        }

        return root;
    }

    public static string ReadEmbeddedText(string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
