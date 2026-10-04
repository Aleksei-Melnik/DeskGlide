#include <streams.h>
#include <initguid.h>
#include <ks.h>
#include <ksmedia.h>
#include <sddl.h>
#include <stdint.h>
#include <stdio.h>

// A user-mode DirectShow device. No kernel driver, NDI code or network access in the host process.
DEFINE_GUID(CLSID_ScreenCaptureCamera,0x72984451,0xd4c4,0x46eb,0xa6,0x10,0x51,0x9d,0xb1,0xf6,0xa8,0x20);
static const int Width=1920, Height=1080, Bytes=Width*Height*4;
static const DWORD Magic=0x53434331;
struct FrameHeader { DWORD magic,width,height,stride; ULONGLONG heartbeat,number; };
static_assert(sizeof(FrameHeader)==32,"IPC header");

static bool ObjectName(wchar_t* dest,size_t capacity,const wchar_t* suffix) {
    HANDLE token=nullptr; DWORD count=0;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return false;
    GetTokenInformation(token,TokenUser,nullptr,0,&count);
    BYTE* bytes=new BYTE[count]; LPWSTR sid=nullptr;
    bool ok=GetTokenInformation(token,TokenUser,bytes,count,&count)&&ConvertSidToStringSidW(((TOKEN_USER*)bytes)->User.Sid,&sid);
    if(ok)swprintf_s(dest,capacity,L"Local\\ScreenCapture.Camera.%s.%s",sid,suffix);
    if(sid)LocalFree(sid);delete[] bytes;CloseHandle(token);return ok;
}

