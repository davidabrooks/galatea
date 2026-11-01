// The two extracted components, built once. Everything they need from Program goes through ProgramHost (HoverHost.cs).
namespace GalatayText;

public static partial class Program
{
    internal static readonly Hover hoverSvc = new(new ProgramHost());
    // a verified feet fit asks for a stand hover check, so the hover is measured on the settled foot pose (PoseHover.StandFeetBlock)
    internal static readonly FeetAuto feetSvc = new(new ProgramHost()) { FeetVerified = _ => hoverSvc.HoverCheckRequest("feet verified", 1500) };
}
