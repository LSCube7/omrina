using System.Globalization;
using System.Xml.Linq;
using Omrina.Core;

internal static class SchoolSheetRegression
{
    public static void Run()
    {
        var basis = new SchoolSheetDefinition { ExamId="exam-2026", LayoutDocumentId="document-1", Title="学校<&>试卷", CandidateIdentity=new(CandidateIdentityMode.Marking,4),
            Questions=Enumerable.Range(1,30).Select(n=> n is 5 or 6 ? new SchoolQuestionDefinition(n,SchoolQuestionType.Subjective,10,"解答<&>",null,n==5?40:25) : new(n,SchoolQuestionType.Choice,2,"正文<&>",new[]{"选项一","选项二","选项三","选项四"})).ToArray() };
        foreach(var paper in new[]{SchoolPaper.A4Portrait,SchoolPaper.A3Landscape})
            foreach(var columns in paper==SchoolPaper.A4Portrait?new[]{1}:new[]{2,3})
            {
                var definition=basis with{Paper=paper,Columns=columns,Duplex=true,RepeatBackIdentity=false};
                var sheet=SchoolAnswerSheet.Create(definition);
                Check(sheet.Pages.SelectMany(p=>p.Questions.Select(q=>q.Number).Concat(p.SubjectiveRegions.Select(r=>r.QuestionNumber))).Order().SequenceEqual(Enumerable.Range(1,30)),"all questions appear once");
                foreach(var page in sheet.Pages)
                {
                    Check(page.WidthMm==(paper==SchoolPaper.A4Portrait?210:420),"paper width");
                    Check(page.SchoolInformationArea!.Value.X==22,"information area begins in first column");
                    Check(page.ExamCodeArea is not null,"all pages have exam code");
                    Check((page.CandidateArea is not null)==(page.SchoolMetadata!.Side==SchoolPageSide.Front),"optional back identity");
                    Check(AnswerSheetLayout.FromJson(page.ToJson()).ToSvg()==page.ToSvg(),"canonical round trip");
                    var svg=XDocument.Parse(page.ToSvg()); Check(svg.Root!.Attribute("width")!.Value==$"{page.WidthMm}mm","actual millimetres");
                    foreach(var region in page.SubjectiveRegions) Check(Math.Abs(region.Rectangle.Width-page.SchoolInformationArea.Value.Width)<.0001,"subjective full column width");
                }
                var regions=sheet.Pages.SelectMany(p=>p.SubjectiveRegions).ToArray();
                Check(regions.Single(r=>r.QuestionNumber==5).Rectangle.Height==40 && regions.Single(r=>r.QuestionNumber==6).Rectangle.Height==25,"independent height");
            }
        var two=SchoolAnswerSheet.Create(basis with{BubbleWidthMm=2,BubbleHeightMm=2}).Pages[0];
        Check(two.Bubbles.All(b=>b.RadiusMm==1),"2mm inclusive");
        Reject(()=>SchoolAnswerSheet.Create(basis with{BubbleWidthMm=2.01}),"2.01 rejected");
        Reject(()=>SchoolAnswerSheet.Create(basis with{BubbleWidthMm=2.1}),"circle diameter 2.1 rejected");
        Reject(()=>SchoolAnswerSheet.Create(basis with{BubbleShape=BubbleShape.Rectangle,BubbleHeightMm=2.1}),"rectangle height 2.1 rejected");
        Reject(()=>SchoolAnswerSheet.Create(basis with{BubbleWidthMm=1.85}),"new dimensions must use 0.1 mm steps");
        var wideDefinition=basis with{BubbleShape=BubbleShape.Rectangle,BubbleWidthMm=3.5000000000000004,BubbleHeightMm=2,
            CandidateIdentity=new(CandidateIdentityMode.Marking,4),Questions=[new(7,SchoolQuestionType.Choice,1,"",["甲","乙","丙","丁"])]};
        var wide=SchoolAnswerSheet.Create(wideDefinition).Pages[0];
        Check(wide.SchoolDefinition!.BubbleWidthMm==3.5 && wide.SchoolDefinition.BubbleHeightMm==2,"floating point noise should save on the tenth millimetre grid");
        Check(wide.Bubbles.Select(b=>b.Center.X).Zip(wide.Bubbles.Skip(1).Select(b=>b.Center.X),(a,b)=>b-a).All(step=>step>=wideDefinition.BubbleWidthMm+3-1e-9),"wide choice bubbles keep a clear gap");
        Check(wide.CandidateDigits[0].Bubbles[0].Center.X==28 && wide.CandidateDigits[1].Bubbles[0].Center.X-wide.CandidateDigits[0].Bubbles[0].Center.X>=wideDefinition.BubbleWidthMm+3-1e-9,"wide candidate digits use dynamic spacing");
        Check(AnswerSheetLayout.FromJson(wide.ToJson()).ToJson()==wide.ToJson(),"wide dimensions round trip with canonical precision");
        var wideRecognition=new AnswerSheetRecognizer().Recognize(wide,Render(wide,true));
        Check(wideRecognition.Status==RecognitionStatus.Accepted && wideRecognition.Questions.Single().Options.Single(o=>o.State==OptionMarkState.Selected).OptionLabel=="B","wide rectangular marks remain recognizable");
        Reject(()=>SchoolAnswerSheet.Create(basis with{BubbleShape=BubbleShape.Rectangle,BubbleWidthMm=90,BubbleHeightMm=2,Questions=[new(1,SchoolQuestionType.Choice,1,"",["甲","乙"])]}),"wide choices that exceed their column are rejected");
        Reject(()=>SchoolAnswerSheet.Create(basis with{Paper=SchoolPaper.A3Landscape,Columns=3,BubbleShape=BubbleShape.Rectangle,BubbleWidthMm=52,BubbleHeightMm=2,Questions=[new(1,SchoolQuestionType.Choice,1,"",["甲","乙"])]}),"wide example must stay clear of QR code");
        Reject(()=>SchoolAnswerSheet.Create(basis with{Title=new string('长',80)}),"oversize title rejected");
        Reject(()=>SchoolAnswerSheet.Create(basis with{Questions=new[]{new SchoolQuestionDefinition(1,SchoolQuestionType.Choice,1,"invalid\u0001",new[]{"",""})}}),"unprintable text rejected");
        Reject(()=>SchoolAnswerSheet.Create(basis with{Questions=new[]{new SchoolQuestionDefinition(1,SchoolQuestionType.Subjective,1,new string('长',8000),null,200)},Mode=SchoolContentMode.WithQuestions}),"long content rejected");
        Reject(()=>AnswerSheetLayout.FromJson(two.ToJson().Replace("exam-2026","other-exam")),"tampered exam rejected");
        Reject(()=>AnswerSheetLayout.FromJson(two.ToJson().Replace("\"version\":1,","")),"missing required field rejected");
        Reject(()=>AnswerSheetLayout.FromJson(two.ToJson().Replace("\"pageIndex\":0","\"pageIndex\":0,\"pageIndex\":0")),"duplicate field rejected");
        var withBody=SchoolAnswerSheet.Create(basis with{Mode=SchoolContentMode.WithQuestions}).Pages[0];
        Check(withBody.ToSvg().Contains("正文&lt;&amp;&gt;",StringComparison.Ordinal),"body escaped");
        VerifyQuestionGroups(basis);
        VerifyChoiceColumns(basis);

        foreach(var shape in new[]{BubbleShape.Circle,BubbleShape.Rectangle})
            foreach(var placement in new[]{LabelPlacement.Inside,LabelPlacement.Outside})
            {
                var definition=basis with{Paper=SchoolPaper.A3Landscape,Columns=3,BubbleShape=shape,LabelPlacement=placement,
                    Questions=new[]{new SchoolQuestionDefinition(7,SchoolQuestionType.Choice,1,"",new[]{"","","",""})}};
                var page=SchoolAnswerSheet.Create(definition).Pages[0];
                var matrix=SchoolMachineCode.EncodeExam(page.SchoolMetadata!);
                var pageCode=SchoolMachineCode.PageCode(page.SchoolMetadata!);
                var codeSize=SchoolMachineCode.ExamCodeSizeMm(page.SchoolMetadata!);
                Check(pageCode.Length==29 && pageCode.StartsWith("OM1",StringComparison.Ordinal) && !pageCode.Contains("exam-2026",StringComparison.Ordinal),"fixed short versioned page payload");
                Check(matrix.Width==matrix.Height && Math.Abs(codeSize.WidthMm-(matrix.Width+2)*SchoolMachineCode.ExamModuleSizeMm)<1e-9,"square Data Matrix with measurable 0.5mm modules and a one-module quiet zone");
                Check(page.ExamCodeArea is {} codeArea && Math.Abs(codeArea.Width-codeSize.WidthMm)<1e-9 && Math.Abs(codeArea.Height-codeSize.HeightMm)<1e-9,"machine-code area matches its real printed footprint");
                var codeImage=RenderExamMatrix(matrix,8);
                Check(SchoolMachineCode.DecodeExam(codeImage)==pageCode,"direct Data Matrix decode with quiet zone");
                Check(SchoolMachineCode.DecodeExam(RenderExamMatrix(matrix,2))==pageCode,"low-resolution Data Matrix decode");
                foreach(var orientation in new[]{PageOrientation.Degrees90,PageOrientation.Degrees180,PageOrientation.Degrees270})
                {
                    Check(SchoolMachineCode.DecodeExam(Rotate(codeImage,orientation))==pageCode,$"rotated Data Matrix decode {orientation}");
                }
                Check(SchoolMachineCode.ParsePageCode(pageCode[..^1]+"B") is null,"noncanonical short-code bits rejected");
                var otherPageCode=SchoolAnswerSheet.Create(definition with{ExamId="other-exam"}).Pages[0];
                Check(SchoolMachineCode.DecodeExam(TwoExamCodes(matrix,SchoolMachineCode.EncodeExam(otherPageCode.SchoolMetadata!))) is null,"multiple page codes are ambiguous");
                Check(page.Bubbles.Concat(page.CandidateDigits.SelectMany(d=>d.Bubbles)).All(b=>b.Shape==shape && b.LabelPlacement==placement),"letters and digits share style");
                var blankImage=Render(page,false);
                var blank=new AnswerSheetRecognizer().Recognize(page,blankImage);
                Check(blank.Status!=RecognitionStatus.Rejected && blank.Questions.Single().State==QuestionMarkState.Blank,$"blank printed glyph {shape}/{placement}: {string.Join(';',blank.Diagnostics.Select(d=>d.Message))}");
                Check(blank.CandidateIdentity!.Digits.All(d=>d.State==QuestionMarkState.Blank),"blank digits");
                var filledImage=Render(page,true);
                var filled=new AnswerSheetRecognizer().Recognize(page,filledImage);
                Check(filled.Status==RecognitionStatus.Accepted,$"filled {shape}/{placement}: {string.Join(';',filled.Diagnostics.Select(d=>d.Message))}");
                Check(filled.Questions.Single().Options.Single(o=>o.State==OptionMarkState.Selected).OptionLabel=="B" && filled.CandidateIdentity!.CandidateId=="1234","answers and candidate identity");
                var key=AnswerKey.Create(page,new Dictionary<int,string>{{7,"B"}});
                Check(ScoringEngine.Score(filled,key).TotalScore==1,"non-contiguous question scoring");
                if (placement==LabelPlacement.Inside)
                {
                    var perspective=new AnswerSheetRecognizer().Recognize(page,Perspective(filledImage));
                    Check(perspective.Status==RecognitionStatus.Accepted && perspective.CandidateIdentity?.CandidateId=="1234",$"perspective {shape}: {string.Join(';',perspective.Diagnostics.Select(d=>d.Message))} digits={string.Join(";",perspective.CandidateIdentity!.Digits.Select(d=>d.State+":"+string.Join(",",d.Options.Select(o=>o.FillRatio.ToString("F2")))))}");
                    var multiple=new AnswerSheetRecognizer().Recognize(page,PaintBubble(filledImage,page.CandidateDigits[0].Bubbles[2],0));
                    Check(multiple.CandidateIdentity!.Digits[0].State==QuestionMarkState.Multiple && multiple.CandidateIdentity.CandidateId is null,"multiple candidate digits require review");
                    var uncertain=new AnswerSheetRecognizer().Recognize(page,PaintBubble(filledImage,page.CandidateDigits[0].Bubbles[1],210));
                    Check(uncertain.CandidateIdentity!.Digits[0].State==QuestionMarkState.Uncertain && uncertain.CandidateIdentity.CandidateId is null,"light candidate digits require review");
                }
                foreach(var orientation in new[]{PageOrientation.Degrees90,PageOrientation.Degrees180,PageOrientation.Degrees270})
                {
                    var rotated=new AnswerSheetRecognizer().Recognize(page,Rotate(filledImage,orientation));
                    Check(rotated.Status==RecognitionStatus.Accepted && rotated.Orientation==orientation,$"orientation {orientation}");
                }
                var wrong=SchoolAnswerSheet.Create(definition with{ExamId="wrong-exam"}).Pages[0];
                Check(new AnswerSheetRecognizer().Recognize(wrong,filledImage).Status==RecognitionStatus.Rejected,"wrong exam code rejected");
                var otherPage=SchoolAnswerSheet.Create(definition with { Questions=Enumerable.Range(1,40).Select(n=>new SchoolQuestionDefinition(n,SchoolQuestionType.Subjective,1,"",null,30)).ToArray() }).Pages[1];
                Check(new AnswerSheetRecognizer().Recognize(otherPage,filledImage).Status==RecognitionStatus.Rejected,"wrong page rejected");
            }
        var barcodePage=SchoolAnswerSheet.Create(basis with { CandidateIdentity=new(CandidateIdentityMode.Barcode,8,"12345678"),Questions=new[]{new SchoolQuestionDefinition(1,SchoolQuestionType.Choice,1,"",new[]{"",""})} }).Pages[0];
        var barcodeResult=new AnswerSheetRecognizer().Recognize(barcodePage,Render(barcodePage,true));
        Check(barcodeResult.CandidateIdentity?.CandidateId=="12345678","Code128 candidate decoded");
        Check(SchoolMachineCode.DecodeCandidate(TwoCandidateCodes("12345678","87654321"),8) is null,"different candidate barcodes require review");
        var scoredPage=SchoolAnswerSheet.Create(basis with { CandidateIdentity=new(CandidateIdentityMode.Barcode,8,"12345678"),Questions=new[]{new SchoolQuestionDefinition(7,SchoolQuestionType.Choice,4,"",new[]{"",""})} }).Pages[0];
        var scoredResult=new AnswerSheetRecognizer().Recognize(scoredPage,Render(scoredPage,true));
        var scoredKey=AnswerKey.Create(scoredPage,new Dictionary<int,string>{{7,"B"}});
        Check(ScoringEngine.Score(scoredResult,scoredKey,new ScoringOptions(99)).TotalScore==4,"school maximum score overrides legacy uniform scoring");
        Reject(()=>ScoringEngine.Score(barcodeResult,scoredKey),"wrong template answer key rejected");
        var tiny=SchoolAnswerSheet.Create(basis with { BubbleWidthMm=.2,BubbleHeightMm=.2,Questions=new[]{new SchoolQuestionDefinition(1,SchoolQuestionType.Choice,1,"",new[]{"",""})} }).Pages[0];
        var tinyResult=new AnswerSheetRecognizer().Recognize(tiny,Render(tiny,true));
        Check(tinyResult.Questions.Single().State==QuestionMarkState.Uncertain,"subpixel bubbles require review");
    }

