using GalatayText;

/// <summary>Test-only builders for roam fixtures (the live client builds prim boxes with RoamPrimWorldBox).</summary>
internal static class RoamFixtures
{
    /// <summary>An axis-aligned floor box centred on (cx, cy), sx by sy across, with its top at <paramref name="top"/>.</summary>
    internal static Program.RoamPrim PrimBox(float cx, float cy, float sx, float sy, float top) =>
        new(cx - sx * 0.5f, cy - sy * 0.5f, cx + sx * 0.5f, cy + sy * 0.5f, top);
}
