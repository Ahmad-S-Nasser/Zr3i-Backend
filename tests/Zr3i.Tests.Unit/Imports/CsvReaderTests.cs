using Zr3i.Kernel.Imports;
using Xunit;

namespace Zr3i.Tests.Unit.Imports;

public class CsvReaderTests
{
    private static string[][] Fields(string csv) => [.. CsvReader.Parse(csv).Records.Select(r => r.Fields)];

    [Fact]
    public void ReadsPlainRows()
    {
        Assert.Equal([["a", "b", "c"], ["1", "2", "3"]], Fields("a,b,c\n1,2,3\n"));
    }

    [Fact]
    public void HandlesQuotesEscapedQuotesAndNewlinesInsideFields()
    {
        var result = CsvReader.Parse("name,note\n\"Smith, J\",\"said \"\"hi\"\"\"\n\"two\nlines\",x\n");

        Assert.Null(result.Error);
        Assert.Equal(["Smith, J", "said \"hi\""], result.Records[1].Fields);
        Assert.Equal(["two\nlines", "x"], result.Records[2].Fields);
    }

    [Fact]
    public void AcceptsAnyLineEndingAndAFinalLineWithoutOne()
    {
        Assert.Equal([["a", "b"], ["1", "2"], ["3", "4"], ["5", "6"]], Fields("a,b\r\n1,2\n3,4\r5,6"));
    }

    [Fact]
    public void DropsAByteOrderMark()
    {
        Assert.Equal("id", Fields("﻿id,name\n1,x")[0][0]);
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3", ';')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    [InlineData("a,b,c\n1,2,3", ',')]
    public void DetectsTheDelimiterFromTheHeader(string csv, char expected)
    {
        var result = CsvReader.Parse(csv);

        Assert.Equal(expected, result.Delimiter);
        Assert.Equal(["1", "2", "3"], result.Records[1].Fields);
    }

    [Fact]
    public void ADelimiterInsideQuotesDoesNotCountWhenDetecting()
    {
        var result = CsvReader.Parse("\"a;b\",c\n1,2");

        Assert.Equal(',', result.Delimiter);
        Assert.Equal(["a;b", "c"], result.Records[0].Fields);
    }

    [Fact]
    public void BlankLinesAreSkippedButLineNumbersStayTrue()
    {
        var result = CsvReader.Parse("a,b\n\n1,2\n\n\n3,4\n");

        Assert.Equal([1, 3, 6], result.Records.Select(r => r.Line));
    }

    [Fact]
    public void LineNumbersCountNewlinesInsideQuotedFields()
    {
        var result = CsvReader.Parse("a,b\n\"x\ny\",1\n2,3\n");

        Assert.Equal([1, 2, 4], result.Records.Select(r => r.Line));
    }

    [Fact]
    public void KeepsEmptyTrailingAndMiddleFieldsAndRaggedRows()
    {
        Assert.Equal([["a", "b", "c"], ["1", "", ""], ["2", "x"], ["3", "4", "5", "6"]], Fields("a,b,c\n1,,\n2,x\n3,4,5,6"));
    }

    [Fact]
    public void KeepsArabicTextIntact()
    {
        var result = CsvReader.Parse("farm_name,village\nمزرعة الدلتا,كفر الشيخ\n");

        Assert.Equal(["مزرعة الدلتا", "كفر الشيخ"], result.Records[1].Fields);
    }

    [Fact]
    public void AQuoteInTheMiddleOfAnUnquotedFieldIsLiteral()
    {
        Assert.Equal(["5\" pipe", "x"], Fields("a,b\n5\" pipe,x")[1]);
    }

    [Fact]
    public void AnUnterminatedQuoteIsReportedWithItsLine()
    {
        var result = CsvReader.Parse("a,b\n1,\"oops\n2,3\n");

        Assert.Equal("unterminated_quote", result.Error);
        Assert.Equal(2, result.ErrorLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n  \n")]
    public void EmptyInputHasNoRecords(string csv)
    {
        var result = CsvReader.Parse(csv);

        Assert.Null(result.Error);
        Assert.Empty(result.Records);
    }
}
