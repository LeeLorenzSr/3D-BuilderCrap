// Independent diagnostic observer. Never injects input or captures another process/window.
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <array>
#include <atomic>
#include <mutex>
#include <vector>
#include <string>
using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
struct Roi { int x,y,width,height; };
struct FrameHeader { long long ordinal,sourceTime100ns,callbackQpc; int width,height,roiCount,byteCount; };
using Callback = void(__stdcall*)(FrameHeader const*,Roi const*,unsigned char const*);
namespace {
std::mutex gate;
std::atomic<bool> stopping=true;
Direct3D11CaptureFramePool pool{nullptr};
GraphicsCaptureSession session{nullptr};
GraphicsCaptureItem item{nullptr};
winrt::com_ptr<ID3D11Device> device;
winrt::com_ptr<ID3D11DeviceContext> context;
std::array<winrt::com_ptr<ID3D11Texture2D>,4> staging;
std::array<Roi,4> regions{};
int regionCount=0;
long long ordinal=0;
Callback callback=nullptr;
winrt::event_token frameEventToken{};
std::wstring error;
void FrameArrived(Direct3D11CaptureFramePool const& sender,winrt::Windows::Foundation::IInspectable const&) noexcept {
    if(stopping.load()) return;
    std::lock_guard lock(gate);
    if(stopping.load()) return;
    try {
        auto frame=sender.TryGetNextFrame(); if(!frame) return;
        LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
        auto size=frame.ContentSize();
        auto access=frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        winrt::com_ptr<ID3D11Texture2D> source;
        check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D),source.put_void()));
        std::vector<unsigned char> bytes;
        std::array<Roi,4> actual{};
        for(int i=0;i<regionCount;++i) {
            auto r=regions[i];
            if(r.x<0||r.y<0||r.width<=0||r.height<=0||r.x+r.width>size.Width||r.y+r.height>size.Height)
                throw hresult_invalid_argument(L"ROI outside current capture size; resize invalidates calibration.");
            actual[i]=r;
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width=r.width;desc.Height=r.height;desc.MipLevels=1;desc.ArraySize=1;
            desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
            desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
            if(!staging[i]) check_hresult(device->CreateTexture2D(&desc,nullptr,staging[i].put()));
            D3D11_BOX box{(UINT)r.x,(UINT)r.y,0,(UINT)(r.x+r.width),(UINT)(r.y+r.height),1};
            context->CopySubresourceRegion(staging[i].get(),0,0,0,0,source.get(),0,&box);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            check_hresult(context->Map(staging[i].get(),0,D3D11_MAP_READ,0,&mapped));
            auto offset=bytes.size();bytes.resize(offset+(size_t)r.width*r.height*4);
            for(int y=0;y<r.height;++y)
                memcpy(bytes.data()+offset+(size_t)y*r.width*4,(unsigned char*)mapped.pData+(size_t)y*mapped.RowPitch,(size_t)r.width*4);
            context->Unmap(staging[i].get(),0);
        }
        FrameHeader header{++ordinal,frame.SystemRelativeTime().count(),now.QuadPart,size.Width,size.Height,regionCount,(int)bytes.size()};
        if(callback) callback(&header,actual.data(),bytes.data());
    } catch(winrt::hresult_error const& e) {error=e.message().c_str();}
      catch(...) {error=L"Observer callback failure.";}
}
}
extern "C" __declspec(dllexport) int __stdcall ObserverStart(HWND hwnd,Roi const* rois,int count,Callback cb) noexcept {
    try {
        DWORD pid=0;GetWindowThreadProcessId(hwnd,&pid);
        if(pid!=GetCurrentProcessId()||!IsWindow(hwnd)||count<1||count>4||!cb) return E_INVALIDARG;
        // The WPF host already initialized its STA; CreateFreeThreaded owns worker delivery.
        std::lock_guard lock(gate);
        if(session) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        if(!GraphicsCaptureSession::IsSupported()) return E_NOTIMPL;
        UINT flags=D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,flags,nullptr,0,D3D11_SDK_VERSION,device.put(),nullptr,context.put()));
        auto dxgi=device.as<IDXGIDevice>();winrt::com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(),inspectable.put()));
        auto directDevice=inspectable.as<IDirect3DDevice>();
        auto factory=winrt::get_activation_factory<GraphicsCaptureItem,IGraphicsCaptureItemInterop>();
        check_hresult(factory->CreateForWindow(hwnd,winrt::guid_of<GraphicsCaptureItem>(),winrt::put_abi(item)));
        regionCount=count;for(int i=0;i<count;++i)regions[i]=rois[i];callback=cb;ordinal=0;
        pool=Direct3D11CaptureFramePool::CreateFreeThreaded(directDevice,DirectXPixelFormat::B8G8R8A8UIntNormalized,3,item.Size());
        frameEventToken=pool.FrameArrived(FrameArrived);session=pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled(false); // Keep default capture border and system policy.
        stopping=false;session.StartCapture();return S_OK;
    } catch(winrt::hresult_error const& e){error=e.message().c_str();return e.code();}
      catch(...){error=L"Observer initialization failure.";return E_FAIL;}
}
extern "C" __declspec(dllexport) void __stdcall ObserverStop() noexcept {
    stopping=true;
    Direct3D11CaptureFramePool oldPool{nullptr};GraphicsCaptureSession oldSession{nullptr};
    {
        std::lock_guard lock(gate);
        oldPool=pool;oldSession=session;session=nullptr;pool=nullptr;item=nullptr;callback=nullptr;
        for(auto& texture:staging)texture=nullptr;context=nullptr;device=nullptr;
    }
    // Closing can wait for in-flight delivery. Do not hold the delivery mutex while closing.
    try{if(oldPool)oldPool.FrameArrived(frameEventToken);if(oldSession)oldSession.Close();if(oldPool)oldPool.Close();}catch(...){}
}
extern "C" __declspec(dllexport) int __stdcall ObserverError(wchar_t* buffer,int capacity) noexcept {
    std::lock_guard lock(gate);if(buffer&&capacity>0)wcsncpy_s(buffer,capacity,error.c_str(),_TRUNCATE);return (int)error.size();
}
