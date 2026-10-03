// SL Texture Vision - part of galatea (https://github.com/davidabrooks/galatea). Same licence terms as the rest of the galatea repository.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreMetaverse;
using LibreMetaverse.Assets;

namespace SlTextureVision;

/// <summary>Result of saving one texture.</summary>
public sealed record SavedTexture(UUID TextureId, string? PngPath, int Width, int Height, int Components, string? Error)
{
    public bool Ok => PngPath != null && Error == null;
}

/// <summary>One face of one prim with its texture.</summary>
public sealed record FaceInfo(UUID PrimId, uint LocalId, int LinkIndex, string Face, UUID TextureId, Color4 Color, bool Skipped, string? SkipReason);

/// <summary>An object (linkset root) with its faces and saved images.</summary>
public sealed class ObjectLook
{
    public UUID Id { get; init; }
    public uint LocalId { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string HoverText { get; set; } = "";
    public UUID OwnerId { get; set; }
    public float Distance { get; set; }
    public Vector3 Position { get; set; }
    public int PrimCount { get; set; }
    public List<FaceInfo> Faces { get; } = new();
    public List<SavedTexture> Saved { get; } = new();
}

/// <summary>Options for <see cref="TextureVision"/>.</summary>
public sealed class TextureVisionOptions
{
    /// <summary>Folder where PNGs are written (created if missing).</summary>
    public string OutputDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "sl-texture-vision");
    /// <summary>Per-texture download timeout.</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>Also write the raw JPEG 2000 (.j2c) next to each PNG.</summary>
    public bool KeepJ2c { get; set; }
    /// <summary>Skip textures smaller than this on both sides (e.g. 1x1 / 4x4 tint helpers).</summary>
    public int MinSide { get; set; } = 8;
    /// <summary>Max textures downloaded in parallel.</summary>
    public int MaxParallel { get; set; } = 4;
}

/// <summary>
/// Lets a headless (text-only) Second Life client "see" textures: download any texture by UUID and decode it to PNG,
/// list the faces of an in-world object with their texture UUIDs, and dump the images of nearby vendor boards.
/// Needs only a logged-in LibreMetaverse <see cref="GridClient"/>; no GPU, no viewer, no native libraries.
/// </summary>
public sealed class TextureVision
{
    /// <summary>Well-known textures that carry no visual information (blank, plywood, transparent, invisible, media).</summary>
    public static readonly IReadOnlyDictionary<UUID, string> KnownBlank = new Dictionary<UUID, string>
    {
        [UUID.Zero] = "none",
        [new UUID("89556747-24cb-43ed-920b-47caed15465f")] = "default plywood",
        [new UUID("5748decc-f629-461c-9a36-a35a221fe21f")] = "blank white",
        [new UUID("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903")] = "transparent",
        [new UUID("38b86f85-2575-52a9-a531-23108d8da837")] = "invisible",
        [new UUID("e97cf410-8e61-7005-ec06-629eba4cd1fb")] = "transparent (alt)",
        [new UUID("f54a0c32-3cd1-d49a-5b4f-7b792bebc204")] = "white (alt)",
        [new UUID("3a367d1c-bef1-6d43-7595-e88c1e3aadb3")] = "invisible (alt)",
        [new UUID("6522e74d-1660-4e7f-b601-6f48c1659a77")] = "media placeholder",
    };

    readonly GridClient client;
    readonly ConcurrentDictionary<UUID, SavedTexture> cache = new();

    public TextureVisionOptions Options { get; }

    public TextureVision(GridClient client, TextureVisionOptions? options = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        Options = options ?? new TextureVisionOptions();
    }

    /// <summary>Why a texture would be skipped, or null if it is worth looking at.</summary>
    public static string? SkipReason(UUID id, Color4 tint)
    {
        if (KnownBlank.TryGetValue(id, out var why)) return why;
        if (tint.A <= 0.001f) return "fully transparent face";
        return null;
    }

