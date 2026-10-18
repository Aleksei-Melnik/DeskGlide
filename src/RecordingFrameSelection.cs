namespace SdrCapture;

static class RecordingFrameSelection
{
    // Capture and recording have independent clock phases. Preserve chronological
    // source frames through ordinary timestamp jitter, rather than alternating
    // between repeating one image and dropping the following available image.
    internal static int Index(ReadOnlySpan<long> times,long due,long previous,long tolerance)
    {
        if(times.IsEmpty)return -1;
        if(tolerance>0&&previous>=0)
            for(int i=0;i<times.Length;i++)
                if(times[i]>previous&&times[i]>=due-tolerance&&times[i]<=due+tolerance)return i;
        for(int i=times.Length-1;i>=0;i--)if(times[i]<=due)return i;
        return 0;
    }
}
