The viewer shows one parcel at a time: a satellite chip with the declared boundary and the measured footprint over it, the 10 m pixel grid, and a date slider through the usable scenes.

Layers are True colour, NDVI and Scene mask (cloud, shadow and unusable pixels hatched). NDVI uses `img-ndvi-low`, `img-ndvi-mid` and `img-ndvi-high` with a legend that names the 0.25, 0.5 and 0.6 reference values. Never show NDVI without its legend and date.

The declared boundary is a solid `img-boundary` line and the measured footprint a dashed `img-measured` line, each with a `zr-navy` halo so they read on any scene; the two differ by dash as well as colour. Draw the pixel grid at 35% white and keep it on by default, because the pixel size is what limits what Zr3i can say.

Always print the sensor, level, resolution, date and cloud percentage as text, and the measurement noise next to any gap. Imagery in this system is a placeholder labelled PLACEHOLDER · SYNTHETIC; replace it with the real chip, never with a stock photo.
