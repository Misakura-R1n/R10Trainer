using System.IO;
using System.Text.Json;
using System.Windows;

namespace R10.App;

public sealed class AppSettings
{
    public string Theme { get; set; } = "dark";
    public double Width { get; set; } = 1060;
    public double Height { get; set; } = 860;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
    public string? GameDirectory { get; set; }
    private static string PathFor(bool demo) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R10Trainer", demo ? "demo-settings.json" : "settings.json");
    public static AppSettings Load(bool demo)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFor(demo))) ?? new();
            value.Theme = value.Theme == "light" ? "light" : "dark";
            value.Width = double.IsFinite(value.Width) ? Math.Clamp(value.Width, 600, 3000) : 1060;
            value.Height = double.IsFinite(value.Height) ? Math.Clamp(value.Height, 480, 2000) : 810;
            if (value.Left is { } left && !double.IsFinite(left)) value.Left = null;
            if (value.Top is { } top && !double.IsFinite(top)) value.Top = null;
            if (string.IsNullOrWhiteSpace(value.GameDirectory) || !Path.IsPathFullyQualified(value.GameDirectory)) value.GameDirectory = null;
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(bool demo)
    {
        var path = PathFor(demo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
