namespace SdrCapture;

static class ReplaySaveTime
{
    // Retain the exact frame boundary internally. Feeding a rounded 16.667-ms
    // boundary back into Ceiling would consume one extra frame on the next save.
    internal static double Boundary(double seconds,int fps)=>Math.Ceiling(Math.Max(0,seconds)*fps-1e-5)/fps;
    // Matroska stores packet timestamps in milliseconds; match it only at export.
    internal static double Packet(double seconds)=>Math.Round(seconds,3,MidpointRounding.AwayFromZero);
}
