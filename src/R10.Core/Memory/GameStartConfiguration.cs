using System.Text.RegularExpressions;

namespace R10.Core.Memory;

public static class GameStartConfiguration
{
    public static void ValidateCodeName(string text)
    {
        var declarations = Regex.Matches(text, @"(?mi)^[ \t]*CodeName[ \t]*=");
        var value = Regex.Match(text, """(?m)^[ \t]*CodeName[ \t]*=[ \t]*"([^"\r\n]+)"[ \t]*(?:;[^\r\n]*)?\r?$""");
        if (declarations.Count != 1 || !value.Success ||
            !string.Equals(value.Groups[1].Value, "Rance10.ain", StringComparison.OrdinalIgnoreCase))
            throw new IOException("启动配置必须且只能声明一次 CodeName = \"Rance10.ain\"。");
    }
}