    private static void VerifyQuestionGroups(SchoolSheetDefinition basis)
    {
        var questions = new SchoolQuestionDefinition[]
        {
            new(1, SchoolQuestionType.Choice, 2, "", ["甲", "乙", "丙"]),
            new(2, SchoolQuestionType.Choice, 3, "", ["甲", "乙", "丙"]),
            new(3, SchoolQuestionType.Choice, 4, "", ["甲", "乙", "丙"]),
            new(4, SchoolQuestionType.Subjective, 5, "", null, 20),
            new(5, SchoolQuestionType.Subjective, 6, "", null, 35)
        };
        var mixedGroups = new SchoolQuestionGroupDefinition[]
        {
            new("choice-a", "第一组", [3, 1]),
            new("subjective-a", "主观组", [5, 4]),
            new("choice-b", "第二客观组", [2])
        };
        var mixedDefinition = basis with
        {
            Mode = SchoolContentMode.AnswerOnly,
            Paper = SchoolPaper.A3Landscape,
            Columns = 2,
            CandidateIdentity = new(CandidateIdentityMode.Barcode, 4),
            Questions = questions,
            Groups = mixedGroups,
            LayoutOrder = SchoolLayoutOrder.Mixed
        };
        var mixedPage = SchoolAnswerSheet.Create(mixedDefinition).Pages.Single();
        Check(mixedPage.SchoolGroups.Select(group => group.GroupId).SequenceEqual(new[] { "choice-a", "subjective-a", "choice-b" }), "Mixed preserves group order");
        Check(mixedPage.SchoolGroups[0].QuestionNumbers.SequenceEqual(new[] { 3, 1 }), "group member order is preserved");
        Check(mixedPage.Questions.Select(question => question.Number).SequenceEqual(new[] { 3, 1, 2 }), "original question numbers are preserved in layout");
        Check(mixedPage.Questions.Single(question => question.Number == 1).Bubbles[0].QuestionNumber == 1, "question score and bubble identity remain tied to the original number");
        Check(mixedPage.SchoolGroups.All(group => Math.Abs(group.RectangleMm.Width - mixedPage.SchoolInformationArea!.Value.Width) < .0001), "group geometry spans the full column");
        Check(mixedPage.SubjectiveRegions.Select(region => region.Rectangle.Height).SequenceEqual(new[] { 35d, 20d }), "subjective regions keep per-question heights and group member order");
        Check(mixedPage.ToSvg().Contains("<line ", StringComparison.Ordinal) && mixedPage.ToSvg().Contains("第一组", StringComparison.Ordinal), "group title and divider render from page geometry");
        Check(AnswerSheetLayout.FromJson(mixedPage.ToJson()).ToSvg() == mixedPage.ToSvg(), "explicit groups survive canonical round trip");
        Check(mixedPage.ToJson().Contains("\"groups\":[", StringComparison.Ordinal) && mixedPage.ToJson().Contains("\"layoutOrder\":\"Mixed\"", StringComparison.Ordinal), "canonical snapshot stores groups and layout order");

        var separatedDefinition = mixedDefinition with
        {
            Groups = [new("subjective-a", "主观组", [5, 4]), new("choice-a", "第一组", [3, 1]), new("choice-b", "第二客观组", [2])],
            LayoutOrder = SchoolLayoutOrder.Separated
        };
        var separatedPage = SchoolAnswerSheet.Create(separatedDefinition).Pages.Single();
        Check(separatedPage.SchoolGroups.Select(group => group.GroupId).SequenceEqual(new[] { "choice-a", "choice-b", "subjective-a" }), "Separated stably puts objective groups before subjective groups");

        var conveniencePage = SchoolAnswerSheet.Create(basis with
        {
            Mode = SchoolContentMode.AnswerOnly,
            CandidateIdentity = new(CandidateIdentityMode.Barcode, 4),
            Questions = Enumerable.Range(1, 4).Select(number => new SchoolQuestionDefinition(number, SchoolQuestionType.Choice, number, "", ["甲", "乙", "丙", "丁"])).ToArray(),
            Groups = [new("four-questions", "四道客观题", [1, 2, 3, 4])]
        }).Pages.Single();
        var autoGroups = SchoolAnswerSheet.Create(basis with
        {
            Mode = SchoolContentMode.AnswerOnly,
            CandidateIdentity = new(CandidateIdentityMode.Barcode, 4),
            Questions = Enumerable.Range(1, 4).Select(number => new SchoolQuestionDefinition(number, SchoolQuestionType.Choice, number, "", ["甲", "乙", "丙", "丁"])).ToArray(),
            Groups = []
        }).Pages.Single();
        Check(autoGroups.SchoolDefinition!.Groups.Select(group => group.Id).SequenceEqual(new[] { "question-1", "question-2", "question-3", "question-4" }), "empty Groups normalizes to stable explicit single-question groups");
        var rowHeights = conveniencePage.Questions.Select(question => question.Bubbles[0].Center.Y).ToArray();
        Check(rowHeights.Zip(rowHeights.Skip(1), (first, second) => second - first).All(height => height is >= 5 and <= 6), "AnswerOnly objective groups use compact 5–6 mm rows");
        Check(conveniencePage.Questions.All(question => question.NumberPosition.Y - question.Bubbles[0].Center.Y is >= 0 and <= 2), "objective question labels share the bubble row");
        Check(AnswerSheetLayout.FromJson(conveniencePage.ToJson()).ToJson() == conveniencePage.ToJson(), "generated groups survive strict snapshot round trip");
        Reject(() => AnswerSheetLayout.FromJson(conveniencePage.ToJson().Replace("\"layoutOrder\":\"Mixed\"", "", StringComparison.Ordinal)), "missing required layout order rejected");

        Reject(() => SchoolAnswerSheet.Create(mixedDefinition with { Groups = [new("same", "组一", [1]), new("same", "组二", [2, 3, 4, 5])] }), "duplicate group IDs rejected");
        Reject(() => SchoolAnswerSheet.Create(mixedDefinition with { Groups = [new("choice-a", "第一组", [1, 2])] }), "incomplete question coverage rejected");
        Reject(() => SchoolAnswerSheet.Create(mixedDefinition with { Groups = [new("choice-a", "第一组", [1, 1]), new("others", "其余题目", [2, 3, 4, 5])] }), "duplicate member numbers rejected");
        Reject(() => SchoolAnswerSheet.Create(mixedDefinition with { Groups = [new("choice-a", "第一组", [1, 999]), new("others", "其余题目", [2, 3, 4, 5])] }), "unknown member numbers rejected");
        Reject(() => SchoolAnswerSheet.Create(mixedDefinition with { Groups = [new("mixed", "混合题型", [1, 4]), new("others", "其余题目", [2, 3, 5])] }), "mixed question types in one group rejected");
        Reject(() => SchoolAnswerSheet.Create(mixedDefinition with { Groups = [new("bad id", "非法 ID", [1, 2, 3, 4, 5])] }), "unstable group ID characters rejected");
        Reject(() => SchoolAnswerSheet.Create(mixedDefinition with { Groups = [new("empty-title", "  ", [1, 2, 3, 4, 5])] }), "blank group titles rejected");

        var paginationQuestions = Enumerable.Range(1, 31).Select(number => new SchoolQuestionDefinition(number, SchoolQuestionType.Choice, 1, "", ["甲", "乙"])).ToArray();
        var pagination = SchoolAnswerSheet.Create(mixedDefinition with
        {
            Questions = paginationQuestions,
            Groups = [new("small", "小组", Enumerable.Range(1, 9).ToArray()), new("large", "整组换栏", Enumerable.Range(10, 20).ToArray()), new("tail", "尾组", [30, 31])]
        });
        Check(pagination.Pages.Count == 1, "whole groups fit across the two columns on one page");
        var paginationPage = pagination.Pages.Single();
        Check(paginationPage.SchoolGroups.Single(group => group.GroupId == "small").RectangleMm.X != paginationPage.SchoolGroups.Single(group => group.GroupId == "large").RectangleMm.X, "group moves intact to the next column when remaining space is insufficient");
        foreach (var group in paginationPage.SchoolGroups)
        {
            var memberColumns = group.QuestionNumbers.Select(number =>
            {
                var choice = paginationPage.Questions.SingleOrDefault(question => question.Number == number);
                return choice is null
                    ? paginationPage.SubjectiveRegions.Single(region => region.QuestionNumber == number).Rectangle.X
                    : paginationPage.SchoolGroups.Single(placed => placed.QuestionNumbers.Contains(number)).RectangleMm.X;
            }).Distinct().ToArray();
            Check(memberColumns.Length == 1, $"group {group.GroupId} remains in one column");
        }

        var tooLarge = Enumerable.Range(1, 40).Select(number => new SchoolQuestionDefinition(number, SchoolQuestionType.Choice, 1, "", ["甲", "乙"])).ToArray();
        Reject(() => SchoolAnswerSheet.Create(basis with { Mode = SchoolContentMode.AnswerOnly, CandidateIdentity = new(CandidateIdentityMode.Barcode, 4), Questions = tooLarge, Groups = [new("too-tall", "超高题组", Enumerable.Range(1, 40).ToArray())] }), "a group larger than a complete column is rejected");

        var longRunQuestions = Enumerable.Range(1, 65).Select(number => new SchoolQuestionDefinition(number, SchoolQuestionType.Subjective, 1, "", null, 200)).ToArray();
        Reject(() => SchoolAnswerSheet.Create(mixedDefinition with { Questions = longRunQuestions, Groups = [], CandidateIdentity = new(CandidateIdentityMode.Marking, 4) }), "layouts over 64 pages are rejected after group pagination");
    }

