using LibreMetaverse;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GalatayText;

// 2026-10-12 David: boots are chosen against the TOP's actual colour (never the bottoms'): every boot colour is scored on
// hue AND lightness distance to the top, then a small random pick among the best 3. No black / very dark boots unless
// the top itself is dark.
public static partial class Program
{
    internal static readonly UUID BootHud = new("ffa1fd83-5875-3240-b8fe-7d2c268f2883");
    // top colour HUDs (bikini, Beth, ARTi'S strapless, Chill tee); shorts/jeans HUDs are deliberately absent
    internal static readonly HashSet<UUID> TopHuds = new()
    {
        new("67c881aa-5adf-3f68-9c5c-0adb67e8e484"), new("6899891e-79d1-3a31-93ab-fac14a3bd75e"),
        new("cb0dc6c4-545c-3be6-8351-e049094315a7"), new("a2591928-d005-3af2-9b02-a04c9e5f93e7"),
    };
    // suede swatch colours, estimated from the swatch names (B1-16)
    internal static readonly Dictionary<string, (int r, int g, int b)> BootRgb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B1"] = (25, 25, 25), ["B2"] = (150, 40, 70), ["B3"] = (110, 110, 60), ["B4"] = (80, 50, 35),
        ["B5"] = (150, 70, 40), ["B6"] = (130, 120, 110), ["B7"] = (190, 150, 110), ["B8"] = (190, 190, 190),
        ["B9"] = (25, 28, 40), ["B10"] = (180, 95, 70), ["B11"] = (150, 165, 135), ["B12"] = (225, 160, 175),
        ["B13"] = (160, 190, 215), ["B14"] = (215, 180, 155), ["B15"] = (180, 120, 60), ["B16"] = (180, 30, 35),
    };
    // top colour words (HUD label names) -> RGB
    internal static readonly Dictionary<string, (int r, int g, int b)> ColorWordRgb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jet"] = (15, 15, 15), ["onyx"] = (30, 30, 32), ["charcoal"] = (55, 55, 58), ["coal"] = (45, 45, 48), ["smoke"] = (110, 110, 115),
        ["midnight"] = (25, 30, 60), ["navy"] = (30, 40, 90), ["azure"] = (60, 130, 210), ["sky"] = (140, 190, 235), ["mist"] = (200, 210, 220),
        ["choco"] = (85, 55, 40), ["cherry"] = (170, 20, 45), ["scarlet"] = (210, 30, 30), ["orange"] = (235, 120, 40), ["lemon"] = (240, 225, 100),
        ["yellow"] = (240, 210, 60), ["vesper"] = (90, 70, 110), ["twilight"] = (80, 90, 140), ["nutmeg"] = (140, 95, 60), ["ivory"] = (240, 235, 215),
        ["pearl"] = (230, 228, 222), ["forest"] = (40, 80, 50), ["sage"] = (150, 170, 140), ["ash"] = (170, 170, 168), ["mint"] = (170, 225, 195),
        ["snow"] = (245, 245, 245), ["peach"] = (245, 185, 150), ["cream"] = (240, 225, 195), ["peony"] = (230, 130, 160), ["sakura"] = (245, 190, 205),
        ["lilac"] = (200, 170, 220), ["cosmic"] = (50, 40, 80), ["graphite"] = (70, 70, 75), ["taupe"] = (140, 125, 110), ["powder"] = (180, 205, 230),
        ["milk"] = (245, 242, 235), ["leaf"] = (100, 160, 70), ["chalk"] = (235, 235, 230), ["blush"] = (235, 175, 175), ["bloom"] = (230, 120, 170),
        ["lavender"] = (190, 170, 230), ["ultraviolet"] = (100, 50, 160), ["fuchsia"] = (210, 40, 140), ["syrup"] = (150, 80, 30), ["iron"] = (95, 95, 100),
        ["gray"] = (150, 150, 150), ["grey"] = (150, 150, 150), ["olive"] = (110, 115, 60), ["purple"] = (120, 50, 140), ["burgundy"] = (110, 20, 40),
        ["cloud"] = (220, 225, 230), ["mermaid"] = (40, 160, 160), ["breeze"] = (170, 215, 225), ["white"] = (245, 245, 245), ["black"] = (20, 20, 20),
        ["silver"] = (200, 200, 205), ["gold"] = (210, 170, 70), ["copper"] = (190, 110, 70), ["pink"] = (240, 160, 190), ["red"] = (200, 30, 30),
    };
    // labels without a colour name whose base colour was measured from the worn texture
    internal static readonly Dictionary<string, (int r, int g, int b)> TopLabelRgb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a2591928-d005-3af2-9b02-a04c9e5f93e7/P13"] = (165, 162, 170),  // Chill tee print 13: light heather grey (diffuse average 104,101,108 is a baked-shading map; worn look is lighter)
    };

    internal static (double h, double s, double l) Hsl((int r, int g, int b) c)
    {
        double r = c.r / 255.0, g = c.g / 255.0, b = c.b / 255.0, mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), l = (mx + mn) / 2, d = mx - mn;
        if (d < 1e-6) return (0, 0, l);
        double s = d / (1 - Math.Abs(2 * l - 1));
        double h = mx == r ? 60 * (((g - b) / d) % 6) : mx == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, Math.Min(1, s), l);
    }

    // pure: lower = closer. Lightness always counts; hue counts in proportion to how coloured both are; grey vs coloured costs saturation
    internal static double BootScore((int r, int g, int b) top, (int r, int g, int b) boot)
    {
        var t = Hsl(top); var b = Hsl(boot);
        double dh = Math.Abs(t.h - b.h); if (dh > 180) dh = 360 - dh;
        double hue = dh / 180.0 * Math.Min(t.s, b.s);
        return 1.5 * Math.Abs(t.l - b.l) + hue + 1.0 * Math.Abs(t.s - b.s);
    }
    internal static bool TopIsDark((int r, int g, int b) top) => Hsl(top).l < 0.30;
    internal static bool BootIsDark((int r, int g, int b) boot) => Hsl(boot).l < 0.25;

    // pure: top colour from the current picks (top HUDs only; bottoms ignored); null when no top colour is known
    internal static (int r, int g, int b)? TopColorFrom(IReadOnlyList<(UUID hud, string label)> picks, out string src)
    {
        src = null;
        foreach (var (hud, label) in picks ?? Array.Empty<(UUID, string)>())
        {
            if (!TopHuds.Contains(hud) || string.IsNullOrWhiteSpace(label)) continue;
            var code = label.Split(' ')[0];
            if (TopLabelRgb.TryGetValue($"{hud}/{code}", out var m)) { src = $"{label} (measured)"; return m; }
            foreach (var w in label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Reverse())
                if (ColorWordRgb.TryGetValue(w, out var c)) { src = label; return c; }
        }
        return null;
    }

    // pure: the best 3 boot options for the top (unknown top colour -> treated as mid grey, so no dark boots)
    internal static List<HudOption> BootOptionsForTop(IReadOnlyList<HudOption> opts, IReadOnlyList<(UUID hud, string label)> picks, out string why, int keep = 3)
    {
        var top = TopColorFrom(picks, out var src);
        var t = top ?? (150, 150, 150);
        bool dark = TopIsDark(t);
        var scored = opts.Select(o => (o, rgb: BootRgb.TryGetValue(o.Label.Split(' ')[0], out var c) ? c : ((int, int, int)?)null))
                         .Where(x => x.rgb != null && (dark || !BootIsDark(x.rgb.Value)))
                         .OrderBy(x => BootScore(t, x.rgb.Value)).Take(keep).Select(x => x.o).ToList();
        why = $"top colour {(top == null ? "unknown -> mid grey" : $"{src} rgb {t.Item1},{t.Item2},{t.Item3}")}: best {scored.Count} by hue+lightness";
        return scored.Count > 0 ? scored : opts.ToList();
    }
}
