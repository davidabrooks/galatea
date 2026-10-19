// galatay-mcp: Second Life client (LibreMetaverse) exposed as an MCP server over stdio.
// stdout carries only MCP JSON-RPC; all logs go to stderr and /workspace/secondlife/textclient.log.
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Core = GalatayText.Program;

namespace GalatayMcp;

public static class McpProgram
{
    public static async Task<int> Main(string[] args)
    {
        Core.StderrLog = true;
        if (args.Contains("--webhook-selftest")) return await Webhook.SelfTest();
        Core.ExitOnLogout = false;
        LibreMetaverse.Settings.LogLevel = LogLevel.Warning;
        // library logs -> stderr only
        LibreMetaverse.Logger.SetLoggerFactory(LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Warning);
            b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        }), "LibreMetaverse");

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<SlTools>();
        var app = builder.Build();
        Core.LoadOfferStoreForMcp(); // saved pending offers / group invites survive a connector restart (OfferStore.cs)
        Webhook.Init();
        // GT_AUTOLOGIN honours the deliberate-stop marker like the boot autostart (2026-10-13); the login tool stays explicit
        if (Environment.GetEnvironmentVariable("GT_AUTOLOGIN") == "1" && !AutoLoginBlocked(Environment.GetEnvironmentVariable("GT_STOP_MARKER") ?? "/home/box/viewers/textclient/run/deliberate-stop"))
            _ = Task.Run(async () => await Core.LoginAsync());
        await app.RunAsync();
        if (Core.LoggedIn) await Core.Shutdown("MCP host closed stdin");
        return 0;
    }

    internal static bool AutoLoginBlocked(string marker) => File.Exists(marker);
}

