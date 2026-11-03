namespace Zr3i.Kernel.Methods;

/// <summary>
/// The first draft of ZR3I-R0-2026-01. No frozen R0 specification exists (confirmed by the owner's partner, 2026-11-06), so this draft is
/// assembled from what is documented, each parameter labelled with how settled it is and where it comes from. Nothing here is presented
/// as agreed that is not. The eight parameters that were undecided were settled by the owner on 2026-11-04; freezing still needs every starting rule approved by name. "PE" = Product Explained; "Owner 2026-11-05/06" = decisions
/// the owner took while this was built.
/// </summary>
public static class R0Draft
{
    public const string MethodId = "ZR3I-R0-2026-01";

    public static MethodVersion Create(string createdBy, DateTime now) => new()
    {
        MethodId = MethodId,
        Name = "Rung-0 verification: declared against measured, per parcel",
        Version = 1,
        State = MethodState.Draft,
        CreatedAt = now,
        CreatedBy = createdBy,
        Parameters =
        [
            // Area and geometry
            P("area.crs", "EPSG:32636", null, ParameterStatus.Specified, "Plan v2; CLAUDE.md"),
            P("area.m2_per_feddan", "4200.83", "m2", ParameterStatus.Specified, "Plan v2; PE 6.1"),
            P("area.method_id", "GEO-AREA-2026-01", null, ParameterStatus.Derived, "Zr3i.Geo: planar area on the EPSG:32636 grid"),

            // Pixel test
            P("pixel.size_m", "10", "m", ParameterStatus.Specified, "Sentinel-2 L2A 10 m bands"),
            P("pixel.buffer_m", "5", "m", ParameterStatus.StartingRule, "PE 3A.3 (EC JRC test); owner default 2026-11-05"),
            P("pixel.definition", "whole pixel inside the shrunk polygon", null, ParameterStatus.StartingRule, "Owner 2026-11-05; matches the PE 3A.3 table of expected clean pixels"),
            P("pixel.min_full_pixels", "8", null, ParameterStatus.StartingRule, "PE 3A.3; owner default 2026-11-05"),
            P("pixel.max_lost_share", "0.6", null, ParameterStatus.StartingRule, "PE 3A.3: a scene is readable when under 60 percent is lost; owner default 2026-11-05"),
            P("pixel.usable_scl_classes", "4,5,6", null, ParameterStatus.StartingRule, "Owner default 2026-11-05: vegetation, not vegetated, water"),
            P("pixel.method_id", "GEO-PIXEL-2026-01", null, ParameterStatus.Derived, "Zr3i.Geo"),

            // Radiometry and indices
            P("reflectance.rule", "DN x 0.0001; minus the 1000-count offset first for native products from baseline 04.00, unless the provider already applied it", null,
                ParameterStatus.Derived, "Sentinel-2 L2A product specification; checked on the W1-5 spike scene"),
            P("index.ndvi", "(B8 - B4) / (B8 + B4)", null, ParameterStatus.Specified, "FRI-2026-01; PE 3A.1"),
            P("index.ndmi.formula", "(B8A - B11) / (B8A + B11)", null, ParameterStatus.Specified, "Owner decision 2026-11-04: FRI-2026-01 (PE 3A.1 had B8 with B11)"),
            P("index.ndmi.min_area_feddan", "2", "feddan", ParameterStatus.StartingRule, "PE 3A.1: B11 is a 20 m band"),
            P("stats.population", "full pixels (whole pixels inside the 5 m inward buffer)", null, ParameterStatus.StartingRule, "Owner 2026-11-06; PE 6.1 item 3. The 15 m buffer in FRI-2026-01 belongs to the Risk module"),
            P("stats.aggregate", "median", null, ParameterStatus.Specified, "FRI-2026-01 (as reported)"),

            // Area gap
            P("area_gap.noise_model", "100 x perimeter_m x boundary_offset_m / area_m2", null, ParameterStatus.StartingRule, "PE 3A.2; owner 2026-11-06 (the documented model, not a pixel-resolution variant)"),
            P("area_gap.boundary_offset_m", "2", "m", ParameterStatus.StartingRule, "PE 3A.2: a planning assumption, not a measurement"),
            P("area_gap.flag_multiplier", "1", null, ParameterStatus.StartingRule, "PE 3A.2: never flag a gap smaller than the field's own noise; owner 2026-11-06"),
            P("area_gap.two_sided", "true", null, ParameterStatus.StartingRule, "FRI-2026-01 area variance is absolute; partner recommendation 2026-11-06"),
            P("area_gap.keep_signed_gap", "true", null, ParameterStatus.StartingRule, "Partner recommendation 2026-11-06: the raw signed gap is kept for audit"),

            // Crop evidence (beet starting rules; PE 3A.1)
            P("crop.beet.greenup_start_below", "0.25", "NDVI", ParameterStatus.StartingRule, "PE 3A.1 rule 1: starting rule, tuned by the hit rate"),
            P("crop.beet.greenup_pass_above", "0.5", "NDVI", ParameterStatus.StartingRule, "PE 3A.1 rule 1"),
            P("crop.beet.greenup_within_days", "60-90", "days from sowing", ParameterStatus.StartingRule, "PE 3A.1 rule 1"),
            P("crop.beet.hold_above", "0.6", "NDVI", ParameterStatus.StartingRule, "PE 3A.1 rule 2"),
            P("crop.beet.sawtooth_drop", "0.25", "NDVI", ParameterStatus.StartingRule, "PE 3A.1 rule 3: a drop this large that then recovers is a berseem cut"),
            P("crop.beet.cycle_days", "195", "days", ParameterStatus.StartingRule, "PE 3A.1 rule 4; a crop-calendar hypothesis for beet, not a universal duration"),
            P("crop.beet.sowing_window", "08-15..11-15", "month-day", ParameterStatus.StartingRule, "PE 3A.1: sowing runs mid-August to mid-November"),
            P("crop.no_crop.bare_after_sowing_days", "60", "days", ParameterStatus.StartingRule, "PE 3A.1: a field still bare 60 days after sowing is the cleanest no-crop evidence"),
            P("crop.min_valid_scenes", "3", "readable scenes", ParameterStatus.Specified, "Owner decision 2026-11-04"),
            P("crop.indeterminate.rule", "decided mainly by the crop's colour (spectral) evidence; the thresholds are per crop and set by the Zr3i administrator; a crop with none set has no rule", null, ParameterStatus.Specified, "Owner decision 2026-11-04. Per-crop values are administrator configuration, not code"),
            P("scene.quality_thresholds", "the readable-pixel rule only (pixel.max_lost_share); no further cloud, shadow, snow or saturation limits", null, ParameterStatus.Specified, "Owner decision 2026-11-04: 60 percent is accepted"),

            // Emergence
            P("emergence.definition", "the stage when a seedling breaks through the soil surface and becomes visible above ground", null, ParameterStatus.Specified, "Owner decision 2026-11-04. The satellite signal that stands for it is per crop and set by the administrator"),
            P("emergence.tolerance_days", "per crop", "days", ParameterStatus.Specified, "Owner decision 2026-11-04: set per crop by the Zr3i administrator; a crop with none set gets no emergence finding"),

            // Passes
            P("pass.min_independent", "2", null, ParameterStatus.Specified, "PE 3A.3 and FRI-2026-01: two independent passes before any observation-derived in-season flag; area_gap is exempt"),
            P("pass.separate_acquisition_dates", "true", null, ParameterStatus.StartingRule, "Partner recommendation 2026-11-06: duplicate processing of one acquisition does not count"),
            P("pass.min_separation_days", "per crop", "days", ParameterStatus.Specified, "Owner decision 2026-11-04: set per crop by the Zr3i administrator; a crop with none set gets no in-season flag"),

            // Verdict
            P("verdict.missing_evidence_is_negative", "false", null, ParameterStatus.Specified, "SOW v2.0 section 5: insufficient evidence must never be silently converted into a negative finding"),
            P("verdict.precedence", "none: the per-finding statuses are the result; there is no combined verdict", null, ParameterStatus.Specified, "Owner decision 2026-11-04. Matches the rule that the platform states facts and a person decides"),

            // Accuracy
            P("accuracy.kill_line", "0.70", null, ParameterStatus.StartingRule, "SOW v2.0 section 15: hit rate of at least 70 percent"),
            P("accuracy.target", "0.80", null, ParameterStatus.StartingRule, "SOW v2.0 section 15: 80 percent or more, subject to the agreed protocol"),
        ],
    };

    private static MethodParameter P(string key, string? value, string? unit, string status, string source) =>
        new() { Key = key, Value = value, Unit = unit, Status = status, Source = source };
}
