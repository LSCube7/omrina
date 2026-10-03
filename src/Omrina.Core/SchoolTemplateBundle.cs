using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omrina.Core;

/// <summary>A short on-paper page code and its complete offline identity mapping.</summary>
public sealed record SchoolTemplateBundlePage(
    [property: JsonRequired] string PageCode,
    [property: JsonRequired] SchoolPageMetadata Metadata);

/// <summary>
/// A portable, offline copy of one immutable school sheet definition and every page identity.
/// It contains no scan, answer-key, or scoring records.
/// </summary>
public sealed class SchoolTemplateBundle
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumJsonBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private SchoolTemplateBundle(SchoolSheetDefinition definition, IReadOnlyList<SchoolTemplateBundlePage> pages)
    {
        Definition = definition;
        Pages = pages;
    }

    public SchoolSheetDefinition Definition { get; }

    public IReadOnlyList<SchoolTemplateBundlePage> Pages { get; }

    public static SchoolTemplateBundle Create(SchoolSheetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var sheet = SchoolAnswerSheet.Create(definition);
        if (sheet.Pages.Count == 0 || sheet.Pages[0].SchoolDefinition is not { } canonicalDefinition)
        {
            throw new ArgumentException("考试资料没有可导出的页面。", nameof(definition));
        }

        var pages = sheet.Pages
            .Select(page =>
            {
                var metadata = page.SchoolMetadata
                    ?? throw new ArgumentException("考试资料中的页面身份不完整。", nameof(definition));
                return new SchoolTemplateBundlePage(SchoolMachineCode.PageCode(metadata), metadata);
            })
            .ToArray();
        EnsureUniquePageCodes(pages);
        return new SchoolTemplateBundle(canonicalDefinition, Array.AsReadOnly(pages));
    }

    public string ToJson()
    {
        var payload = new BundlePayload(CurrentSchemaVersion, Definition, Pages);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var hash = Hash(payloadJson);
        var json = JsonSerializer.Serialize(new BundleDocument(CurrentSchemaVersion, Definition, Pages, hash), JsonOptions);
        EnsureJsonSize(json);
        return json;
    }

    public static SchoolTemplateBundle FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        EnsureJsonSize(json);

        BundleDocument document;
        try
        {
            using var parsedJson = JsonDocument.Parse(json);
            ValidateNoDuplicateProperties(parsedJson.RootElement);
            document = JsonSerializer.Deserialize<BundleDocument>(json, JsonOptions)
                ?? throw new ArgumentException("考试资料包内容为空。", nameof(json));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("考试资料包格式无效。", nameof(json), exception);
        }

        if (document.SchemaVersion != CurrentSchemaVersion
            || document.Definition is null
            || document.Pages is null
            || document.Pages.Any(page => page is null || string.IsNullOrEmpty(page.PageCode) || page.Metadata is null)
            || !IsHash(document.Hash))
        {
            throw new ArgumentException("考试资料包版本、页面映射或完整性摘要无效，页面映射不能缺失或为空。", nameof(json));
        }

        SchoolTemplateBundle rebuilt;
        try
        {
            rebuilt = Create(document.Definition);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException("考试资料包中的答题纸定义无效。", nameof(json), exception);
        }

        if (!DefinitionsEqual(document.Definition, rebuilt.Definition)
            || !PagesEqual(document.Pages, rebuilt.Pages))
        {
            throw new ArgumentException("考试资料包的页面身份与答题纸定义不一致。", nameof(json));
        }

        var payload = new BundlePayload(document.SchemaVersion, document.Definition, document.Pages);
        var actualHash = Hash(JsonSerializer.Serialize(payload, JsonOptions));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(document.Hash),
                Convert.FromHexString(actualHash)))
        {
            throw new ArgumentException("考试资料包完整性校验失败。", nameof(json));
        }

        return rebuilt;
    }

    /// <summary>Returns the matching page mapping, or null for a valid but unknown short code.</summary>
    public SchoolTemplateBundlePage? FindPage(string pageCode)
    {
        if (SchoolMachineCode.ParsePageCode(pageCode) is null)
        {
            return null;
        }

        return Pages.SingleOrDefault(page => string.Equals(page.PageCode, pageCode, StringComparison.Ordinal));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static string Hash(string json)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    private static bool IsHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool DefinitionsEqual(SchoolSheetDefinition first, SchoolSheetDefinition second)
        => string.Equals(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonSerializer.Serialize(second, JsonOptions),
            StringComparison.Ordinal);

    private static bool PagesEqual(
        IReadOnlyList<SchoolTemplateBundlePage> first,
        IReadOnlyList<SchoolTemplateBundlePage> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (!string.Equals(first[index].PageCode, second[index].PageCode, StringComparison.Ordinal)
                || first[index].Metadata != second[index].Metadata)
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureUniquePageCodes(IReadOnlyList<SchoolTemplateBundlePage> pages)
    {
        var identitiesByPageCode = new Dictionary<string, SchoolPageMetadata>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (!identitiesByPageCode.TryAdd(page.PageCode, page.Metadata))
            {
                throw new InvalidOperationException("考试资料中有重复的页面短标识。请重新创建答题纸版本。");
            }
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException("考试资料包不能包含重复字段。", nameof(element));
                }

                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item);
            }
        }
    }

    private static void EnsureJsonSize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
        {
            throw new ArgumentException("考试资料包超过 1 MB 上限。", nameof(json));
        }
    }

    private sealed record BundlePayload(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] SchoolSheetDefinition Definition,
        [property: JsonRequired] IReadOnlyList<SchoolTemplateBundlePage> Pages);

    private sealed record BundleDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] SchoolSheetDefinition Definition,
        [property: JsonRequired] IReadOnlyList<SchoolTemplateBundlePage> Pages,
        [property: JsonRequired] string Hash);
}
