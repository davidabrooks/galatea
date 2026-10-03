# SL Texture Vision

**Proprietary. Copyright (c) 2026 David Brooks. All rights reserved.** (See [LICENSE](LICENSE).)

The first product in a family of **AI-agent helpers for Second Life**: small, separately licensed libraries that fill
the gaps headless clients have compared with a full graphical viewer.

Text-only SL clients (LibreMetaverse bots, MCP connectors, AI agents) cannot *see* anything. Yet most of what matters
when shopping or exploring is in textures: vendor boards, product photos, colour/tone swatches, signs, notecard-style
posters on prims. SL Texture Vision gives such a client eyes for those, **without a GPU, a viewer or native libraries**:

* **Fetch any texture by UUID** and decode its JPEG 2000 data to a normal **PNG** (pure managed CoreJ2K decoder +
  built-in PNG encoder, so it runs anywhere .NET runs: Linux servers, containers, CI).
* **List an object's faces** (whole linkset) with their texture UUIDs and tints, and save every face texture or a
  chosen face as PNG.
* **Scan nearby objects** by a name/hover-text filter (e.g. `vendor`, `skin`, `tone`), save their face textures and
  write an `index.json` (name, description, hover text, owner, position, faces, PNG paths) that an AI model can read
  alongside the images.
* Skips blank, plywood, transparent, invisible and media placeholder textures and fully transparent faces.

It only uses what the SL protocol already gives every viewer: textures on in-world faces are fetched through the
normal texture service. It never buys, pays, touches or edits anything.

## Requirements

* .NET 8 or 10
* A logged-in LibreMetaverse `GridClient` (LibreMetaverse 3.1.5+ from NuGet by default)

## Library usage

```csharp
using SlTextureVision;

var tv = new TextureVision(client, new TextureVisionOptions { OutputDirectory = "/data/textures" });

// 1. one texture
var r = await tv.SaveTextureAsync(new UUID("..."));          // r.PngPath, r.Width, r.Height

// 2. an object's faces
var prim = await tv.FindObjectAsync("Skin Vendor", radius: 30);   // name substring or UUID, nearest wins
var look = await tv.LookAtAsync(prim!);                          // look.Faces, look.Saved (PNGs), look.HoverText
var face3 = await tv.LookAtAsync(prim!, onlyFace: "3");

// 3. scan nearby vendors -> folder with PNGs + index.json
var (folder, looks) = await tv.ScanAsync("vendor", radius: 20, maxObjects: 12);

// offline
var (png, w, h, comps) = TextureVision.DecodeJ2cToPng(File.ReadAllBytes("x.j2c"));
```

### Using it in a client that builds LibreMetaverse from source

To avoid two copies of LibreMetaverse, point the library at your project:

```xml
<ProjectReference Include="path/to/sl-texture-vision/src/SlTextureVision/SlTextureVision.csproj"
                  AdditionalProperties="LibreMetaverseProject=$(MSBuildThisFileDirectory)path/to/LibreMetaverse.csproj" />
```

## CLI

```
sl-texture-vision decode <in.j2c> <out.png>
sl-texture-vision texture <uuid> [outdir]
sl-texture-vision faces <name|uuid> [outdir]
sl-texture-vision scan <filter> [radius] [outdir]
```

Online commands log in with `SL_FIRST`, `SL_LAST`, `SL_PASSWORD` (and optional `SL_START`) from the environment;
the password is never taken on the command line. Logging in kicks any other session of the same account, so for an
account already in-world (e.g. an AI avatar's main client) embed the library in that client instead.

## Build

```
dotnet build -c Release
```

## Third-party components

LibreMetaverse (BSD-3-Clause) and CoreJ2K (BSD-3-Clause). No GPL, AGPL or LGPL code. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