    /// <summary>Decode JPEG 2000 bytes (an SL texture asset) to PNG bytes. Pure managed (CoreJ2K via LibreMetaverse).</summary>
    public static (byte[] png, int width, int height, int components) DecodeJ2cToPng(byte[] j2c)
    {
        var tex = new AssetTexture(UUID.Zero, j2c);
        if (!tex.Decode() || tex.Image == null) throw new InvalidDataException("JPEG 2000 decode failed");
        return (PngWriter.Encode(tex.Image), tex.Image.Width, tex.Image.Height, tex.Components);
    }

    /// <summary>Download a texture by UUID and save it as PNG. Returns the saved path or an error. Results are cached per UUID.</summary>
    public async Task<SavedTexture> SaveTextureAsync(UUID textureId, string? fileStem = null, CancellationToken ct = default)
    {
        if (cache.TryGetValue(textureId, out var hit) && hit.Ok && File.Exists(hit.PngPath)) return hit;
        if (textureId == UUID.Zero) return new SavedTexture(textureId, null, 0, 0, 0, "zero UUID");
        Directory.CreateDirectory(Options.OutputDirectory);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Options.DownloadTimeout);
        AssetTexture? asset;
        try { asset = await client.Assets.RequestImageAsync(textureId, ImageType.Normal, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new SavedTexture(textureId, null, 0, 0, 0, "download timed out"); }
        catch (Exception ex) { return new SavedTexture(textureId, null, 0, 0, 0, "download failed: " + ex.GetBaseException().Message); }
        if (asset?.AssetData == null || asset.AssetData.Length == 0) return new SavedTexture(textureId, null, 0, 0, 0, "not found / no permission to fetch");
        try
        {
            var (png, w, h, comps) = DecodeJ2cToPng(asset.AssetData);
            var stem = SafeName(fileStem ?? textureId.ToString());
            var path = Path.Combine(Options.OutputDirectory, stem + ".png");
            await File.WriteAllBytesAsync(path, png, ct).ConfigureAwait(false);
            if (Options.KeepJ2c) await File.WriteAllBytesAsync(Path.ChangeExtension(path, ".j2c"), asset.AssetData, ct).ConfigureAwait(false);
            var r = new SavedTexture(textureId, path, w, h, comps, null);
            cache[textureId] = r;
            return r;
        }
        catch (Exception ex) { return new SavedTexture(textureId, null, 0, 0, 0, "decode failed: " + ex.GetBaseException().Message); }
    }

