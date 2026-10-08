#include <new>
#include <streams.h>
#include <initguid.h>
#include <ks.h>
#include <ksmedia.h>
#include <sddl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdarg.h>

// A user-mode DirectShow device. No kernel driver, NDI code or network access in the host process.
DEFINE_GUID(CLSID_ScreenCaptureCamera,0x72984451,0xd4c4,0x46eb,0xa6,0x10,0x51,0x9d,0xb1,0xf6,0xa8,0x20);
static const int MaxBytes=7680*4320*4;
static const DWORD Magic=0x53434331;
struct FrameHeader { DWORD magic,width,height,stride; ULONGLONG heartbeat,number; };
static_assert(sizeof(FrameHeader)==32,"IPC header");

// Bounded, per-process diagnostics: format negotiation and frame counters only.
static void Trace(const char* format,...) {
    wchar_t root[MAX_PATH],path[MAX_PATH],exe[MAX_PATH];
    DWORD length=GetEnvironmentVariableW(L"LOCALAPPDATA",root,MAX_PATH);if(!length||length>MAX_PATH-80)return;
    swprintf_s(path,L"%s\\SdrCapture",root);CreateDirectoryW(path,nullptr);
    swprintf_s(path,L"%s\\SdrCapture\\Discord",root);CreateDirectoryW(path,nullptr);
    swprintf_s(path,L"%s\\SdrCapture\\Discord\\camera-%lu.log",root,GetCurrentProcessId());
    HANDLE file=CreateFileW(path,FILE_APPEND_DATA|FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);if(file==INVALID_HANDLE_VALUE)return;
    LARGE_INTEGER size;if(GetFileSizeEx(file,&size)&&size.QuadPart<128*1024){
        char message[512],line[900];va_list args;va_start(args,format);vsnprintf_s(message,sizeof(message),_TRUNCATE,format,args);va_end(args);
        GetModuleFileNameW(nullptr,exe,MAX_PATH);const wchar_t* name=wcsrchr(exe,L'\\');name=name?name+1:exe;
        int n=sprintf_s(line,"%llu %ls %s\r\n",GetTickCount64(),name,message);DWORD written;if(n>0)WriteFile(file,line,n,&written,nullptr);
    }
    CloseHandle(file);
}

static bool ObjectName(wchar_t* dest,size_t capacity,const wchar_t* suffix) {
    HANDLE token=nullptr; DWORD count=0;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return false;
    GetTokenInformation(token,TokenUser,nullptr,0,&count);
    BYTE* bytes=new BYTE[count]; LPWSTR sid=nullptr;
    bool ok=GetTokenInformation(token,TokenUser,bytes,count,&count)&&ConvertSidToStringSidW(((TOKEN_USER*)bytes)->User.Sid,&sid);
    if(ok)swprintf_s(dest,capacity,L"Local\\ScreenCapture.Camera.v3.%s.%s",sid,suffix);
    if(sid)LocalFree(sid);delete[] bytes;CloseHandle(token);return ok;
}