    private static void VerifyChoiceColumns(SchoolSheetDefinition basis)
    {
        var questions = new SchoolQuestionDefinition[]
        {
            new(7, SchoolQuestionType.Choice, 1, "", ["甲", "乙", "丙", "丁"]),
            new(1, SchoolQuestionType.Choice, 1, "", ["甲", "乙", "丙", "丁"]),
            new(9, SchoolQuestionType.Choice, 1, "", ["甲", "乙", "丙", "丁"]),
            new(2, SchoolQuestionType.Choice, 1, "", ["甲", "乙", "丙", "丁"]),
            new(10, SchoolQuestionType.Choice, 1, "", ["甲", "乙", "丙", "丁"]),
            new(20, SchoolQuestionType.Choice, 1, "", ["甲", "乙", "丙", "丁"])
        };
        var baseDefinition = basis with
        {
            Mode = SchoolContentMode.AnswerOnly,
            Paper = SchoolPaper.A4Portrait,
            Columns = 1,
            Questions = questions,
            Groups =
            [
                new("compact", "紧凑客观组", [7, 1, 9, 2, 10]),
                new("later", "后续题目", [20])
            ]
        };
        var oneColumn = SchoolAnswerSheet.Create(baseDefinition).Pages[0];
        var twoColumnDefinition = baseDefinition with
        {
            Groups = [baseDefinition.Groups[0] with { ChoiceColumns = 2 }, baseDefinition.Groups[1]]
        };
        var twoColumns = SchoolAnswerSheet.Create(twoColumnDefinition).Pages[0];
        var originalGroup = oneColumn.SchoolGroups.Single(group => group.GroupId == "compact");
        var compactGroup = twoColumns.SchoolGroups.Single(group => group.GroupId == "compact");
        var followingGroup = twoColumns.SchoolGroups.Single(group => group.GroupId == "later");
        Check(compactGroup.ChoiceColumns == 2, "group geometry records the number of objective blocks");
        Check(Math.Abs(compactGroup.RectangleMm.Width - twoColumns.SchoolInformationArea!.Value.Width) < .0001, "compact group still spans the full column");
        Check(compactGroup.RectangleMm.Height < originalGroup.RectangleMm.Height, "two objective blocks reduce the group to three rows");
        Check(compactGroup.RectangleMm.Y + compactGroup.RectangleMm.Height <= followingGroup.RectangleMm.Y + 1e-9, "reduced group height does not overlap the next group");
        Check(twoColumns.Questions.Select(question => question.Number).SequenceEqual(new[] { 7, 1, 9, 2, 10, 20 }), "two-block layout preserves row-major question order");
        var firstRowFirstBlock = twoColumns.Questions[0].Bubbles[0].Center;
        var firstRowSecondBlock = twoColumns.Questions[1].Bubbles[0].Center;
        var secondRowFirstBlock = twoColumns.Questions[2].Bubbles[0].Center;
        var secondRowSecondBlock = twoColumns.Questions[3].Bubbles[0].Center;
        var thirdRowFirstBlock = twoColumns.Questions[4].Bubbles[0].Center;
        Check(firstRowFirstBlock.X < firstRowSecondBlock.X && Math.Abs(firstRowFirstBlock.Y - firstRowSecondBlock.Y) < 1e-9, "first row uses adjacent blocks");
        Check(Math.Abs(firstRowFirstBlock.X - secondRowFirstBlock.X) < 1e-9 && Math.Abs(firstRowSecondBlock.X - secondRowSecondBlock.X) < 1e-9, "members fill each block in row-major order");
        Check(secondRowFirstBlock.Y > firstRowFirstBlock.Y && secondRowSecondBlock.Y == secondRowFirstBlock.Y, "second row aligns across blocks");
        Check(thirdRowFirstBlock.Y > secondRowFirstBlock.Y, "odd group member count ends on a partial row");
        var restored = AnswerSheetLayout.FromJson(twoColumns.ToJson());
        Check(restored.SchoolDefinition!.Groups.Single(group => group.Id == "compact").ChoiceColumns == 2, "choice column count survives template serialization");
        var importedBundle = SchoolTemplateBundle.FromJson(SchoolTemplateBundle.Create(twoColumnDefinition).ToJson());
        var generatedBundleTemplateIds = SchoolAnswerSheet.Create(twoColumnDefinition).Pages.Select(page => page.SchoolMetadata!.TemplateId);
        Check(importedBundle.Definition.Groups.Single(group => group.Id == "compact").ChoiceColumns == 2
            && importedBundle.Pages.Select(page => page.Metadata.TemplateId).SequenceEqual(generatedBundleTemplateIds), "choice column layout and page identities survive offline transfer");

        Reject(() => SchoolAnswerSheet.Create(twoColumnDefinition with { Groups = [baseDefinition.Groups[0] with { ChoiceColumns = 0 }, baseDefinition.Groups[1]] }), "zero objective blocks rejected");
        Reject(() => SchoolAnswerSheet.Create(twoColumnDefinition with { Groups = [baseDefinition.Groups[0] with { ChoiceColumns = 4 }, baseDefinition.Groups[1]] }), "more than three objective blocks rejected");
        Reject(() => SchoolAnswerSheet.Create(twoColumnDefinition with
        {
            Mode = SchoolContentMode.WithQuestions,
            Groups = [baseDefinition.Groups[0] with { ChoiceColumns = 2 }, baseDefinition.Groups[1]]
        }), "multiple blocks with question text rejected");
        Reject(() => SchoolAnswerSheet.Create(twoColumnDefinition with
        {
            Questions = [new SchoolQuestionDefinition(21, SchoolQuestionType.Subjective, 1, "", null, 30)],
            Groups = [new("subjective", "主观组", [21], 2)]
        }), "multiple blocks for subjective questions rejected");
        Reject(() => SchoolAnswerSheet.Create(twoColumnDefinition with
        {
            Questions = [new SchoolQuestionDefinition(1, SchoolQuestionType.Choice, 1, "", ["甲", "乙", "丙", "丁", "戊", "己"])],
            Groups = [new("too-wide", "选项过多", [1], 3)]
        }), "choices wider than their objective block rejected");
    }

