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
 float p[28]={w,h,w,h, 128,64,104,48, 23,30,30,.8f, .55f,0,0,0, 1,0,0,0, .98f,.98f,.97f,.8f, 0,0,1,0}; // 28: the offscreen helper uploads the full 112-byte cbuffer
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
double SrgbLuminance(const BYTE* bgra) {
 auto lin=[](BYTE v){double c=v/255.0;return c<=.04045?c/12.92:std::pow((c+.055)/1.055,2.4);};
 return .2126*lin(bgra[2])+.7152*lin(bgra[1])+.0722*lin(bgra[0]);
}
double RowEnergy(const std::vector<BYTE>& px,UINT w,UINT y,UINT x0,UINT x1) {
 double e=0;for(UINT x=x0;x<x1;++x)for(int c=0;c<3;++c)e+=std::abs(px[(y*w+x+1)*4+c]-px[(y*w+x)*4+c]);return e/(x1-x0);
}
// rev2 continuous glass. Thresholds fixed before first run: no inner boundary on a uniform field
// (adjacent delta <= 3), rim/body detail ratio <= 2 over stripes, luminance bounds for text contrast.
void ContinuousTests(void* handle) {
 auto r=static_cast<Renderer*>(handle);const UINT w=256,h=128;
 ComPtr<ID3DBlob> blob,error;
 Check(D3DCompile(acceptedC1Shader,strlen(acceptedC1Shader),"AcceptedC1",nullptr,nullptr,"PS","ps_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&blob,&error));
 ComPtr<ID3D11PixelShader> accepted;Check(r->device->CreatePixelShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,&accepted));
 // C1 geometry; light polarity: lift to .42, min tint .22, edge .35.
 float p[28]={w,h,w,h, 128,64,104,48, 23,30,30,.8f, .55f,0,0,0, 1,0,0,0, .98f,.98f,.98f,.22f, 0,.42f,1,.20f};
 DrawStripes(handle,w,h,0xffd8e4ea);
 const auto reference=RenderOffscreen(r,accepted.Get(),p,w,h,false);
 Require(RenderOffscreen(r,r->pixel.Get(),p,w,h,false)==reference,"rev2-off output differs from accepted C1");
 p[24]=1;Require(OpticalDrawEx(handle,w,h,p,28,FALSE)==E_UNEXPECTED,"rev2 drawn without a valid copy");
 Check(OpticalSceneFrost(handle,6));
 const auto glass=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);
 for(UINT y=0;y<h;y+=127)for(UINT x=0;x<w;x+=255)for(int c=0;c<4;++c)Require(glass[(y*w+x)*4+c]==reference[(y*w+x)*4+c],"rev2 changed pixels outside the lens");
 // Rim keeps the same regime as the body: detail energy along the centre row.
 const double rim=RowEnergy(glass,w,64,24+3,24+15), body=RowEnergy(glass,w,64,98,158);
 p[24]=0;p[17]=1;p[23]=.8f;const auto rev1=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);p[17]=0;p[23]=.22f;p[24]=1;
 const double rim1=RowEnergy(rev1,w,64,24+3,24+15), body1=RowEnergy(rev1,w,64,98,158);
 std::cout<<"INFO: rim/body detail energy rev2 "<<rim<<"/"<<body<<"  rev1 "<<rim1<<"/"<<body1<<"\n";
 // Uniform field: no inner boundary anywhere between the anti-aliased edges.
 Check(OpticalSceneBegin(handle,w,h,1,0xff808890));Check(OpticalSceneEnd(handle));Check(OpticalSceneFrost(handle,6));
 const auto flat=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);int worst=0;
 for(UINT y=40;y<=88;y+=8)for(UINT x=24+4;x<232-4;++x)for(int c=0;c<3;++c)worst=std::max(worst,std::abs(flat[(y*w+x+1)*4+c]-flat[(y*w+x)*4+c]));
 std::cout<<"INFO: rev2 max adjacent delta on uniform field "<<worst<<"\n";
 std::cout<<"INFO: rev2 rim/body energy on uniform field (edge shading only) "<<RowEnergy(flat,w,64,24+3,24+15)<<"/"<<RowEnergy(flat,w,64,98,158)<<"\n";
 // Contrast bound at the text-safe centre over extreme and saturated backdrops, both polarities.
 const UINT backs[]={0xff000000,0xffffffff,0xffff0000,0xffffff00,0xff0000ff,0xff00ff00,0xff808080};
 double minLight=1,maxDark=0;
 for(UINT b:backs){Check(OpticalSceneBegin(handle,w,h,1,b));Check(OpticalSceneEnd(handle));Check(OpticalSceneFrost(handle,6));
  p[20]=p[21]=p[22]=250/255.f;p[23]=.22f;p[25]=.42f;p[26]=1;auto lt=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);minLight=std::min(minLight,SrgbLuminance(&lt[(64*w+128)*4]));
  p[20]=p[21]=p[22]=32/255.f;p[23]=.30f;p[25]=.08f;p[26]=-1;auto dk=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);maxDark=std::max(maxDark,SrgbLuminance(&dk[(64*w+128)*4]));}
 std::cout<<"INFO: rev2 body luminance light min "<<minLight<<" (text #000000E4 contrast "<<(minLight+.05)/(.0+.05)<<"), dark max "<<maxDark<<" (white text contrast "<<1.05/(maxDark+.05)<<")\n";
 // Assertions after all measurements are printed; thresholds unchanged from the frozen definition.
 Require(rim<=2*std::max(body,1.0),"rev2 rim and body read as different regimes");
 Require(worst<=3,"rev2 shows an inner boundary on a uniform field");
 Require(minLight>=.40&&maxDark<=.09,"rev2 luminance bound violated");
 // Validation: count, exclusivity with rev1, polarity.
 Require(OpticalDrawEx(handle,w,h,p,27,FALSE)==E_INVALIDARG,"Bad constant count accepted");
 p[17]=1;Require(OpticalDrawEx(handle,w,h,p,28,FALSE)==E_INVALIDARG,"rev1+rev2 accepted");p[17]=0;
 p[26]=0;Require(OpticalDrawEx(handle,w,h,p,28,FALSE)==E_INVALIDARG,"Zero polarity accepted");p[26]=-1;
 Check(OpticalDrawEx(handle,w,h,p,28,FALSE));
 // Blurred scene image primitive: rejects bad sigma, frame effects released at EndDraw.
 Check(OpticalSceneBegin(handle,w,h,1,0xff000000));
 Require(OpticalSceneImageBlur(handle,L"X:\\missing.png",0,0,8,8,TRUE,0)==E_INVALIDARG,"Zero sigma accepted");
 Require(FAILED(OpticalSceneImageBlur(handle,L"X:\\missing.png",0,0,8,8,TRUE,4)),"Missing blurred image did not fail");
 Check(OpticalSceneEnd(handle));Require(r->scene.frameEffects.empty()&&r->scene.frameImages.empty(),"Scene effects retained after EndDraw");
 // Pixel-buffer artwork (app image pipeline output): exact sharp placement, validation, blurred path.
 std::vector<BYTE> art(4*4*4);for(size_t i=0;i<art.size();i+=4){art[i]=0x20;art[i+1]=0x80;art[i+2]=0xe0;art[i+3]=0xff;}
 Check(OpticalSceneBegin(handle,32,32,1,0xff000000));
 Require(OpticalScenePixels(handle,art.data(),4,4,static_cast<UINT>(art.size())-4,0,0,32,32,TRUE,0)==E_INVALIDARG,"Short pixel buffer accepted");
 Require(OpticalScenePixels(handle,art.data(),4,4,static_cast<UINT>(art.size()),0,0,32,32,TRUE,-1)==E_INVALIDARG,"Negative sigma accepted");
 Check(OpticalScenePixels(handle,art.data(),4,4,static_cast<UINT>(art.size()),0,0,32,32,TRUE,0));Check(OpticalSceneEnd(handle));
 auto placed=ReadTexture(r,r->sourceTexture.Get());Require(placed[(16*32+16)*4+0]==0x20&&placed[(16*32+16)*4+1]==0x80&&placed[(16*32+16)*4+2]==0xe0,"Pixel artwork not placed exactly");
 Check(OpticalSceneBegin(handle,32,32,1,0xff000000));Check(OpticalScenePixels(handle,art.data(),4,4,static_cast<UINT>(art.size()),0,0,32,32,TRUE,6));Check(OpticalSceneEnd(handle));
 auto soft=ReadTexture(r,r->sourceTexture.Get());Require(std::abs(soft[(16*32+16)*4+1]-0x80)<=2,"Blurred uniform artwork changed colour");
 Require(r->scene.frameEffects.empty()&&r->scene.frameImages.empty(),"Pixel scene resources retained after EndDraw");
}
// 完全清透 (fully clear continuous glass). Same frozen thresholds as rev2, declared before the first run:
// outside unchanged, no inner boundary on a uniform field (<= 3), rim/body detail ratio <= 2, luminance bounds.
void ClearTests(void* handle) {
 auto r=static_cast<Renderer*>(handle);const UINT w=256,h=128;
 float p[28]={w,h,w,h, 128,64,104,48, 23,30,30,.8f, .55f,0,0,0, 1,0,1,0, .98f,.98f,.98f,.10f, 1,.42f,1,.20f};
 Check(OpticalSceneFrost(handle,0));Require(!r->frostValid,"Frost copy still valid");
 DrawStripes(handle,w,h,0xffd8e4ea);
 Require(OpticalDrawEx(handle,w,h,p,28,FALSE)==S_OK,"Clear draw required a blurred copy");
 p[24]=0;Require(OpticalDrawEx(handle,w,h,p,28,FALSE)==E_INVALIDARG,"Clear without continuous material accepted");p[24]=1;
 p[18]=.5f;Require(OpticalDrawEx(handle,w,h,p,28,FALSE)==E_INVALIDARG,"Fractional clear flag accepted");p[18]=1;
 float off[28]={};std::memcpy(off,p,sizeof(off));off[18]=0;off[24]=0;off[23]=0;
 const auto source=RenderOffscreen(r,r->pixel.Get(),off,w,h,false);
 const auto glass=RenderOffscreen(r,r->pixel.Get(),p,w,h,false);
 for(UINT y=0;y<h;y+=127)for(UINT x=0;x<w;x+=255)for(int c=0;c<4;++c)Require(glass[(y*w+x)*4+c]==source[(y*w+x)*4+c],"Clear changed pixels outside the lens");
 const double rim=RowEnergy(glass,w,64,24+3,24+15), body=RowEnergy(glass,w,64,98,158);
 Check(OpticalSceneBegin(handle,w,h,1,0xff808890));Check(OpticalSceneEnd(handle));
 const auto flat=RenderOffscreen(r,r->pixel.Get(),p,w,h,false);int worst=0;
 for(UINT y=40;y<=88;y+=8)for(UINT x=24+4;x<232-4;++x)for(int c=0;c<3;++c)worst=std::max(worst,std::abs(flat[(y*w+x+1)*4+c]-flat[(y*w+x)*4+c]));
 const UINT backs[]={0xff000000,0xffffffff,0xffff0000,0xffffff00,0xff0000ff,0xff00ff00,0xff808080};
 double minLight=1,maxDark=0;
 for(UINT b:backs){Check(OpticalSceneBegin(handle,w,h,1,b));Check(OpticalSceneEnd(handle));
  p[20]=p[21]=p[22]=250/255.f;p[23]=.10f;p[25]=.42f;p[26]=1;auto lt=RenderOffscreen(r,r->pixel.Get(),p,w,h,false);minLight=std::min(minLight,SrgbLuminance(&lt[(64*w+128)*4]));
  p[20]=p[21]=p[22]=32/255.f;p[23]=.16f;p[25]=.08f;p[26]=-1;auto dk=RenderOffscreen(r,r->pixel.Get(),p,w,h,false);maxDark=std::max(maxDark,SrgbLuminance(&dk[(64*w+128)*4]));}
 std::cout<<"INFO: clear rim/body "<<rim<<"/"<<body<<"; uniform max delta "<<worst<<"; light min L "<<minLight<<"; dark max L "<<maxDark<<"\n";
 Require(rim<=2*std::max(body,1.0),"Clear rim and body read as different regimes");
 Require(worst<=3,"Clear shows an inner boundary on a uniform field");
 Require(minLight>=.40&&maxDark<=.09,"Clear luminance bound violated");
}
void UnrefractedTests(void* handle) {
 auto r=static_cast<Renderer*>(handle);const UINT w=256,h=128;
 // Neutral shading isolates geometric registration, including the outer rim.
 float p[28]={w,h,w,h, 128,64,104,48, 18,4,0,0, 0,0,0,0, 1,0,0,0, .98f,.98f,.98f,0, 0,.30f,1,0};
 DrawStripes(handle,w,h,0xffd8e4ea);
 const auto source=ReadTexture(r,r->sourceTexture.Get());
 Require(RenderOffscreen(r,r->pixel.Get(),p,w,h,false)==source,"Zero-refraction geometry displaced the source");
 Check(OpticalSceneFrost(handle,1));
 p[12]=.10f;p[20]=p[21]=p[22]=250/255.f;p[23]=.055f;p[24]=1;p[27]=.04f;
 const auto soft=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);
 for(UINT y=0;y<h;++y)for(UINT x=0;x<20;++x)for(int c=0;c<4;++c)
  Require(soft[(y*w+x)*4+c]==source[(y*w+x)*4+c],"Soft plate blurred pixels outside its bounds");
 const UINT backs[]={0xff000000,0xffffffff,0xffff0000,0xffffff00,0xff0000ff,0xff00ff00,0xff808080};
 double minLight=1,maxDark=0;
 for(UINT b:backs){Check(OpticalSceneBegin(handle,w,h,1,b));Check(OpticalSceneEnd(handle));Check(OpticalSceneFrost(handle,1));
  p[20]=p[21]=p[22]=250/255.f;p[23]=.055f;p[25]=.30f;p[26]=1;auto lt=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);minLight=std::min(minLight,SrgbLuminance(&lt[(64*w+128)*4]));
  p[20]=p[21]=p[22]=32/255.f;p[23]=.075f;p[25]=.10f;p[26]=-1;auto dk=RenderOffscreen(r,r->pixel.Get(),p,w,h,true);maxDark=std::max(maxDark,SrgbLuminance(&dk[(64*w+128)*4]));}
 Require(minLight>=.29&&maxDark<=.11,"Soft plate weakened the existing readable bounds");
 std::cout<<"PASS: unrefracted registration equals source; soft blur stays inside plate; 7 backdrops x 2 polarities bounded (light "<<minLight<<", dark "<<maxDark<<").\n";
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
  Check(OpticalCreate(&panel,&handle));FrostTests(handle);ContinuousTests(handle);ClearTests(handle);UnrefractedTests(handle);
  panel.failDetach=true;Require(OpticalDestroy(handle)==E_FAIL,"Frost failed detach lost owner");
  panel.failDetach=false;Check(OpticalDestroy(handle));
  Require(resourceCounts[0]==resourceCounts[1]&&panel.refs==1,"Frost graph retained after destroy");
  CoUninitialize();
  std::cout<<"PASS: 132 exact GPU readbacks; 129 same-size uploads use 1 texture; dimension changes; rejected uploads; detach retry; balanced owner.\n";
  std::cout<<"PASS: 160 exact dynamic scene readbacks; 1 target; reentrancy rejection; resize; failed image load; balanced scene owner.\n";
  std::cout<<"PASS: frost off == accepted C1 bit-exact (with and without bound copy); rim unchanged; body within tint bound; mean-preserving blur; 17 passes reuse 1 texture; stale/upload/resize invalidation fails closed; retirement and re-entry; balanced owner.\n";
  std::cout<<"PASS: rev2 continuous glass: off == accepted C1; outside unchanged; rim/body same regime; no inner boundary on uniform field; luminance bounds over 7 backdrops x 2 themes; validation; blurred scene images released.\n";
  std::cout<<"PASS: clear (完全清透): no blurred copy required; validation; outside unchanged; no inner boundary on uniform field; luminance bounds over 7 backdrops x 2 themes.\n";
  return 0;
 } catch(HRESULT hr) {std::cerr<<"HRESULT "<<std::hex<<hr<<"\n";return 1;}
 catch(const std::exception& e) {std::cerr<<e.what()<<"\n";return 1;}
}
