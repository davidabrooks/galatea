"""blender -b --factory-startup --python npy_to_exr.py -- in.npy out.exr : a scene-linear (h, w, 3) float32 array (rows bottom-up, as
Blender's pixels) -> 32-bit OpenEXR (the layered photo hd's master; no OpenEXR python module on the box)."""
import sys, bpy, numpy as np
src, dst = sys.argv[sys.argv.index("--") + 1:][:2]
a = np.load(src); h, w = a.shape[:2]
im = bpy.data.images.new("m", w, h, alpha=True, float_buffer=True); im.colorspace_settings.name = "Linear Rec.709"
o = np.ones((h, w, 4), np.float32); o[..., :3] = a[..., :3]; im.pixels.foreach_set(o.ravel())
sc = bpy.context.scene; s = sc.render.image_settings; s.file_format = "OPEN_EXR"; s.color_mode = "RGB"; s.color_depth = "32"; s.exr_codec = "ZIP"
im.save_render(dst, scene=sc)
