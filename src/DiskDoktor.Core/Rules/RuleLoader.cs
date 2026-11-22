using System.Text.Json;
using System.Text.Json.Serialization;
using DiskDoktor.Core.Platform;

namespace DiskDoktor.Core.Rules;

public sealed class RuleValidationException(string message) : Exception(message);

public static class RuleLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<CleanupRule> LoadBuiltIn()
    {
        using var stream = typeof(RuleLoader).Assembly.GetManifestResourceStream("DiskDoktor.Core.rules.json")
            ?? throw new InvalidOperationException("Gömülü rules.json bulunamadı.");
        return Parse(stream, allowCommands: true);
    }

    /// <param name="allowCommands">Only built-in rules may run programs; user files are data, never code.</param>
    public static IReadOnlyList<CleanupRule> Parse(Stream json, bool allowCommands = false)
    {
        List<CleanupRule>? rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<CleanupRule>>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new RuleValidationException($"Kural dosyası okunamadı: {ex.Message}");
        }
        rules ??= [];
        Validate(rules, allowCommands);
        return rules;
    }

    public static IReadOnlyList<CleanupRule> Parse(string json, bool allowCommands = false)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return Parse(ms, allowCommands);
    }

    /// <summary>
    /// Built-in rules plus the optional user file (same JSON shape). A user rule with an existing id replaces it.
    /// A broken user file is reported via <paramref name="warning"/> and ignored, never fatal.
    /// </summary>
    public static IReadOnlyList<CleanupRule> LoadAll(ISystemEnvironment env, out string? warning)
    {
        warning = null;
        var byId = LoadBuiltIn().ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var userFile = AppPaths.UserRulesFile(env);
        if (File.Exists(userFile))
        {
            try
            {
                using var fs = File.OpenRead(userFile);
                foreach (var r in Parse(fs)) byId[r.Id] = r;
            }
            catch (Exception ex) when (ex is RuleValidationException or IOException or UnauthorizedAccessException)
            {
                warning = $"Kullanıcı kural dosyası yok sayıldı ({userFile}): {ex.Message}";
            }
        }
        return byId.Values.Where(r => r.AppliesTo(env.Os)).ToList();
    }

    internal static void Validate(IReadOnlyList<CleanupRule> rules, bool allowCommands)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) throw new RuleValidationException("Kimliği boş bir kural var.");
            if (!seen.Add(r.Id)) throw new RuleValidationException($"Yinelenen kural kimliği: {r.Id}");
            if (string.IsNullOrWhiteSpace(r.Name)) throw new RuleValidationException($"{r.Id}: ad boş.");
            if (r.Kind == TargetKind.Command)
            {
                if (!allowCommands) throw new RuleValidationException($"{r.Id}: komut kuralları yalnız yerleşik olabilir.");
                if (r.Command.Length == 0 && r.Builtin is null) throw new RuleValidationException($"{r.Id}: komut boş.");
            }
            else if (r.Command.Length > 0 || r.Builtin is not null)
                throw new RuleValidationException($"{r.Id}: komut yalnız Command türünde kullanılabilir.");
            else if (r.Paths.Length == 0) throw new RuleValidationException($"{r.Id}: en az bir yol gerekli.");
            if (r.MinAgeDays < 0) throw new RuleValidationException($"{r.Id}: minAgeDays negatif olamaz.");
            if (r.Kind == TargetKind.Files && r.Patterns.Length == 0)
                throw new RuleValidationException($"{r.Id}: Files türü için en az bir desen gerekli.");
            foreach (var p in r.Paths.Concat(r.SizePaths))
            {
                if (string.IsNullOrWhiteSpace(p)) throw new RuleValidationException($"{r.Id}: boş yol.");
                if (p.Replace('\\', '/').Split('/').Any(s => s == ".."))
                    throw new RuleValidationException($"{r.Id}: yolda '..' kullanılamaz ({p}).");
            }
            foreach (var pat in r.Patterns)
                if (pat.Contains('/') || pat.Contains('\\'))
                    throw new RuleValidationException($"{r.Id}: desen klasör ayırıcı içeremez ({pat}).");
        }
    }
}
