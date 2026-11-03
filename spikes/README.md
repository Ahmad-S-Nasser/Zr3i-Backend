# Spikes

Throw-away experiments that answer one question each. Not part of `Zr3i.slnx`, not built by CI, and nothing in `src/` depends on them.

## Zr3i.Spike.Raster (W1-5)

Can .NET read one parcel window from a Sentinel-2 COG with GDAL, compute the area in EPSG:32636 and count the pixels under the polygon, matching an independent calculation?

```
dotnet run --project spikes/Zr3i.Spike.Raster            # the checks; writes spikes/data/report.md
dotnet run --project spikes/Zr3i.Spike.Raster -- --bench 96   # throughput probe
```

- GDAL comes from the `MaxRev.Gdal.*` packages (native binaries included, no system install).
- The parcel is SYNTHETIC. `spikes/tools/make_fixture.py` regenerates it and `spikes/data/expected.json` with shapely and pyproj, independently of the C# code.
- The scene (`S2C_36RTV_20261027_0_L2A`) is public data from the Earth Search STAC API, read over HTTPS with `/vsicurl/`. Nothing is downloaded to disk.
- libcurl inside the package needs a CA file. Set `ZR3I_CA_BUNDLE` to one; the default is the Git for Windows bundle. TLS verification is never switched off.
