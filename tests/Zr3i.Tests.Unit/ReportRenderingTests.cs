using System.Text;
using System.Text.RegularExpressions;
using Zr3i.Reports;
using Xunit;

namespace Zr3i.Tests.Unit;

/// <summary>What a frozen report says: the same snapshot gives the same bytes, both languages are complete, and no decision words appear.</summary>
public class ReportRenderingTests
{
    private static readonly DateTime At = new(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc);

    private static ReportSnapshot FarmRisk(bool synthetic = false) => new(
        "farm_risk", "2026-27", "Delta Agro", "Delta Agro Estate", At, "FRI-2026-01", "1", "specified", "NOT_ESTABLISHED", synthetic,
        [new("farm", "Delta Agro Estate"), new("governorate", "Beheira"), new("fri", "40.4"), new("awcri", "38.7"), new("adjustments", "1.7"), new("parcels", "2"), new("totalArea", "12.50")],
        ["parcel", "crop", "area", "cri", "status", "band"],
        [["ZR-KS-03", "wheat", "5.00", "50.3", "included", "C"], ["ZR-KS-04", "maize", "7.50", "", "no_cri", "no_band"]], 2);

    private static ReportSnapshot Tranche() => new(
        "tranche", "2026-27", "Nile Bank", "TR-02", At, null, null, null, "NOT_ESTABLISHED", false,
        [new("tranche", "TR-02"), new("scheduled", null), new("parcels", "6"), new("MEASURED", "3"), new("FLAGGED", "1"), new("PENDING", "1"), new("NOT_ASSESSABLE", "1"), new("none", "0"), new("needsALook", "3")],
        ["parcel", "farm", "verdict", "measured", "declared", "gap"],
        [["P-2", "Farm A", "FLAGGED", "3.50", "5.00", "30"]], 3);

    [Fact]
    public void TheSameSnapshotGivesTheSameHashAndTheSameBytes()
    {
        var a = FarmRisk();
        var b = FarmRisk();
        Assert.Equal(a.Sha256(), b.Sha256());
        Assert.Equal(ReportHtml.Render(a, "en", a.Sha256()), ReportHtml.Render(b, "en", b.Sha256()));
        Assert.Equal(ReportCsv.Render(a), ReportCsv.Render(b));
    }

    [Fact]
    public void AChangedValueChangesTheHash() =>
        Assert.NotEqual(FarmRisk().Sha256(), (FarmRisk() with { Subject = "Another farm" }).Sha256());

    [Fact]
    public void TheSnapshotSurvivesAJsonRoundTrip()
    {
        var s = FarmRisk();
        Assert.Equal(s.Sha256(), ReportSnapshot.FromJson(s.ToJson()).Sha256());
    }

    [Fact]
    public void EveryRiskPageStatesTheMaturityTheMethodAndTheHash()
    {
        var s = FarmRisk();
        foreach (var lang in new[] { "en", "ar" })
        {
            var html = ReportHtml.Render(s, lang, s.Sha256());
            Assert.Contains(ReportStrings.Text(lang, "maturity.specified"), html);
            Assert.Contains("FRI-2026-01", html);
            Assert.Contains(s.Sha256(), html);
        }
    }

    [Fact]
    public void TheEnglishPageReadsLeftToRightAndTheArabicPageRightToLeftWithArabicText()
    {
        var s = FarmRisk();
        var en = ReportHtml.Render(s, "en", s.Sha256());
        var ar = ReportHtml.Render(s, "ar", s.Sha256());
        Assert.Contains("lang=\"en\" dir=\"ltr\"", en);
        Assert.Contains("lang=\"ar\" dir=\"rtl\"", ar);
        Assert.Contains("تقرير مخاطر المزرعة", ar);
        Assert.Matches(new Regex("[؀-ۿ]{3,}"), ar);
        Assert.DoesNotContain("Farm risk report", ar);
    }

    [Fact]
    public void ThePageUsesLogicalCssOnly()
    {
        var html = ReportHtml.Render(FarmRisk(), "ar", "x");
        Assert.DoesNotMatch(new Regex(@"(margin|padding|border)-(left|right)|\b(left|right)\s*:|text-align\s*:\s*(left|right)"), html);
    }

    [Fact]
    public void ASyntheticOrganisationGetsTheBannerAndTheWatermark()
    {
        var plain = ReportHtml.Render(FarmRisk(), "en", "x");
        var synthetic = ReportHtml.Render(FarmRisk(synthetic: true), "en", "x");
        Assert.DoesNotContain("Synthetic namespace", plain);
        Assert.Contains("Synthetic namespace · Illustrative · No client data", synthetic);
        Assert.Contains("class=\"mark\"", synthetic);
        Assert.Contains("مساحة بيانات تجريبية", ReportHtml.Render(FarmRisk(synthetic: true), "ar", "x"));
    }

    [Fact]
    public void ABandIsShownWithItsLetterAndWordAndMissingCriIsNotAnInventedNumber()
    {
        var html = ReportHtml.Render(FarmRisk(), "en", "x");
        Assert.Contains("C · Medium", html);
        Assert.Contains("No · no CRI recorded", html);
        Assert.Contains("No reference distribution", html);
    }

