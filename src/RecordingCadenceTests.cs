namespace SdrCapture;

static class RecordingCadenceTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        int legacyRepeats=0,actualRepeats=0;
        foreach(int fps in new[]{60,120})foreach(long phase in new long[]{0,1000,4000,7000,10000,15000})
        {
            long period=1000000/fps;
            long[] source=Enumerable.Range(0,600).Select(i=>1000000L+i*period+(i%2==0?-1200:1200)).ToArray();
            long oldPrevious=-1,newPrevious=-1;int oldIndex=-1,newIndex=-1;
            for(int frame=10;frame<580;frame++)
            {
                long due=1000000L+frame*period+phase;
                int a=RecordingFrameSelection.Index(source,due,oldPrevious,0);
                int b=RecordingFrameSelection.Index(source,due,newPrevious,period);
                if(a==oldIndex)legacyRepeats++;if(b==newIndex)actualRepeats++;
                Require(b>=newIndex,"Jitter recovery reordered source frames.");
                oldPrevious=source[a];newPrevious=source[b];oldIndex=a;newIndex=b;
            }
        }
        Require(legacyRepeats>100&&actualRepeats==0,"Jitter resampling did not remove artificial repeat/drop pairs.");
        // Relative times from a real 120 FPS desktop/NDI trace: the 3/4-period
        // window discarded the -8.689 ms image and repeated its successor.
        // All three images were available before encoding (100 ms playout).
        long[] observed=[991311,1000000,1012438,1020425];
        long prior=980000,legacyPrior=prior;int repeats=0,legacyWindowRepeats=0;
        for(int frame=0;frame<3;frame++)
        {
            long due=997751+frame*8333;
            long selected=observed[RecordingFrameSelection.Index(observed,due,prior,8333)];
            long legacy=observed[RecordingFrameSelection.Index(observed,due,legacyPrior,6249)];
            if(selected==prior)repeats++;if(legacy==legacyPrior)legacyWindowRepeats++;
            Require(selected==observed[frame],"Available jittered source image was discarded at an output boundary.");
            prior=selected;legacyPrior=legacy;
        }
        Require(repeats==0&&legacyWindowRepeats==1,"Real 120 FPS trace did not reproduce and eliminate the narrow-window repeat.");
        Require(RecordingFrameSelection.Index([],100,-1,20)==-1,"Empty source history accepted.");
        Require(RecordingFrameSelection.Index([10,20,100],50,20,10)==1,"Missing source footage was replaced with a distant future frame.");
        Program.Write("recording-cadence-tests.json",new{Pass=true,Fps=new[]{60,120},LegacyArtificialRepeats=legacyRepeats,NewArtificialRepeats=actualRepeats,NarrowWindowRepeats=legacyWindowRepeats,RealTraceRepeats=repeats,SourceOrderPreserved=true,RealSourceGapsNotInvented=true});
    }
}
