using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Omrina.Core;

namespace Omrina.Desktop;

/// <summary>App-owned immutable exam layouts and shared answer keys. Never exposed through a grant catalog.</summary>
public sealed class SchoolExamStore
{
    private readonly string _directory;
    private readonly object _gate = new();
    private const int MaximumDocumentBytes = 1024 * 1024;

    public SchoolExamStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _directory = Path.Combine(Path.GetFullPath(rootDirectory), "school-exams");
    }

    public SchoolSheetDefinition SaveDefinition(SchoolSheetDefinition definition)
    {
        var firstPage = SchoolAnswerSheet.Create(definition).Pages[0];
        var snapshot = firstPage.ToJson();
        var path = DefinitionPath(definition.ExamId, definition.LayoutDocumentId, definition.Version);
        lock (_gate)
        {
            EnsureDirectory();
            if (File.Exists(path))
            {
                var existing = ReadLayout(path);
                if (existing.ToJson() != snapshot)
                    throw new InvalidOperationException("此答题纸版本已保存。请增加版本号后保存修改，历史版本不能覆盖。");
                return existing.SchoolDefinition!;
            }
            WriteAtomic(path, snapshot, overwrite: false);
            return firstPage.SchoolDefinition!;
        }
    }

    public IReadOnlyList<SchoolSheetDefinition> ListDefinitions()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return Array.Empty<SchoolSheetDefinition>();
            RejectLink(_directory);
            return Directory.EnumerateFiles(_directory, "*.layout.json")
                .Select(path => ReadLayout(path).SchoolDefinition!)
                .OrderBy(definition => definition.Title, StringComparer.Ordinal)
                .ThenByDescending(definition => definition.Version).ToArray();
        }
    }

    public SchoolSheetDefinition? GetDefinition(string examId, string layoutDocumentId, int version)
    {
        var path = DefinitionPath(examId, layoutDocumentId, version);
        lock (_gate) return File.Exists(path) ? ReadLayout(path).SchoolDefinition : null;
    }

    public void SaveAnswerKey(SchoolSheetDefinition definition, IReadOnlyDictionary<int, string> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var canonical = GetDefinition(definition.ExamId, definition.LayoutDocumentId, definition.Version)
            ?? throw new InvalidOperationException("请先保存答题纸版本，再设置标准答案。");
        var choice = SchoolAnswerSheet.Create(canonical).Pages.SelectMany(page => page.Questions).ToArray();
        if (answers.Count != choice.Length || choice.Any(question =>
            !answers.TryGetValue(question.Number, out var answer) || !question.Bubbles.Any(bubble => bubble.OptionLabel == answer)))
            throw new ArgumentException("标准答案必须完整包含该考试的选择题，并使用题目中的合法选项。", nameof(answers));
        var json = JsonSerializer.Serialize(answers.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value));
        lock (_gate)
        {
            EnsureDirectory();
            WriteAtomic(AnswerPath(canonical), json, overwrite: true);
        }
    }

    public IReadOnlyDictionary<int, string>? ReadAnswerKey(SchoolSheetDefinition definition)
    {
        var canonical = GetDefinition(definition.ExamId, definition.LayoutDocumentId, definition.Version);
        if (canonical is null) return null;
        var path = AnswerPath(canonical);
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            var answers = JsonSerializer.Deserialize<Dictionary<int, string>>(ReadBounded(path))
                ?? throw new InvalidOperationException("考试标准答案已损坏。");
            var choice = SchoolAnswerSheet.Create(canonical).Pages.SelectMany(page => page.Questions).ToArray();
            if (answers.Count != choice.Length || choice.Any(question => !answers.TryGetValue(question.Number, out var answer)
                || !question.Bubbles.Any(bubble => bubble.OptionLabel == answer)))
                throw new InvalidOperationException("考试标准答案与已保存的答题纸版本不一致。");
            return new ReadOnlyDictionary<int, string>(answers);
        }
    }

    private string DefinitionPath(string examId, string documentId, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { examId, documentId, version }))));
        return Path.Combine(_directory, key + ".layout.json");
    }

    private string AnswerPath(SchoolSheetDefinition definition) =>
        DefinitionPath(definition.ExamId, definition.LayoutDocumentId, definition.Version).Replace(".layout.json", ".answers.json", StringComparison.Ordinal);

    private AnswerSheetLayout ReadLayout(string path)
    {
        RejectLink(_directory);
        var layout = AnswerSheetLayout.FromJson(ReadBounded(path));
        var definition = layout.SchoolDefinition ?? throw new InvalidOperationException("考试答题纸版本无效。");
        if (!string.Equals(path, DefinitionPath(definition.ExamId, definition.LayoutDocumentId, definition.Version), StringComparison.OrdinalIgnoreCase)
            || layout.SchoolPageIndex != 0)
            throw new InvalidOperationException("考试答题纸版本与保存位置不一致。");
        return layout;
    }

    private static string ReadBounded(string path)
    {
        RejectLink(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumDocumentBytes) throw new InvalidOperationException("考试资料大小无效。");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }

    private void EnsureDirectory()
    {
        RejectLink(Path.GetDirectoryName(_directory)!);
        Directory.CreateDirectory(_directory);
        RejectLink(_directory);
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("考试资料路径无效，无法安全访问。");
    }

    private static void WriteAtomic(string path, string json, bool overwrite)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes) throw new InvalidOperationException("考试资料过大。");
        RejectLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
