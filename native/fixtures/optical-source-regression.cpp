// Isolated hardware-D3D regression; no profile, game, window or capture.
// Includes the actual renderer so readback can validate uploads and ownership.
#include "../TheOrder.WinUI/Materials/Native/OpticalRenderer.cpp"
#include <vector>
#include <iostream>
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
  CoUninitialize();
  std::cout<<"PASS: 132 exact GPU readbacks; 129 same-size uploads use 1 texture; dimension changes; rejected uploads; detach retry; balanced owner.\n";
  std::cout<<"PASS: 160 exact dynamic scene readbacks; 1 target; reentrancy rejection; resize; failed image load; balanced scene owner.\n";
  return 0;
 } catch(HRESULT hr) {std::cerr<<"HRESULT "<<std::hex<<hr<<"\n";return 1;}
 catch(const std::exception& e) {std::cerr<<e.what()<<"\n";return 1;}
}
