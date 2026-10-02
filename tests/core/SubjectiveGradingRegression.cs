using System.Text.Json.Nodes;
using Omrina.Core;

internal static class SubjectiveGradingRegression
{
    private static readonly DateTimeOffset StartTime = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        VerifyPixelRectanglesAndDefinitions();
        VerifyAtomicBatchAndVersioning();
        VerifyReviewLifecycleAndFinalSubtotal();
        VerifyResetAndReconfirmation();
        VerifyJsonRoundTripAndStrictRehydration();
        VerifyQuestionAndHistoryLimits();
    }

    private static void VerifyPixelRectanglesAndDefinitions()
    {
        var edgeRectangle = SubjectivePixelRectangle.Create(90, 70, 10, 30, 100, 100);
        AssertEqual(90, edgeRectangle.X, "pixel region should retain its x coordinate");
        AssertEqual(10, edgeRectangle.Width, "right edge may equal image width");

        AssertThrows<ArgumentOutOfRangeException>(
            () => SubjectivePixelRectangle.Create(-1, 0, 1, 1, 100, 100),
            "negative x coordinate");
        AssertThrows<ArgumentOutOfRangeException>(
            () => SubjectivePixelRectangle.Create(0, 0, 0, 1, 100, 100),
            "zero rectangle width");
        AssertThrows<ArgumentOutOfRangeException>(
            () => SubjectivePixelRectangle.Create(99, 0, 2, 1, 100, 100),
            "rectangle outside image width");
        AssertThrows<ArgumentOutOfRangeException>(
            () => SubjectivePixelRectangle.Create(int.MaxValue, 0, int.MaxValue, 1, int.MaxValue, 100),
            "rectangle coordinate addition overflow");
        AssertThrows<ArgumentOutOfRangeException>(
            () => SubjectivePixelRectangle.Create(0, 0, 1, 1, 0, 100),
            "zero image width");

        var firstId = Guid.NewGuid();
        var first = CreateRegion(firstId, 1, 10m);
        var second = CreateRegion(Guid.NewGuid(), 2, 20m);
        var snapshot = SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, [first, second], StartTime);
        AssertEqual(2, snapshot.Questions.Count, "start should retain each defined region");
        AssertEqual(SubjectiveReviewStatus.Unreviewed, snapshot.GetQuestion(1).Status, "new regions start unreviewed");

        AssertThrows<ArgumentException>(
            () => SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, [first, CreateRegion(firstId, 2, 10m)], StartTime),
            "duplicate question ID");
        AssertThrows<ArgumentException>(
            () => SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, [first, CreateRegion(Guid.NewGuid(), 1, 10m)], StartTime),
            "duplicate question number");
        AssertThrows<ArgumentException>(
            () => SubjectiveGradingSnapshot.Start("not-a-guid", 1000, 800, [first], StartTime),
            "invalid capture ID");
        AssertThrows<ArgumentOutOfRangeException>(
            () => CreateRegion(Guid.NewGuid(), 3, 0m),
            "zero maximum score");
        AssertThrows<ArgumentOutOfRangeException>(
            () => CreateRegion(Guid.NewGuid(), 3, SubjectiveRegionDefinition.MaximumPointsLimit + 1m),
            "maximum score above limit");
        AssertThrows<ArgumentOutOfRangeException>(
            () => SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, [CreateRegion(Guid.NewGuid(), 3, 1m, (SubjectivePixelRectangle)default)], StartTime),
            "default invalid pixel rectangle");
    }

    private static void VerifyAtomicBatchAndVersioning()
    {
        var regions = CreateTwoRegions();
        var original = SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, regions, StartTime);
        var edits = new[]
        {
            new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Draft, 8m, "说明一"),
            new SubjectiveGradeEdit(regions[1].QuestionId, SubjectiveReviewStatus.Draft, 17.5m, "说明二")
        };
        var edited = original.ApplyEdits(edits, "阅卷人甲", expectedVersion: 1, StartTime.AddMinutes(1));

        AssertEqual(1L, original.Version, "source snapshot version should not change");
        AssertEqual(0, original.History.Count, "source history should not change");
        AssertEqual(2L, edited.Version, "one batch should add exactly one version");
        AssertEqual(1, edited.History.Count, "one batch should add one history entry");
        AssertEqual(2, edited.History[0].Changes.Count, "one history entry should retain all question changes");
        AssertEqual(SubjectiveReviewStatus.Unreviewed, edited.History[0].Changes[0].Before.Status, "history should keep the original state");
        AssertEqual(SubjectiveReviewStatus.Draft, edited.History[0].Changes[0].After.Status, "history should keep the edited state");
        AssertEqual("阅卷人甲", edited.History[0].Reviewer, "history should retain the batch reviewer");
        AssertTrue(edited.FinalSubtotal is null, "draft scores must not produce a final subtotal");

        AssertThrows<InvalidOperationException>(
            () => edited.ApplyEdits(
                [new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Draft, 9m, null)],
                "阅卷人甲",
                expectedVersion: 1,
                StartTime.AddMinutes(2)),
            "stale expected version");
        AssertEqual(2L, edited.Version, "failed version check must not mutate the snapshot");
        AssertEqual(8m, edited.GetQuestion(1).Score, "failed version check must retain original score");

        AssertThrows<ArgumentOutOfRangeException>(
            () => original.ApplyEdits(
                [
                    new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Draft, 7m, null),
                    new SubjectiveGradeEdit(regions[1].QuestionId, SubjectiveReviewStatus.Draft, 21m, null)
                ],
                "阅卷人甲",
                expectedVersion: 1,
                StartTime.AddMinutes(1)),
            "invalid score in a batch");
        AssertEqual(1L, original.Version, "failed batch must leave the original version unchanged");
        AssertEqual(0, original.History.Count, "failed batch must not append partial history");

        AssertThrows<ArgumentException>(
            () => original.ApplyEdits(
                [
                    new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Draft, 7m, null),
                    new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Draft, 8m, null)
                ],
                "阅卷人甲",
                expectedVersion: 1,
                StartTime.AddMinutes(1)),
            "duplicate question in a batch");
        AssertEqual(1L, original.Version, "duplicate batch must not mutate the source");
    }

    private static void VerifyReviewLifecycleAndFinalSubtotal()
    {
        var regions = CreateTwoRegions();
        var unreviewed = SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, regions, StartTime);
        var draft = unreviewed.ApplyEdits(
            [
                new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Draft, 8m, "已批阅第一题"),
                new SubjectiveGradeEdit(regions[1].QuestionId, SubjectiveReviewStatus.Draft, 15m, null)
            ],
            "阅卷人甲",
            expectedVersion: 1,
            StartTime.AddMinutes(1));
        var firstConfirmed = draft.ApplyEdits(
            [new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Confirmed, 8m, "已批阅第一题")],
            "复核人乙",
            expectedVersion: 2,
            StartTime.AddMinutes(2));

        AssertEqual(SubjectiveReviewStatus.Confirmed, firstConfirmed.GetQuestion(1).Status, "draft should be confirmable");
        AssertTrue(!firstConfirmed.IsFinal, "one unconfirmed question keeps the subtotal provisional");
        AssertTrue(firstConfirmed.FinalSubtotal is null, "partial confirmation must not expose a subtotal");

        var final = firstConfirmed.ApplyEdits(
            [new SubjectiveGradeEdit(regions[1].QuestionId, SubjectiveReviewStatus.Confirmed, 15m, null)],
            "复核人乙",
            expectedVersion: 3,
            StartTime.AddMinutes(3));
        AssertTrue(final.IsFinal, "all confirmed questions should make the subtotal final");
        AssertEqual(23m, final.FinalSubtotal, "final subtotal should sum confirmed scores");
        AssertEqual(4L, final.Version, "each atomic confirmation batch should increment once");
        AssertEqual(3, final.History.Count, "every batch should remain in immutable history");
    }

    private static void VerifyResetAndReconfirmation()
    {
        var region = CreateRegion(Guid.NewGuid(), 1, 10m);
        var initial = SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, [region], StartTime);
        var draft = initial.ApplyEdits(
            [new SubjectiveGradeEdit(region.QuestionId, SubjectiveReviewStatus.Draft, 8m, "初评")],
            "阅卷人甲",
            1,
            StartTime.AddMinutes(1));
        var confirmed = draft.ApplyEdits(
            [new SubjectiveGradeEdit(region.QuestionId, SubjectiveReviewStatus.Confirmed, 8m, "初评")],
            "复核人乙",
            2,
            StartTime.AddMinutes(2));

        AssertThrows<ArgumentException>(
            () => confirmed.ApplyEdits(
                [new SubjectiveGradeEdit(region.QuestionId, SubjectiveReviewStatus.Confirmed, 7m, "改分")],
                "阅卷人甲",
                3,
                StartTime.AddMinutes(3)),
            "confirmed score cannot be changed while remaining confirmed");

        var reopened = confirmed.ApplyEdits(
            [new SubjectiveGradeEdit(region.QuestionId, SubjectiveReviewStatus.Draft, 7m, "复核后改分")],
            "阅卷人甲",
            3,
            StartTime.AddMinutes(3));
        AssertEqual(SubjectiveReviewStatus.Draft, reopened.GetQuestion(1).Status, "edited confirmed score must return to draft");
        AssertTrue(reopened.FinalSubtotal is null, "reopened grading must no longer have a final subtotal");
        AssertEqual(SubjectiveGradingAction.SetDraft, reopened.History[^1].Changes[0].Action, "reopening should be audited as a draft edit");

        var reconfirmed = reopened.ApplyEdits(
            [new SubjectiveGradeEdit(region.QuestionId, SubjectiveReviewStatus.Confirmed, 7m, "复核后改分")],
            "复核人丙",
            4,
            StartTime.AddMinutes(4));
        AssertEqual(7m, reconfirmed.FinalSubtotal, "reconfirmation should publish the updated score");

        var reset = reconfirmed.ApplyEdits(
            [new SubjectiveGradeEdit(region.QuestionId, SubjectiveReviewStatus.Unreviewed, null, null)],
            "阅卷人甲",
            5,
            StartTime.AddMinutes(5));
        AssertEqual(SubjectiveReviewStatus.Unreviewed, reset.GetQuestion(1).Status, "reset should clear the grade state");
        AssertTrue(reset.GetQuestion(1).Score is null && reset.GetQuestion(1).Comment is null, "reset should clear score and comment");
        AssertTrue(reset.FinalSubtotal is null, "reset must clear final status");
        AssertEqual(SubjectiveGradingAction.Reset, reset.History[^1].Changes[0].Action, "reset should be present in history");
    }

    private static void VerifyJsonRoundTripAndStrictRehydration()
    {
        var regions = CreateTwoRegions();
        var initial = SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, regions, StartTime);
        var draft = initial.ApplyEdits(
            [
                new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Draft, 8m, "保留评语"),
                new SubjectiveGradeEdit(regions[1].QuestionId, SubjectiveReviewStatus.Draft, 17m, null)
            ],
            "阅卷人甲",
            1,
            StartTime.AddMinutes(1));
        var final = draft.ApplyEdits(
            [
                new SubjectiveGradeEdit(regions[0].QuestionId, SubjectiveReviewStatus.Confirmed, 8m, "保留评语"),
                new SubjectiveGradeEdit(regions[1].QuestionId, SubjectiveReviewStatus.Confirmed, 17m, null)
            ],
            "复核人乙",
            2,
            StartTime.AddMinutes(2));
        var json = final.ToJson();
        var restored = SubjectiveGradingSnapshot.FromJson(json);

        AssertEqual(final.CaptureId, restored.CaptureId, "capture ID should round trip");
        AssertEqual(final.Version, restored.Version, "version should round trip");
        AssertEqual(final.UpdatedAtUtc, restored.UpdatedAtUtc, "updated time should round trip");
        AssertEqual(final.FinalSubtotal, restored.FinalSubtotal, "final subtotal should round trip");
        AssertSequenceEqual(final.Questions, restored.Questions, "current question snapshots should round trip");
        AssertEqual(final.History.Count, restored.History.Count, "all batch history should round trip");
        AssertSequenceEqual(final.History[0].Changes, restored.History[0].Changes, "before/after records should round trip");

        var wrongScore = JsonNode.Parse(json)!;
        wrongScore["questions"]![0]!["score"] = 99m;
        AssertThrows<ArgumentException>(
            () => SubjectiveGradingSnapshot.FromJson(wrongScore.ToJsonString()),
            "stored score outside maximum");

        var wrongHistory = JsonNode.Parse(json)!;
        wrongHistory["history"]![0]!["changes"]![0]!["after"]!["score"] = 7m;
        AssertThrows<ArgumentException>(
            () => SubjectiveGradingSnapshot.FromJson(wrongHistory.ToJsonString()),
            "history transition inconsistent with final snapshot");

        var noOpHistory = JsonNode.Parse(draft.ToJson())!.AsObject();
        var noOpBatch = (JsonObject)noOpHistory["history"]![0]!.DeepClone();
        noOpBatch["version"] = 3;
        noOpBatch["timestampUtc"] = StartTime.AddMinutes(2).ToString("O");
        var noOpChange = (JsonObject)noOpBatch["changes"]![0]!.DeepClone();
        var unchangedDraftState = noOpChange["after"]!.DeepClone();
        noOpChange["before"] = unchangedDraftState.DeepClone();
        noOpChange["after"] = unchangedDraftState;
        noOpBatch["changes"] = new JsonArray(noOpChange);
        noOpHistory["history"]!.AsArray().Add(noOpBatch);
        noOpHistory["version"] = 3;
        noOpHistory["updatedAtUtc"] = StartTime.AddMinutes(2).ToString("O");
        AssertThrows<ArgumentException>(
            () => SubjectiveGradingSnapshot.FromJson(noOpHistory.ToJsonString()),
            "no-op history transition");

        var wrongSubtotal = JsonNode.Parse(json)!;
        wrongSubtotal["finalSubtotal"] = 999m;
        AssertThrows<ArgumentException>(
            () => SubjectiveGradingSnapshot.FromJson(wrongSubtotal.ToJsonString()),
            "forged final subtotal");

        var missingHistory = JsonNode.Parse(json)!;
        missingHistory.AsObject().Remove("history");
        AssertThrows<ArgumentException>(
            () => SubjectiveGradingSnapshot.FromJson(missingHistory.ToJsonString()),
            "missing required snapshot history");

        AssertThrows<ArgumentOutOfRangeException>(
            () => SubjectiveGradingSnapshot.FromJson(json + new string(' ', SubjectiveGradingSnapshot.MaximumJsonBytes)),
            "oversized snapshot JSON");
    }

    private static void VerifyQuestionAndHistoryLimits()
    {
        var sixtyFourRegions = Enumerable.Range(1, SubjectiveGradingSnapshot.MaximumQuestionCount)
            .Select(
                questionNumber => CreateRegion(
                    Guid.NewGuid(),
                    questionNumber,
                    SubjectiveRegionDefinition.MaximumPointsLimit,
                    SubjectivePixelRectangle.Create(
                        ((questionNumber - 1) % 8) * 10,
                        ((questionNumber - 1) / 8) * 10,
                        5,
                        5,
                        100,
                        100)))
            .ToArray();
        var atLimit = SubjectiveGradingSnapshot.Start(CaptureId(), 100, 100, sixtyFourRegions, StartTime);
        AssertEqual(64, atLimit.Questions.Count, "64 regions should be accepted");
        AssertEqual(SubjectiveRegionDefinition.MaximumPointsLimit, atLimit.Questions[0].MaximumScore, "maximum allowed score should be accepted");

        AssertThrows<ArgumentOutOfRangeException>(
            () => SubjectiveGradingSnapshot.Start(
                CaptureId(),
                100,
                100,
                sixtyFourRegions.Append(CreateRegion(Guid.NewGuid(), 65, 1m)),
                StartTime),
            "65 regions");

        var oneRegion = CreateRegion(Guid.NewGuid(), 1, 1m);
        var snapshot = SubjectiveGradingSnapshot.Start(CaptureId(), 1000, 800, [oneRegion], StartTime);
        for (var index = 0; index < SubjectiveGradingSnapshot.MaximumHistoryBatchCount; index++)
        {
            var current = snapshot.GetQuestion(1);
            var edit = current.Status == SubjectiveReviewStatus.Unreviewed
                ? new SubjectiveGradeEdit(oneRegion.QuestionId, SubjectiveReviewStatus.Draft, 1m, null)
                : new SubjectiveGradeEdit(oneRegion.QuestionId, SubjectiveReviewStatus.Unreviewed, null, null);
            snapshot = snapshot.ApplyEdits(
                [edit],
                "阅卷人",
                snapshot.Version,
                StartTime.AddMinutes(index + 1));
        }

        AssertEqual(128, snapshot.History.Count, "128 history batches should be accepted");
        var atHistoryLimit = snapshot;
        AssertThrows<InvalidOperationException>(
            () => atHistoryLimit.ApplyEdits(
                [new SubjectiveGradeEdit(oneRegion.QuestionId, SubjectiveReviewStatus.Draft, 1m, null)],
                "阅卷人",
                atHistoryLimit.Version,
                StartTime.AddMinutes(129)),
            "129th history batch");
        AssertEqual(128, atHistoryLimit.History.Count, "failed history overflow must not truncate or append history");
    }

    private static IReadOnlyList<SubjectiveRegionDefinition> CreateTwoRegions()
    {
        return
        [
            CreateRegion(Guid.NewGuid(), 1, 10m, SubjectivePixelRectangle.Create(10, 10, 200, 80, 1000, 800)),
            CreateRegion(Guid.NewGuid(), 2, 20m, SubjectivePixelRectangle.Create(10, 100, 200, 80, 1000, 800))
        ];
    }

    private static SubjectiveRegionDefinition CreateRegion(
        Guid questionId,
        int questionNumber,
        decimal maximumScore,
        SubjectivePixelRectangle? rectangle = null)
    {
        return SubjectiveRegionDefinition.Create(
            questionId,
            questionNumber,
            rectangle ?? SubjectivePixelRectangle.Create(10, 10, 200, 80, 1000, 800),
            maximumScore,
            1000,
            800);
    }

    private static string CaptureId() => Guid.NewGuid().ToString("N");

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T? actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} for {message}.");
    }
}
