// LoadUrlLog.cs (2026-10-15, David: touching the CasperSafe orb's CTRL PANEL sends an llLoadURL (visitor-log website) that the client
// silently dropped). Subscribes to Self.LoadURL: every llLoadURL is written as a "[loadurl]" line to textclient.log and the latest one to
// last-loadurl.txt (mode 600). The URL may carry a token, so it deliberately does NOT go through Log(): no console echo, no events queue
// (poll_events), no webhook, no fast-chat history. Nothing here clicks or fetches the URL.
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string LastLoadUrlFile = Env("GT_LAST_LOADURL", Path.Combine(Path.GetDirectoryName(LogPath) ?? ".", "last-loadurl.txt"));

    static string OneLine(string? s) => (s ?? "").Replace("\r", " ").Replace("\n", " ");

    // pure (test): the text after "[loadurl] "
    internal static string LoadUrlMsg(string objectName, UUID objectId, UUID ownerId, bool ownerIsGroup, string message, string url) =>
        $"object '{OneLine(objectName)}' {objectId} owner {ownerId}{(ownerIsGroup ? " (group)" : "")} message '{OneLine(message)}' url {OneLine(url)}";

    // pure (test): the whole last-loadurl.txt
    internal static string LoadUrlFileText(DateTime now, string objectName, UUID objectId, UUID ownerId, bool ownerIsGroup, string message, string url) =>
        $"time: {now:yyyy-MM-dd HH:mm:ss}\nobject: {OneLine(objectName)} ({objectId})\nowner: {ownerId}{(ownerIsGroup ? " (group)" : "")}\nmessage: {OneLine(message)}\nurl: {OneLine(url)}\n";

    static void OnLoadUrl(object? sender, LoadUrlEventArgs e)
    {
        try
        {
            var now = DateTime.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss} [loadurl] {LoadUrlMsg(e.ObjectName, e.ObjectID, e.OwnerID, e.OwnerIsGroup, e.Message, e.URL)}";
            lock (logLock) { try { File.AppendAllText(LogPath, line + "\n"); } catch { } }
            WriteLastLoadUrl(LastLoadUrlFile, LoadUrlFileText(now, e.ObjectName, e.ObjectID, e.OwnerID, e.OwnerIsGroup, e.Message, e.URL));
        }
        catch (Exception ex) { Log("loadurl", "handler error: " + ex.GetType().Name); }
    }

    // temp file created 0600 then renamed over the target (never world-readable, not torn)
    internal static void WriteLastLoadUrl(string path, string text)
    {
        var tmp = path + ".tmp";
        try { File.Delete(tmp); } catch { }
        using (var fs = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            fs.Write(Encoding.UTF8.GetBytes(text));
        File.Move(tmp, path, true);
    }

    static void HookLoadUrl() => client.Self.LoadURL += OnLoadUrl;
}
