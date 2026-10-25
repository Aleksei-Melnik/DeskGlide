namespace SdrCapture;

static class RecordingFrameSelection
{
    // Prefer the nearest picture when the buffered continuation can still
    // supply the following deadlines. Capture density can vary during a game;
    // switching policies at a density threshold would shift the motion phase.
    internal static int Index(ReadOnlySpan<long> times,long due,long previous,long tolerance,long period=0)
    {
        if(times.IsEmpty)return -1;
        if(tolerance>0&&previous>=0)
        {
            if(period<=0)period=tolerance;
            // A short burst is not sustained oversampling. Choosing its nearest
            // image may consume the only image for a following deadline. Only
            // skip chronological candidates when the buffered continuation can
            // supply the buffered output slots within the bound. Use only
            // fully buffered deadlines: requiring an unavailable fixed horizon
            // would toggle nearest/chronological pacing as readback age varies.
            // Inventory and feasibility must cover the same history. A fixed
            // eight-slot check can miss a gap followed by a later source burst.
            int lookahead=(int)Math.Clamp((times[^1]-due-tolerance)/period,0,times.Length);
            int first=0;
            while(first<times.Length&&(times[first]<=previous||times[first]<due-tolerance))first++;
            // Equal-rate jitter can borrow a future picture for several slots
            // before the finite continuation notices the debt. Only discard
            // pictures that exceed the remaining buffered output slots. Reserve
            // every slot that can fit in the jitter allowance, so early equal-
            // rate arrivals cannot be mistaken for extra source pictures.
            long slots=Math.Max(0,(times[^1]-due)/period+1);
            long reserve=(tolerance+period-1)/period;
            int last=first+(int)Math.Max(0,times.Length-first-slots-reserve);
            int best=-1;long distance=long.MaxValue;
            for(int i=0;i<times.Length;i++)
                if(times[i]>previous&&times[i]>=due-tolerance&&times[i]<=due+tolerance)
                {
                    if(lookahead<2)return i;
                    long error=Math.Abs(times[i]-due);
                    if(i<=last&&error<distance&&ContinuationFits(times,i,due,tolerance,period,lookahead)){distance=error;best=i;}
                }
            if(best>=0)return best;
            for(int i=0;i<times.Length;i++)
                if(times[i]>previous&&times[i]>=due-tolerance&&times[i]<=due+tolerance)return i;
        }
        for(int i=times.Length-1;i>=0;i--)if(times[i]<=due)return i;
        return 0;
    }
    static bool ContinuationFits(ReadOnlySpan<long> times,int at,long due,long tolerance,long period,int slots)
    {
        for(int slot=1;slot<=slots;slot++)
        {
            long target=due+period*slot;
            do{at++;}while(at<times.Length&&times[at]<target-tolerance);
            if(at==times.Length||times[at]>target+tolerance)return false;
        }
        return true;
    }
}
