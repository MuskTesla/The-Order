#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include <d3dcompiler.h>
#include <microsoft.ui.xaml.media.dxinterop.h>
#include <wrl/client.h>
#include <memory>
#include <cstring>
#include <stdexcept>
#include <string>
#include <cmath>
#include <d2d1_1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <vector>
using Microsoft::WRL::ComPtr;
namespace {
UINT64 resourceCounts[6]={}; // UI-thread diagnostic totals, not driver residency.
void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
#include "GallerySource.h"
// Original, local approximation: rounded-rectangle distance field, bounded bevel,
// one coordinate per channel. No Apple code, private hook or desktop capture.
const char* shader = R"shader(
cbuffer Parameters : register(b0) {
 float4 viewSource; // stage DIP, source DIP
 float4 lens;       // center DIP, half extent DIP
 float4 optics;     // corner radius, bevel width, optical depth, dispersion DIP
 float4 lighting;   // gloss, dark, background translation DIP
 float4 raster;     // scale, reserved
};
Texture2D sourceImage : register(t0);
SamplerState linearClamp : register(s0);
struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
Vertex VS(uint id : SV_VertexID) {
 Vertex o; float2 uv=float2((id<<1)&2,id&2);
 o.position=float4(uv*float2(2,-2)+float2(-1,1),0,1);o.uv=uv;return o;
}
float3 Read(float2 p) { return sourceImage.SampleLevel(linearClamp,(p-lighting.zw)/viewSource.zw,0).rgb; }
float4 PS(Vertex input) : SV_TARGET {
 float2 p=input.uv*viewSource.xy;
 float3 original=Read(p);
 float2 local=p-lens.xy;
 float2 q=abs(local)-lens.zw+optics.x;
 float2 outside=max(q,0);
 float sd=length(outside)+min(max(q.x,q.y),0)-optics.x;
 float coverage=saturate(.5-sd*raster.x);
 if(coverage<=0) return float4(original,1);
 float d=max(-sd,0);
 // Smooth the inner medial transition where a bevel extends beyond corner radius.
 float2 soft=log(1+exp(clamp(q/2,-40,40)))*2;
 float2 normal=normalize(soft+float2(.00001,.00001))*sign(local);
 float t=saturate(d/optics.y);
 // Bounded derivative avoids the grazing-angle fold / repeated glyph segments
 // observed in round 1. Both profiles keep 1.35 * .55 * depth / width < 1.
 float2 shift=-normal*(.55*optics.z*pow(1-t,1.35));
 float2 coord=p+shift;
 float spectral=optics.w*pow(1-t,2);
 float3 color=Read(coord);
 if(spectral>.001) {
   color.r=Read(coord+normal*spectral).r;
   color.b=Read(coord-normal*spectral).b;
 }
 float incidence=dot(normal,normalize(float2(-.55,-.83)));
 float bright=pow(saturate(incidence),3);
 float reverse=pow(saturate(-incidence),3);
 float lobe=exp(-d/1.1)*.65+exp(-pow((d-optics.y*.22)/(optics.y*.16),2))*.16;
 color=lerp(color,float3(1,1,1),saturate(lighting.x*bright*lobe));
 color*=1-lighting.x*reverse*exp(-d/2.2)*.22;
 return float4(lerp(original,color,coverage),1);
}
)shader";
struct Renderer {
 Renderer() { ++resourceCounts[0]; }
 ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
 ComPtr<IDXGISwapChain1> chain; ComPtr<ISwapChainPanelNative> panel;
 ComPtr<ID3D11RenderTargetView> target; ComPtr<ID3D11VertexShader> vertex;
 ComPtr<ID3D11PixelShader> pixel, backgroundPixel; ComPtr<ID3D11Buffer> constants;
 ComPtr<ID3D11SamplerState> sampler; ComPtr<ID3D11ShaderResourceView> source;
 ComPtr<ID3D11Texture2D> sourceTexture;
 GallerySource scene;
 UINT sourceWidth=0,sourceHeight=0;
 UINT width=0,height=0;
 ~Renderer() { if(panel) panel->SetSwapChain(nullptr); if(context) { context->ClearState(); context->Flush(); } ++resourceCounts[1]; }
 void Resize(UINT w,UINT h) {
   if(w==width&&h==height) return;
   context->OMSetRenderTargets(0,nullptr,nullptr);target.Reset();
   if(chain) { Check(chain->ResizeBuffers(2,w,h,DXGI_FORMAT_B8G8R8A8_UNORM,0)); ++resourceCounts[5]; }
   else {
    ComPtr<IDXGIDevice> dxgi;Check(device.As(&dxgi));
    ComPtr<IDXGIAdapter> adapter;Check(dxgi->GetAdapter(&adapter));
    ComPtr<IDXGIFactory2> factory;Check(adapter->GetParent(IID_PPV_ARGS(&factory)));
    DXGI_SWAP_CHAIN_DESC1 desc={};desc.Width=w;desc.Height=h;
    desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
    desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;
    desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;desc.AlphaMode=DXGI_ALPHA_MODE_IGNORE;
    desc.Scaling=DXGI_SCALING_STRETCH;
    Check(factory->CreateSwapChainForComposition(device.Get(),&desc,nullptr,&chain));
    ++resourceCounts[4];
    Check(panel->SetSwapChain(chain.Get()));
   }
   ComPtr<ID3D11Texture2D> back;Check(chain->GetBuffer(0,IID_PPV_ARGS(&back)));
   Check(device->CreateRenderTargetView(back.Get(),nullptr,&target)); width=w;height=h;
 }
 void Init(IUnknown* p) {
  Check(p->QueryInterface(IID_PPV_ARGS(&panel)));
  D3D_FEATURE_LEVEL feature;
  Check(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    nullptr,0,D3D11_SDK_VERSION,&device,&feature,&context));
  ComPtr<ID3DBlob> vs,ps,error;
  Check(D3DCompile(shader,strlen(shader),"TheOrderOptical",nullptr,nullptr,"VS","vs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&vs,&error));
  Check(D3DCompile(shader,strlen(shader),"TheOrderOptical",nullptr,nullptr,"PS","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&ps,&error));
  Check(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex));
  Check(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel));
  // Separate plain presentation entry; the accepted optical shader is unchanged.
  const auto background=std::string(shader)+"\nfloat4 BackgroundPS(Vertex input):SV_TARGET { return float4(Read(input.uv*viewSource.xy),1); }";
  Check(D3DCompile(background.c_str(),background.size(),"TheOrderGalleryBackground",nullptr,nullptr,"BackgroundPS","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&ps,&error));
  Check(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&backgroundPixel));
  D3D11_BUFFER_DESC buffer={};buffer.ByteWidth=80;buffer.Usage=D3D11_USAGE_DEFAULT;buffer.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
  Check(device->CreateBuffer(&buffer,nullptr,&constants));
  D3D11_SAMPLER_DESC sample={};sample.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
  sample.AddressU=sample.AddressV=sample.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sample.MaxLOD=D3D11_FLOAT32_MAX;
  Check(device->CreateSamplerState(&sample,&sampler));
 }
};
template<class F> HRESULT Guard(F&& f) noexcept { try { f(); return S_OK; } catch(HRESULT hr) { return hr; } catch(...) { return E_FAIL; } }
}
extern "C" __declspec(dllexport) HRESULT OpticalCreate(IUnknown* panel, void** result) noexcept {
 if(!panel||!result)return E_INVALIDARG;*result=nullptr;
 return Guard([&] { auto r=std::make_unique<Renderer>();r->Init(panel);*result=r.release(); });
}
extern "C" __declspec(dllexport) HRESULT OpticalUpload(void* handle,const BYTE* data,UINT w,UINT h,UINT length) noexcept {
 if(!handle||!data||w==0||h==0||w>8192||h>8192||static_cast<UINT64>(w)*h*4!=length)return E_INVALIDARG;
 return Guard([&] {auto r=static_cast<Renderer*>(handle);
  if(r->sourceTexture&&w==r->sourceWidth&&h==r->sourceHeight) {
   // The immediate context preserves upload/draw ordering, including in-flight
   // reads. UpdateSubresource copies caller bytes before this call returns.
   r->context->UpdateSubresource(r->sourceTexture.Get(),0,nullptr,data,w*4,0);
   ++resourceCounts[2];return;
  }
  D3D11_TEXTURE2D_DESC d={};d.Width=w;d.Height=h;d.MipLevels=1;d.ArraySize=1;
  d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;d.SampleDesc.Count=1;d.Usage=D3D11_USAGE_DEFAULT;d.BindFlags=D3D11_BIND_SHADER_RESOURCE;
  D3D11_SUBRESOURCE_DATA s={data,w*4,0};ComPtr<ID3D11Texture2D> texture;
  Check(r->device->CreateTexture2D(&d,&s,&texture));
  ++resourceCounts[3];
  ComPtr<ID3D11ShaderResourceView> view;Check(r->device->CreateShaderResourceView(texture.Get(),nullptr,&view));
  r->source=std::move(view);
  r->sourceTexture=std::move(texture);r->sourceWidth=w;r->sourceHeight=h;
  ++resourceCounts[2];
 });
}
static HRESULT Draw(void* handle,UINT width,UINT height,const float* p,bool background) noexcept {
 if(!handle||!p||width==0||height==0||width>8192||height>8192)return E_INVALIDARG;
 return Guard([&] {auto r=static_cast<Renderer*>(handle);if(!r->source)throw E_UNEXPECTED;r->Resize(width,height);
 ComPtr<IDXGISwapChain2> scaled;Check(r->chain.As(&scaled));
 DXGI_MATRIX_3X2_F matrix={1/p[16],0,0,1/p[16],0,0};Check(scaled->SetMatrixTransform(&matrix));
 r->context->UpdateSubresource(r->constants.Get(),0,nullptr,p,0,0);
 r->context->OMSetRenderTargets(1,r->target.GetAddressOf(),nullptr);
 D3D11_VIEWPORT viewport={0,0,static_cast<float>(width),static_cast<float>(height),0,1};r->context->RSSetViewports(1,&viewport);
 r->context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
 r->context->VSSetShader(r->vertex.Get(),nullptr,0);r->context->PSSetShader(background?r->backgroundPixel.Get():r->pixel.Get(),nullptr,0);
 r->context->VSSetConstantBuffers(0,1,r->constants.GetAddressOf());r->context->PSSetConstantBuffers(0,1,r->constants.GetAddressOf());
 r->context->PSSetSamplers(0,1,r->sampler.GetAddressOf());r->context->PSSetShaderResources(0,1,r->source.GetAddressOf());
 r->context->Draw(3,0);Check(r->chain->Present(1,0));
 });
}
extern "C" __declspec(dllexport) HRESULT OpticalDraw(void* h,UINT w,UINT height,const float* p) noexcept { return Draw(h,w,height,p,false); }
extern "C" __declspec(dllexport) HRESULT OpticalDrawBackground(void* h,UINT w,UINT height,const float* p) noexcept { return Draw(h,w,height,p,true); }
extern "C" __declspec(dllexport) HRESULT OpticalSceneBegin(void* handle,UINT w,UINT h,float scale,UINT color) noexcept {
 if(!handle||w==0||h==0||w>8192||h>8192||!std::isfinite(scale)||scale<=0)return E_INVALIDARG;
 return Guard([&]{auto r=static_cast<Renderer*>(handle);r->scene.Init(r->device.Get());
  if(r->scene.drawing)throw E_ILLEGAL_METHOD_CALL;
  ID3D11ShaderResourceView* empty=nullptr;r->context->PSSetShaderResources(0,1,&empty);
  r->context->OMSetRenderTargets(0,nullptr,nullptr);
  if(!r->scene.target||r->sourceWidth!=w||r->sourceHeight!=h){
   D3D11_TEXTURE2D_DESC d={};d.Width=w;d.Height=h;d.MipLevels=1;d.ArraySize=1;
   d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;d.SampleDesc.Count=1;d.Usage=D3D11_USAGE_DEFAULT;
   d.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
   ComPtr<ID3D11Texture2D> texture;ComPtr<ID3D11ShaderResourceView> view;
   Check(r->device->CreateTexture2D(&d,nullptr,&texture));Check(r->device->CreateShaderResourceView(texture.Get(),nullptr,&view));
   r->scene.SetSurface(texture.Get(),scale);
   r->sourceTexture=std::move(texture);r->source=std::move(view);r->sourceWidth=w;r->sourceHeight=h;++resourceCounts[3];
  }
  r->scene.Begin(scale,color);
 });
}
extern "C" __declspec(dllexport) HRESULT OpticalSceneRect(void* h,float x,float y,float w,float height,UINT color) noexcept {
 if(!h)return E_INVALIDARG;return Guard([&]{static_cast<Renderer*>(h)->scene.Rect(x,y,w,height,color);});
}
extern "C" __declspec(dllexport) HRESULT OpticalSceneText(void* h,const wchar_t* text,float x,float y,float size,UINT color,UINT weight) noexcept {
 if(!h||!text)return E_INVALIDARG;return Guard([&]{static_cast<Renderer*>(h)->scene.Text(text,x,y,size,color,weight);});
}
extern "C" __declspec(dllexport) HRESULT OpticalSceneImage(void* h,const wchar_t* path,float x,float y,float w,float height,BOOL fill) noexcept {
 if(!h||!path)return E_INVALIDARG;return Guard([&]{static_cast<Renderer*>(h)->scene.Image(path,x,y,w,height,fill!=FALSE);});
}
extern "C" __declspec(dllexport) HRESULT OpticalSceneEnd(void* h) noexcept {
 if(!h)return E_INVALIDARG;return static_cast<Renderer*>(h)->scene.End();
}
extern "C" __declspec(dllexport) HRESULT OpticalSceneDiagnostics(void* h,UINT64* values) noexcept {
 if(!h||!values)return E_INVALIDARG;auto& s=static_cast<Renderer*>(h)->scene;
 values[0]=s.completed;values[1]=s.images;values[2]=s.texts;values[3]=s.targets;
 values[4]=s.drawing?1:0;values[5]=s.frameImages.size();values[6]=s.context?1:0;return S_OK;
}
extern "C" __declspec(dllexport) HRESULT OpticalDestroy(void* handle) noexcept {
 if(!handle)return S_OK;auto r=static_cast<Renderer*>(handle);
 // Detach on the owning UI thread before releasing the graph; retain for retry on failure.
 HRESULT hr=r->panel->SetSwapChain(nullptr);if(FAILED(hr))return hr;
 r->panel.Reset();delete r;return S_OK;
}

// Queried only by the explicit Test Gallery probe. No device, sampler or timer
// is created. Values describe this process across adapters, including XAML.
extern "C" __declspec(dllexport) HRESULT OpticalDiagnostics(UINT64* counts,UINT64* rows,UINT capacity,UINT* count) noexcept {
 if(!counts||!rows||!count||capacity<10)return E_INVALIDARG;*count=0;
 return Guard([&] {
  std::memcpy(counts,resourceCounts,sizeof(resourceCounts));
  ComPtr<IDXGIFactory1> factory;Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
  for(UINT index=0;index<capacity/10;++index) {
   ComPtr<IDXGIAdapter1> adapter;HRESULT hr=factory->EnumAdapters1(index,&adapter);
   if(hr==DXGI_ERROR_NOT_FOUND)break;Check(hr);
   DXGI_ADAPTER_DESC1 desc={};Check(adapter->GetDesc1(&desc));
   auto p=rows+index*10;p[0]=desc.AdapterLuid.LowPart;p[1]=static_cast<UINT>(desc.AdapterLuid.HighPart);p[2]=desc.Flags;p[3]=index;
   ComPtr<IDXGIAdapter3> memory;hr=adapter.As(&memory);
   DXGI_QUERY_VIDEO_MEMORY_INFO local={},nonlocal={};
   HRESULT a=FAILED(hr)?hr:memory->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&local);
   HRESULT b=FAILED(hr)?hr:memory->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL,&nonlocal);
   p[4]=static_cast<UINT>(a);p[5]=local.CurrentUsage;p[6]=local.Budget;
   p[7]=static_cast<UINT>(b);p[8]=nonlocal.CurrentUsage;p[9]=nonlocal.Budget;++*count;
  }
 });
}