    private static GrayImage Render(AnswerSheetLayout layout,bool filled)
    {
        const double scale=8;
        var width=(int)(layout.WidthMm*scale); var height=(int)(layout.HeightMm*scale); var pixels=Enumerable.Repeat((byte)255,width*height).ToArray();
        static double Number(XElement node,string name) => double.Parse(node.Attribute(name)?.Value??"0",CultureInfo.InvariantCulture);
        void Paint(double x,double y,double w,double h,Func<double,double,bool>? inside=null)
        {
            for(var py=Math.Max(0,(int)Math.Floor(y*scale));py<Math.Min(height,(int)Math.Ceiling((y+h)*scale));py++)
                for(var px=Math.Max(0,(int)Math.Floor(x*scale));px<Math.Min(width,(int)Math.Ceiling((x+w)*scale));px++)
                    if ((px+.5)/scale >= x && (px+.5)/scale < x+w && (py+.5)/scale >= y && (py+.5)/scale < y+h && (inside is null || inside((px+.5)/scale,(py+.5)/scale))) pixels[py*width+px]=0;
        }
        foreach(var node in XDocument.Parse(layout.ToSvg()).Root!.Elements())
        {
            var name=node.Name.LocalName;
            if(name=="rect")
            {
                var x=Number(node,"x");var y=Number(node,"y");var w=Number(node,"width");var h=Number(node,"height");
                if(node.Attribute("fill")?.Value=="black") Paint(x,y,w,h);
                else if(node.Attribute("fill")?.Value=="none")
                {
                    var stroke=Number(node,"stroke-width"); Paint(x,y,w,stroke);Paint(x,y+h-stroke,w,stroke);Paint(x,y,stroke,h);Paint(x+w-stroke,y,stroke,h);
                }
            }
            else if(name=="circle")
            {
                var cx=Number(node,"cx");var cy=Number(node,"cy");var r=Number(node,"r");
                Paint(cx-r,cy-r,r*2,r*2,(x,y)=>Math.Abs(Math.Sqrt((x-cx)*(x-cx)+(y-cy)*(y-cy))-r)<.06);
            }
            else if(name=="text" && Number(node,"font-size")<=1)
            {
                // Conservative solid printed-glyph footprint; no student mark is present here.
                var size=Number(node,"font-size");var x=Number(node,"x");var y=Number(node,"y");
                Paint(x-size*.3,y-size*.7,size*.6,size*.75);
            }
        }
        if(filled)
            foreach(var bubble in layout.Bubbles.Where(b=>b.OptionIndex==2).Concat(layout.CandidateDigits.Select(d=>d.Bubbles[d.Position%10])))
            {
                var halfHeight=bubble.HeightMm/2;
                Paint(bubble.Center.X-bubble.RadiusMm,bubble.Center.Y-halfHeight,bubble.RadiusMm*2,halfHeight*2,
                    (x,y)=>bubble.Shape==BubbleShape.Rectangle || Math.Pow(x-bubble.Center.X,2)+Math.Pow(y-bubble.Center.Y,2)<=bubble.RadiusMm*bubble.RadiusMm);
            }
        return new(width,height,pixels,false);
    }
    private static GrayImage Rotate(GrayImage image,PageOrientation orientation)
    {
        var sideways=orientation is PageOrientation.Degrees90 or PageOrientation.Degrees270;
        var width=sideways?image.Height:image.Width; var height=sideways?image.Width:image.Height; var output=new byte[width*height];
        for(var y=0;y<image.Height;y++) for(var x=0;x<image.Width;x++)
        {
            var (px,py)=orientation switch{PageOrientation.Degrees90=>(image.Height-1-y,x),PageOrientation.Degrees180=>(image.Width-1-x,image.Height-1-y),_=>(y,image.Width-1-x)};
            output[py*width+px]=image.GetPixel(x,y);
        }
        return new(width,height,output,false);
    }
    private static GrayImage PaintBubble(GrayImage image,AnswerBubble bubble,byte value)
    {
        var pixels=image.Pixels.ToArray();
        for(var y=(int)((bubble.Center.Y-bubble.HeightMm/2)*8);y<=(bubble.Center.Y+bubble.HeightMm/2)*8;y++)
            for(var x=(int)((bubble.Center.X-bubble.RadiusMm)*8);x<=(bubble.Center.X+bubble.RadiusMm)*8;x++)
                if(bubble.Shape==BubbleShape.Rectangle || Math.Pow((x+.5)/8-bubble.Center.X,2)+Math.Pow((y+.5)/8-bubble.Center.Y,2)<=bubble.RadiusMm*bubble.RadiusMm)
                    pixels[y*image.Width+x]=value;
        return new(image.Width,image.Height,pixels,false);
    }
    private static GrayImage Perspective(GrayImage image)
    {
        var transform=new PageTransform(8,.04,5,.02,8,4,.00006,.00004,1);
        var pixels=new byte[image.Width*image.Height];
        for(var y=0;y<image.Height;y++) for(var x=0;x<image.Width;x++)
            pixels[y*image.Width+x]=transform.TryMapInverse(new(x,y),out var mm)?image.GetPixelOrWhite((int)Math.Round(mm.X*8),(int)Math.Round(mm.Y*8)):(byte)255;
        return new(image.Width,image.Height,pixels,false);
    }
    private static GrayImage TwoCandidateCodes(string first,string second)
    {
        const int width=900,height=500,scale=4;
        var pixels=Enumerable.Repeat((byte)255,width*height).ToArray();
        foreach(var (id,top) in new[]{(first,50),(second,300)})
        {
            var matrix=SchoolMachineCode.EncodeCandidate(id);
            for(var y=0;y<matrix.Height*scale;y++) for(var x=0;x<matrix.Width*scale;x++)
                pixels[(top+y)*width+x+100]=matrix[x/scale,y/scale]?(byte)0:(byte)255;
        }
        return new(width,height,pixels,false);
    }

