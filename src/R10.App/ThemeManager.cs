using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace R10.App;

public static class ThemeManager
{
    public static string Current { get; private set; } = "dark";
    public static void Apply(string theme)
    {
        Current = theme == "light" ? "light" : "dark";
        var dark = Current == "dark";
        var colors = new Dictionary<string, string>
        {
            ["Surface"] = dark ? "#212121" : "#FFFFFF", ["Sidebar"] = dark ? "#191919" : "#F7F7F8",
            ["Text"] = dark ? "#ECECEC" : "#292929", ["Muted"] = dark ? "#A2A2A2" : "#737373",
            ["Line"] = dark ? "#353535" : "#EBEBED", ["Hover"] = dark ? "#303030" : "#EEEEF0",
            ["Selected"] = dark ? "#333333" : "#E8E8EA", ["Control"] = dark ? "#282828" : "#FAFAFA",
            ["On"] = dark ? "#E5E5E5" : "#2B2B2B", ["OnText"] = dark ? "#292929" : "#FFFFFF",
            ["Off"] = dark ? "#4B4B4B" : "#D9D9DC", ["Knob"] = dark ? "#292929" : "#FFFFFF",
            ["Green"] = dark ? "#82B49A" : "#408466", ["Error"] = dark ? "#EEA298" : "#B24738",
            ["Overlay"] = dark ? "#A0000000" : "#66000000"
        };
        foreach (var (key, value) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze();
            Application.Current.Resources[key + "Brush"] = brush;
        }
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = Current == "dark" ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
