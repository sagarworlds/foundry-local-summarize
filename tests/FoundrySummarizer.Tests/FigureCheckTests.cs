using FoundrySummarizer.Core.Verification;

namespace FoundrySummarizer.Tests;

/// <summary>
/// The figure check catches invented or altered amounts, percentages and dates, while accepting the same value
/// written another way, so it warns about real problems and nothing else.
/// </summary>
public class FigureCheckTests
{
    private static readonly FigureChecker Checker = new();

    [Theory]
    [InlineData("$1.5 million", "The contract is worth $1,500,000.")]
    [InlineData("$150k", "The approved budget is $150,000.")]
    [InlineData("150,000 USD", "The approved budget is $150,000.")]
    [InlineData("€1,200,000", "The vendor contract is worth €1.2 million.")]
    [InlineData("€1.2m", "The vendor contract is worth EUR 1,200,000.")]
    [InlineData("₹1.5 million", "Phase 2 costs Rs. 15,00,000.")]                  // Indian digit grouping
    [InlineData("$2.5bn", "Revenue reached $2,500,000,000.")]
    [InlineData("$1 trillion", "Market size: $1,000,000,000,000.")]
    [InlineData("₹15 lakh", "Phase 2 costs Rs. 15,00,000.")]
    [InlineData("₹2 crores", "The grant is INR 20,000,000.")]
    [InlineData("12 Sept. 2026", "Kick-off on 12 September 2026.")]
    [InlineData("15 percent", "Costs rise by 15%.")]
    [InlineData("12.5%", "A contingency of 12.5 per cent applies.")]
    [InlineData("July 15, 2026", "The migration is due 2026-07-15.")]
    [InlineData("15 July 2026", "Due on July 15th, 2026.")]
    [InlineData("8 June 2026", "Review meeting on 06/08/2026.")]                  // day/month reading
    [InlineData("June 8, 2026", "Review meeting on 06/08/2026.")]                 // month/day reading
    [InlineData("June 2026", "Minutes of 30 June 2026.")]                         // no day: any day of that month
    [InlineData("1,350", "Headcount rises to 1350 staff.")]
    [InlineData("4.2", "Section 4.2 covers penalties.")]
    [InlineData("150,000", "The approved budget is $150,000.")]                   // a bare number matches an amount
    [InlineData("15 days", "Termination upon fifteen (15) days written notice.")]
    [InlineData("17.5 months", "The payback period is estimated at 17.5 months.")]
    [InlineData("3x", "Liability shall be capped at 3x the total fees.")]
    [InlineData("30 business days", "Invoices are due within 30 days.")]
    public void TheSameValueWrittenAnotherWay_IsFound(string figure, string document)
    {
        var result = Checker.Check($"- Key figure: {figure}.", document);

        Assert.Single(result.Checked);
        Assert.True(result.AllFound, $"'{figure}' was not recognized in: {document}");
    }

    [Theory]
    [InlineData("$165,000", "The approved budget is $150,000.")]                  // a computed or invented total
    [InlineData("15%", "A contingency of 12.5% applies.")]
    [InlineData("15%", "The team has 15 staff.")]                                 // a count is not a percentage
    [InlineData("31 June 2026", "Minutes of 30 June 2026.")]                      // an impossible date, not "June 2026"
    [InlineData("July 16, 2026", "The migration is due 2026-07-15.")]
    [InlineData("15 July 2027", "The migration is due 2026-07-15.")]
    [InlineData("4.3", "Section 4.2 covers penalties.")]
    [InlineData("30 days", "Termination upon fifteen (15) days written notice.")]  // small numbers with a unit are checked
    [InlineData("5x", "Liability shall be capped at 3x the total fees.")]
    [InlineData("2027", "The plan runs until 2026.")]
    [InlineData("$1.5 million", "The contract is worth $1,500.")]                 // the scale word matters
    public void AFigureTheDocumentDoesNotContain_IsReported(string figure, string document)
    {
        var result = Checker.Check($"- Key figure: {figure}.", document);

        var missing = Assert.Single(result.NotFound);
        Assert.Equal(figure, missing.Text);
        Assert.False(result.AllFound);
    }

    [Theory]
    [InlineData("There are 3 risks and 2 owners.")]                               // small counts
    [InlineData("### 2. Financial & Cost Assessment")]                            // headings and list markers
    [InlineData("The budget is set out in [P12] and [P3].")]                      // chat citations
    [InlineData("Model phi-4-mini, Q3 review, section 4.")]
    [InlineData("")]
    public void TextWithoutFigures_HasNothingToCheck(string text)
    {
        var result = Checker.Check(text, "Anything.");

        Assert.Empty(result.Checked);
        Assert.True(result.AllFound);
    }

    [Fact]
    public void FiguresAreClassified_InOrder_AndListedOnce()
    {
        var result = Checker.Check(
            "Budget $150,000 (repeat: $150,000), contingency 12.5%, due 30 June 2026, headcount 1,350.",
            "Budget $150,000, contingency 12.5%, due 30 June 2026, headcount 1,350.");

        Assert.Equal(
            new[] { ("$150,000", FigureKind.Amount), ("12.5%", FigureKind.Percentage), ("30 June 2026", FigureKind.Date), ("1,350", FigureKind.Number) },
            result.Checked.Select(f => (f.Text, f.Kind)));
        Assert.Equal(FigureKind.Quantity, Checker.Check("within 30 days", "within 30 days").Checked.Single().Kind);
        Assert.True(result.AllFound);
    }

    [Fact]
    public void ARealisticSummary_FlagsOnlyTheFiguresThatAreNotInTheDocument()
    {
        const string document = """
            Project Atlas minutes (30 June 2026). The approved budget is $150,000, with a contingency of 12.5%.
            Priya Shah owns the data migration, due 2026-07-15. Headcount rises from 1,200 to 1,350 staff.
            """;
        const string summary = """
            ### 1. Executive Summary & Strategic Value
            - Budget of $150k approved [P1] with 12.5 percent contingency.
            ### 2. Financial & Cost Assessment
            - Total with contingency: $168,750.
            ### 3. Key Milestones & Critical Path
            - Data migration (Priya Shah) due July 15, 2026; headcount 1,200 → 1,350.
            """;

        var result = Checker.Check(summary, document);

        Assert.Equal(new[] { "$168,750" }, result.NotFound.Select(f => f.Text));
        Assert.Equal(6, result.Checked.Count);
    }

    public static TheoryData<string> SampleDocuments()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "FoundrySummarizer.slnx"))) root = Path.GetDirectoryName(root)!;
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "samples"))) data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(SampleDocuments))]
    public async Task EveryFigureInADocument_IsFoundInThatDocument(string path)
    {
        // Extraction and matching must agree: text checked against itself can never have a missing figure.
        var text = (await new FoundrySummarizer.Core.Ingestion.DocumentIngestionPipeline().IngestFileAsync(path)).ExtractedText;

        var result = Checker.Check(text, text);

        Assert.NotEmpty(result.Checked);
        Assert.Empty(result.NotFound.Select(f => f.Text));
    }

    [Fact]
    public void AMissingDocument_ReportsEveryFigure()
    {
        var result = Checker.Check("Budget $150,000.", null!);

        Assert.Single(result.NotFound);
    }
}
