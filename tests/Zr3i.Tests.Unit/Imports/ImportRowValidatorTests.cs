using System.Globalization;
using Zr3i.Geo;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imports;
using Xunit;

namespace Zr3i.Tests.Unit.Imports;

public class ImportRowValidatorTests
{
    private static readonly CustomFieldDefinition[] Defs =
    [
        new() { Entity = CustomFieldEntities.Farm, Key = "irrigation", Type = CustomFieldTypes.Choice, Options = ["drip", "pivot"], LabelEn = "x", LabelAr = "x" },
        new() { Entity = CustomFieldEntities.Farm, Key = "contract_no", Type = CustomFieldTypes.Text, Required = true, LabelEn = "x", LabelAr = "x" },
        new() { Entity = CustomFieldEntities.Parcel, Key = "soil", Type = CustomFieldTypes.Text, LabelEn = "x", LabelAr = "x" },
    ];

    private static readonly ImportRowValidator Validator = new(Defs);

    private static Dictionary<string, string> Row(params (string Key, string Value)[] extra)
    {
        var row = new Dictionary<string, string>
        {
            ["parcel_external_id"] = "P-1", ["farm_external_id"] = "F-1", ["farm_name"] = "Delta Agro Estate", ["farm.contract_no"] = "CS-1",
        };
        foreach (var (k, v) in extra) row[k] = v;
        return row;
    }

    private static string SquareGeoJson(double side = 150, bool bowTie = false)
    {
        (double, double)[] utm = bowTie
            ? [(303800, 3409200), (303900, 3409300), (303900, 3409200), (303800, 3409300), (303800, 3409200)]
            : [(303800, 3409200), (303800 + side, 3409200), (303800 + side, 3409200 + side), (303800, 3409200 + side), (303800, 3409200)];
        var coords = utm.Select(p => { var (lon, lat) = Epsg32636.ToLonLat(p.Item1, p.Item2); return $"[{lon.ToString("F7", CultureInfo.InvariantCulture)},{lat.ToString("F7", CultureInfo.InvariantCulture)}]"; });
        return "{\"type\":\"Polygon\",\"coordinates\":[[" + string.Join(",", coords) + "]]}";
    }

    private static string[] ErrorsOf(Dictionary<string, string> row) => [.. Validator.Validate(row).Errors.Select(e => $"{e.Field}:{e.Code}").Order()];

    [Fact]
    public void AMinimalRowIsValidAndTheLabelDefaultsToTheExternalId()
    {
        var result = Validator.Validate(Row());

        Assert.Empty(result.Errors);
        Assert.Equal(("P-1", "F-1", "Delta Agro Estate", "P-1"), (result.Row!.ParcelExternalId, result.Row.FarmExternalId, result.Row.FarmName, result.Row.ParcelLabel));
        Assert.Null(result.Row.Season);
        Assert.Null(result.Row.Boundary);
    }

