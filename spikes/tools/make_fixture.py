"""Generates the SYNTHETIC spike parcel and the independent expected values (shapely + pyproj) used to check the C# spike."""
import sys, json
import numpy as np
from pyproj import Transformer, Geod
from shapely.geometry import Polygon, box
from shapely.ops import transform
from shapely.prepared import prep

out = sys.argv[1]   # usage: python make_fixture.py <output dir>   (needs: pip install numpy shapely pyproj)
to_utm = Transformer.from_crs(4326, 32636, always_xy=True)
to_ll = Transformer.from_crs(32636, 4326, always_xy=True)

# Synthetic parcel near the Nile Delta, defined in UTM metres as an irregular, rotated quadrilateral (not grid aligned).
cx, cy = to_utm.transform(30.95, 30.80)
offs = [(-112.0, -88.0), (96.0, -121.0), (131.0, 94.0), (-84.0, 117.0)]
utm = Polygon([(cx + dx, cy + dy) for dx, dy in offs])
scale = (10 * 4200.83 / utm.area) ** 0.5            # scale to about 10 feddan declared
utm = Polygon([(cx + dx * scale, cy + dy * scale) for dx, dy in offs])

ring = [list(np.round(to_ll.transform(x, y), 7)) for x, y in utm.exterior.coords]
fc = {"type": "FeatureCollection", "features": [{
    "type": "Feature",
    "properties": {"id": "ZR-SPIKE-01", "declared_feddan": 10.0, "note": "SYNTHETIC parcel for the W1-5 raster spike. Not a real field."},
    "geometry": {"type": "Polygon", "coordinates": [ring]}}]}
json.dump(fc, open(out + "/parcel-synthetic-zr-spike-01.geojson", "w"), indent=2)

# Expected values, computed independently of the C# code from the stored (rounded) lon/lat vertices.
ll = Polygon([tuple(p) for p in ring])
p = transform(to_utm.transform, ll)
FEDDAN = 4200.83
gt = (199980.0, 10.0, 3500040.0, -10.0)             # tile 36RTV, read from the COG header
def counts(poly):
    minx, miny, maxx, maxy = poly.bounds
    c0, c1 = int((minx - gt[0]) // 10) - 1, int((maxx - gt[0]) // 10) + 2
    r0, r1 = int((gt[2] - maxy) // 10) - 1, int((gt[2] - miny) // 10) + 2
    pp = prep(poly); centre = whole = 0
    for r in range(r0, r1):
        for c in range(c0, c1):
            x0 = gt[0] + c * 10; y1 = gt[2] - r * 10
            if pp.contains(box(x0, y1 - 10, x0 + 10, y1).centroid): centre += 1
            if pp.contains(box(x0, y1 - 10, x0 + 10, y1)): whole += 1
    return centre, whole
inner = p.buffer(-5)
g = Geod(ellps="WGS84")
res = {
  "planar_area_m2_epsg32636": p.area,
  "planar_area_feddan": p.area / FEDDAN,
  "geodesic_area_m2": abs(g.geometry_area_perimeter(ll)[0]),
  "inner5m_area_m2": inner.area,
  "pixels_centre_in_polygon": counts(p)[0],
  "pixels_whole_in_polygon": counts(p)[1],
  "pixels_centre_in_inner5m": counts(inner)[0],
  "pixels_whole_in_inner5m": counts(inner)[1],
  "bbox_epsg32636": list(p.bounds),
}
json.dump(res, open(out + "/expected.json", "w"), indent=2)
print(json.dumps(res, indent=2))
