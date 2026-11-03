using Zr3i.Kernel.Farms;
using Xunit;

namespace Zr3i.Tests.Unit.Farms;

public class CustomFieldRulesTests
{
    private static CustomFieldDefinition Def(string key, string type, bool required = false, params string[] options) =>
        new() { Key = key, Type = type, Required = required, Options = [.. options], LabelEn = key, LabelAr = key };

    private static readonly CustomFieldDefinition[] Defs =
    [
        Def("irrigation", CustomFieldTypes.Choice, false, "drip", "pivot", "flood"),
        Def("elevation_m", CustomFieldTypes.Number),
        Def("planted_on", CustomFieldTypes.Date),
        Def("has_well", CustomFieldTypes.Bool),
        Def("contract_no", CustomFieldTypes.Text, required: true),
    ];

    private static CustomFieldResult Run(Dictionary<string, string?> values) => CustomFieldRules.Validate(Defs, values);

    [Fact]
    public void AcceptsAndNormalisesEachType()
    {
        var r = Run(new()
        {
            ["irrigation"] = "drip", ["elevation_m"] = " 12.50 ", ["planted_on"] = "2026-10-01", ["has_well"] = "TRUE", ["contract_no"] = "  CS-4471 ",
        });

        Assert.Empty(r.Errors);
        Assert.Equal("drip", r.Values["irrigation"]);
        Assert.Equal("12.5", r.Values["elevation_m"]);
        Assert.Equal("2026-10-01", r.Values["planted_on"]);
        Assert.Equal("true", r.Values["has_well"]);
        Assert.Equal("CS-4471", r.Values["contract_no"]);
    }

    [Fact]
    public void ARequiredFieldMustBePresentAndNotBlank()
    {
        Assert.Contains(new CustomFieldError("contract_no", "required"), Run(new()).Errors);
        Assert.Contains(new CustomFieldError("contract_no", "required"), Run(new() { ["contract_no"] = "   " }).Errors);
        Assert.Contains(new CustomFieldError("contract_no", "required"), Run(new() { ["contract_no"] = null }).Errors);
    }

    [Fact]
    public void BlankOptionalFieldsAreLeftOut()
    {
        var r = Run(new() { ["contract_no"] = "x", ["elevation_m"] = "", ["has_well"] = null });

        Assert.Empty(r.Errors);
        Assert.Equal(["contract_no"], r.Values.Keys);
    }

    [Theory]
    [InlineData("irrigation", "sprinkler", "invalid_choice")]
    [InlineData("irrigation", "Drip", "invalid_choice")]
    [InlineData("elevation_m", "twelve", "invalid_number")]
    [InlineData("elevation_m", "NaN", "invalid_number")]
    [InlineData("elevation_m", "1e999", "invalid_number")]
    [InlineData("elevation_m", "1,5", "invalid_number")]
    [InlineData("planted_on", "01/10/2026", "invalid_date")]
    [InlineData("planted_on", "2026-02-30", "invalid_date")]
    [InlineData("has_well", "yes", "invalid_bool")]
    [InlineData("nope", "x", "unknown_field")]
    public void RefusesBadValuesWithAStableCode(string key, string value, string code)
    {
        var r = Run(new() { ["contract_no"] = "x", [key] = value });

        Assert.Contains(new CustomFieldError(key, code), r.Errors);
    }

    [Fact]
    public void TextIsCappedAndSeveralErrorsAreAllReported()
    {
        var r = Run(new() { ["contract_no"] = new string('x', CustomFieldRules.MaxTextLength + 1), ["elevation_m"] = "abc", ["has_well"] = "maybe" });

        Assert.Equal(3, r.Errors.Count);
        Assert.Contains(new CustomFieldError("contract_no", "too_long"), r.Errors);
    }

    [Fact]
    public void NoValuesAtAllIsFineWhenNothingIsRequired()
    {
        Assert.Empty(CustomFieldRules.Validate([], null).Errors);
        Assert.Empty(CustomFieldRules.Validate([], null).Values);
        Assert.Contains(new CustomFieldError("x", "unknown_field"), CustomFieldRules.Validate([], new Dictionary<string, string?> { ["x"] = "1" }).Errors);
    }

    [Theory]
    [InlineData("irrigation", true)]
    [InlineData("a", true)]
    [InlineData("a_1", true)]
    [InlineData("", false)]
    [InlineData("Irrigation", false)]
    [InlineData("1abc", false)]
    [InlineData("has space", false)]
    [InlineData("a.b", false)]
    [InlineData("$set", false)]
    public void KeysMustBeSafeToStoreAsDocumentFieldNames(string key, bool ok)
    {
        Assert.Equal(ok, CustomFieldRules.IsValidKey(key));
    }
}
