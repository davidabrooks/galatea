// Copyright (c) 2026 David Brooks. All rights reserved. Proprietary; see LICENSE.
// sl-texture-vision CLI
//   decode <in.j2c> <out.png>                         offline JPEG 2000 -> PNG
//   texture <uuid> [outdir]                           log in, save one texture
//   faces <name-or-uuid> [outdir]                     log in, save an object's face textures
//   scan <name-filter> [radius] [outdir]              log in, dump nearby matching objects + index.json
// Online commands log in with SL_FIRST, SL_LAST and SL_PASSWORD from the environment (password never on argv),
// optional SL_START (uri:Region&x&y&z or "last"). Do NOT use an account that is already logged in elsewhere:
// a second login kicks the first session. Inside an existing client, use the library directly instead.
using LibreMetaverse;
using SlTextureVision;

if (args.Length == 0) { Console.Error.WriteLine("usage: sl-texture-vision decode <in.j2c> <out.png> | texture <uuid> [outdir] | faces <name|uuid> [outdir] | scan <filter> [radius] [outdir]"); return 2; }
if (args[0] == "decode" && args.Length >= 3)
{
    var (png, w, h, c) = TextureVision.DecodeJ2cToPng(File.ReadAllBytes(args[1]));
    File.WriteAllBytes(args[2], png);
    Console.WriteLine($"{args[2]}  {w}x{h}  {c} components");
    return 0;
}
string first = Environment.GetEnvironmentVariable("SL_FIRST") ?? "", last = Environment.GetEnvironmentVariable("SL_LAST") ?? "Resident",
       pass = Environment.GetEnvironmentVariable("SL_PASSWORD") ?? "", start = Environment.GetEnvironmentVariable("SL_START") ?? "last";
if (first == "" || pass == "") { Console.Error.WriteLine("set SL_FIRST, SL_LAST, SL_PASSWORD"); return 2; }
var client = new GridClient();
var lp = client.Network.DefaultLoginParams(first, last, pass, "sl-texture-vision", "0.1.0");
lp.Start = start;
if (!await client.Network.LoginAsync(lp)) { Console.Error.WriteLine("login failed: " + client.Network.LoginMessage); return 3; }
try
{
    await Task.Delay(8000); // let the object list fill in
    string Arg(int i, string d) => args.Length > i ? args[i] : d;
    switch (args[0])
    {
        case "texture":
        {
            var tv = new TextureVision(client, new() { OutputDirectory = Arg(2, ".") });
            var r = await tv.SaveTextureAsync(new UUID(args[1]));
            Console.WriteLine(r.Ok ? $"{r.PngPath}  {r.Width}x{r.Height}" : "error: " + r.Error);
            return r.Ok ? 0 : 1;
        }
        case "faces":
        {
            var tv = new TextureVision(client, new() { OutputDirectory = Arg(2, ".") });
            var p = await tv.FindObjectAsync(args[1]);
            if (p == null) { Console.Error.WriteLine("object not found"); return 1; }
            Console.WriteLine(TextureVision.ToJson(await tv.LookAtAsync(p)));
            return 0;
        }
        case "scan":
        {
            var tv = new TextureVision(client, new() { OutputDirectory = Arg(3, ".") });
            var (folder, looks) = await tv.ScanAsync(args[1], float.Parse(Arg(2, "20"), System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine($"{folder}  ({looks.Count} objects, {looks.Sum(l => l.Saved.Count(s => s.Ok))} PNGs)");
            return 0;
        }
        default: Console.Error.WriteLine("unknown command"); return 2;
    }
}
finally { await client.Network.LogoutAsync(); }
