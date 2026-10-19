using System.Globalization;
using AwrTool.Core;

namespace AwrTool.Cli;

/// <summary>
/// 手写的命令行解析。不用 System.CommandLine：这个工具的参数面很小，
/// 自己写能把错误消息和校验时机控制到位，也没有额外依赖。
/// </summary>
/// <remarks>
/// 校验分层：能在解析阶段判死的错误（缺值、类型不对、选项不属于该命令）一律抛
/// <see cref="ExitCode.Usage"/>，绝不拖到连了数据库才报。互斥关系（如
/// <c>--last</c> 与 <c>--begin-snap</c>）留给 <see cref="SnapshotSelector"/>，
/// 因为那里才知道快照的实际语义。
/// </remarks>
public static class CliParser
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-o"] = "--out",
        ["-v"] = "--verbose",
        ["-h"] = "--help",
        ["--connect-string"] = "--connect",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--connect", "--dsn", "--user", "--password", "--dbid", "--timeout",
        "--type", "--format", "--last", "--begin-snap", "--end-snap",
        "--begin-time", "--end-time", "--inst-num", "--out", "--limit", "--since",
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "--quiet", "--verbose", "--overwrite", "--help", "--version",
    };

    /// <summary>解析命令行。任何用法问题都抛 <see cref="AwrToolException"/>。</summary>
    public static CliOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            throw AwrToolException.Usage("缺少子命令。" + Environment.NewLine + HelpText.Full);
        }

        var (command, start) = ParseCommand(args);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = start; i < args.Length; i++)
        {
            var token = args[i];

            if (token.Length == 0)
            {
                throw AwrToolException.Usage("出现空参数。" + HelpText.Brief);
            }

            if (!token.StartsWith('-'))
            {
                throw AwrToolException.Usage(
                    $"多余的参数 '{token}'。所有取值都必须通过 --选项 传入。" + HelpText.Brief);
            }

            var equals = token.IndexOf('=');
            var name = equals > 0 ? token[..equals] : token;
            var inlineValue = equals > 0 ? token[(equals + 1)..] : null;

            name = Aliases.TryGetValue(name, out var canonical) ? canonical : name;

            if (ValueOptions.Contains(name))
            {
                if (inlineValue is not null)
                {
                    values[name] = inlineValue;
                }
                else
                {
                    if (i + 1 >= args.Length)
                    {
                        throw AwrToolException.Usage($"选项 '{name}' 缺少取值。" + HelpText.Brief);
                    }

                    values[name] = args[++i];
                }
            }
            else if (FlagOptions.Contains(name))
            {
                if (inlineValue is not null)
                {
                    throw AwrToolException.Usage($"选项 '{name}' 不接受取值。");
                }

                flags.Add(name);
            }
            else
            {
                throw AwrToolException.Usage($"未知选项 '{name}'。" + HelpText.Brief);
            }
        }

        if (flags.Contains("--help") || command == CliCommand.Help)
        {
            return new CliOptions { Command = command, Help = true };
        }

        if (flags.Contains("--version"))
        {
            return new CliOptions { Command = CliCommand.Version };
        }

        ValidateOptionsBelongToCommand(command, values.Keys.Concat(flags));

        return Build(command, values, flags);
    }

    private static (CliCommand Command, int Start) ParseCommand(string[] args)
    {
        var first = args[0];

        if (first.StartsWith('-'))
        {
            return first switch
            {
                "-h" or "--help" => (CliCommand.Help, 0),
                "--version" => (CliCommand.Version, 0),
                _ => throw AwrToolException.Usage($"缺少子命令。" + HelpText.Brief),
            };
        }

        return first.ToLowerInvariant() switch
        {
            "report" => (CliCommand.Report, 1),
            "snapshots" => (CliCommand.Snapshots, 1),
            "doctor" => (CliCommand.Doctor, 1),
            "mcp" => (CliCommand.Mcp, 1),
            "version" => (CliCommand.Version, 1),
            "help" => (CliCommand.Help, 1),
            _ => throw AwrToolException.Usage(
                $"未知子命令 '{first}'。可用命令：report / snapshots / doctor / mcp / version / help。" + HelpText.Brief),
        };
    }

    private static readonly string[] ConnectionOptionNames =
        ["--connect", "--dsn", "--user", "--password", "--dbid", "--timeout", "--quiet", "--verbose"];

    private static readonly string[] ReportOptionNames =
    [
        "--type", "--format", "--last", "--begin-snap", "--end-snap",
        "--begin-time", "--end-time", "--inst-num", "--out", "--overwrite",
    ];

    private static readonly string[] SnapshotOptionNames = ["--inst-num", "--limit", "--since"];

    private static HashSet<string> AllowedFor(CliCommand command)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "--help" };

        if (command is CliCommand.Help)
        {
            return allowed;
        }

        allowed.UnionWith(ConnectionOptionNames);

        switch (command)
        {
            case CliCommand.Report:
                allowed.UnionWith(ReportOptionNames);
                break;
            case CliCommand.Snapshots:
                allowed.UnionWith(SnapshotOptionNames);
                break;
        }

        return allowed;
    }

    private static string CommandName(CliCommand command) => command switch
    {
        CliCommand.Report => "report",
        CliCommand.Snapshots => "snapshots",
        CliCommand.Doctor => "doctor",
        CliCommand.Mcp => "mcp",
        CliCommand.Version => "version",
        _ => "help",
    };

    private static void ValidateOptionsBelongToCommand(CliCommand command, IEnumerable<string> used)
    {
        var allowed = AllowedFor(command);

        foreach (var name in used)
        {
            if (allowed.Contains(name))
            {
                continue;
            }

            // 静默忽略会让调用方以为选项生效了，必须报错并说清它该用在哪。
            var owners = Enum.GetValues<CliCommand>()
                .Where(c => AllowedFor(c).Contains(name))
                .Select(CommandName)
                .ToArray();

            var hint = owners.Length == 0
                ? string.Empty
                : $"该选项只适用于 {string.Join(" / ", owners)} 命令。";

            throw AwrToolException.Usage(
                $"{name} 不能用在 {CommandName(command)} 上。" + hint + HelpText.Brief);
        }
    }

    private static CliOptions Build(
        CliCommand command, Dictionary<string, string> values, HashSet<string> flags) => new()
        {
            Command = command,
            ConnectString = GetString(values, "--connect"),
            Dsn = GetString(values, "--dsn"),
            User = GetString(values, "--user"),
            Password = GetString(values, "--password"),
            Dbid = GetLong(values, "--dbid"),
            TimeoutSeconds = GetInt(values, "--timeout") ?? 600,
            Quiet = flags.Contains("--quiet"),
            Verbose = flags.Contains("--verbose"),
            Type = ReportKindParser.Parse(GetString(values, "--type")),
            Format = ReportFormatParser.Parse(GetString(values, "--format")),
            Last = GetDuration(values, "--last"),
            BeginSnap = GetInt(values, "--begin-snap"),
            EndSnap = GetInt(values, "--end-snap"),
            BeginTime = GetTimestamp(values, "--begin-time"),
            EndTime = GetTimestamp(values, "--end-time"),
            Instances = GetInstances(values, "--inst-num"),
            Out = GetString(values, "--out"),
            Overwrite = flags.Contains("--overwrite"),
            Limit = GetInt(values, "--limit") ?? 24,
            Since = GetDuration(values, "--since"),
        };

    private static string? GetString(Dictionary<string, string> values, string name)
        => values.TryGetValue(name, out var raw) ? raw : null;

    private static int? GetInt(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            throw AwrToolException.Usage($"{name} 需要一个不小于 1 的整数，收到 '{raw}'。");
        }

        return parsed;
    }

    private static long? GetLong(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            throw AwrToolException.Usage($"{name} 需要一个不小于 1 的整数，收到 '{raw}'。");
        }

        return parsed;
    }

    private static TimeSpan? GetDuration(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var raw))
        {
            return null;
        }

        try
        {
            return Duration.Parse(raw);
        }
        catch (FormatException ex)
        {
            throw AwrToolException.Usage($"{name}: {ex.Message}", ex);
        }
    }

    private static DateTime? GetTimestamp(Dictionary<string, string> values, string name)
        => values.ContainsKey(name) ? Timestamp.Parse(values[name], name) : null;

    private static InstanceList? GetInstances(Dictionary<string, string> values, string name)
    {
        if (!values.TryGetValue(name, out var raw))
        {
            return null;
        }

        try
        {
            return InstanceList.Parse(raw);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw AwrToolException.Usage($"{name}: {ex.Message}", ex);
        }
    }
}
