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
        Reject(()=>SchoolAnswerSheet.Create(basis with{Title=new string('长',80)}),"oversize title rejected");
        Reject(()=>SchoolAnswerSheet.Create(basis with{Questions=new[]{new SchoolQuestionDefinition(1,SchoolQuestionType.Choice,1,"invalid\u0001",new[]{"",""})}}),"unprintable text rejected");
        Reject(()=>SchoolAnswerSheet.Create(basis with{Questions=new[]{new SchoolQuestionDefinition(1,SchoolQuestionType.Subjective,1,new string('长',8000),null,200)},Mode=SchoolContentMode.WithQuestions}),"long content rejected");
        Reject(()=>AnswerSheetLayout.FromJson(two.ToJson().Replace("exam-2026","other-exam")),"tampered exam rejected");
        Reject(()=>AnswerSheetLayout.FromJson(two.ToJson().Replace("\"version\":1,","")),"missing required field rejected");
        Reject(()=>AnswerSheetLayout.FromJson(two.ToJson().Replace("\"pageIndex\":0","\"pageIndex\":0,\"pageIndex\":0")),"duplicate field rejected");
        var withBody=SchoolAnswerSheet.Create(basis with{Mode=SchoolContentMode.WithQuestions}).Pages[0];
        Check(withBody.ToSvg().Contains("正文&lt;&amp;&gt;",StringComparison.Ordinal),"body escaped");

        foreach(var shape in new[]{BubbleShape.Circle,BubbleShape.Rectangle})
            foreach(var placement in new[]{LabelPlacement.Inside,LabelPlacement.Outside})
            {
                var definition=basis with{Paper=SchoolPaper.A3Landscape,Columns=3,BubbleShape=shape,LabelPlacement=placement,
                    Questions=new[]{new SchoolQuestionDefinition(7,SchoolQuestionType.Choice,1,"",new[]{"","","",""})}};
                var page=SchoolAnswerSheet.Create(definition).Pages[0];
                var qr=SchoolMachineCode.EncodeExam(page.SchoolMetadata!);
                var qrPixels=new byte[qr.Width*qr.Height*64];
                for(var py=0;py<qr.Height*8;py++) for(var px=0;px<qr.Width*8;px++) qrPixels[py*qr.Width*8+px]=qr[px/8,py/8]?(byte)0:(byte)255;
                Check(SchoolMachineCode.DecodeExam(new(qr.Width*8,qr.Height*8,qrPixels))==page.SchoolMetadata,"direct qr decode");
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
    private static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action,string message) { try{action();}catch(ArgumentException){return;}throw new InvalidOperationException(message); }
}