    private static GrayImage RenderExamMatrix(ZXing.Common.BitMatrix matrix,int scale)
    {
        const int quietModules=1;
        var width=(matrix.Width+quietModules*2)*scale;
        var height=(matrix.Height+quietModules*2)*scale;
        var pixels=Enumerable.Repeat((byte)255,width*height).ToArray();
        for(var y=0;y<matrix.Height;y++) for(var x=0;x<matrix.Width;x++)
            if(matrix[x,y])
                for(var py=0;py<scale;py++) for(var px=0;px<scale;px++)
                    pixels[(y+quietModules)*scale*width+py*width+(x+quietModules)*scale+px]=0;
        return new(width,height,pixels,false);
    }

    private static GrayImage TwoExamCodes(ZXing.Common.BitMatrix first,ZXing.Common.BitMatrix second)
    {
        const int scale=5;
        var codeWidth=(first.Width+2)*scale;
        var codeHeight=(first.Height+2)*scale;
        var width=codeWidth*2+20;
        var height=Math.Max(codeHeight,(second.Height+2)*scale)+10;
        var pixels=Enumerable.Repeat((byte)255,width*height).ToArray();
        Paint(first,5,5);
        Paint(second,codeWidth+15,5);
        return new(width,height,pixels,false);

        void Paint(ZXing.Common.BitMatrix matrix,int left,int top)
        {
            for(var y=0;y<matrix.Height;y++) for(var x=0;x<matrix.Width;x++)
                if(matrix[x,y])
                    for(var py=0;py<scale;py++) for(var px=0;px<scale;px++)
                        pixels[(top+(y+1)*scale+py)*width+left+(x+1)*scale+px]=0;
        }
    }

    private static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action,string message) { try{action();}catch(ArgumentException){return;}throw new InvalidOperationException(message); }
}