[McpServerToolType]
public sealed class SlTools
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = false };
    static string Json(object o) => JsonSerializer.Serialize(o, J);
    static async Task<string> R(string line)
    {
        var txt = await Core.Run(line);
        var ok = !(txt.StartsWith("not logged in") || txt.StartsWith("usage") || txt.StartsWith("error") || txt.StartsWith("rate limit")
                   || txt.StartsWith("could not") || txt.StartsWith("unknown") || txt.StartsWith("FAILED") || txt.StartsWith("REFUSED") || txt.Contains("failed"));
        if (txt.StartsWith("skipped: ")) return Json(new { ok = true, skipped = true, result = txt.TrimEnd() }); // im guard: a normal skip, not an error
        return Json(new { ok, result = txt.TrimEnd() });
    }
    static string Clean(string s) => (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();

    [McpServerTool(Name = "login"), Description("Log Galatay Resident into Second Life (start location 'last'). Refuses if Firestorm or another Galatay client is logged in. Password is read internally from the box secret store.")]
    public static Task<string> Login() => R("login");

    [McpServerTool(Name = "logout"), Description("Log out of Second Life cleanly (the MCP server keeps running).")]
    public static Task<string> Logout() => R("logout");

    [McpServerTool(Name = "status"), Description("Connection state, region, position, seat, follow target, pending teleport offer, memory use.")]
    public static string Status() => Json(Core.StatusObj());

    [McpServerTool(Name = "poll_events"), Description("Drain queued incoming events (local chat, IMs, teleport offers, dialogs, alerts, sit/teleport results) since the last poll. Returns a JSON array oldest-first.")]
    public static string PollEvents([Description("Maximum events to return (default 100)")] int max = 100) => Json(Core.DrainEvents(Math.Clamp(max, 1, 1000)));

    [McpServerTool(Name = "say"), Description("Say text in local chat (20 m). Speak deliberately; rate-limited to 8/min.")]
    public static Task<string> Say(string text) => R("say " + Clean(text));

    [McpServerTool(Name = "im"), Description("Send an instant message to an avatar by legacy name ('First Last'), username, or UUID. Rate-limited. Duplicate guard: returns ok=true, skipped=true if I IMed them < 5 s ago or already answered their latest IM. headsup=true: ONE short 'give me a bit, I need to check with David' note per their latest IM (still the 5 s window).")]
    public static Task<string> Im([Description("Avatar name or UUID")] string to, string text,
                                  [Description("true = one heads-up note allowed after an answered IM, at most once until they write again")] bool headsup = false,
                                  [Description("comma-separated msg_id(s) of the incoming IMs this reply answers (claim-and-send: skipped if any was already answered)")] string re = "")
        => R($"im {(string.IsNullOrWhiteSpace(re) ? "" : $"--re {new string(re.Where(c => char.IsDigit(c) || c == ',').ToArray())} ")}{(headsup ? "--headsup " : "")}\"{Clean(to).Replace("\"", "")}\" {Clean(text)}");

    [McpServerTool(Name = "nearby"), Description("List avatars in view and nearby objects (name, uuid, distance, position, owner, who is sitting on it) as JSON.")]
    public static async Task<string> Nearby([Description("Object search radius in metres (default 20)")] float radius = 20,
                                            [Description("Optional case-insensitive object-name filter")] string filter = "",
                                            [Description("Max objects returned (default 60)")] int max = 60)
    {
        if (!Core.LoggedIn) return Json(new { ok = false, result = "not logged in" });
        return Json(await Core.NearbyObj(radius, filter ?? "", Math.Clamp(max, 1, 500)));
    }

    [McpServerTool(Name = "object_info"), Description("Details for one object UUID: name, description, owner, position, distance, occupancy.")]
    public static Task<string> ObjectInfo(string uuid) => R("objinfo " + Clean(uuid));

    [McpServerTool(Name = "walk_to"), Description("Walk (server autopilot) to region-local coordinates. Straight line; may stop at obstacles. Where a nav grid covers both ends (e.g. home in Peronaut) and the straight line is blocked, she walks the planned route around walls and through doors instead (never flies). Check status afterwards.")]
    public static Task<string> WalkTo(float x, float y, float z) => R(FormattableString.Invariant($"moveto {x} {y} {z}"));

    [McpServerTool(Name = "walk"), Description("Walk forward the given number of metres in the current facing direction.")]
    public static Task<string> Walk(float meters) => R(FormattableString.Invariant($"walk {meters}"));

    [McpServerTool(Name = "turn"), Description("Turn in place by degrees (positive = left / counter-clockwise).")]
    public static Task<string> Turn(float degrees) => R(FormattableString.Invariant($"turn {degrees}"));

    [McpServerTool(Name = "stop"), Description("Stop walking/following.")]
    public static Task<string> Stop() => R("stop");

    [McpServerTool(Name = "teleport"), Description("Teleport to a region by name and local coordinates.")]
    public static Task<string> Teleport(string region, float x, float y, float z) => R(FormattableString.Invariant($"teleport {Clean(region)} {x} {y} {z}"));

    [McpServerTool(Name = "sit"), Description("Sit on an object by UUID (AgentRequestSit + AgentSit). Use nearby to find an unoccupied object.")]
    public static Task<string> Sit(string uuid) => R("sit " + Clean(uuid));

    [McpServerTool(Name = "stand"), Description("Stand up.")]
    public static Task<string> Stand() => R("stand");

    [McpServerTool(Name = "accept_teleport"), Description("Accept the pending teleport offer. Only offers from allow-listed avatars (David Nightingale) are ever held as pending.")]
    public static Task<string> AcceptTeleport() => R("accept");

    [McpServerTool(Name = "decline_teleport"), Description("Decline the pending (allow-listed) teleport offer.")]
    public static Task<string> DeclineTeleport() => R("decline");

    [McpServerTool(Name = "auto_accept_teleports"), Description("Turn automatic acceptance of teleport offers from the allow-list (David Nightingale) on or off.")]
    public static Task<string> AutoAccept(bool on) => R("autolure " + (on ? "on" : "off"));

    [McpServerTool(Name = "follow"), Description("Follow an avatar in view by name, keeping ~2.5 m behind them (stops at 2-3 m, resumes past 3.5 m, routes around furniture, handles doors); pass 'off' to stop. Optional distance_m (1-8) applies to this follow only.")]
    public static Task<string> Follow(string name, [Description("Optional follow distance in metres (1-8) for this follow; default is the persisted 'follow dist' (2.5 m).")] double? distance_m = null)
        => R("follow " + Clean(name) + (distance_m is double d && !Clean(name).Equals("off", StringComparison.OrdinalIgnoreCase) ? " " + d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : ""));

    [McpServerTool(Name = "get_profile"), Description("Read Galatay's own profile (About text, first-life text, image ids, URL, publish flags). Uses the AgentProfile capability, else legacy UDP.")]
    public static async Task<string> GetProfile() => Json(await Core.GetProfileObj());

    [McpServerTool(Name = "set_profile"), Description("Set ONLY the profile About (Second Life) text. Reads the current profile first (backup saved to /workspace/secondlife/profile-backup-*.json), updates just the About text (AgentProfile capability partial PUT, or legacy AvatarPropertiesUpdate resending all other fields unchanged), then reads it back and reports whether it matches and whether any other field changed.")]
    public static async Task<string> SetProfile([Description("New About text (max 4000 chars)")] string about_text) => Json(await Core.SetAboutObj(about_text));

    [McpServerTool(Name = "webhook_test"), Description("Send one test POST {kind:'test'} to the configured chat webhook and return the HTTP status, or 'not configured' if the URL file or key is missing. Counts toward the 15 s / 120-per-day webhook limits.")]
    public static Task<string> WebhookTest() => Webhook.Test();

    [McpServerTool(Name = "webhook_status"), Description("Show whether the chat webhook URL and key are configured (never shows the key), POSTs sent today, and pending batch size.")]
    public static string WebhookStatus() => Webhook.ConfigSummary();

    [McpServerTool(Name = "voice"), Description("LISTEN-ONLY Second Life voice (WebRTC; the parcel/region voice channel where I stand) with local speech-to-text: 'on' joins and transcribes to /workspace/secondlife/voice/transcript-<date>.md with speaker names (my mic is never sent), 'off' leaves, 'status' shows connection/wake mode/who is speaking, 'tail' returns transcript lines, 'wake off|name|all' controls chat-webhook wakes on voice (default name = only when a line mentions Galatea; replies go in local text chat). Off after every login; only turn it on when David asks.")]
    public static Task<string> Voice([Description("on | off | status | tail | wake")] string action = "status",
                                     [Description("tail: line count (default 15). wake: off|name|all|test|selftest")] string arg = "",
                                     [Description("tail only: number of lines (default 15, max 200)")] int lines = 15)
    {
        var a = Clean(action).ToLowerInvariant();
        var rest = Clean(arg);
        if (a == "wake") return R(string.IsNullOrEmpty(rest) ? "voice wake" : "voice wake " + rest);
        if (a is not ("on" or "off" or "status" or "tail")) return Task.FromResult(Json(new { ok = false, result = "action must be on, off, status, tail or wake" }));
        return R(a == "tail" ? $"voice tail {Math.Clamp(lines, 1, 200)}" : "voice " + a);
    }

    [McpServerTool(Name = "pay_object"), Description("Pay an in-world object (vendor) an exact L$ amount (MoneyTransferRequest, PayObject 5008). ONLY with David's explicit OK in chat for this exact object and amount. Refused above the GT_PAY_MAX cap (default L$2500), over the balance, or for an object not in view. Returns balance before/after, the MoneyBalanceReply and any new inventory offer. Never retry without checking 'balance' first.")]
    public static Task<string> PayObject([Description("object UUID")] string object_uuid, [Description("exact L$ amount")] int amount)
    {
        if (!LibreMetaverse.UUID.TryParse(Clean(object_uuid), out var u) || u == LibreMetaverse.UUID.Zero) return Task.FromResult(Json(new { ok = false, result = "refused: explicit object uuid required" }));
        if (amount < 1 || amount > Core.PayMax) return Task.FromResult(Json(new { ok = false, result = $"refused: amount must be 1..L${Core.PayMax} (GT_PAY_MAX)" }));
        return R($"pay object {u} {amount} confirm");
    }

    [McpServerTool(Name = "balance"), Description("Current L$ balance (read-only).")]
    public static Task<string> Balance() => R("balance");

    [McpServerTool(Name = "stop_animations"), Description("Stop every animation playing on me and pause the wander. A worn AO in dance mode restarts dances: toggle it on the HUD with touch_attachment first.")]
    public static Task<string> StopAnimations() => R("stopanims");

    [McpServerTool(Name = "touch_attachment"), Description("Touch one prim/face of a worn attachment or HUD ('ao' = the Vista AO HUD). link = link number or prim name; omit link to list the attachment's child prims (names, link numbers). Never for anything involving payment.")]
    public static Task<string> TouchAttachment([Description("attachment name, or 'ao'")] string attachment, [Description("link number or prim name; empty = list prims")] string link = "", int face = -1, [Description("optional touch UV 'u,v'")] string st = "")
    {
        var at = Clean(attachment).Replace("\"", ""); var l = Clean(link).Replace("\"", "");
        if (l.Length == 0) return R($"worn links \"{(at == "ao" ? "MARTHA STS BENTO AO" : at)}\"");
        return R($"touch-attachment \"{at}\" \"{l}\"{(face >= 0 ? " " + face : "")}{(Clean(st).Length > 0 ? " st=" + Clean(st) : "")}");
    }

    [McpServerTool(Name = "anim_list"), Description("List animations currently playing on me with their source.")]
    public static Task<string> AnimList() => R("anim list");

    [McpServerTool(Name = "offers"), Description("List pending inventory/friendship offers (id, sender, item, allow-listed or not). Read-only.")]
    public static Task<string> Offers([Description("pending (default) or all")] string which = "pending") => R(Clean(which) == "all" ? "offers all" : "offers list");

    [McpServerTool(Name = "accept_offer"), Description("Accept or decline ONE pending offer by id (from 'offers'). Accepting an offer from a sender that is not allow-listed needs confirm=true, i.e. David's explicit OK for that exact offer.")]
    public static Task<string> AcceptOffer([Description("offer id (#n from offers)")] int id, [Description("accept | decline")] string action = "accept", bool confirm = false)
    {
        var a = Clean(action).ToLowerInvariant();
        if (a is not ("accept" or "decline")) return Task.FromResult(Json(new { ok = false, result = "action must be accept or decline" }));
        return R($"offers {a} {id}{(a == "accept" && confirm ? " confirm" : "")}");
    }

    [McpServerTool(Name = "touch_object"), Description("Touch an in-world object by UUID (e.g. a vendor's redelivery menu); any menu arrives as a dialog event. Never use to buy or pay.")]
    public static Task<string> TouchObject([Description("object UUID")] string object_uuid)
    {
        if (!LibreMetaverse.UUID.TryParse(Clean(object_uuid), out var u) || u == LibreMetaverse.UUID.Zero) return Task.FromResult(Json(new { ok = false, result = "explicit object uuid required" }));
        return R($"touch {u}");
    }

    [McpServerTool(Name = "display_name"), Description("Read my SL display name (no args), or set it with new_name (SetDisplayName capability; SL allows one change per week, the reply gives the next allowed time). Changes nothing else.")]
    public static async Task<string> DisplayName([Description("empty = read only; else the exact new display name")] string new_name = "")
    {
        var n = (new_name ?? "").Trim();
        if (n.Length == 0) return Json(await Core.DisplayNameObj());
        if (n.IndexOfAny(new[] { '"', '\'', '\u2018', '\u2019', '\u201C', '\u201D', '`' }) >= 0) return Json(new { ok = false, result = "refused: quote characters in the name" });
        return Json(await Core.SetDisplayNameObj(n));
    }

    [McpServerTool(Name = "inv_find"), Description("READ-ONLY recursive inventory search (path, type, item id).")]
    public static Task<string> InvFind(string text) => R("inv find " + Clean(text));

    [McpServerTool(Name = "inv_ls"), Description("READ-ONLY direct contents of one inventory folder by uuid.")]
    public static Task<string> InvLs(string folder_uuid) => R("inv ls " + Clean(folder_uuid));

    [McpServerTool(Name = "rez_item"), Description("Rez one of MY Object items (e.g. a delivery box) 1.5 m in front of me. Never wears anything.")]
    public static Task<string> RezItem(string item_uuid) => R("rez " + Clean(item_uuid));

    [McpServerTool(Name = "take_object"), Description("Take back an object I own (action take = to Objects, delete = to Trash). Refused for objects I don't own.")]
    public static Task<string> TakeObject(string object_uuid, [Description("take | delete")] string action = "take")
        => R((Clean(action) == "delete" ? "delete " : "take ") + Clean(object_uuid));

    [McpServerTool(Name = "offer_allow"), Description("Accept, for N minutes (max 15), ONE inventory give from my own rezzed object with exactly this name (an unpacker box). Everything else stays pending.")]
    public static Task<string> OfferAllow(string object_name, int minutes = 5) => R($"offer allow {Clean(object_name)} {Math.Clamp(minutes, 1, 15)}");

    [McpServerTool(Name = "dialog_reply"), Description("Press a button on the most recent script dialog (e.g. an AVsitter pose menu). Never use for anything involving payment.")]
    public static Task<string> DialogReply(string button)
    {
        var b = Clean(button);
        if (b.Contains("L$") || b.Contains("pay", StringComparison.OrdinalIgnoreCase) || b.Contains("buy", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Json(new { ok = false, result = "refused: money-related button (spending is never allowed)" }));
        return R("dialog " + b);
    }
}
