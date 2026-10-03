using System.Text.Json;
using Omrina.Core;
using Omrina.Desktop;
using Omrina.Platform;
using Omrina.Protocol;
using Omrina.Scanning;
using SkiaSharp;
using ZXing.Common;

namespace Omrina.M3.Desktop.Tests;

internal static class SchoolGroupRegression
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "omrina-group-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var definition = new SchoolSheetDefinition
            {
                ExamId = "group-exam", LayoutDocumentId = "group-layout", Title = "题组回归",
                Questions = [new(31, SchoolQuestionType.Subjective, 7, SubjectiveHeightMm: 65),
                    new(42, SchoolQuestionType.Subjective, 9, SubjectiveHeightMm: 65)],
                Groups = [new("g.1", "阅读与解答", [31,42])]
            };
            var layout = SchoolAnswerSheet.Create(definition).Pages.Single();
            await VerifyPerspectiveAsync(root, layout);
            var path = Path.Combine(root, "paper.png");
            Render(layout, path);
            var captures = new CaptureStore(root);
            var capture = await captures.ImportAsync(layout, new LocalInputImageFile(path));
            var service = new LocalSubjectiveReviewService(captures);
            var review = await service.CreateAsync(capture.Manifest.CaptureId);
            var group = review.Groups.Single();
            Check(group.GroupId == "g.1" && group.QuestionNumbers.SequenceEqual(new[] {31,42}) && group.MaximumScore == 16,
                "shared paper group retains independent global question identities and scores");
            var mapped = await SubjectiveCaptureTemplateMapper.LocateAndMapAsync(layout, new LocalInputImageFile(path), 3360, 4752);
            var store = new SubjectiveReviewStore(root);
            Check(store.LoadForGrant(review.ReviewId,"other-grant") is null, "grant cannot read another owner review/group mapping");
            await RejectStoreAsync(() => store.CreateAsync(Guid.NewGuid(), SubjectiveReviewStore.DesktopLocalOwner,
                review.Snapshot, default, mapped with { TemplateJson = mapped.TemplateJson.Replace("g.1","tampered-group",StringComparison.Ordinal) }));
            var imageGroup = SchoolGroupMapper.MapRegistered(layout, mapped.PageTransform, 3360, 4752).Single();
            Check(imageGroup.ImageRegion.RectangleMm == layout.SchoolGroups.Single().RectangleMm &&
                imageGroup.ImageRegion.OutputWidth * (long)imageGroup.ImageRegion.OutputHeight > (long)SubjectiveImageCropper.MaximumRegionPixelCount,
                "whole printed group uses actual rectangle and independent larger decode budget");
            var image = await service.ReadGroupImageAsync(review.ReviewId, group.GroupId);
            using (var decoded = SKBitmap.Decode(image.Bytes))
            {
                Check(decoded.Width == imageGroup.ImageRegion.OutputWidth && decoded.Height == imageGroup.ImageRegion.OutputHeight,
                    "whole group rectifies to mapped dimensions");
                var header = decoded.GetPixel(8,8);
                Check(header.Blue > 200 && header.Red < 80, "crop includes blue shared header outside individual answer regions");
            }
            foreach (var id in group.QuestionIds)
                Check((await service.ReadQuestionImageAsync(review.ReviewId, id)).Bytes.SequenceEqual(image.Bytes), "all members share exact group resource");
            await RejectAsync(() => service.ReadGroupImageAsync(review.ReviewId, "wrong-group"), "SUBJECTIVE_GROUP_NOT_FOUND");
            foreach (var invalidGroupId in new[] { "../private", ".bad", "_bad", "-bad", "..", "bad/path", "bad\\path" })
                await RejectAsync(() => service.ReadCaptureGroupImageAsync(capture.Manifest.CaptureId, invalidGroupId), "SUBJECTIVE_GROUP_NOT_FOUND");
            review = await service.ApplyEditsAsync(review.ReviewId, review.Snapshot.Version, "teacher",
                [new(group.QuestionIds[0], SubjectiveReviewStatus.Draft, 6, "first"), new(group.QuestionIds[1], SubjectiveReviewStatus.Draft, 8, "second")]);
            review = await service.ApplyEditsAsync(review.ReviewId, review.Snapshot.Version, "teacher",
                [new(group.QuestionIds[0], SubjectiveReviewStatus.Confirmed, 6, "first")]);
            Check(review.Groups.Single().Status == "provisional" && review.Groups.Single().ProvisionalSubtotal == 14
                && review.Groups.Single().FinalSubtotal is null, "one confirmed member cannot finalize shared group");
            review = await service.ApplyEditsAsync(review.ReviewId, review.Snapshot.Version, "teacher",
                [new(group.QuestionIds[1], SubjectiveReviewStatus.Confirmed, 8, "second")]);
            Check(review.Groups.Single().FinalSubtotal == 14 && review.Snapshot.FinalSubtotal == 14, "all independent confirmations finalize group subtotal");
            Check(review.Capture?.IdentityStatus == "RequireAssociation" && review.Capture.CandidateId is null,
                "page grading completion preserves unresolved student identity instead of asserting a student final grade");
            await RejectAsync(() => service.ApplyEditsAsync(review.ReviewId,review.Snapshot.Version-1,"teacher",
                [new(group.QuestionIds[0],SubjectiveReviewStatus.Draft,1,"stale")]), "VERSION_CONFLICT");
            Check((await service.ReadAsync(review.ReviewId)).Groups.Single().FinalSubtotal == 14,
                "stale group edits cannot alter final subtotal or persisted member grades");
            var reopened = await new LocalSubjectiveReviewService(captures).ReadAsync(review.ReviewId);
            Check(reopened.Groups.Single().QuestionIds.SequenceEqual(group.QuestionIds), "group association persists across reopen");
            var json = await service.ExportAsync(review.ReviewId, "json");
            using (var document = JsonDocument.Parse(json.Content))
                Check(document.RootElement.GetProperty("groups")[0].GetProperty("groupId").GetString() == "g.1"
                    && document.RootElement.GetProperty("questions")[0].GetProperty("imageGroupId").GetString() == "g.1", "JSON distinguishes shared image group and individual grades");
            Check((await service.ExportAsync(review.ReviewId, "csv")).Content.Contains("imageGroupId,imageGroupTitle,groupStatus"), "CSV preserves group association");
            using (var unknownExport = JsonDocument.Parse(json.Content))
                Check(unknownExport.RootElement.GetProperty("identityStatus").GetString() == "RequireAssociation"
                    && unknownExport.RootElement.GetProperty("candidateId").ValueKind == JsonValueKind.Null,
                    "unassociated grades export an explicit pending identity");
            Check((await service.ExportAsync(review.ReviewId,"csv")).Content.Contains("RequireAssociation"),
                "CSV preserves unresolved identity alongside individual grades");
            review = await service.ApplyEditsAsync(review.ReviewId, review.Snapshot.Version, "teacher",
                [new(group.QuestionIds[0], SubjectiveReviewStatus.Draft, 5, "revision")]);
            Check(!review.Snapshot.IsFinal && review.Groups.Single().FinalSubtotal is null && review.Groups.Single().QuestionIds.SequenceEqual(group.QuestionIds),
                "revision removes final subtotal without changing image/member association");
            var objective = SchoolAnswerSheet.Create(definition with
            {
                Questions = [new(5, SchoolQuestionType.Choice, 2, Options: ["a","b"])],
                Groups = [new("objective-group", "选择题组", [5])]
            }).Pages.Single();
            Render(objective, path);
            await RejectMappingAsync(() => SubjectiveCaptureTemplateMapper.LocateAndMapAsync(layout,
                new LocalInputImageFile(path),3360,4752));
            var objectiveCapture = await captures.ImportAsync(objective, new LocalInputImageFile(path));
            Check((await service.ReadCaptureGroupImageAsync(objectiveCapture.Manifest.CaptureId, "objective-group")).Bytes.Length > 8,
                "objective-only capture exposes group image without subjective review template");
            await RejectAsync(() => service.ReadCaptureGroupImageAsync(objectiveCapture.Manifest.CaptureId, "g.1"), "SUBJECTIVE_GROUP_NOT_FOUND");
            var identifiedLayout = SchoolAnswerSheet.Create(definition with
            {
                CandidateIdentity = new(CandidateIdentityMode.Barcode,4,"0123"),
                Questions = [new(31,SchoolQuestionType.Subjective,7,SubjectiveHeightMm:20)],
                Groups = [new("g.1","阅读与解答",[31])]
            }).Pages.Single();
            Render(identifiedLayout,path);
            var identifiedCapture = await captures.ImportAsync(identifiedLayout,new LocalInputImageFile(path));
            Check(identifiedCapture.Manifest.IdentityStatus == "Identified", "synthetic candidate barcode is identified before export");
            var identifiedReview = await service.CreateAsync(identifiedCapture.Manifest.CaptureId);
            using (var identifiedExport = JsonDocument.Parse((await service.ExportAsync(identifiedReview.ReviewId,"json")).Content))
                Check(identifiedExport.RootElement.GetProperty("candidateId").GetString() == "0123"
                    && identifiedExport.RootElement.GetProperty("identityStatus").GetString() == "Identified",
                    "trusted JSON export preserves validated candidate identity");
            Check((await service.ExportAsync(identifiedReview.ReviewId,"csv")).Content.Contains("\"0123\",\"Identified\""),
                "CSV keeps candidate leading zeroes and validated identity");
            var operations = new DesktopLocalAgentOperations(captures,new NoScanner(),new SkiaAnswerSheetRecognitionService(),_ => { });
            using (var template = JsonDocument.Parse(SchoolTemplateBundle.Create(identifiedLayout.SchoolDefinition!).ToJson()))
                await operations.RunAsync(new("identity-grant",TaskOperation.SchoolTemplateImport,
                    JsonSerializer.SerializeToElement(new { bundle = template.RootElement })),null,default);
            JsonElement uploaded;
            await using (var input = File.OpenRead(path))
                uploaded = await operations.RunAsync(new("identity-grant",TaskOperation.Upload,
                    JsonSerializer.SerializeToElement(new { templateId=identifiedLayout.TemplateId,fileName="identity.png" })),input,default);
            var grantReview = await operations.RunAsync(new("identity-grant",TaskOperation.SubjectiveCreate,
                JsonSerializer.SerializeToElement(new { captureId=uploaded.GetProperty("captureId").GetString() })),null,default);
            var grantExport = await operations.RunAsync(new("identity-grant",TaskOperation.SubjectiveExport,
                JsonSerializer.SerializeToElement(new { reviewId=grantReview.GetProperty("reviewId").GetString(),format="json" })),null,default);
            using (var grantDocument=JsonDocument.Parse(grantExport.GetProperty("content").GetString()!))
                Check(grantDocument.RootElement.GetProperty("candidateId").GetString()=="0123"
                    && grantDocument.RootElement.GetProperty("identityStatus").GetString()=="Identified",
                    "grant JSON export obtains identity only from its registered capture");
            Directory.Move(identifiedCapture.CaptureDirectory,Path.Combine(root,"unavailable-capture"));
            using (var missingExport = JsonDocument.Parse((await service.ExportAsync(identifiedReview.ReviewId,"json")).Content))
                Check(missingExport.RootElement.GetProperty("candidateId").ValueKind==JsonValueKind.Null
                    && missingExport.RootElement.GetProperty("identityStatus").GetString()=="RequireAssociation",
                    "missing capture preserves grades while explicitly requiring identity association");
            Console.WriteLine("PASS: whole school group color crop, large group budget, independent member grading, provisional/final/revision, persistence, group exports and objective-only crops.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyPerspectiveAsync(string root, AnswerSheetLayout layout)
    {
        const int width = 160, height = 160;
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            bitmap.SetPixel(x,y,new SKColor((byte)x,(byte)y,20));
        var path = Path.Combine(root,"perspective.png");
        using (var encoded = bitmap.Encode(SKEncodedImageFormat.Png,100))
        using (var stream = File.Create(path)) encoded.SaveTo(stream);
        var transform = new PageTransform(0.4,0.05,5,0.03,0.5,10,0.001,0.0003,1);
        var group = SchoolGroupMapper.MapRegistered(layout,transform,width,height).Single();
        using var result = SKBitmap.Decode(await SubjectiveImageCropper.RectifyToPngAsync(
            new LocalInputImageFile(path),group.ImageRegion,width,height));
        var r = group.ImageRegion.RectangleMm;
        foreach (var x in new[] {3,result.Width/2,result.Width-4})
        foreach (var y in new[] {3,result.Height/2,result.Height-4})
        {
            var source = transform.Map(new PointMm(r.X + (x+0.5)*r.Width/result.Width, r.Y + (y+0.5)*r.Height/result.Height));
            var actual = result.GetPixel(x,y);
            Check(Math.Abs(actual.Red - (source.X-0.5)) <= 1 && Math.Abs(actual.Green - (source.Y-0.5)) <= 1 && actual.Blue == 20,
                "whole group perspective sampler retains source color at paper coordinates");
        }
        Console.WriteLine("PASS: whole school group perspective sampler at nine actual RGB pixels.");
    }

    private static void Render(AnswerSheetLayout layout, string path)
    {
        const int scale = 16;
        using var bitmap = new SKBitmap((int)layout.WidthMm * scale, (int)layout.HeightMm * scale);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { IsAntialias = false };
        void Rectangle(RectMm r, SKColor color)
        { paint.Color = color; canvas.DrawRect((float)r.X * scale, (float)r.Y * scale, (float)r.Width * scale, (float)r.Height * scale, paint); }
        foreach (var group in layout.SchoolGroups) Rectangle(group.RectangleMm, new SKColor(40, 230, 255));
        foreach (var region in layout.SubjectiveRegions) Rectangle(region.Rectangle, region.QuestionNumber == 31 ? new SKColor(255,180,180) : new SKColor(180,255,180));
        foreach (var mark in layout.RegistrationMarks) Rectangle(new(mark.TopLeft.X,mark.TopLeft.Y,mark.SizeMm,mark.SizeMm), SKColors.Black);
        var marker = layout.OrientationMarker;
        Rectangle(new(marker.TopLeft.X, marker.TopLeft.Y, marker.WidthMm, marker.HeightMm), SKColors.Black);
        void Matrix(BitMatrix matrix, RectMm area)
        {
            for (var y=0;y<matrix.Height;y++) for(var x=0;x<matrix.Width;x++)
                if(matrix[x,y]) Rectangle(new(area.X + 0.5 + x*0.5, area.Y + 0.5 + y*0.5,
                    0.5, 0.5), SKColors.Black);
        }
        Matrix(SchoolMachineCode.EncodeExam(layout.SchoolMetadata!), layout.ExamCodeArea!.Value);
        if(layout.SchoolDefinition!.CandidateIdentity.CandidateId is { } candidateId && layout.CandidateArea is { } candidateArea)
        {
            var candidate = SchoolMachineCode.EncodeCandidate(candidateId);
            for(var y=0;y<candidate.Height;y++) for(var x=0;x<candidate.Width;x++)
                if(candidate[x,y]) Rectangle(new(candidateArea.X+x*candidateArea.Width/candidate.Width,
                    candidateArea.Y+y*candidateArea.Height/candidate.Height,candidateArea.Width/candidate.Width,candidateArea.Height/candidate.Height),SKColors.Black);
        }
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png,100);
        using var stream = File.Create(path); encoded.SaveTo(stream);
    }
    private static async Task RejectStoreAsync(Func<Task> action)
    {
        try { await action(); } catch(SubjectiveReviewStoreException exception) when(exception.Code == "SUBJECTIVE_REVIEW_INVALID") { return; }
        throw new InvalidOperationException("Tampered group template must be rejected before persistence");
    }
    private static async Task RejectMappingAsync(Func<Task> action)
    {
        try { await action(); } catch(SubjectiveCaptureMappingException exception) when(exception.Code == "SUBJECTIVE_PAGE_NOT_LOCATED") { return; }
        throw new InvalidOperationException("Wrong page identity must not map group geometry");
    }
    private static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static async Task RejectAsync(Func<Task> action,string code)
    {
        try { await action(); } catch(LocalSubjectiveReviewException exception) when(exception.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}

internal sealed class NoScanner : IScannerService
{
    public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken=default)
        => Task.FromResult<IReadOnlyList<ScannerDevice>>(Array.Empty<ScannerDevice>());
    public Task<ScanResult> ScanAsync(ScannerDevice device,ScanOptions options,CancellationToken cancellationToken=default)
        => throw new InvalidOperationException("No hardware is available in this synthetic regression.");
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
