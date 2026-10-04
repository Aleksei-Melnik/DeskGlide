#include <windows.h>
#include <dshow.h>
#include <stdio.h>
#include <stdint.h>
struct __declspec(uuid("0579154A-2B53-4994-B0D0-E773148EFF85")) GrabCallback: IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SampleCB(double,IMediaSample*)=0;
    virtual HRESULT STDMETHODCALLTYPE BufferCB(double,BYTE*,long)=0;
};
struct __declspec(uuid("6B652FFF-11FE-4FCE-92AD-0266B5D7C78F")) Grabber: IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetOneShot(BOOL)=0;
    virtual HRESULT STDMETHODCALLTYPE SetMediaType(const AM_MEDIA_TYPE*)=0;
    virtual HRESULT STDMETHODCALLTYPE GetConnectedMediaType(AM_MEDIA_TYPE*)=0;
    virtual HRESULT STDMETHODCALLTYPE SetBufferSamples(BOOL)=0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentBuffer(long*,long*)=0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentSample(IMediaSample**)=0;
    virtual HRESULT STDMETHODCALLTYPE SetCallback(GrabCallback*,long)=0;
};
static const CLSID camera={0x72984451,0xd4c4,0x46eb,{0xa6,0x10,0x51,0x9d,0xb1,0xf6,0xa8,0x20}};
static const CLSID grabClass={0xc1f400a0,0x3f08,0x11d3,{0x9f,0x0b,0,0x60,0x08,0x03,0x9e,0x37}};
static const CLSID nullClass={0xc1f400a4,0x3f08,0x11d3,{0x9f,0x0b,0,0x60,0x08,0x03,0x9e,0x37}};
struct Counter:GrabCallback {
    LONG refs=1,count=0,unique=0,badSize=0;DWORD last=0;double first=-1,end=0,maxGap=0;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** p)override{if(!p)return E_POINTER;*p=nullptr;if(id==IID_IUnknown||id==__uuidof(GrabCallback)){*p=this;AddRef();return S_OK;}return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef()override{return InterlockedIncrement(&refs);}
    ULONG STDMETHODCALLTYPE Release()override{return InterlockedDecrement(&refs);}
    HRESULT STDMETHODCALLTYPE SampleCB(double,IMediaSample*)override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE BufferCB(double time,BYTE* buffer,long length)override{
        if(length!=1920*1080*4)badSize++;
        if(first<0)first=time;else if(time-end>maxGap)maxGap=time-end;end=time;
        DWORD pixel=*(DWORD*)(buffer+((1080/2)*1920+1920/2)*4);if(pixel!=last){unique++;last=pixel;}count++;return S_OK;
    }
};
static void Check(HRESULT hr){if(FAILED(hr)){fprintf(stderr,"HRESULT %08lx\n",hr);ExitProcess(2);}}
static IPin* Pin(IBaseFilter* filter,PIN_DIRECTION direction){IEnumPins* list=nullptr;Check(filter->EnumPins(&list));IPin* pin=nullptr;while(list->Next(1,&pin,nullptr)==S_OK){PIN_DIRECTION dir;pin->QueryDirection(&dir);if(dir==direction){list->Release();return pin;}pin->Release();}list->Release();return nullptr;}
static bool DeviceEnumeration(){
    // Process-local HKCR override: exercise the real device enumerator without installing a camera.
    ICreateDevEnum* devices=nullptr;Check(CoCreateInstance(CLSID_SystemDeviceEnum,nullptr,CLSCTX_INPROC_SERVER,IID_ICreateDevEnum,(void**)&devices));
    wchar_t path[128];swprintf_s(path,L"Software\\ScreenCapture\\CameraProbe-%lu",GetCurrentProcessId());HKEY root=nullptr,entry=nullptr;
    if(RegCreateKeyExW(HKEY_CURRENT_USER,path,0,nullptr,0,KEY_ALL_ACCESS,nullptr,&root,nullptr)!=ERROR_SUCCESS)return false;
    RegCreateKeyExW(root,L"CLSID\\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\\Instance\\{72984451-D4C4-46EB-A610-519DB1F6A820}",0,nullptr,0,KEY_ALL_ACCESS,nullptr,&entry,nullptr);
    const wchar_t* id=L"{72984451-D4C4-46EB-A610-519DB1F6A820}";const wchar_t* name=L"ScreenCapture Camera";
    RegSetValueExW(entry,L"CLSID",0,REG_SZ,(const BYTE*)id,(DWORD)((wcslen(id)+1)*2));RegSetValueExW(entry,L"FriendlyName",0,REG_SZ,(const BYTE*)name,(DWORD)((wcslen(name)+1)*2));RegCloseKey(entry);
    bool found=false;
    if(RegOverridePredefKey(HKEY_CLASSES_ROOT,root)==ERROR_SUCCESS){
        IEnumMoniker* list=nullptr;
        if(devices->CreateClassEnumerator(CLSID_VideoInputDeviceCategory,&list,0)==S_OK){
            IMoniker* moniker=nullptr;while(list->Next(1,&moniker,nullptr)==S_OK){IPropertyBag* bag=nullptr;if(SUCCEEDED(moniker->BindToStorage(nullptr,nullptr,IID_IPropertyBag,(void**)&bag))){VARIANT value;VariantInit(&value);if(SUCCEEDED(bag->Read(L"FriendlyName",&value,nullptr))&&value.vt==VT_BSTR&&wcscmp(value.bstrVal,name)==0)found=true;VariantClear(&value);bag->Release();}moniker->Release();}list->Release();
        }
        RegOverridePredefKey(HKEY_CLASSES_ROOT,nullptr);
    }
    RegCloseKey(root);RegDeleteTreeW(HKEY_CURRENT_USER,path);devices->Release();return found;
}
int wmain(int argc,wchar_t** argv){
    if(argc!=2)return 1;Check(CoInitializeEx(nullptr,COINIT_MULTITHREADED));
    bool enumerated=DeviceEnumeration();if(!enumerated){fprintf(stderr,"Device enumeration failed\n");return 4;}
    HMODULE module=LoadLibraryW(argv[1]);if(!module){fprintf(stderr,"LoadLibrary %lu\n",GetLastError());return 2;}
    auto create=(HRESULT(WINAPI*)(REFCLSID,REFIID,void**))GetProcAddress(module,"DllGetClassObject");IClassFactory* factory=nullptr;Check(create(camera,IID_IClassFactory,(void**)&factory));
    IBaseFilter *source=nullptr,*grabFilter=nullptr,*sink=nullptr;Check(factory->CreateInstance(nullptr,IID_IBaseFilter,(void**)&source));factory->Release();
    IPin* out=Pin(source,PINDIR_OUTPUT);IAMStreamConfig* config=nullptr;Check(out->QueryInterface(IID_IAMStreamConfig,(void**)&config));
    AM_MEDIA_TYPE* format=nullptr;Check(config->GetFormat(&format));auto info=(VIDEOINFOHEADER*)format->pbFormat;
    bool native60=info->bmiHeader.biWidth==1920&&info->bmiHeader.biHeight==1080&&info->AvgTimePerFrame==166667;
    info->AvgTimePerFrame=333333;bool rejects30=FAILED(config->SetFormat(format));info->AvgTimePerFrame=166667;Check(config->SetFormat(format));
    CoTaskMemFree(format->pbFormat);CoTaskMemFree(format);config->Release();
    IGraphBuilder* graph=nullptr;Check(CoCreateInstance(CLSID_FilterGraph,nullptr,CLSCTX_INPROC_SERVER,IID_IGraphBuilder,(void**)&graph));Check(graph->AddFilter(source,L"Camera"));
    Check(CoCreateInstance(grabClass,nullptr,CLSCTX_INPROC_SERVER,IID_IBaseFilter,(void**)&grabFilter));Check(graph->AddFilter(grabFilter,L"Probe"));
    Grabber* grab=nullptr;Check(grabFilter->QueryInterface(__uuidof(Grabber),(void**)&grab));AM_MEDIA_TYPE type={};type.majortype=MEDIATYPE_Video;type.subtype=MEDIASUBTYPE_RGB32;type.formattype=FORMAT_VideoInfo;Check(grab->SetMediaType(&type));Counter counter;Check(grab->SetCallback(&counter,1));
    Check(CoCreateInstance(nullClass,nullptr,CLSCTX_INPROC_SERVER,IID_IBaseFilter,(void**)&sink));Check(graph->AddFilter(sink,L"Sink"));
    IPin* in=Pin(grabFilter,PINDIR_INPUT);Check(graph->ConnectDirect(out,in,nullptr));in->Release();out->Release();out=Pin(grabFilter,PINDIR_OUTPUT);in=Pin(sink,PINDIR_INPUT);Check(graph->ConnectDirect(out,in,nullptr));in->Release();out->Release();
    IMediaControl* control=nullptr;Check(graph->QueryInterface(IID_IMediaControl,(void**)&control));ULONGLONG started=GetTickCount64();Check(control->Run());Sleep(4000);Check(control->Stop());double seconds=(GetTickCount64()-started)/1000.0;
    printf("{\"Frames\":%ld,\"Unique\":%ld,\"BadSize\":%ld,\"Seconds\":%.3f,\"TimestampFps\":%.5f,\"MaxGapMs\":%.3f,\"Native60\":%s,\"Rejects30\":%s,\"DeviceEnumerated\":true}\n",counter.count,counter.unique,counter.badSize,seconds,(counter.count-1)/(counter.end-counter.first),counter.maxGap*1000,native60?"true":"false",rejects30?"true":"false");
    grab->SetCallback(nullptr,1);control->Release();grab->Release();sink->Release();grabFilter->Release();source->Release();graph->Release();FreeLibrary(module);CoUninitialize();
    return native60&&rejects30&&counter.count>=230&&counter.count<=250&&counter.badSize==0&&counter.unique>=200?0:3;
}
