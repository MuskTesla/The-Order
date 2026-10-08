// Isolated hardware-D3D regression; no profile, game, window or capture.
// Includes the actual renderer so readback can validate uploads and ownership.
#include "../TheOrder.WinUI/Materials/Native/OpticalRenderer.cpp"
#include <vector>
#include <iostream>
#include <cmath>
#include "accepted-c1-shader.h"
struct TestPanel final : ISwapChainPanelNative {
 ULONG refs=1;bool failDetach=false;
 HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** value) override {
  if(!value)return E_POINTER;*value=nullptr;
  if(iid==__uuidof(IUnknown)||iid==__uuidof(ISwapChainPanelNative)) {*value=static_cast<ISwapChainPanelNative*>(this);AddRef();return S_OK;}
  return E_NOINTERFACE;
 }
 ULONG STDMETHODCALLTYPE AddRef() override {return ++refs;}
 ULONG STDMETHODCALLTYPE Release() override {return --refs;}
 HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain*) override {return failDetach?E_FAIL:S_OK;}
};
void Require(bool value,const char* message) {if(!value)throw std::runtime_error(message);}
void UploadAndRead(void* handle,UINT w,UINT h,BYTE value) {
 std::vector<BYTE> data(static_cast<size_t>(w)*h*4,value);
 Check(OpticalUpload(handle,data.data(),w,h,static_cast<UINT>(data.size())));
 auto r=static_cast<Renderer*>(handle);
 D3D11_TEXTURE2D_DESC d={};r->sourceTexture->GetDesc(&d);
 d.BindFlags=0;d.Usage=D3D11_USAGE_STAGING;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
 ComPtr<ID3D11Texture2D> read;Check(r->device->CreateTexture2D(&d,nullptr,&read));
 r->context->CopyResource(read.Get(),r->sourceTexture.Get());
 D3D11_MAPPED_SUBRESOURCE map={};Check(r->context->Map(read.Get(),0,D3D11_MAP_READ,0,&map));
 bool equal=true;
 for(UINT y=0;y<h;++y)equal=equal&&std::memcmp(static_cast<BYTE*>(map.pData)+y*map.RowPitch,data.data()+static_cast<size_t>(y)*w*4,w*4)==0;
 r->context->Unmap(read.Get(),0);Require(equal,"Stale or corrupt upload readback");
}
// Runs the real pipeline into an offscreen target so pixels can be compared without a presented swapchain.
std::vector<BYTE> RenderOffscreen(Renderer* r,ID3D11PixelShader* ps,const float* p,UINT w,UINT h,bool frost) {
 D3D11_TEXTURE2D_DESC d={};d.Width=w;d.Height=h;d.MipLevels=1;d.ArraySize=1;d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
 d.SampleDesc.Count=1;d.BindFlags=D3D11_BIND_RENDER_TARGET;ComPtr<ID3D11Texture2D> target;Check(r->device->CreateTexture2D(&d,nullptr,&target));
 ComPtr<ID3D11RenderTargetView> view;Check(r->device->CreateRenderTargetView(target.Get(),nullptr,&view));
 r->context->UpdateSubresource(r->constants.Get(),0,nullptr,p,0,0);
 r->context->OMSetRenderTargets(1,view.GetAddressOf(),nullptr);
 D3D11_VIEWPORT viewport={0,0,static_cast<float>(w),static_cast<float>(h),0,1};r->context->RSSetViewports(1,&viewport);
 r->context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);r->context->VSSetShader(r->vertex.Get(),nullptr,0);
 r->context->PSSetShader(ps,nullptr,0);r->context->PSSetConstantBuffers(0,1,r->constants.GetAddressOf());r->context->PSSetSamplers(0,1,r->sampler.GetAddressOf());
 ID3D11ShaderResourceView* views[2]={r->source.Get(),frost?r->frost.Get():nullptr};r->context->PSSetShaderResources(0,2,views);
 r->context->Draw(3,0);r->context->OMSetRenderTargets(0,nullptr,nullptr);
 ID3D11ShaderResourceView* empty[2]={};r->context->PSSetShaderResources(0,2,empty);
 d.BindFlags=0;d.Usage=D3D11_USAGE_STAGING;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;ComPtr<ID3D11Texture2D> read;Check(r->device->CreateTexture2D(&d,nullptr,&read));
 r->context->CopyResource(read.Get(),target.Get());D3D11_MAPPED_SUBRESOURCE map={};Check(r->context->Map(read.Get(),0,D3D11_MAP_READ,0,&map));
 std::vector<BYTE> pixels(static_cast<size_t>(w)*h*4);
 for(UINT y=0;y<h;++y)std::memcpy(pixels.data()+static_cast<size_t>(y)*w*4,static_cast<BYTE*>(map.pData)+y*map.RowPitch,w*4);
 r->context->Unmap(read.Get(),0);return pixels;
}
std::vector<BYTE> ReadTexture(Renderer* r,ID3D11Texture2D* texture) {
 D3D11_TEXTURE2D_DESC d={};texture->GetDesc(&d);d.BindFlags=0;d.Usage=D3D11_USAGE_STAGING;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
 ComPtr<ID3D11Texture2D> read;Check(r->device->CreateTexture2D(&d,nullptr,&read));r->context->CopyResource(read.Get(),texture);
 D3D11_MAPPED_SUBRESOURCE map={};Check(r->context->Map(read.Get(),0,D3D11_MAP_READ,0,&map));
 std::vector<BYTE> pixels(static_cast<size_t>(d.Width)*d.Height*4);
 for(UINT y=0;y<d.Height;++y)std::memcpy(pixels.data()+static_cast<size_t>(y)*d.Width*4,static_cast<BYTE*>(map.pData)+y*map.RowPitch,d.Width*4);
 r->context->Unmap(read.Get(),0);return pixels;
}
void DrawStripes(void* handle,UINT w,UINT h,UINT color) {
 Check(OpticalSceneBegin(handle,w,h,1,color));
 for(UINT x=0;x<w;x+=8)Check(OpticalSceneRect(handle,static_cast<float>(x),0,4,static_cast<float>(h),0xff101418));
 Check(OpticalSceneRect(handle,40,30,60,50,0xffe05030));Check(OpticalSceneText(handle,L"GLASS 光学 ガラス",20,60,18,0xff2050e0,600));
 Check(OpticalSceneEnd(handle));
}
// Stage-plate frost: C1 preserved bit-exactly when off; bounded, event-driven, fail-closed when stale.
void FrostTests(void* handle) {
 auto r=static_cast<Renderer*>(handle);const UINT w=256,h=128;
 DrawStripes(handle,w,h,0xffd8e4ea);
 ComPtr<ID3DBlob> blob,error;
 Check(D3DCompile(acceptedC1Shader,strlen(acceptedC1Shader),"AcceptedC1",nullptr,nullptr,"PS","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&blob,&error));
 ComPtr<ID3D11PixelShader> accepted;Check(r->device->CreatePixelShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,&accepted));
 // C1 reference profile: corner 23, bevel 30, depth 30, dispersion .8, gloss .55; tint alpha .8.
 float p[24]={w,h,w,h, 128,64,104,48, 23,30,30,.8f, .55f,0,0,0, 1,0,0,0, .98f,.98f,.97f,.8f};
 const auto reference=RenderOffscreen(r,accepted.Get(),p,w,h,false);
 Require(RenderOffscreen(r,r->pixel.Get(),p,w,h,false)==reference,"Frost-off output differs from accepted C1");
 Require(OpticalDrawEx(handle,w,h,p,24,FALSE)==S_OK,"Frost-off 24-float draw failed");
 Require(OpticalDraw(handle,w,h,p)==S_OK,"Legacy 20-float draw failed");
 p[17]=1;Require(OpticalDrawEx(handle,w,h,p,24,FALSE)==E_UNEXPECTED,"Frost without a valid copy was drawn");
 Check(OpticalSceneFrost(handle,6));Require(r->frostValid&&r->frostCreated==1,"Frost copy not published");
 p[17]=0;Require(RenderOffscreen(r,r->pixel.Get(),p,w,h,true)==reference,"Bound frost changed frost-off output");
 p[17]=1;const auto frosted=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);
 const auto blurred=ReadTexture(r,r->frostTexture.Get());const auto sharp=ReadTexture(r,r->sourceTexture.Get());
 // Interior: blurred copy under the tint. Clear rim (outer bevel*.6-6 = 12 DIP): identical to C1.
 const size_t centre=(64*w+128)*4;const float tint[3]={.97f*255,.98f*255,.98f*255}; // BGRA order
 for(int c=0;c<3;++c)Require(std::abs(frosted[centre+c]-(blurred[centre+c]*.2f+tint[c]*.8f))<=2.5f,"Frost body colour outside tint bound");
 for(UINT y=40;y<88;++y){const size_t rim=(y*w+(128-104+4))*4;for(int c=0;c<4;++c)Require(frosted[rim+c]==reference[rim+c],"Frost altered the refracting rim");}
 double meanS=0,meanB=0,varS=0,varB=0;const size_t n=static_cast<size_t>(w)*h;
 for(size_t i=0;i<n;++i){meanS+=sharp[i*4+1];meanB+=blurred[i*4+1];}meanS/=n;meanB/=n;
 for(size_t i=0;i<n;++i){varS+=std::pow(sharp[i*4+1]-meanS,2);varB+=std::pow(blurred[i*4+1]-meanB,2);}
 Require(std::abs(meanS-meanB)<=meanS*.03&&varB<varS*.5,"Frost copy is not a mean-preserving blur");
 Check(OpticalDrawEx(handle,w,h,p,24,FALSE));
 Require(OpticalDrawEx(handle,w,h,p,21,FALSE)==E_INVALIDARG,"Bad constant count accepted");
 p[17]=1.5f;Require(OpticalDrawEx(handle,w,h,p,24,FALSE)==E_INVALIDARG,"Out-of-range frost accepted");p[17]=1;
 Require(OpticalSceneFrost(handle,NAN)==E_INVALIDARG&&OpticalSceneFrost(handle,65)==E_INVALIDARG,"Invalid sigma accepted");
 for(UINT i=0;i<16;++i){DrawStripes(handle,w,h,0xff000000|i*9);Require(!r->frostValid,"Scene revision left stale frost valid");
  Require(OpticalDrawEx(handle,w,h,p,24,FALSE)==E_UNEXPECTED,"Stale frost drawn after revision");Check(OpticalSceneFrost(handle,6));}
 Require(r->frostCreated==1&&r->frostPasses==17,"Same-size frost not reused");
 BYTE pixels[64*64*4]={};Check(OpticalUpload(handle,pixels,64,64,sizeof(pixels)));Require(!r->frostValid,"Upload left frost valid");
 DrawStripes(handle,192,96,0xff406080);Check(OpticalSceneFrost(handle,6));Require(r->frostCreated==2,"Resized frost not recreated");
 Check(OpticalSceneFrost(handle,0));Require(!r->frostTexture&&!r->frost&&!r->frostValid&&!r->scene.frostTarget,"Frost retirement retained resources");
 Check(OpticalSceneFrost(handle,6));Require(r->frostTexture&&r->frostValid,"Frost re-entry failed");
}
int main(int argc,char** argv) {
 if(argc!=2||std::strcmp(argv[1],"Test")!=0)return 2;
 try {
  Check(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED));
  TestPanel panel;void* handle=nullptr;Check(OpticalCreate(&panel,&handle));
  UploadAndRead(handle,64,64,17);auto first=static_cast<Renderer*>(handle)->sourceTexture.Get();
  // Exercise updates while the previous source remains bound for sampling.
  auto r=static_cast<Renderer*>(handle);r->context->PSSetShaderResources(0,1,r->source.GetAddressOf());
  for(int i=0;i<128;++i) {UploadAndRead(handle,64,64,static_cast<BYTE>(i));Require(r->sourceTexture.Get()==first,"Same-size upload recreated texture");}
  Require(resourceCounts[3]==1,"Source creation not bounded");
  BYTE invalid[4]={};Require(OpticalUpload(handle,invalid,64,64,4)==E_INVALIDARG,"Invalid length accepted");
  Require(OpticalUpload(handle,invalid,0,1,0)==E_INVALIDARG,"Zero size accepted");
  Require(OpticalUpload(handle,invalid,8193,1,4)==E_INVALIDARG,"Oversize accepted");
  Require(r->sourceTexture.Get()==first,"Rejected upload altered source");
  UploadAndRead(handle,96,40,207);UploadAndRead(handle,96,40,223);UploadAndRead(handle,64,64,239);
  Require(resourceCounts[3]==3&&resourceCounts[2]==132,"Size-change allocation or upload accounting incorrect");
  panel.failDetach=true;Require(OpticalDestroy(handle)==E_FAIL,"Failed detach lost owner");
  Require(resourceCounts[1]==0&&r->sourceTexture!=nullptr,"Failed detach released graph prematurely");
  panel.failDetach=false;Check(OpticalDestroy(handle));Check(OpticalDestroy(nullptr));
  Require(resourceCounts[0]==resourceCounts[1]&&panel.refs==1,"Renderer/panel ownership unbalanced");
  Check(OpticalCreate(&panel,&handle));r=static_cast<Renderer*>(handle);
  for(UINT i=0;i<160;++i) {
   const UINT color=0xff000000|i;
   Check(OpticalSceneBegin(handle,64,64,1,color));
   Require(FAILED(OpticalSceneBegin(handle,64,64,1,color)),"Reentrant scene accepted");
   Check(OpticalSceneRect(handle,4,4,20,20,0xffff3020));Check(OpticalSceneEnd(handle));
   Require(!r->scene.drawing&&r->scene.frameImages.empty(),"Scene frame retained after EndDraw");
   D3D11_TEXTURE2D_DESC desc={};r->sourceTexture->GetDesc(&desc);
   desc.BindFlags=0;desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
   ComPtr<ID3D11Texture2D> read;Check(r->device->CreateTexture2D(&desc,nullptr,&read));
   r->context->CopyResource(read.Get(),r->sourceTexture.Get());
   D3D11_MAPPED_SUBRESOURCE map={};Check(r->context->Map(read.Get(),0,D3D11_MAP_READ,0,&map));
   const auto background=*static_cast<const UINT*>(map.pData);
   const auto rect=*reinterpret_cast<const UINT*>(static_cast<const BYTE*>(map.pData)+map.RowPitch*8+8*4);
   r->context->Unmap(read.Get(),0);
   Require(background==color&&rect==0xffff3020,"Scene did not publish new content exactly");
  }
  Require(r->scene.targets==1&&r->scene.completed==160,"Same-size scene resources not reused");
  Check(OpticalSceneBegin(handle,96,40,1,0xff203040));
  Require(FAILED(OpticalSceneImage(handle,L"X:\\missing-test-image.png",0,0,8,8,FALSE)),"Missing image did not fail");
  Check(OpticalSceneEnd(handle));
  Check(OpticalSceneBegin(handle,96,40,1,0xffaabbcc));Check(OpticalSceneEnd(handle));
  Require(r->scene.targets==2&&!r->scene.drawing,"Scene resize/error retirement failed");
  panel.failDetach=true;Require(OpticalDestroy(handle)==E_FAIL,"Scene failed detach lost owner");
  panel.failDetach=false;Check(OpticalDestroy(handle));
  Require(resourceCounts[0]==resourceCounts[1]&&panel.refs==1,"Scene graph retained after destroy");
  Check(OpticalCreate(&panel,&handle));FrostTests(handle);
  panel.failDetach=true;Require(OpticalDestroy(handle)==E_FAIL,"Frost failed detach lost owner");
  panel.failDetach=false;Check(OpticalDestroy(handle));
  Require(resourceCounts[0]==resourceCounts[1]&&panel.refs==1,"Frost graph retained after destroy");
  CoUninitialize();
  std::cout<<"PASS: 132 exact GPU readbacks; 129 same-size uploads use 1 texture; dimension changes; rejected uploads; detach retry; balanced owner.\n";
  std::cout<<"PASS: 160 exact dynamic scene readbacks; 1 target; reentrancy rejection; resize; failed image load; balanced scene owner.\n";
  std::cout<<"PASS: frost off == accepted C1 bit-exact (with and without bound copy); rim unchanged; body within tint bound; mean-preserving blur; 17 passes reuse 1 texture; stale/upload/resize invalidation fails closed; retirement and re-entry; balanced owner.\n";
  return 0;
 } catch(HRESULT hr) {std::cerr<<"HRESULT "<<std::hex<<hr<<"\n";return 1;}
 catch(const std::exception& e) {std::cerr<<e.what()<<"\n";return 1;}
}
