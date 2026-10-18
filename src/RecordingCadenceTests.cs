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
                int b=RecordingFrameSelection.Index(source,due,newPrevious,period*3/4);
                if(a==oldIndex)legacyRepeats++;if(b==newIndex)actualRepeats++;
                Require(b>=newIndex,"Jitter recovery reordered source frames.");
                oldPrevious=source[a];newPrevious=source[b];oldIndex=a;newIndex=b;
            }
        }
        Require(legacyRepeats>100&&actualRepeats==0,"Jitter resampling did not remove artificial repeat/drop pairs.");
        Require(RecordingFrameSelection.Index([],100,-1,20)==-1,"Empty source history accepted.");
        Require(RecordingFrameSelection.Index([10,20,100],50,20,10)==1,"Missing source footage was replaced with a distant future frame.");
        Program.Write("recording-cadence-tests.json",new{Pass=true,Fps=new[]{60,120},LegacyArtificialRepeats=legacyRepeats,NewArtificialRepeats=actualRepeats,SourceOrderPreserved=true,RealSourceGapsNotInvented=true});
    }
}