    /// <summary>All prims of the linkset that <paramref name="prim"/> belongs to (root first).</summary>
    public static List<Primitive> Linkset(Simulator sim, Primitive prim)
    {
        var root = prim;
        if (prim.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(prim.ParentID, out var r)) root = r;
        var list = new List<Primitive> { root };
        list.AddRange(sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == root.LocalID && p.PrimData.PCode == PCode.Prim).OrderBy(p => p.LocalID));
        return list;
    }

    /// <summary>List the faces of a prim/linkset with their texture UUIDs. Faces without an override report the prim's default texture as face "default".</summary>
    public static List<FaceInfo> ListFaces(Simulator sim, Primitive prim, bool wholeLinkset = true)
    {
        var res = new List<FaceInfo>();
        var prims = wholeLinkset ? Linkset(sim, prim) : new List<Primitive> { prim };
        for (int li = 0; li < prims.Count; li++)
        {
            var p = prims[li]; var te = p.Textures;
            if (te == null) continue;
            if (te.DefaultTexture != null)
            {
                var d = te.DefaultTexture;
                var why = SkipReason(d.TextureID, d.RGBA);
                res.Add(new FaceInfo(p.ID, p.LocalID, li, "default", d.TextureID, d.RGBA, why != null, why));
            }
            for (int f = 0; f < te.FaceTextures.Length; f++)
            {
                var ft = te.FaceTextures[f];
                if (ft == null) continue;
                var why = SkipReason(ft.TextureID, ft.RGBA);
                res.Add(new FaceInfo(p.ID, p.LocalID, li, f.ToString(), ft.TextureID, ft.RGBA, why != null, why));
            }
        }
        return res;
    }

    /// <summary>Request names / descriptions (ObjectProperties) for prims that do not have them yet.</summary>
    public async Task EnsurePropertiesAsync(Simulator sim, IReadOnlyCollection<Primitive> prims, CancellationToken ct = default)
    {
        var need = prims.Where(p => p.Properties == null).ToList();
        if (need.Count == 0) return;
        var waiting = new HashSet<UUID>(need.Select(p => p.ID));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object? s, ObjectPropertiesEventArgs e) { lock (waiting) { waiting.Remove(e.Properties.ObjectID); if (waiting.Count == 0) tcs.TrySetResult(true); } }
        client.Objects.ObjectProperties += H;
        try
        {
            foreach (var chunk in need.Chunk(50)) client.Objects.SelectObjects(sim, chunk.Select(p => p.LocalID).ToArray(), true);
            await Task.WhenAny(tcs.Task, Task.Delay(Math.Min(15000, 2000 + 20 * need.Count), ct)).ConfigureAwait(false);
        }
        finally
        {
            client.Objects.ObjectProperties -= H;
            try { foreach (var chunk in need.Chunk(50)) client.Objects.DeselectObjects(sim, chunk.Select(p => p.LocalID).ToArray()); } catch { }
        }
    }

    /// <summary>Find a nearby object by UUID or by (case-insensitive) name substring; nearest match wins.</summary>
    public async Task<Primitive?> FindObjectAsync(string nameOrId, float radius = 96, CancellationToken ct = default)
    {
        var sim = client.Network.CurrentSim ?? throw new InvalidOperationException("not connected to a region");
        nameOrId = nameOrId.Trim().Trim('"');
        if (UUID.TryParse(nameOrId, out var id))
            return sim.ObjectsPrimitives.Values.FirstOrDefault(p => p != null && p.ID == id);
        var hits = await NearbyAsync(nameOrId, radius, 1, ct).ConfigureAwait(false);
        return hits.FirstOrDefault().prim;
    }

    /// <summary>Root prims within <paramref name="radius"/> m whose name contains <paramref name="nameFilter"/> (null/empty = all), nearest first.</summary>
    public async Task<List<(Primitive prim, float distance)>> NearbyAsync(string? nameFilter, float radius, int max = 50, CancellationToken ct = default)
    {
        var sim = client.Network.CurrentSim ?? throw new InvalidOperationException("not connected to a region");
        var me = client.Self.SimPosition;
        var roots = sim.ObjectsPrimitives.Values
            .Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim)
            .Select(p => (prim: p, distance: Vector3.Distance(p.Position, me)))
            .Where(t => t.distance <= radius).OrderBy(t => t.distance).Take(3000).ToList();
        await EnsurePropertiesAsync(sim, roots.Select(t => t.prim).ToList(), ct).ConfigureAwait(false);
        return roots.Where(t => string.IsNullOrWhiteSpace(nameFilter)
                                || (t.prim.Properties?.Name ?? "").Contains(nameFilter!, StringComparison.OrdinalIgnoreCase)
                                || (t.prim.Text ?? "").Contains(nameFilter!, StringComparison.OrdinalIgnoreCase))
                    .Take(max).ToList();
    }

    /// <summary>Describe an object and save its face textures (all non-blank faces, or only <paramref name="onlyFace"/>, e.g. "0", "3", "default").</summary>
    public async Task<ObjectLook> LookAtAsync(Primitive prim, string? onlyFace = null, string? subFolder = null, CancellationToken ct = default)
    {
        var sim = client.Network.CurrentSim ?? throw new InvalidOperationException("not connected to a region");
        var set = Linkset(sim, prim);
        await EnsurePropertiesAsync(sim, set.Take(1).ToList(), ct).ConfigureAwait(false);
        var root = set[0];
        var look = new ObjectLook
        {
            Id = root.ID, LocalId = root.LocalID, Name = root.Properties?.Name ?? "", Description = root.Properties?.Description ?? "",
            HoverText = root.Text ?? "", OwnerId = root.Properties?.OwnerID ?? UUID.Zero, Position = root.Position,
            Distance = Vector3.Distance(root.Position, client.Self.SimPosition), PrimCount = set.Count,
        };
        look.Faces.AddRange(ListFaces(sim, root));
        var want = look.Faces.Where(f => !f.Skipped && (onlyFace == null || f.Face == onlyFace)).Select(f => f.TextureId).Distinct().ToList();
        var outDir = subFolder == null ? Options.OutputDirectory : Path.Combine(Options.OutputDirectory, SafeName(subFolder));
        var sub = new TextureVision(client, new TextureVisionOptions { OutputDirectory = outDir, DownloadTimeout = Options.DownloadTimeout, KeepJ2c = Options.KeepJ2c, MinSide = Options.MinSide, MaxParallel = Options.MaxParallel });
        foreach (var kv in cache) sub.cache[kv.Key] = kv.Value;
        using var gate = new SemaphoreSlim(Math.Max(1, Options.MaxParallel));
        var tasks = want.Select(async t =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await sub.SaveTextureAsync(t, null, ct).ConfigureAwait(false); } finally { gate.Release(); }
        }).ToList();
        foreach (var r in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (r.Ok && r.Width < Options.MinSide && r.Height < Options.MinSide) { look.Saved.Add(r with { Error = $"tiny {r.Width}x{r.Height} texture skipped" }); continue; }
            look.Saved.Add(r);
            if (r.Ok) cache[r.TextureId] = r;
        }
        return look;
    }

    /// <summary>Scan nearby objects matching <paramref name="nameFilter"/> (name or hover text), save their face textures into a
    /// timestamped folder and write index.json describing every object, face and PNG. Returns the folder and the looks.</summary>
    public async Task<(string folder, List<ObjectLook> looks)> ScanAsync(string? nameFilter, float radius = 20, int maxObjects = 12, CancellationToken ct = default)
    {
        var folder = Path.Combine(Options.OutputDirectory, "scan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (string.IsNullOrWhiteSpace(nameFilter) ? "" : "-" + SafeName(nameFilter!)));
        Directory.CreateDirectory(folder);
        var scanner = new TextureVision(client, new TextureVisionOptions { OutputDirectory = folder, DownloadTimeout = Options.DownloadTimeout, KeepJ2c = Options.KeepJ2c, MinSide = Options.MinSide, MaxParallel = Options.MaxParallel });
        var looks = new List<ObjectLook>();
        foreach (var (prim, _) in await NearbyAsync(nameFilter, radius, maxObjects, ct).ConfigureAwait(false))
            looks.Add(await scanner.LookAtAsync(prim, null, null, ct).ConfigureAwait(false));
        await File.WriteAllTextAsync(Path.Combine(folder, "index.json"), ToJson(new { filter = nameFilter, radius, region = client.Network.CurrentSim?.Name, at = DateTimeOffset.Now, objects = looks }), ct).ConfigureAwait(false);
        return (folder, looks);
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new ToStringConverter<UUID>(), new ToStringConverter<Vector3>(), new ToStringConverter<Color4>() },
    };
    /// <summary>Serialize results (UUIDs, vectors and colors as strings).</summary>
    public static string ToJson(object o) => JsonSerializer.Serialize(o, Json);

    sealed class ToStringConverter<T> : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter w, T v, JsonSerializerOptions o) => w.WriteStringValue(v?.ToString());
    }

    /// <summary>File-system-safe short name.</summary>
    public static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\', ':' }).ToHashSet();
        var t = new string(s.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return t.Length > 60 ? t[..60] : (t.Length == 0 ? "x" : t);
    }
}