class CameraPin final:public CSourceStream,public IAMStreamConfig,public IKsPropertySet {
    HANDLE mapping=nullptr,mutex=nullptr,timer=nullptr;
    const FrameHeader* shared=nullptr;
    LONGLONG origin=0,frequency=0,index=0;
    BYTE* latest=nullptr;
    ULONGLONG latestTime=0;
    ULONGLONG latestNumber=0;
    bool first=true;
    void CloseShared(){if(shared)UnmapViewOfFile(shared);if(mapping)CloseHandle(mapping);if(mutex)CloseHandle(mutex);shared=nullptr;mapping=nullptr;mutex=nullptr;}
    void OpenShared(){
        if(shared)return;
        wchar_t name[256];if(!ObjectName(name,256,L"frame"))return;
        mapping=OpenFileMappingW(FILE_MAP_READ,FALSE,name);if(!mapping)return;
        ObjectName(name,256,L"lock");mutex=OpenMutexW(SYNCHRONIZE|MUTEX_MODIFY_STATE,FALSE,name);
        if(mutex)shared=(const FrameHeader*)MapViewOfFile(mapping,FILE_MAP_READ,0,0,sizeof(FrameHeader)+Bytes);
        if(!shared)CloseShared();
    }
public:
    CameraPin(HRESULT* hr,CSource* filter):CSourceStream(NAME("ScreenCapture Camera"),hr,filter,L"Capture") {
        LARGE_INTEGER value;QueryPerformanceFrequency(&value);frequency=value.QuadPart;
        timer=CreateWaitableTimerExW(nullptr,nullptr,2,TIMER_ALL_ACCESS);
        if(!timer)timer=CreateWaitableTimerW(nullptr,FALSE,nullptr);
        latest=new BYTE[Bytes]();
    }
    ~CameraPin(){CloseShared();if(timer)CloseHandle(timer);delete[] latest;}
    DECLARE_IUNKNOWN;
    STDMETHODIMP NonDelegatingQueryInterface(REFIID id,void** out) override {
        if(id==IID_IAMStreamConfig)return GetInterface((IAMStreamConfig*)this,out);
        if(id==IID_IKsPropertySet)return GetInterface((IKsPropertySet*)this,out);
        return CSourceStream::NonDelegatingQueryInterface(id,out);
    }
    HRESULT GetMediaType(CMediaType* type) override {
        if(!type)return E_POINTER;
        type->InitMediaType();type->SetType(&MEDIATYPE_Video);type->SetSubtype(&MEDIASUBTYPE_RGB32);
        type->SetFormatType(&FORMAT_VideoInfo);type->SetTemporalCompression(FALSE);type->SetSampleSize(Bytes);
        auto info=(VIDEOINFOHEADER*)type->AllocFormatBuffer(sizeof(VIDEOINFOHEADER));if(!info)return E_OUTOFMEMORY;
        ZeroMemory(info,sizeof(*info));info->AvgTimePerFrame=166667;info->dwBitRate=Bytes*8u*60;
        info->bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info->bmiHeader.biWidth=Width;info->bmiHeader.biHeight=Height;
        info->bmiHeader.biPlanes=1;info->bmiHeader.biBitCount=32;info->bmiHeader.biCompression=BI_RGB;info->bmiHeader.biSizeImage=Bytes;
        return S_OK;
    }
    HRESULT CheckMediaType(const CMediaType* type) override {
        if(!type||*type->Type()!=MEDIATYPE_Video||*type->Subtype()!=MEDIASUBTYPE_RGB32||*type->FormatType()!=FORMAT_VideoInfo||type->FormatLength()<sizeof(VIDEOINFOHEADER))return E_INVALIDARG;
        auto info=(const VIDEOINFOHEADER*)type->Format();
        return info->bmiHeader.biWidth==Width&&info->bmiHeader.biHeight==Height&&info->bmiHeader.biBitCount==32&&info->bmiHeader.biPlanes==1&&info->bmiHeader.biCompression==BI_RGB&&info->AvgTimePerFrame>=166666&&info->AvgTimePerFrame<=166667?S_OK:E_INVALIDARG;
    }
    HRESULT DecideBufferSize(IMemAllocator* allocator,ALLOCATOR_PROPERTIES* properties) override {
        if(!allocator||!properties)return E_POINTER;
        properties->cBuffers=2;properties->cbBuffer=Bytes;ALLOCATOR_PROPERTIES actual={};
        HRESULT hr=allocator->SetProperties(properties,&actual);return FAILED(hr)?hr:actual.cbBuffer<Bytes?E_FAIL:S_OK;
    }
    HRESULT OnThreadStartPlay() override {LARGE_INTEGER now;QueryPerformanceCounter(&now);origin=now.QuadPart;index=0;first=true;return S_OK;}
    HRESULT FillBuffer(IMediaSample* sample) override {
        LARGE_INTEGER now;QueryPerformanceCounter(&now);
        LONGLONG elapsed=now.QuadPart-origin;
        if(elapsed>frequency*(index+2)/60)index=elapsed*60/frequency;
        LONGLONG remaining=origin+index*frequency/60-now.QuadPart;
        if(remaining>0){LARGE_INTEGER due;due.QuadPart=-(remaining*10000000/frequency);if(timer){SetWaitableTimer(timer,&due,0,nullptr,nullptr,FALSE);WaitForSingleObject(timer,20);}else Sleep((DWORD)(remaining*1000/frequency));}
        BYTE* target=nullptr;HRESULT hr=sample->GetPointer(&target);if(FAILED(hr))return hr;if(sample->GetSize()<Bytes)return E_FAIL;
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
        if(GetTickCount64()-latestTime<2000)CopyMemory(target,latest,Bytes);else ZeroMemory(target,Bytes);
        REFERENCE_TIME start=index*10000000/60,end=(index+1)*10000000/60;++index;
        sample->SetTime(&start,&end);sample->SetSyncPoint(TRUE);sample->SetDiscontinuity(first);first=false;sample->SetActualDataLength(Bytes);return S_OK;
    }
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* type) override {if(!type)return E_POINTER;CMediaType check(*type);HRESULT hr=CheckMediaType(&check);if(FAILED(hr))return hr;CAutoLock lock(m_pFilter->pStateLock());if(IsConnected())return VFW_E_NOT_STOPPED;m_mt=check;return S_OK;}
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** type) override {if(!type)return E_POINTER;CMediaType value;HRESULT hr=GetMediaType(&value);if(FAILED(hr))return hr;*type=CreateMediaType(&value);return *type?S_OK:E_OUTOFMEMORY;}
    STDMETHODIMP GetNumberOfCapabilities(int* count,int* size) override {if(!count||!size)return E_POINTER;*count=1;*size=sizeof(VIDEO_STREAM_CONFIG_CAPS);return S_OK;}
    STDMETHODIMP GetStreamCaps(int i,AM_MEDIA_TYPE** type,BYTE* caps) override {
        if(!type||!caps)return E_POINTER;if(i!=0)return S_FALSE;
        auto c=(VIDEO_STREAM_CONFIG_CAPS*)caps;ZeroMemory(c,sizeof(*c));c->guid=FORMAT_VideoInfo;c->VideoStandard=0;
        c->InputSize=c->MinCroppingSize=c->MaxCroppingSize=c->MinOutputSize=c->MaxOutputSize={Width,Height};
        c->CropGranularityX=c->CropGranularityY=c->CropAlignX=c->CropAlignY=c->OutputGranularityX=c->OutputGranularityY=1;
        c->MinFrameInterval=c->MaxFrameInterval=166667;c->MinBitsPerSecond=c->MaxBitsPerSecond=0x7fffffff;return GetFormat(type);
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
    Camera(LPUNKNOWN outer,HRESULT* hr):CSource(NAME("ScreenCapture Camera"),outer,CLSID_ScreenCaptureCamera,hr){if(!new CameraPin(hr,this))*hr=E_OUTOFMEMORY;}
    DECLARE_IUNKNOWN;
    STDMETHODIMP NonDelegatingQueryInterface(REFIID id,void** out) override {if(id==IID_IAMFilterMiscFlags)return GetInterface((IAMFilterMiscFlags*)this,out);return CSource::NonDelegatingQueryInterface(id,out);}
    STDMETHODIMP_(ULONG) GetMiscFlags() override{return AM_FILTER_MISC_FLAGS_IS_SOURCE;}
    static CUnknown* WINAPI Create(LPUNKNOWN outer,HRESULT* hr){return new Camera(outer,hr);}
};
CFactoryTemplate g_Templates[]={{L"ScreenCapture Camera",&CLSID_ScreenCaptureCamera,Camera::Create,nullptr,nullptr}};
int g_cTemplates=1;
extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE,ULONG,LPVOID);
BOOL APIENTRY DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved){return DllEntryPoint(instance,reason,reserved);}
