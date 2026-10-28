// The two extracted components, built once. Everything they need from Program goes through ProgramHost (HoverHost.cs).
namespace GalatayText;

public static partial class Program
{
    internal static readonly Hover hoverSvc = new(new ProgramHost());
    internal static readonly FeetAuto feetSvc = new(new ProgramHost());
}