    [Fact]
    public void ATrancheReportStatesVerdictsAsFactsAndCarriesNoRiskLabel()
    {
        var html = ReportHtml.Render(Tranche(), "en", "x");
        Assert.Contains("Flagged", html);
        Assert.DoesNotContain("Specified · not calibrated", html);
    }

    [Fact]
    public void NoTemplateOrStringUsesAWordThatIsADecision()
    {
        var banned = new Regex(@"\b(approv\w*|reject\w*|recommend\w*|score\w*|rating|safe|good|bad|passed|failed|rank\w*)\b", RegexOptions.IgnoreCase);
        var text = string.Join('\n', ReportStrings.English.Values) + "\n" + ReportHtml.Render(FarmRisk(), "en", "x") + ReportHtml.Render(Tranche(), "en", "x");
        Assert.DoesNotMatch(banned, text);
    }

    [Fact]
    public void BothLanguagesHaveTheSameKeys()
    {
        Assert.Empty(ReportStrings.English.Keys.Except(ReportStrings.Arabic.Keys));
        Assert.Empty(ReportStrings.Arabic.Keys.Except(ReportStrings.English.Keys));
    }

    [Fact]
    public void EveryCodeASnapshotCanCarryHasAWordInBothLanguages()
    {
        foreach (var lang in new[] { "en", "ar" })
        {
            var all = ReportStrings.For(lang);
            foreach (var kind in new[] { "tranche", "farm_risk", "farmer_results" }) Assert.True(all.ContainsKey($"title.{kind}"));
            foreach (var c in new[] { "parcel", "farm", "crop", "verdict", "measured", "declared", "gap", "area", "cri", "status", "band", "parcels", "fri", "awcri" }) Assert.True(all.ContainsKey($"col.{c}"), c);
            foreach (var v in new[] { "MEASURED", "FLAGGED", "PENDING", "NOT_ASSESSABLE", "none", "included", "insufficient", "no_cri", "no_boundary", "no_band" }) Assert.True(all.ContainsKey($"val.{v}"), v);
            foreach (var f in new[] { "tranche", "scheduled", "parcels", "MEASURED", "FLAGGED", "PENDING", "NOT_ASSESSABLE", "none", "needsALook", "farm", "governorate", "fri", "awcri", "adjustments", "totalArea", "farms" }) Assert.True(all.ContainsKey($"fact.{f}"), f);
        }
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("-0.17", "-0.17")]
    [InlineData("50.3", "50.3")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void CsvCellsCannotRunAsFormulas(string input, string expected) => Assert.Equal(expected, ReportCsv.Cell(input));

    [Fact]
    public void TheCsvHasCodeHeadersAndOneLinePerRow()
    {
        var lines = ReportCsv.Render(FarmRisk()).TrimEnd().Split("\r\n");
        Assert.Equal("parcel,crop,area,cri,status,band", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Equal("ZR-KS-03,wheat,5.00,50.3,included,C", lines[1]);
    }

    [Fact]
    public void TheManifestListsFilesInNameOrderWithTheirHashes()
    {
        var json = ReportManifest.Render("r1", "farm_risk", "abc", At, [new("b.csv", 2, "h2"), new("a.html", 1, "h1")]);
        Assert.True(json.IndexOf("a.html", StringComparison.Ordinal) < json.IndexOf("b.csv", StringComparison.Ordinal));
        Assert.Contains("\"sha256\": \"h1\"", json);
        Assert.Contains("\"snapshotSha256\": \"abc\"", json);
    }

    [Fact]
    public void ThePdfDateStampIsReplacedWithoutChangingTheFileLength()
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n/CreationDate (D:20260101000000+00'00')\n/ModDate (D:20260101000000+00'00')\n");
        var fixedPdf = PdfDates.Fix(pdf, At);
        var text = Encoding.Latin1.GetString(fixedPdf);
        Assert.Equal(pdf.Length, fixedPdf.Length);
        Assert.Contains("D:20261105093000Z", text);
        Assert.DoesNotContain("D:20260101", text);
    }
    [Fact]
    public async Task WithoutABrowserPathRenderingFailsWithTheCodeAndNeverReturnsEmptyBytes()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ChromiumPdfRenderer(null).RenderAsync("<html></html>", At, default));
        Assert.Equal("browser_not_configured", ex.Message);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ChromiumPdfRenderer(@"C:
o\suchrowser.exe").RenderAsync("<html></html>", At, default));
        Assert.Equal("browser_not_configured", ex.Message);
    }

    /// <summary>Runs only on a machine that has Edge or Chrome (or ZR3I_TEST_BROWSER set); elsewhere there is nothing to render with.</summary>
    [Fact]
    public async Task ARealBrowserPrintsABothLanguagePdfWithTheFreezeDate()
    {
        var browser = new[] { Environment.GetEnvironmentVariable("ZR3I_TEST_BROWSER"), @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe" }
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (browser is null) return;

        var s = FarmRisk();
        foreach (var lang in new[] { "en", "ar" })
        {
            var pdf = await new ChromiumPdfRenderer(browser).RenderAsync(ReportHtml.Render(s, lang, s.Sha256()), At, default);
            Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(pdf, 0, 5));
            Assert.True(pdf.Length > 5000);
        }
    }
}
