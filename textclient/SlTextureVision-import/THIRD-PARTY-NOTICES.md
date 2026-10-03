# Third-party notices

SL Texture Vision links against the following third-party packages (not vendored; restored from NuGet
or referenced as a project). All are permissively licensed; none is GPL/AGPL/LGPL.

| Component | Use | License |
|---|---|---|
| LibreMetaverse (Sjofn LLC / openmetaverse.co) | Second Life protocol, texture fetch | BSD-3-Clause |
| CoreJ2K (via LibreMetaverse) | JPEG 2000 decoding, pure managed C# | BSD-3-Clause (plus JJ2000 permissive notice) |

The PNG encoder is original code in this repository (uses only System.IO.Compression from .NET, MIT).

Binary redistributions must include the BSD-3-Clause notices of LibreMetaverse and CoreJ2K.
