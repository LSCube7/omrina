using Omrina.Core;
using System.Text.Json.Nodes;

internal static class SchoolTemplateBundleRegression
{
    public static void Run()
    {
        var objectiveDefinition = CreateDefinition("offline-objective") with
        {
            Duplex = true,
            Questions = Enumerable.Range(1, 35)
                .Select(number => new SchoolQuestionDefinition(number, SchoolQuestionType.Choice, 2, "", ["甲", "乙", "丙", "丁"]))
                .ToArray()
        };
        VerifyRoundTrip(objectiveDefinition, "objective multi-page bundle");

        var subjectiveDefinition = CreateDefinition("offline-subjective") with
        {
            Mode = SchoolContentMode.WithQuestions,
            Questions =
            [
                new SchoolQuestionDefinition(11, SchoolQuestionType.Subjective, 8, "阅读材料后作答", null, 40),
                new SchoolQuestionDefinition(12, SchoolQuestionType.Subjective, 10, "说明理由", null, 45)
            ]
        };
        VerifyRoundTrip(subjectiveDefinition, "pure subjective bundle with question text");

        var bundle = SchoolTemplateBundle.Create(objectiveDefinition);
        var json = bundle.ToJson();
        var firstPageCode = bundle.Pages[0].PageCode;
        Check(bundle.FindPage(firstPageCode)?.Metadata == bundle.Pages[0].Metadata, "known short page code resolves through the bundle");
        Check(bundle.FindPage("OM1AAAAAAAAAAAAAAAAAAAAAAAAAA") is null, "unknown short page code remains unresolved");
        Check(json.Contains("\"schemaVersion\":1", StringComparison.Ordinal), "bundle is versioned");
        Check(!json.Contains("captureRecords", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("scanImages", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("studentData", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("answerKey", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("scoringRecords", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("secret", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("filePath", StringComparison.OrdinalIgnoreCase), "bundle excludes captured answers, grading records, secrets, and paths");

        ExpectReject(() => SchoolTemplateBundle.FromJson(TamperHash(json)), "modified bundle hash rejected");
        ExpectReject(() => SchoolTemplateBundle.FromJson(json.Replace(firstPageCode, "OM1AAAAAAAAAAAAAAAAAAAAAAAAAA", StringComparison.Ordinal)), "modified page identity rejected");
        var nullPage = JsonNode.Parse(json)!;
        nullPage["pages"]![0] = null;
        ExpectReject(() => SchoolTemplateBundle.FromJson(nullPage.ToJsonString()), "null page identity rejected");
        ExpectReject(() => SchoolTemplateBundle.FromJson(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)), "unknown bundle version rejected");
        ExpectReject(() => SchoolTemplateBundle.FromJson(json[..^1] + ",\"schemaVersion\":1}"), "duplicate bundle property rejected");
        ExpectReject(() => SchoolTemplateBundle.FromJson(json[..^1] + ",\"unexpected\":true}"), "unknown bundle property rejected");
        ExpectReject(() => SchoolTemplateBundle.FromJson(new string(' ', SchoolTemplateBundle.MaximumJsonBytes + 1)), "oversized bundle rejected");
    }

    private static void VerifyRoundTrip(SchoolSheetDefinition definition, string label)
    {
        var originalPages = SchoolAnswerSheet.Create(definition).Pages;
        var exported = SchoolTemplateBundle.Create(definition);
        var imported = SchoolTemplateBundle.FromJson(exported.ToJson());
        var regeneratedPages = SchoolAnswerSheet.Create(imported.Definition).Pages;

        Check(imported.Pages.Count == originalPages.Count, $"{label}: every page identity is included");
        Check(imported.Pages.Count == regeneratedPages.Count, $"{label}: imported definition regenerates all pages");
        for (var index = 0; index < imported.Pages.Count; index++)
        {
            var originalMetadata = originalPages[index].SchoolMetadata!;
            var regeneratedMetadata = regeneratedPages[index].SchoolMetadata!;
            Check(imported.Pages[index].Metadata == originalMetadata, $"{label}: exported metadata matches page {index + 1}");
            Check(imported.Pages[index].Metadata == regeneratedMetadata, $"{label}: imported metadata matches page {index + 1}");
            Check(imported.Pages[index].PageCode == SchoolMachineCode.PageCode(regeneratedMetadata), $"{label}: short code maps back to page {index + 1}");
        }
    }

    private static SchoolSheetDefinition CreateDefinition(string id)
        => new()
        {
            ExamId = id,
            LayoutDocumentId = $"{id}-layout",
            Version = 3,
            Title = "离线资料包回归",
            CandidateIdentity = new(CandidateIdentityMode.Marking, 4),
            Questions = [new SchoolQuestionDefinition(1, SchoolQuestionType.Choice, 2, "", ["甲", "乙"])]
        };

    private static string TamperHash(string json)
    {
        const string marker = "\"hash\":\"";
        var index = json.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("bundle did not include its hash");
        }

        var hashIndex = index + marker.Length;
        var replacement = json[hashIndex] == '0' ? '1' : '0';
        var characters = json.ToCharArray();
        characters[hashIndex] = replacement;
        return new string(characters);
    }

    private static void ExpectReject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
