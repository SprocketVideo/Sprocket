# Bundled camera-vendor LUTs

These `.cube` 3D LUTs are DJI's **official** log-to-Rec.709 conversion LUTs, downloaded from DJI's
Download Center (PLAN.md step 37). They are embedded as resources in `Sprocket.Render` and applied by
the `builtin.colortransform` effect (GPU, SkSL — never FFmpeg `lut3d`).

| File | Profile | Source |
| --- | --- | --- |
| `DJI-DLog-to-Rec709.cube` | D-Log (D-Gamut), rev 20210924 | https://www.dji.com/downloads/softwares/transcoding-mavic-3 |
| `DJI-DLogM-to-Rec709.cube` | D-Log M (Mavic 3 Pro, 2023-03-24) | https://www.dji.com/downloads/softwares/dji-mavic-3-d-log-m-709-lut |

DJI distributes these free of charge for grading DJI footage; they remain © DJI. The D-Log curve and
D-Gamut matrices are also published in DJI's public whitepaper ("White Paper on D-Log and D-Gamut of
DJI Cinema Color System", rev 1.0), which the Render tests use to cross-check the D-Log LUT's math.
