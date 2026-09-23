using System.Collections.ObjectModel;

namespace Omrina.Core;

/// <summary>
/// The expected answer for every question on one answer-sheet template.
/// An answer key is an immutable, complete snapshot: every question number
/// from one through <see cref="QuestionCount"/> must have one option label.
/// </summary>
public sealed class AnswerKey
{
    private const string OptionLabels = "ABCDEF";
    private readonly IReadOnlyDictionary<int, string> answers;

    private AnswerKey(
        int questionCount,
        int optionsPerQuestion,
        IReadOnlyDictionary<int, string> answers)
    {
        QuestionCount = questionCount;
        OptionsPerQuestion = optionsPerQuestion;
        this.answers = answers;
    }

    public int QuestionCount { get; }

    public int OptionsPerQuestion { get; }

    /// <summary>Returns the expected option label for each one-based question number.</summary>
    public IReadOnlyDictionary<int, string> Answers => answers;

    public string this[int questionNumber] => answers[questionNumber];

    public bool TryGetAnswer(int questionNumber, out string answer)
    {
        return answers.TryGetValue(questionNumber, out answer!);
    }

    /// <summary>
    /// Creates a complete answer key for a supported one-page template.
    /// Option labels are the uppercase labels printed by <see cref="AnswerSheetLayout"/>.
    /// </summary>
    public static AnswerKey Create(
        int questionCount,
        int optionsPerQuestion,
        IReadOnlyDictionary<int, string> answers)
    {
        if (questionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(questionCount),
                questionCount,
                "题数必须大于零。");
        }

        ValidateOptionsPerQuestion(optionsPerQuestion);
        var maximumQuestionCount = AnswerSheetLayout.MaxQuestionCount(optionsPerQuestion);
        if (questionCount > maximumQuestionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(questionCount),
                questionCount,
                $"每题 {optionsPerQuestion} 个选项的答题纸最多支持 {maximumQuestionCount} 题。");
        }

        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count != questionCount)
        {
            throw new ArgumentException(
                $"答案表必须完整包含 {questionCount} 道题，实际包含 {answers.Count} 道题。",
                nameof(answers));
        }

        var copy = new Dictionary<int, string>(questionCount);
        for (var questionNumber = 1; questionNumber <= questionCount; questionNumber++)
        {
            if (!answers.TryGetValue(questionNumber, out var answer))
            {
                throw new ArgumentException(
                    $"答案表缺少第 {questionNumber} 题。",
                    nameof(answers));
            }

            if (answer is null)
            {
                throw new ArgumentException(
                    $"第 {questionNumber} 题的答案不能为空。",
                    nameof(answers));
            }

            if (!IsValidOptionLabel(answer, optionsPerQuestion))
            {
                throw new ArgumentException(
                    $"第 {questionNumber} 题的答案“{answer}”不是 2–6 个选项中的合法大写选项标签。",
                    nameof(answers));
            }

            copy.Add(questionNumber, answer);
        }

        // The count check above plus the complete one-based loop rejects keys
        // with out-of-range question numbers as well as missing question numbers.
        if (answers.Keys.Any(questionNumber => questionNumber < 1 || questionNumber > questionCount))
        {
            throw new ArgumentException(
                "答案表包含超出题数范围的题号。",
                nameof(answers));
        }

        return new AnswerKey(
            questionCount,
            optionsPerQuestion,
            new ReadOnlyDictionary<int, string>(copy));
    }

    internal static bool IsValidOptionLabel(string? label, int optionsPerQuestion)
    {
        if (string.IsNullOrEmpty(label)
            || label.Length != 1
            || optionsPerQuestion < AnswerSheetLayout.MinOptionsPerQuestion
            || optionsPerQuestion > AnswerSheetLayout.MaxOptionsPerQuestion)
        {
            return false;
        }

        var optionIndex = label[0] - 'A';
        return optionIndex >= 0 && optionIndex < optionsPerQuestion;
    }

    internal static void ValidateOptionsPerQuestion(int optionsPerQuestion)
    {
        if (optionsPerQuestion < AnswerSheetLayout.MinOptionsPerQuestion
            || optionsPerQuestion > AnswerSheetLayout.MaxOptionsPerQuestion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(optionsPerQuestion),
                optionsPerQuestion,
                $"每题选项数必须在 {AnswerSheetLayout.MinOptionsPerQuestion} 到 {AnswerSheetLayout.MaxOptionsPerQuestion} 之间。");
        }
    }

    internal static string OptionLabelForIndex(int zeroBasedOptionIndex)
    {
        if ((uint)zeroBasedOptionIndex >= OptionLabels.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedOptionIndex));
        }

        return OptionLabels[zeroBasedOptionIndex].ToString();
    }
}
