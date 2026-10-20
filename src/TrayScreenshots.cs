namespace SdrCapture;

sealed partial class TrayApp
{
    readonly ScreenshotHotkey screenshotHotkey=new();
    readonly CancellationTokenSource screenshotLifetime=new();
    ScreenshotEditor? screenshotEditor;
    bool screenshotBusy;
    async void TakeScreenshot()
    {
        if(closing||screenshotBusy)return;
        if(screenshotEditor is {IsDisposed:false}){screenshotEditor.Activate();return;}
        screenshotBusy=true;ScreenshotFrame? frame=null;bool paused=false;
        try
        {
            // Freeze before showing any overlay. Do not stop recording, change HDR
            // settings, inject input or include our editor in the captured image.
            var token=screenshotLifetime.Token;
            frame=await Task.Run(()=>ScreenshotFrame.Capture(token),token);
            if(closing){frame.Dispose();frame=null;return;}
            // Block new edge activations before the queued return-to-local is
            // processed; reversing these can race a physical edge movement.
            if(controller!=null)controller.ViewerActive=true;paused=true;controller?.ReturnLocal();
            var ownedFrame=frame;var editor=new ScreenshotEditor(ownedFrame,settings.Screenshots,path=>notifications.Enqueue(UiStrings.T("Screenshot saved: ")+path));
            screenshotEditor=editor;
            editor.FormClosed+=(_,_)=>
            {
                screenshotEditor=null;ownedFrame.Dispose();
                RestoreScreenshotInput();
            };
            try{editor.Show();frame=null;}
            catch{screenshotEditor=null;editor.Dispose();throw;}
        }
        catch(OperationCanceledException){}
        catch(Exception e){if(!closing){Log.Write("Screenshot: "+e);notifications.Enqueue(UiStrings.T("Screenshot failed: ")+e.Message);}}
        finally{frame?.Dispose();screenshotBusy=false;if(paused&&screenshotEditor==null)RestoreScreenshotInput();}
    }
    void RestoreScreenshotInput()
    {if(controller!=null)controller.ViewerActive=viewers.Values.Any(v=>!v.IsClosed&&v.IsActive);}
    void CloseScreenshots()
    {screenshotLifetime.Cancel();screenshotEditor?.Close();screenshotHotkey.Dispose();screenshotLifetime.Dispose();}
}