class CameraPin final:public CSourceStream,public IAMStreamConfig,public IKsPropertySet {
    int Width=1920,Height=1080,Bytes=Width*Height*4;
    HANDLE mapping=nullptr,mutex=nullptr,timer=nullptr;
    const FrameHeader* shared=nullptr;
    LONGLONG origin=0,frequency=0,index=0;
    BYTE* latest=nullptr;
    int latestCapacity=0;
    ULONGLONG latestTime=0;
    ULONGLONG latestNumber=0;
    bool first=true;
    DWORD sharedError=0;
    bool configured=false;
    CMediaType requested;
    int outputBytes=Width*Height*3,bpp=3;
    bool topDown=false;
    REFERENCE_TIME interval=166666;
    LONGLONG Stamp(LONGLONG frame)const{return interval<=166667?frame*10000000/60:frame*interval;}
    void CloseShared(){if(shared)UnmapViewOfFile(shared);if(mapping)CloseHandle(mapping);if(mutex)CloseHandle(mutex);shared=nullptr;mapping=nullptr;mutex=nullptr;}
    void OpenShared(){
        if(shared)return;
        wchar_t name[256];if(!ObjectName(name,256,L"frame"))return;
        mapping=OpenFileMappingW(FILE_MAP_READ,FALSE,name);if(!mapping){sharedError=GetLastError();return;}
        ObjectName(name,256,L"lock");mutex=OpenMutexW(SYNCHRONIZE|MUTEX_MODIFY_STATE,FALSE,name);
        if(mutex)shared=(const FrameHeader*)MapViewOfFile(mapping,FILE_MAP_READ,0,0,sizeof(FrameHeader)+MaxBytes);
        if(!shared){sharedError=GetLastError();CloseShared();}else sharedError=0;
    }
    void Dimensions(){
        if(configured||IsConnected())return;OpenShared();if(!shared)return;
        DWORD wait=WaitForSingleObject(mutex,5);if(wait!=WAIT_OBJECT_0&&wait!=WAIT_ABANDONED)return;
        if(shared->magic==Magic&&shared->width>=2&&shared->height>=2&&shared->width<=8192&&shared->height<=8192&&(ULONGLONG)shared->width*shared->height*4<=MaxBytes&&shared->stride==shared->width*4){Width=(int)shared->width;Height=(int)shared->height;Bytes=Width*Height*4;}
        ReleaseMutex(mutex);
    }
public:
    CameraPin(HRESULT* hr,CSource* filter):CSourceStream(NAME("DeskGlide Camera"),hr,filter,L"Capture") {
        LARGE_INTEGER value;QueryPerformanceFrequency(&value);frequency=value.QuadPart;
        timer=CreateWaitableTimerExW(nullptr,nullptr,2,TIMER_ALL_ACCESS);
        if(!timer)timer=CreateWaitableTimerW(nullptr,FALSE,nullptr);
        Dimensions();latest=new BYTE[Bytes]();latestCapacity=Bytes;Trace("create native camera %dx%d",Width,Height);
    }
    ~CameraPin(){CloseShared();if(timer)CloseHandle(timer);delete[] latest;}
    DECLARE_IUNKNOWN;
    STDMETHODIMP NonDelegatingQueryInterface(REFIID id,void** out) override {
        if(id==IID_IAMStreamConfig)return GetInterface((IAMStreamConfig*)this,out);
        if(id==IID_IKsPropertySet)return GetInterface((IKsPropertySet*)this,out);
        return CSourceStream::NonDelegatingQueryInterface(id,out);
    }
    HRESULT MakeType(CMediaType* type,int bits,REFERENCE_TIME duration) {
        Dimensions();
        if(!type)return E_POINTER;
        type->InitMediaType();type->SetType(&MEDIATYPE_Video);type->SetSubtype(bits==24?&MEDIASUBTYPE_RGB24:&MEDIASUBTYPE_RGB32);
        int bytes=Width*Height*(bits/8);
        type->SetFormatType(&FORMAT_VideoInfo);type->SetTemporalCompression(FALSE);type->SetSampleSize(bytes);
        auto info=(VIDEOINFOHEADER*)type->AllocFormatBuffer(sizeof(VIDEOINFOHEADER));if(!info)return E_OUTOFMEMORY;
        ZeroMemory(info,sizeof(*info));info->AvgTimePerFrame=duration;info->dwBitRate=(DWORD)((ULONGLONG)bytes*8*10000000/duration);
        info->bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info->bmiHeader.biWidth=Width;info->bmiHeader.biHeight=Height;
        info->bmiHeader.biPlanes=1;info->bmiHeader.biBitCount=(WORD)bits;info->bmiHeader.biCompression=BI_RGB;info->bmiHeader.biSizeImage=bytes;
        return S_OK;
    }
    HRESULT GetMediaType(CMediaType* type) override {return GetMediaType(0,type);}
    HRESULT GetMediaType(int position,CMediaType* type) override {
        if(!type)return E_POINTER;if(position<0)return E_INVALIDARG;
        CAutoLock lock(m_pFilter->pStateLock());
        if(configured){if(position)return VFW_S_NO_MORE_ITEMS;*type=requested;return S_OK;}
        if(position>=4)return VFW_S_NO_MORE_ITEMS;
        // WebRTC's native DirectShow backend accepts RGB24 but ignores RGB32.
        return MakeType(type,position<2?24:32,position%2?333333:166666);
    }
    HRESULT CheckMediaType(const CMediaType* type) override {
        CAutoLock lock(m_pFilter->pStateLock());Dimensions();
        if(!type||*type->Type()!=MEDIATYPE_Video||(*type->Subtype()!=MEDIASUBTYPE_RGB32&&*type->Subtype()!=MEDIASUBTYPE_RGB24)||*type->FormatType()!=FORMAT_VideoInfo||!type->Format()||type->FormatLength()<sizeof(VIDEOINFOHEADER))return VFW_E_TYPE_NOT_ACCEPTED;
        auto info=(const VIDEOINFOHEADER*)type->Format();
        int bits=*type->Subtype()==MEDIASUBTYPE_RGB24?24:32;
        return info->bmiHeader.biWidth==Width&&(info->bmiHeader.biHeight==Height||info->bmiHeader.biHeight==-Height)&&info->bmiHeader.biBitCount==bits&&info->bmiHeader.biPlanes==1&&info->bmiHeader.biCompression==BI_RGB&&info->AvgTimePerFrame>=166666&&info->AvgTimePerFrame<=1000000?S_OK:VFW_E_TYPE_NOT_ACCEPTED;
    }
    HRESULT SetMediaType(const CMediaType* type) override {
        HRESULT hr=CheckMediaType(type);if(FAILED(hr))return hr;
        hr=CSourceStream::SetMediaType(type);if(FAILED(hr))return hr;
        auto info=(const VIDEOINFOHEADER*)type->Format();bpp=info->bmiHeader.biBitCount/8;outputBytes=Width*Height*bpp;topDown=info->bmiHeader.biHeight<0;interval=info->AvgTimePerFrame;
        if(latestCapacity<Bytes){delete[] latest;latest=new(std::nothrow) BYTE[Bytes]();if(!latest)return E_OUTOFMEMORY;latestCapacity=Bytes;latestNumber=0;}
        Trace("connected RGB%d %dx%d interval=%lld",bpp*8,Width,info->bmiHeader.biHeight,interval);return S_OK;
    }
    HRESULT DecideBufferSize(IMemAllocator* allocator,ALLOCATOR_PROPERTIES* properties) override {
        if(!allocator||!properties)return E_POINTER;
        properties->cBuffers=2;properties->cbBuffer=outputBytes;ALLOCATOR_PROPERTIES actual={};
        HRESULT hr=allocator->SetProperties(properties,&actual);return FAILED(hr)?hr:actual.cbBuffer<outputBytes?E_FAIL:S_OK;
    }
    HRESULT OnThreadStartPlay() override {LARGE_INTEGER now;QueryPerformanceCounter(&now);origin=now.QuadPart;index=0;first=true;Trace("start RGB%d interval=%lld",bpp*8,interval);return S_OK;}
    HRESULT FillBuffer(IMediaSample* sample) override {
        LARGE_INTEGER now;QueryPerformanceCounter(&now);
        LONGLONG elapsed=now.QuadPart-origin;
        LONGLONG elapsedTime=elapsed*10000000/frequency;
        if(elapsedTime>Stamp(index+2))index=interval<=166667?elapsedTime*60/10000000:elapsedTime/interval;
        LONGLONG remaining=Stamp(index)-elapsedTime;
        if(remaining>0){LARGE_INTEGER due;due.QuadPart=-remaining;if(timer){SetWaitableTimer(timer,&due,0,nullptr,nullptr,FALSE);WaitForSingleObject(timer,110);}else Sleep((DWORD)(remaining/10000));}
        BYTE* target=nullptr;HRESULT hr=sample->GetPointer(&target);if(FAILED(hr))return hr;if(sample->GetSize()<outputBytes)return E_FAIL;
        OpenShared();
        // A bounded 4 ms allowance absorbs receive/decode jitter without building a frame queue.
        if(shared)for(int attempt=0;attempt<5;attempt++){
            bool fresh=false;DWORD wait=WaitForSingleObject(mutex,1);
            if(wait==WAIT_OBJECT_0||wait==WAIT_ABANDONED){
                if(shared->magic==Magic&&shared->width==Width&&shared->height==Height&&shared->stride==Width*4&&GetTickCount64()-shared->heartbeat<2000&&shared->number!=latestNumber){CopyMemory(latest,shared+1,Bytes);latestTime=shared->heartbeat;latestNumber=shared->number;fresh=true;}
                ReleaseMutex(mutex);
            }
            if(fresh||attempt==4)break;Sleep(1);
        }
        // Retain the last received picture through short network/display resets.
        // Source silence must not replace a valid picture with a black flash.
        bool valid=latestNumber!=0;
        if(!valid)ZeroMemory(target,outputBytes);
        else if(bpp==4&&!topDown)CopyMemory(target,latest,Bytes);
        else for(int y=0;y<Height;y++){
            const BYTE* from=latest+(topDown?Height-1-y:y)*Width*4;BYTE* to=target+y*Width*bpp;
            if(bpp==4)CopyMemory(to,from,Width*4);
            else for(int x=0;x<Width;x++){to[x*3]=from[x*4];to[x*3+1]=from[x*4+1];to[x*3+2]=from[x*4+2];}
        }
        if(index%600==0)Trace("frame index=%lld shared=%d error=%lu fresh=%d number=%llu",index,shared!=nullptr,sharedError,valid,latestNumber);
        REFERENCE_TIME start=Stamp(index),end=Stamp(index+1);++index;
        sample->SetTime(&start,&end);sample->SetSyncPoint(TRUE);sample->SetDiscontinuity(first);first=false;sample->SetActualDataLength(outputBytes);return S_OK;
    }
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* type) override {
        if(type&&type->pbFormat&&type->formattype==FORMAT_VideoInfo&&type->cbFormat>=sizeof(VIDEOINFOHEADER)){auto info=(const VIDEOINFOHEADER*)type->pbFormat;Trace("request bits=%u %ldx%ld compression=%lu interval=%lld",info->bmiHeader.biBitCount,info->bmiHeader.biWidth,info->bmiHeader.biHeight,info->bmiHeader.biCompression,info->AvgTimePerFrame);}
        if(!type)return E_POINTER;CMediaType check(*type);HRESULT hr=CheckMediaType(&check);
        Trace("SetFormat result=%08lx",hr);if(FAILED(hr))return hr;
        CMediaType previous;bool wasConfigured,connected;
        {CAutoLock lock(m_pFilter->pStateLock());if(!m_pFilter->IsStopped())return VFW_E_NOT_STOPPED;
         previous=requested;wasConfigured=configured;requested=check;configured=true;connected=IsConnected();if(connected&&m_mt==check)return S_OK;}
        if(connected){hr=m_pFilter->ReconnectPin(this,&check);if(FAILED(hr)){CAutoLock lock(m_pFilter->pStateLock());requested=previous;configured=wasConfigured;}}
        return hr;
    }
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** type) override {if(!type)return E_POINTER;CAutoLock lock(m_pFilter->pStateLock());CMediaType value;HRESULT hr=IsConnected()?(value=m_mt,S_OK):GetMediaType(&value);if(FAILED(hr))return hr;*type=CreateMediaType(&value);return *type?S_OK:E_OUTOFMEMORY;}
    STDMETHODIMP GetNumberOfCapabilities(int* count,int* size) override {if(!count||!size)return E_POINTER;*count=4;*size=sizeof(VIDEO_STREAM_CONFIG_CAPS);return S_OK;}
    STDMETHODIMP GetStreamCaps(int i,AM_MEDIA_TYPE** type,BYTE* caps) override {
        CAutoLock lock(m_pFilter->pStateLock());Dimensions();
        if(!type||!caps)return E_POINTER;*type=nullptr;if(i<0||i>=4)return S_FALSE;
        auto c=(VIDEO_STREAM_CONFIG_CAPS*)caps;ZeroMemory(c,sizeof(*c));c->guid=FORMAT_VideoInfo;c->VideoStandard=0;
        c->InputSize=c->MinCroppingSize=c->MaxCroppingSize=c->MinOutputSize=c->MaxOutputSize={Width,Height};
        c->CropGranularityX=c->CropGranularityY=c->CropAlignX=c->CropAlignY=c->OutputGranularityX=c->OutputGranularityY=1;
        c->MinFrameInterval=166666;c->MaxFrameInterval=1000000;c->MinBitsPerSecond=(LONG)min((LONGLONG)Width*Height*24*10,0x7fffffffLL);c->MaxBitsPerSecond=0x7fffffff;
        CMediaType value;HRESULT hr=MakeType(&value,i<2?24:32,i%2?333333:166666);if(FAILED(hr))return hr;*type=CreateMediaType(&value);return *type?S_OK:E_OUTOFMEMORY;
    }
    STDMETHODIMP Set(REFGUID,DWORD,LPVOID,DWORD,LPVOID,DWORD) override{return E_NOTIMPL;}
    STDMETHODIMP Get(REFGUID set,DWORD id,LPVOID,DWORD,LPVOID data,DWORD size,DWORD* returned) override {
        if(set!=AMPROPSETID_Pin)return E_PROP_SET_UNSUPPORTED;if(id!=AMPROPERTY_PIN_CATEGORY)return E_PROP_ID_UNSUPPORTED;
        if(returned)*returned=sizeof(GUID);if(!data)return returned?S_OK:E_POINTER;if(size<sizeof(GUID))return E_UNEXPECTED;*(GUID*)data=PIN_CATEGORY_CAPTURE;return S_OK;
    }
    STDMETHODIMP QuerySupported(REFGUID set,DWORD id,DWORD* support) override {if(!support)return E_POINTER;*support=0;if(set!=AMPROPSETID_Pin)return E_PROP_SET_UNSUPPORTED;if(id!=AMPROPERTY_PIN_CATEGORY)return E_PROP_ID_UNSUPPORTED;*support=KSPROPERTY_SUPPORT_GET;return S_OK;}
};
class Camera final:public CSource,public IAMFilterMiscFlags {
public:
    Camera(LPUNKNOWN outer,HRESULT* hr):CSource(NAME("DeskGlide Camera"),outer,CLSID_ScreenCaptureCamera,hr){if(!new CameraPin(hr,this))*hr=E_OUTOFMEMORY;}
    DECLARE_IUNKNOWN;
    STDMETHODIMP NonDelegatingQueryInterface(REFIID id,void** out) override {if(id==IID_IAMFilterMiscFlags)return GetInterface((IAMFilterMiscFlags*)this,out);return CSource::NonDelegatingQueryInterface(id,out);}
    STDMETHODIMP_(ULONG) GetMiscFlags() override{return AM_FILTER_MISC_FLAGS_IS_SOURCE;}
    static CUnknown* WINAPI Create(LPUNKNOWN outer,HRESULT* hr){return new Camera(outer,hr);}
};
CFactoryTemplate g_Templates[]={{L"DeskGlide Camera",&CLSID_ScreenCaptureCamera,Camera::Create,nullptr,nullptr}};
int g_cTemplates=1;
extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE,ULONG,LPVOID);
BOOL APIENTRY DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved){return DllEntryPoint(instance,reason,reserved);}
