#!/usr/bin/env python3
# Seeds Firestorm's per-user settings (~/.firestorm_x64/user_settings/settings.xml) with low-performance defaults
# and a small 800x500 non-maximized window; run by the launchers before each start (only while no viewer runs).
import os, shutil, sys
import xml.etree.ElementTree as ET

INSTALL = "/home/box/viewers/firestorm-current"  # -> Phoenix-Firestorm-Releasex64_AVX2-7-2-5-81788 (7.2.4 deleted 2026-11-09)
USER_DIR = os.path.expanduser("~/.firestorm_x64/user_settings")
USER_FILE = os.path.join(USER_DIR, "settings.xml")

# name -> value (names verified against INSTALL/app_settings/settings.xml)
SETTINGS = {
    # window
    "WindowWidth": 800, "WindowHeight": 500, "WindowMaximized": False, "FullScreen": False,
    "WindowX": 10, "WindowY": 10,
    # graphics preset + draw distance
    "RenderQualityPerformance": 0,           # lowest preset slot (0 = Low)
    "RenderFarClip": 32.0,
    # shadows / SSAO / reflections / water / post effects off
    "RenderShadowDetail": 0, "RenderDeferredSSAO": 0,
    "RenderReflectionProbeLevel": 0, "RenderReflectionProbeDetail": -1, "RenderScreenSpaceReflections": False,
    "RenderMirrors": False, "RenderTransparentWater": False, "RenderDepthOfField": False, "RenderGlow": False,
    "RenderDisableVintageMode": False,      # False = "vintage" mode: skips HDR/emissive extras (Low preset value)
    "RenderFSAAType": 0, "RenderFSAASamples": 0, "RenderAnisotropic": False,
    # avatars
    "RenderAvatarMaxComplexity": 130000, "RenderAvatarMaxNonImpostors": 3,
    "RenderAvatarPhysicsLODFactor": 0.0,  # avatar physics off
    # geometry / particles / textures / lights (mostly Low-preset values)
    "RenderVolumeLODFactor": 1.0, "RenderMaxPartCount": 256, "RenderTreeLODFactor": 0.0,
    "RenderTerrainDetail": 0, "RenderTerrainPBRDetail": -4, "RenderFlexTimeFactor": 0.0,
    "RenderLocalLightCount": 8, "RenderGlowResolutionPow": 8, "WLSkyDetail": 56,
    "RenderMaxTextureResolution": 512,
    # frame-rate cap
    "FSLimitFramerate": True, "FramePerSecondLimit": 15,
    # media autoplay off
    "ParcelMediaAutoPlayEnable": 0, "MediaTentativeAutoPlay": False, "MediaAutoPlayHuds": False,
    "FSParcelMusicAutoPlay": False,
    # first-run antivirus whitelist popup off
    "FSShowWhitelistReminder": False,
}

def pairs(map_el):
    kids = list(map_el)
    return [(kids[i].text, kids[i + 1]) for i in range(0, len(kids) - 1, 2)]

def load_defs():
    root = ET.parse(os.path.join(INSTALL, "app_settings", "settings.xml")).getroot()
    defs = {}
    for name, m in pairs(root.find("map")):
        d = dict((k, v) for k, v in pairs(m))
        defs[name] = (d.get("Type").text if d.get("Type") is not None else None,
                      d.get("Comment").text if d.get("Comment") is not None else "")
    return defs

def value_el(typ, val):
    if typ == "Boolean":
        e = ET.Element("boolean"); e.text = "1" if val else "0"
    elif typ in ("U32", "S32"):
        e = ET.Element("integer"); e.text = str(int(val))
    elif typ == "F32":
        e = ET.Element("real"); e.text = repr(float(val))
    else:
        e = ET.Element("string"); e.text = str(val)
    return e

def main():
    defs = load_defs()
    missing = [k for k in SETTINGS if k not in defs]
    if missing:
        print("fs-lowperf-settings: unknown setting names (skipped):", ", ".join(missing), file=sys.stderr)
    os.makedirs(USER_DIR, exist_ok=True)
    if os.path.exists(USER_FILE):
        tree = ET.parse(USER_FILE); root = tree.getroot()
        bak = USER_FILE + ".bak-before-lowperf"
        if not os.path.exists(bak):
            shutil.copy2(USER_FILE, bak)
    else:
        root = ET.Element("llsd"); ET.SubElement(root, "map"); tree = ET.ElementTree(root)
    top = root.find("map")
    existing = dict(pairs(top))
    changed = 0
    for name, val in SETTINGS.items():
        if name not in defs:
            continue
        typ, comment = defs[name]
        new_v = value_el(typ, val)
        m = existing.get(name)
        if m is None:
            k = ET.SubElement(top, "key"); k.text = name
            m = ET.SubElement(top, "map")
            for fk, fe in (("Backup", value_el("Boolean", True)), ("Comment", value_el("String", comment)),
                           ("Type", value_el("String", typ))):
                ek = ET.SubElement(m, "key"); ek.text = fk; m.append(fe)
            ek = ET.SubElement(m, "key"); ek.text = "Value"; m.append(new_v)
            changed += 1
        else:
            d = pairs(m)
            vals = [v for k, v in d if k == "Value"]
            if vals:
                old = vals[0]
                if old.tag != new_v.tag or (old.text or "") != new_v.text:
                    idx = list(m).index(old); m.remove(old); m.insert(idx, new_v); changed += 1
            else:
                ek = ET.SubElement(m, "key"); ek.text = "Value"; m.append(new_v); changed += 1
    ET.indent(tree, space="    ")
    tmp = USER_FILE + ".tmp"
    tree.write(tmp, encoding="UTF-8", xml_declaration=False)
    os.replace(tmp, USER_FILE)
    print(f"fs-lowperf-settings: {len(SETTINGS) - len(missing)} settings enforced ({changed} changed) in {USER_FILE}")

if __name__ == "__main__":
    main()
