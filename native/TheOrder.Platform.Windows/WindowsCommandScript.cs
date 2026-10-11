using System.Text;
using TheOrder.Application;
namespace TheOrder.Platform.Windows;

internal sealed record WindowsCommandScript(string Interpreter, string Command, string EnvironmentBlock)
{
    public static WindowsCommandScript Build(string script, IEnumerable<string> arguments)
    {
        var values = new[] { script }.Concat(arguments).ToArray();
        // Quotes/newlines cannot be represented as literal CMD arguments without changing
        // script parsing. Never silently turn a structured argument into command syntax.
        if (values.Any(v => v.IndexOfAny(['"', '\r', '\n', '\0']) >= 0) || values.Sum(v => v.Length + 3) > 8000)
            throw new LaunchValidationException(LaunchOutcome.LaunchFailed, UiText.Get("cmd 的路径和参数不能含引号或换行，且总长度不能超过 8000 字符。请在安装设置中修改参数，或选择游戏 exe。"));
        var interpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var prefix = "THE_ORDER_SCRIPT_" + Guid.NewGuid().ToString("N") + "_";
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables()) environment[(string)pair.Key] = (string)pair.Value!;
        var invocation = new List<string>();
        for (int i = 0; i < values.Length; i++)
        {
            var name = prefix + i;
            var value = values[i];
            // Keep the standard Windows argument spelling when a script forwards %* to
            // a native game: a closing quote must not be consumed by a trailing backslash.
            if (i > 0) value += new string('\\', value.Length - value.TrimEnd('\\').Length);
            environment[name] = value; invocation.Add("\"%" + name + "%\"");
        }
        // Expand each value once from the child's private environment. Percent signs in
        // filenames/arguments are not re-expanded; delayed expansion and AutoRun are off.
        var command = "\"" + interpreter + "\" /d /v:off /s /c \"" + string.Join(' ', invocation) + "\"";
        var block = new StringBuilder();
        if (environment.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(pair => pair.Value.Length + 3) > 8000)
            throw new LaunchValidationException(LaunchOutcome.LaunchFailed, UiText.Get("cmd 参数在转义后超过长度限制，请缩短参数或选择游戏 exe。"));
        foreach (var pair in environment) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        block.Append('\0');
        return new(interpreter, command, block.ToString());
    }
}
