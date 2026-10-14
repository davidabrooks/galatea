# `photo hd` and the SL environment (2026-10-13)

David: HD photos follow **all the shared environment settings** by default (like Firestorm's "Use shared environment":
the parcel's EEP environment, else the region's), a named preset overrides it on request, and Galatea's own quick
`look` renders ignore the environment entirely.

## Commands

    photo hd [self|view|<avatar or object>] [landscape|portrait] [<preset>]
    photo hd ... preset=<name>        (or sky=<name>)

Presets: `shared` (default), `default`, `midday`, `sunrise`, `sunset`, `midnight`, `night`, `foggy`. Example:
`photo hd self landscape sunset`, `photo hd view preset=midnight`. look.py: `--hd[=...] --sky=<preset>`.
The presets are the viewer library's skies (Firestorm `indra/newview/app_settings/windlight/skies/<Name>.xml`, in
`vision/cloud-render/sky_presets.json`), converted like `LLSettingsSky::translateLegacySettings`. A preset replaces the
sky only; the water settings stay the shared ones.

## Quick looks (her own vision)

`look` exports with `noenv` (the client does not fetch the parcel/region environment) and look.py / look_cam.py pass no
env to the renderer: a fixed neutral sun and sky, whatever the time of day in-world.

## What the client receives

`scene export` stores the full EEP settings (`GetParcelEnvironmentAsync`, else `GetRegionEnvironmentAsync`):
day length / offset, the day cycle's water track (track 0) and four sky tracks (ground + 3 altitudes) with their frames.

## Mapping (photo hd)

| EEP setting | Rendered as |
|---|---|
| sun_rotation / moon_rotation (day position interpolated) | sun lamp or moonlight direction, sun / moon discs |
| sunlight_color | sun lamp colour + strength, sun disc / glow colour |
| ambient (+ cloud_shadow) | un-occluded ambient on all surfaces (the viewer's amblit) |
| blue_horizon, blue_density, haze_horizon, haze_density, density_multiplier, max_y, glow | sky colours from the viewer's sky shader (skyF.glsl port) by day and night |
| glow (size, focus) + haze_horizon | sun haze glow around the sun |
| density_multiplier x distance_multiplier x (blue_density + haze_density) | distance haze on geometry (mist pass, toward the horizon colour) |
| gamma (scene gamma) | view gamma |
| cloud_color, cloud_shadow (coverage), cloud_scale, cloud_pos_density1 (density, position), cloud_pos_density2 z (detail), cloud_scroll_rate | procedural cloud layer: colour, coverage, size, density, offset at the export time, detail |
| star_brightness | star field at night |
| moon_brightness | moonlight strength, moon disc |
| sun_scale, moon_scale | disc sizes |
| water_fog_color, water_fog_density | sea body colour |
| fresnel_scale | sky-reflection falloff of the sea |

## Not supported (yet)

- PBR-sky settings: rayleigh/mie/absorption configs, planet/sky radii, droplet radius, moisture/ice level, dome offset/radius, reflection probe ambiance.
- The sky's textures: cloud_id (clouds are procedural noise), sun_id / moon_id / bloom_id / halo_id / rainbow_id (plain discs, no halo or rainbow), the viewer's star map.
- Altitude sky tracks 2-4 (the ground track is always used).
- Water: normal_map, normal_scale, wave1/2 direction (calm plane, no waves), fresnel_offset, blur_multiplier, scale_above / scale_below, underwater fog (the camera is above water), reflections of objects in the water.
- Haze is one grey extinction (channel mean), without in-scatter toward the sun; gamma is Blender's view gamma, close to but not exactly the viewer's soft clip.
- Personal (non-shared) environments other than the presets listed above, and day-cycle presets.