    [Fact]
    public void AFullRowIsParsedIntoTypedValues()
    {
        var result = Validator.Validate(Row(
            ("portfolio_external_id", "PF-1"), ("portfolio_name", "Delta book"), ("parcel_label", " ZR-KS-03 "), ("governorate", "Kafr El Sheikh"),
            ("season", "2026-27-winter"), ("declared_crop", "beet"), ("declared_area_feddan", "10.5"), ("declared_sowing_date", "2026-10-01"),
            ("boundary_geojson", SquareGeoJson()), ("boundary_source", "survey"), ("captured_by", "surveyor-2"), ("captured_at", "2026-11-01T08:00:00Z"), ("accuracy_m", "0.5"),
            ("farm.irrigation", "drip"), ("parcel.soil", "clay")));

        Assert.Empty(result.Errors);
        var r = result.Row!;
        Assert.Equal(("PF-1", "Delta book", "ZR-KS-03", "Kafr El Sheikh"), (r.PortfolioExternalId, r.PortfolioName, r.ParcelLabel, r.Governorate));
        Assert.Equal(("2026-27-winter", "beet", 10.5, "2026-10-01"), (r.Season, r.DeclaredCrop, r.DeclaredAreaFeddan, r.DeclaredSowingDate));
        Assert.Equal(("survey", "surveyor-2", new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), 0.5), (r.BoundarySource, r.CapturedBy, r.CapturedAt, r.AccuracyM));
        Assert.NotNull(r.Boundary);
        Assert.Equal("drip", r.FarmFields["irrigation"]);
        Assert.Equal("clay", r.ParcelFields["soil"]);
    }

    [Fact]
    public void RequiredColumnsMustHaveValues()
    {
        var row = Row(("parcel_external_id", "  "));
        row.Remove("farm_name");

        Assert.Equal(["farm_name:required", "parcel_external_id:required"], ErrorsOf(row));
    }

    [Fact]
    public void OverlongValuesAreRefused()
    {
        Assert.Equal(["parcel_external_id:too_long"], ErrorsOf(Row(("parcel_external_id", new string('x', FarmRules.MaxExternalId + 1)))));
        Assert.Equal(["farm_name:too_long"], ErrorsOf(Row(("farm_name", new string('x', FarmRules.MaxName + 1)))));
    }

    [Fact]
    public void SeasonColumnsNeedASeason()
    {
        Assert.Equal(["season:season_required"], ErrorsOf(Row(("declared_crop", "beet"))));
        Assert.Equal(["season:season_required"], ErrorsOf(Row(("declared_area_feddan", "5"))));
    }

    [Theory]
    [InlineData("season", "Winter 26", "season:invalid_season")]
    [InlineData("declared_crop", "Sugar Beet!", "declared_crop:invalid_crop")]
    [InlineData("declared_area_feddan", "ten", "declared_area_feddan:invalid_number")]
    [InlineData("declared_area_feddan", "-3", "declared_area_feddan:invalid_declared_area")]
    [InlineData("declared_area_feddan", "99999", "declared_area_feddan:invalid_declared_area")]
    [InlineData("declared_sowing_date", "1 Oct", "declared_sowing_date:invalid_sowing_date")]
    public void ASeasonDeclarationIsChecked(string column, string value, string expected)
    {
        var row = Row(("season", "2026-27-winter"), ("declared_crop", "beet"), ("declared_area_feddan", "5"), (column, value));

        Assert.Equal([expected], ErrorsOf(row));
    }

    [Theory]
    [InlineData("not json", "invalid_geojson")]
    [InlineData("{\"type\":\"Point\",\"coordinates\":[31,30]}", "not_a_polygon")]
    public void ABoundaryThatIsNotAPolygonIsRefused(string geojson, string code)
    {
        Assert.Equal([$"boundary_geojson:{code}"], ErrorsOf(Row(("boundary_geojson", geojson))));
    }

    [Fact]
    public void ABoundaryThatFailsGeometryChecksCarriesTheGeometryCode()
    {
        Assert.Equal(["boundary_geojson:self_intersection"], ErrorsOf(Row(("boundary_geojson", SquareGeoJson(bowTie: true)))));
    }

    [Fact]
    public void CaptureColumnsWithoutABoundaryAreRefused()
    {
        Assert.Equal(["boundary_geojson:required"], ErrorsOf(Row(("captured_by", "agronomist-7"))));
    }

    [Theory]
    [InlineData("boundary_source", "magic", "boundary_source:invalid_source")]
    [InlineData("captured_at", "yesterday", "captured_at:invalid_date")]
    [InlineData("accuracy_m", "-1", "accuracy_m:invalid_number")]
    [InlineData("accuracy_m", "abc", "accuracy_m:invalid_number")]
    public void BoundaryProvenanceColumnsAreChecked(string column, string value, string expected)
    {
        Assert.Equal([expected], ErrorsOf(Row(("boundary_geojson", SquareGeoJson()), (column, value))));
    }

    [Fact]
    public void CustomFieldErrorsPointAtTheirColumn()
    {
        var row = Row(("farm.irrigation", "flood"));
        row.Remove("farm.contract_no");

        Assert.Equal(["farm.contract_no:required", "farm.irrigation:invalid_choice"], ErrorsOf(row));
    }

    [Fact]
    public void ValuesAreTrimmedAndBlankMeansNotSet()
    {
        var r = Validator.Validate(Row(("farm_name", "  Spaced  "), ("village", "   "), ("markaz", ""))).Row!;

        Assert.Equal("Spaced", r.FarmName);
        Assert.Null(r.Village);
        Assert.Null(r.Markaz);
    }

    // --- across the rows of one file ---

    private static ParsedRow Parsed(params (string, string)[] extra) => Validator.Validate(Row(extra)).Row!;

    [Fact]
    public void TheSameParcelAndSeasonTwiceIsADuplicate()
    {
        var check = new CrossRowChecker();
        var first = Parsed(("season", "2026-27-winter"));
        var second = Parsed(("season", "2026-27-winter"));

        Assert.Empty(check.Check(first));
        Assert.Equal(["parcel_external_id:duplicate_in_file"], check.Check(second).Select(e => $"{e.Field}:{e.Code}"));
    }

    [Fact]
    public void TheSameParcelInTwoSeasonsIsFine()
    {
        var check = new CrossRowChecker();

        Assert.Empty(check.Check(Parsed(("season", "2026-27-winter"))));
        Assert.Empty(check.Check(Parsed(("season", "2027-summer"))));
    }

    [Fact]
    public void ARowWithoutASeasonAndAnotherWithOneDoNotCollide()
    {
        var check = new CrossRowChecker();

        Assert.Empty(check.Check(Parsed()));
        Assert.Empty(check.Check(Parsed(("season", "2026-27-winter"))));
    }

    [Fact]
    public void RowsDisagreeingAboutTheSameFarmOrParcelAreFlagged()
    {
        var check = new CrossRowChecker();
        check.Check(Parsed());

        var otherName = Parsed(("parcel_external_id", "P-2"), ("farm_name", "A different name"));
        Assert.Equal(["farm_external_id:conflicting_farm_data"], check.Check(otherName).Select(e => $"{e.Field}:{e.Code}"));

        var otherFarm = Parsed(("farm_external_id", "F-9"), ("farm_name", "Another farm"));   // same parcel id P-1, different farm
        Assert.Contains("parcel_external_id:conflicting_parcel_data", check.Check(otherFarm).Select(e => $"{e.Field}:{e.Code}"));
    }

    [Fact]
    public void ManyParcelsOfOneFarmWithTheSameFarmDataAreFine()
    {
        var check = new CrossRowChecker();
        for (var i = 0; i < 20; i++) Assert.Empty(check.Check(Parsed(("parcel_external_id", $"P-{i}"))));
    }
}
