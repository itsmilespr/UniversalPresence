using System.Text.Json;
using UniversalPresence.Configuration;

namespace UniversalPresence.Infrastructure;

public static class ProfileService
{
    public static string DirectoryPath => Path.Combine(
        Path.GetDirectoryName(AppOptions.DefaultConfigPath) ?? "",
        "profiles");

    public static string PathFor(string name) =>
        Path.Combine(DirectoryPath, Sanitize(name) + ".json");

    public static IReadOnlyList<string> List()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var names = Directory.EnumerateFiles(DirectoryPath, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToList();
            if (!names.Contains("default", StringComparer.OrdinalIgnoreCase))
            {
                names.Insert(0, "default");
            }
            return names;
        }
        catch
        {
            return new[] { "default" };
        }
    }

    public static bool Exists(string name) =>
        string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)
        || File.Exists(PathFor(name));

    public static void Save(AppOptions options, string name)
    {
        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
        {
            options.ActiveProfile = "default";
            options.ConfigPath = AppOptions.DefaultConfigPath;
            options.Save();
            return;
        }

        Directory.CreateDirectory(DirectoryPath);
        options.ActiveProfile = name;
        options.ConfigPath = PathFor(name);
        File.WriteAllText(options.ConfigPath, JsonSerializer.Serialize(options, AppOptions.SerializerOptionsPublic));
    }

    public static AppOptions Load(string name)
    {
        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
        {
            var options = AppOptions.Load(new Cli.CliOptions());
            options.ActiveProfile = "default";
            options.ConfigPath = AppOptions.DefaultConfigPath;
            return options;
        }

        var path = PathFor(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Profile not found.", path);
        }

        var json = File.ReadAllText(path);
        var options2 = JsonSerializer.Deserialize<AppOptions>(json, AppOptions.SerializerOptionsPublic) ?? new AppOptions();
        options2.ActiveProfile = name;
        options2.ConfigPath = path;
        options2.Normalize();
        return options2;
    }

    public static void Delete(string name)
    {
        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try { File.Delete(PathFor(name)); } catch { }
    }

    public static bool Switch(AppOptions current, string name)
    {
        if (!Exists(name)) return false;

        try
        {
            // Persist the current profile first.
            Save(current, string.IsNullOrWhiteSpace(current.ActiveProfile) ? "default" : current.ActiveProfile);
        }
        catch
        {
            // Best effort: switching is more important than a failed save.
        }

        var loaded = Load(name);
        CopyInto(current, loaded);
        return true;
    }

    private static void CopyInto(AppOptions target, AppOptions source)
    {
        foreach (var property in typeof(AppOptions).GetProperties())
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            if (property.Name is "ConfigPath" or "SecretsPath") continue;
            property.SetValue(target, property.GetValue(source));
        }
        target.ConfigPath = source.ConfigPath;
        target.ActiveProfile = source.ActiveProfile;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "profile" : name.Trim();
    }
}