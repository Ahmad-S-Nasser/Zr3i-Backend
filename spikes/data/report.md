# W1-5 raster spike report

Result: **ALL CHECKS PASS**. Parcel `ZR-SPIKE-01` is SYNTHETIC (declared 10.0 feddan). Scene `S2C_36RTV_20261027_0_L2A` (2026-10-27, Sentinel-2 L2A, EPSG:32636, 10 m).

## Numbers to compare with QGIS

| Quantity | Value |
| --- | --- |
| Area, planar in EPSG:32636 (NTS + ProjNet) | 42009.0845 m2 = 10.0002 feddan |
| Area, planar in EPSG:32636 (GDAL/OGR + PROJ) | 42009.0845 m2 |
| Area, 5 m inward buffer | 38008.7688 m2 |
| Parcel bounding box, EPSG:32636 | E 303759.41 to 303993.43, N 3409117.84 to 3409347.03 |
| 10 m pixels, centre inside polygon | 422 |
| 10 m pixels, whole pixel inside polygon | 374 |
| 10 m pixels, centre inside 5 m inward polygon | 381 |
| 10 m pixels, whole pixel inside 5 m inward polygon | 337 |
| Red band (B04) digital numbers at centre-inside pixels | min 246, mean 540.1, max 1266 |
| Scene classification at those pixels | 4 vegetation: 352, 5 not vegetated: 70 |

Which definition of 'pixel inside' the Verification rule uses is not decided here: the R0 specification must say. All four are reported.

## Read cost

- Open and read header: 1192 ms. Red window 27 x 26 px (1404 bytes uncompressed): 2111 ms. Scene classification window 15 x 15 px: 268 ms.
- The full red tile is 10980 x 10980 px (241 MB uncompressed); only the window was requested.

## Checks

| Check | Result | Detail |
| --- | --- | --- |
| area NTS+ProjNet vs independent (m2) | pass | 42009.0845 vs 42009.0845 |
| area GDAL/PROJ vs independent (m2) | pass | 42009.0845 vs 42009.0845 |
| 5 m inward buffer area vs independent (m2) | pass | 38008.7688 vs 38008.7688 |
| raster CRS is EPSG:32636 | pass | EPSG:32636 |
| pixel size is 10 m | pass | 10 x -10 |
| window is a small part of the tile | pass | 27x26 px of 10980x10980 |
| pixels, centre inside polygon | pass | 422 vs 422 |
| pixels, whole pixel inside polygon | pass | 374 vs 374 |
| pixels, centre inside 5 m inward polygon | pass | 381 vs 381 |
| pixels, whole pixel inside 5 m inward polygon | pass | 337 vs 337 |
| window read equals single-pixel reads (25 pixels) | pass | 0 mismatches |
| red band values are plausible digital numbers | pass | min 246, max 1266 |
| scene classification covers every parcel pixel | pass | 422 of 422 |

## Repeat in QGIS

1. Project CRS EPSG:32636. Add the GeoJSON (it is EPSG:4326) and the raster `/vsicurl/https://sentinel-cogs.s3.us-west-2.amazonaws.com/sentinel-s2-l2a-cogs/36/R/TV/2026/10/S2C_36RTV_20261027_0_L2A/B04.tif`.
2. Area: field calculator `area(transform($geometry,'EPSG:4326','EPSG:32636'))` gives the planar value above. `$area` uses the project ellipsoid and will differ by about 0.015 percent; that is expected.
3. Pixels: Processing > Zonal statistics (count) with the polygon, or Raster pixels to points then Select by location, using pixel centres; for the buffer use Buffer with distance -5.
