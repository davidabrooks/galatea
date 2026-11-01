// McpSurface.cs (2026-10-26, David): what the MCP connector needs to see and check her height without the old daemon socket.
//   look      - `look` over MCP: the saved image paths (LookCmd; no new renderer), every render option (vision/look_scope.py)
//   seat_info - the object she sits on, its position / rotation, her position and her offset from the seat (before / after an AVsitter adjust)
//   client_cmd- an allow-list of the text-client commands the daemon socket used to give her: look, hover (get|check|repin|apply|calibrate), height / diag, posehover (status|check)
using System.Globalization;
using System.Text;
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // render option keys look.py takes as --key=value (look_scope.py validates the values); `small` is the one bare word
    internal static readonly string[] LookOptKeys = { "preset", "scope", "floor", "avatars", "hide-attach", "textures", "materials", "scale", "size", "samples", "jpeg", "exposure", "cam-margin", "cam-lens", "cam-fov", "cam-height", "zoom" };
    internal static readonly string[] LookPresets = { "hover-ground", "hover-seat", "photo-fast", "outfit", "photo-hd" };

    // pure (test): `look self ... key=value ... small` -> (the words that are not render options, look.py --options, preset or null)
    internal static (string[] rest, List<string> opts, string preset) LookOptions(string[] a)
    {
        var rest = new List<string>(); var opts = new List<string>(); string preset = null;
        foreach (var x in a)
        {
            var eq = x.IndexOf('=');
            if (x == "small") { opts.Add("--small"); continue; }
            if (eq > 0 && LookOptKeys.Contains(x[..eq].ToLowerInvariant()))
            {
                var k = x[..eq].ToLowerInvariant(); var v = x[(eq + 1)..];
                if (k == "preset") preset = v.ToLowerInvariant();
                opts.Add($"--{k}={v}"); continue;
            }
            rest.Add(x);
        }
        return (rest.ToArray(), opts, preset);
    }

    // pure (test): an MCP tool argument as text, whatever JSON type the caller sent. 2026-10-26 11:57: `look {cam_margin: 0.15, cam_height: 0.3}` (JSON numbers)
    // failed with the connector's generic "An error occurred invoking look" because those parameters were declared string and the SDK refuses a number
    // there before the method runs. The tolerant parameters are JsonElement? and go through this: number -> its text, bool -> true/false, null -> ""
    internal static string McpOpt(JsonElement? e)
    {
        if (e is not JsonElement j) return "";
        return j.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => "",
            JsonValueKind.String => j.GetString() ?? "",
            JsonValueKind.Number => j.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => j.GetRawText(),
        };
    }
    // pure (test): an MCP boolean argument sent as true/false, "true"/"yes"/"on"/"1", or 0/1
    internal static bool McpFlag(JsonElement? e, bool dflt)
    {
        var t = McpOpt(e).Trim().ToLowerInvariant();
        return t.Length == 0 ? dflt : t is "true" or "yes" or "on" or "1";
    }

    // pure (test): the MCP `look` arguments -> the client command line. target: self | view | around | <avatar or object name>
    internal static string McpLookLine(string target, string views, bool fast, bool far, IEnumerable<(string key, string val)> options = null)
    {
        var t = (target ?? "").Trim(); if (t.Length == 0) t = "self";
        var w = new List<string> { "look" }; var tl = t.ToLowerInvariant();
        if (tl is "self" or "view" or "around")
        {
            if (tl != "view") w.Add(tl);   // view = bare `look`: over her shoulder
            if (tl == "self" && !string.IsNullOrWhiteSpace(views)) w.Add(string.Join(",", views.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)));
        }
        else { w.Add("at"); w.Add(string.Join(" ", t.Split(new[] { '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim()); }
        if (fast) w.Add("fast");
        if (far) w.Add("far");
        foreach (var (k, v) in options ?? Array.Empty<(string, string)>())
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (k == "small") { if (v is "true" or "on" or "1") w.Add("small"); continue; }
            w.Add(k + "=" + string.Join(",", v.Split(new[] { ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)));
        }
        return string.Join(" ", w);
    }

    // pure (test): LookCmd's reply -> image paths, the look.py JSON summary line, and whether it is still rendering / failed
    internal static (List<string> images, string summary, bool pending, bool failed, string text) ParseLookOutput(string txt)
    {
        var lines = (txt ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var images = lines.Where(l => l.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || l.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        var summary = lines.LastOrDefault(l => l.StartsWith("{") && l.EndsWith("}"));
        bool pending = (txt ?? "").Contains("still rendering"), failed = images.Count == 0 && !pending;
        return (images, summary, pending, failed, (txt ?? "").Trim());
    }

    public static async Task<object> LookObj(string target, string views, bool fast, bool far, IEnumerable<(string key, string val)> options)
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        var line = McpLookLine(target, views, fast, far, options);
        var txt = await Exec(line);
        var (images, summary, pending, failed, text) = ParseLookOutput(txt);
        object sum = null; try { if (summary != null) { var d = JsonDocument.Parse(summary).RootElement; sum = new { seconds = d.TryGetProperty("total", out var tt) ? tt.GetDouble() : (double?)null, mode = d.TryGetProperty("mode", out var mm) ? mm.GetString() : null }; } } catch { }
        return new { ok = !failed, command = line, images, pending, summary = sum, result = failed || pending ? text : null };
    }

    // ---- seat_info -------------------------------------------------------
    // pure (test): her offset from the seat: world delta and the same delta in the seat's own frame (what an AVsitter Z+/Z- adjust changes)
    internal static (Vector3 world, Vector3 local) SeatOffset(Vector3 seatPos, Quaternion seatRot, Vector3 avPos)
    {
        var w = avPos - seatPos;
        return (w, w * Quaternion.Inverse(seatRot));
    }

    static Quaternion WorldRot(Simulator sim, Primitive p)
    {
        var q = p.Rotation; var cur = p;
        for (int i = 0; i < 16 && cur.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(cur.ParentID, out var par); i++) { q = par.Rotation * q; cur = par; }
        return q;
    }
    static double[] V3(Vector3 v) => new double[] { Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4) };
    static double[] EulerDeg(Quaternion q) { q.GetEulerAngles(out var r, out var p, out var y); const double k = 180.0 / Math.PI; return new[] { Math.Round(r * k, 2), Math.Round(p * k, 2), Math.Round(y * k, 2) }; }

    public static async Task<object> SeatInfoObj()
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        var sim = Sim; var av = SelfAv(); uint sit = client.Self.SittingOn;
        if (sit == 0 || av == null || !sim.ObjectsPrimitives.TryGetValue(sit, out var seat)) return new { ok = true, seated = false, result = "not sitting on an object", pos = V3(client.Self.SimPosition) };
        var root = seat; for (int i = 0; i < 16 && root.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(root.ParentID, out var par); i++) root = par;
        await EnsureProperties(sim, root == seat ? new() { seat } : new() { seat, root });
        var seatPos = PositionHelper.GetPrimPosition(sim, seat); var seatRot = WorldRot(sim, seat);
        var avPos = PositionHelper.GetAvatarPosition(sim, av);
        var (w, l) = SeatOffset(seatPos, seatRot, avPos);
        var rootPos = PositionHelper.GetPrimPosition(sim, root);
        return new
        {
            ok = true, seated = true,
            @object = new { uuid = root.ID.ToString(), name = root.Properties?.Name, owner = root.Properties != null ? NameOf(root.Properties.OwnerID) : null, owner_id = root.Properties?.OwnerID.ToString(), pos = V3(rootPos), rot_deg = EulerDeg(WorldRot(sim, root)) },
            seat_prim = new { uuid = seat.ID.ToString(), name = seat.Properties?.Name, sit_name = seat.Properties?.SitName, is_root = root == seat, pos = V3(seatPos), rot_deg = EulerDeg(seatRot), scale = V3(seat.Scale) },
            avatar = new { pos = V3(avPos), sim_relative_pos = V3(av.Position) },
            sit_offset = new { world = V3(w), seat_frame = V3(l), z = Math.Round(w.Z, 4), note = "z = my world z minus the seat prim's z; seat_frame = the same offset in the seat prim's own axes (what the furniture's adjust Z+/Z- moves). Compare before / after an adjust." },
            region = sim.Name
        };
    }

    // ---- client_cmd: a read-only allow-list over the command layer ------------
    // pure (test): the line to run, or the reason it is refused. look (render options validated by LookCmd), hover [get|check], height / diag,
    // posehover status|check (= hover check). Everything else (say, im, pay, logout, hover set, ...) is refused.
    internal static (bool ok, string line, string error) ClientCmdGate(string line)
    {
        var l = (line ?? "").Trim();
        if (l.Length == 0 || l.Length > 400) return (false, null, "empty or too long");
        if (l.Any(c => char.IsControl(c))) return (false, null, "control characters are not allowed");
        var w = l.Split(' ', StringSplitOptions.RemoveEmptyEntries); var cmd = w[0].ToLowerInvariant(); var rest = w[1..].Select(x => x.ToLowerInvariant()).ToArray();
        switch (cmd)
        {
            case "look": return (true, string.Join(" ", w.Skip(1).Prepend("look")), null);
            case "hover" when rest.Length == 0 || rest is ["get"] or ["check"] or ["repin"]: return (true, string.Join(" ", w.Select(x => x.ToLowerInvariant())), null);
            // 2026-10-26: `hover apply <m>` sets only HER OWN hover, within +-0.5 m, and writes nothing to hover.txt (applied now, never remembered);
            // `hover repin` re-posts the normal pinned hover; `hover calibrate ...` only edits the renderer's bias file. `hover set` stays refused: it pins
            // the value in hover.txt for every later login and region change, which David's rule (no saved hover) forbids
            case "hover" when (rest is ["apply", _] || rest is ["apply", _, "force"]) && double.TryParse(rest[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hv) && Math.Abs(hv) <= HoverApplyMax:
                return (true, "hover apply " + rest[1] + (rest.Length == 3 ? " force" : ""), null);   // force: also while sitting (Hover.HoverApplyBlock)
            case "hover" when rest.Length > 0 && rest[0] == "calibrate" && Hover.HoverCalibrateParse(rest[1..]).ok: return (true, "hover calibrate " + string.Join(" ", rest[1..]), null);
            case "height" or "diag" when rest.Length == 0: return (true, cmd, null);
            case "posehover" when rest is ["check"]: return (true, "hover check", null);
            case "posehover" when rest is ["status"] or []: return (true, "posehover status", null);
        }
        return (false, null, $"refused: '{cmd}{(rest.Length > 0 ? " " + rest[0] : "")}' is not in the allow-list (look, hover [get|check|repin|apply <m, +-0.5>|calibrate stand|sit|here <cm>|show|reset ...], height|diag, posehover status|check)");
    }

    static string PoseHoverTail(int n)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long len = fs.Length, from = Math.Max(0, len - 512 * 1024); fs.Seek(from, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var lines = sr.ReadToEnd().Split('\n').Where(x => x.Contains("[posehover]")).TakeLast(n);
            return string.Join("\n", lines);
        }
        catch (Exception e) { return "log unreadable: " + e.Message; }
    }

    public static async Task<string> ClientCmd(string line)
    {
        var (ok, run, err) = ClientCmdGate(line);
        if (!ok) return "refused: " + err;
        if (!LoggedIn) return "not logged in";
        if (run == "posehover status") return await Exec("hover get") + "\n" + (PoseHoverTail(12) is { Length: > 0 } t ? t : "(no posehover log lines yet)");
        return await Exec(run);
    }
}
