// Pay.cs (2026-10-04, David: pay a little rent on our Casper rental meter; its dialog says right-click > PAY)
// payprice <object uuid>                 read-only: RequestPayPrice -> the object's quick-pay buttons + default price
// pay object <uuid> <L$> confirm         GiveObjectMoney (PayObject, with the object's name, as the viewer's Pay dialog does).
//     Refused without 'confirm', for amount < 1, over the balance, when the balance is unknown, or for an object that is not
//     in view. Prints balance before/after and any chat/IM the object sends back within 10 s. Never automatic.
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static string PayPriceText(int v) => v == (int)PayPriceType.Hide ? "hidden" : v == (int)PayPriceType.Default ? "default" : $"L${v}";

    static async Task<(Primitive p, string err)> PayTarget(string idText)
    {
        if (!UUID.TryParse(idText, out var id)) return (null, "bad uuid");
        var sim = Sim; var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x.ID == id);
        if (p == null) return (null, "object not in view");
        await EnsureProperties(sim, new() { p });
        return (p, null);
    }

    static async Task<string> PayPriceCmd(string[] a)
    {
        if (a.Length < 1) return "usage: payprice <object uuid>";
        var (p, err) = await PayTarget(a[0]); if (err != null) return err;
        var tcs = new TaskCompletionSource<PayPriceReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, PayPriceReplyEventArgs e) { if (e.ObjectID == p.ID) tcs.TrySetResult(e); }
        client.Objects.PayPriceReply += H;
        try
        {
            client.Objects.RequestPayPrice(Sim, p.ID);
            if (await Task.WhenAny(tcs.Task, Task.Delay(8000)) != tcs.Task) return $"no PayPriceReply for '{p.Properties?.Name}' {p.ID} in 8 s (not payable, or no money() event)";
            var r = tcs.Task.Result;
            return $"payprice '{p.Properties?.Name}' {p.ID}: default (custom amount field) {PayPriceText(r.DefaultPrice)}; buttons [{string.Join(", ", r.ButtonPrices.Select(PayPriceText))}]";
        }
        finally { client.Objects.PayPriceReply -= H; }
    }

    // pure (selftest): why a payment is refused, or null
    public static string PayRefusal(int amount, bool confirm, int? balance) =>
        amount < 1 ? "amount must be >= L$1" : !confirm ? $"refused: paying L${amount} needs 'confirm' (David's explicit OK)" :
        balance == null ? "refused: balance unknown (no reply)" : amount > balance ? $"refused: L${amount} is over the balance L${balance}" : null;

    static async Task<string> PayCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest")
        {
            var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
            C(PayRefusal(10, false, 100)?.Contains("confirm") == true, "no 'confirm' -> refused");
            C(PayRefusal(101, true, 100)?.Contains("over the balance") == true, "over the balance -> refused");
            C(PayRefusal(0, true, 100) != null && PayRefusal(10, true, null) != null, "amount 0 / unknown balance -> refused");
            C(PayRefusal(100, true, 100) == null, "L$100 of L$100 with confirm -> allowed");
            return $"pay selftest: {pass} PASS, {fail} FAIL (pure; nothing paid)\n" + sb.ToString().TrimEnd();
        }
        if (a.Length < 3 || a[0] != "object" || !int.TryParse(a[2], out var amount)) return "usage: pay object <uuid> <L$> confirm | pay selftest";
        bool confirm = a.Skip(3).Any(x => x.Equals("confirm", StringComparison.OrdinalIgnoreCase));
        var pre = PayRefusal(amount, confirm, int.MaxValue); if (pre != null) { Log("pay", $"object {a[1]} L${amount}: {pre}"); return pre; }
        var (p, err) = await PayTarget(a[1]); if (err != null) return "refused: " + err;
        var name = p.Properties?.Name ?? "";
        var before = await BalanceAsync();
        var why = PayRefusal(amount, confirm, before); if (why != null) { Log("pay", $"'{name}' {p.ID} L${amount}: {why}"); return why; }
        var replies = new List<string>();
        void Ch(object s, ChatEventArgs e) { if (e.SourceID == p.ID || e.FromName == name) lock (replies) replies.Add($"chat {e.Type}: {e.Message}"); }
        void Im(object s, InstantMessageEventArgs e) { if (e.IM.FromAgentID == p.ID || e.IM.FromAgentName == name) lock (replies) replies.Add($"im ({e.IM.Dialog}): {e.IM.Message}"); }
        client.Self.ChatFromSimulator += Ch; client.Self.IM += Im;
        try
        {
            Log("pay", $"PAYING '{name}' {p.ID} L${amount} (balance before L${before})");
            client.Self.GiveObjectMoney(p.ID, amount, name);
            await Task.Delay(10000);
        }
        finally { client.Self.ChatFromSimulator -= Ch; client.Self.IM -= Im; }
        var after = await BalanceAsync();
        var res = $"paid '{name}' {p.ID} L${amount}: balance L${before} -> {(after.HasValue ? "L$" + after : "?")}" +
                  (after == before ? " (UNCHANGED: payment may have failed)" : "") +
                  $"; object replies in 10 s: {(replies.Count == 0 ? "none" : string.Join(" | ", replies))}";
        Log("pay", res); return res;
    }
}
