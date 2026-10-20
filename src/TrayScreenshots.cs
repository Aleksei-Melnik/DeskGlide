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
        screenshotBusy=true;ScreenshotFrame? frame=null;
        try
        {
            // Freeze before showing any overlay. Do not stop recording, change HDR
            // settings, inject input or include our editor in the captured image.
            var token=screenshotLifetime.Token;
            frame=await Task.Run(()=>ScreenshotFrame.Capture(token),token);
            if(closing){frame.Dispose();frame=null;return;}
            controller?.ReturnLocal();if(controller!=null)controller.ViewerActive=true;
            var ownedFrame=frame;var editor=new ScreenshotEditor(ownedFrame,settings.Screenshots,path=>notifications.Enqueue(UiStrings.T("Screenshot saved: ")+path));
            screenshotEditor=editor;
            editor.FormClosed+=(_,_)=>
            {
                screenshotEditor=null;ownedFrame.Dispose();
                if(controller!=null)controller.ViewerActive=viewers.Values.Any(v=>!v.IsClosed&&v.IsActive);
            };
            editor.Show();frame=null;
        }
        catch(OperationCanceledException){}
        catch(Exception e){if(!closing){Log.Write("Screenshot: "+e);notifications.Enqueue(UiStrings.T("Screenshot failed: ")+e.Message);}}
        finally{frame?.Dispose();screenshotBusy=false;}
    }
    void CloseScreenshots()
    {screenshotLifetime.Cancel();screenshotEditor?.Close();screenshotHotkey.Dispose();screenshotLifetime.Dispose();}
}
