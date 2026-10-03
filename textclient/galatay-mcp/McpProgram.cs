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
        Webhook.Init();
        if (Environment.GetEnvironmentVariable("GT_AUTOLOGIN") == "1")
            _ = Task.Run(async () => await Core.LoginAsync());
        await app.RunAsync();
        if (Core.LoggedIn) await Core.Shutdown("MCP host closed stdin");
        return 0;
    }
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

    [McpServerTool(Name = "im"), Description("Send an instant message to an avatar by legacy name ('First Last'), username, or UUID. Rate-limited.")]
    public static Task<string> Im([Description("Avatar name or UUID")] string to, string text) => R($"im \"{Clean(to).Replace("\"", "")}\" {Clean(text)}");

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

    [McpServerTool(Name = "walk_to"), Description("Walk (server autopilot) to region-local coordinates. Straight line; may stop at obstacles. Check status afterwards.")]
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

    [McpServerTool(Name = "follow"), Description("Follow an avatar in view by name; pass 'off' to stop.")]
    public static Task<string> Follow(string name) => R("follow " + Clean(name));

    [McpServerTool(Name = "get_profile"), Description("Read Galatay's own profile (About text, first-life text, image ids, URL, publish flags). Uses the AgentProfile capability, else legacy UDP.")]
    public static async Task<string> GetProfile() => Json(await Core.GetProfileObj());

    [McpServerTool(Name = "set_profile"), Description("Set ONLY the profile About (Second Life) text. Reads the current profile first (backup saved to /workspace/secondlife/profile-backup-*.json), updates just the About text (AgentProfile capability partial PUT, or legacy AvatarPropertiesUpdate resending all other fields unchanged), then reads it back and reports whether it matches and whether any other field changed.")]
    public static async Task<string> SetProfile([Description("New About text (max 4000 chars)")] string about_text) => Json(await Core.SetAboutObj(about_text));

    [McpServerTool(Name = "webhook_test"), Description("Send one test POST {kind:'test'} to the configured chat webhook and return the HTTP status, or 'not configured' if the URL file or key is missing. Counts toward the 15 s / 120-per-day webhook limits.")]
    public static Task<string> WebhookTest() => Webhook.Test();

    [McpServerTool(Name = "webhook_status"), Description("Show whether the chat webhook URL and key are configured (never shows the key), POSTs sent today, and pending batch size.")]
    public static string WebhookStatus() => Webhook.ConfigSummary();

    [McpServerTool(Name = "dialog_reply"), Description("Press a button on the most recent script dialog (e.g. an AVsitter pose menu). Never use for anything involving payment.")]
    public static Task<string> DialogReply(string button)
    {
        var b = Clean(button);
        if (b.Contains("L$") || b.Contains("pay", StringComparison.OrdinalIgnoreCase) || b.Contains("buy", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Json(new { ok = false, result = "refused: money-related button (spending is never allowed)" }));
        return R("dialog " + b);
    }
}
