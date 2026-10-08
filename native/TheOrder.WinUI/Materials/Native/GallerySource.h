#pragma once
#include <d2d1_1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <vector>
#pragma comment(lib, "dxguid.lib") // CLSID_D2D1GaussianBlur

// Gallery-owned scene producer, never a capture API. All calls use the owning
// UI thread and the renderer's D3D device. EndDraw submits before the same
// immediate context samples the surface. D2D/D3D retain in-flight GPU resources;
// no CPU fence, sleep, forced collection, or application-owned retirement queue.
struct GallerySource {
 ComPtr<ID2D1Factory1> factory;
 ComPtr<ID2D1Device> device;
 ComPtr<ID2D1DeviceContext> context;
 ComPtr<ID2D1Bitmap1> target;
 ComPtr<ID2D1SolidColorBrush> brush;
 ComPtr<IDWriteFactory> textFactory;
 ComPtr<IWICImagingFactory> wic;
 ComPtr<ID2D1Effect> blur;              // one per device context; input detached after each pass
 ComPtr<ID2D1Bitmap1> frostTarget;      // wraps the renderer-owned frost texture
 ID3D11Texture2D* frostTexture=nullptr; // identity only; the renderer holds the reference
 float dpiScale=1;
 std::vector<ComPtr<ID2D1Bitmap1>> frameImages; // <=6; only until EndDraw.
 std::vector<ComPtr<ID2D1Effect>> frameEffects; // blurred images in this frame; only until EndDraw.
 bool drawing=false;
 UINT64 completed=0, images=0, texts=0, targets=0;
 static D2D1_COLOR_F Color(UINT argb) {
  return D2D1::ColorF(((argb>>16)&255)/255.f,((argb>>8)&255)/255.f,(argb&255)/255.f,((argb>>24)&255)/255.f);
 }
 void Init(ID3D11Device* d3d) {
  if(context)return;
  Check(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED,factory.GetAddressOf()));
  ComPtr<IDXGIDevice> dxgi; Check(d3d->QueryInterface(IID_PPV_ARGS(&dxgi)));
  Check(factory->CreateDevice(dxgi.Get(),&device));
  Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,&context));
  Check(context->CreateSolidColorBrush(D2D1::ColorF(0,0,0,1),&brush));
  Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED,__uuidof(IDWriteFactory),reinterpret_cast<IUnknown**>(textFactory.GetAddressOf())));
  Check(CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic)));
 }
 void SetSurface(ID3D11Texture2D* texture,float scale) {
  if(drawing)throw E_ILLEGAL_METHOD_CALL;
  context->SetTarget(nullptr);target.Reset();
  ComPtr<IDXGISurface> surface;Check(texture->QueryInterface(IID_PPV_ARGS(&surface)));
  // TARGET without CANNOT_DRAW: the frost pass reads this bitmap as an effect input.
  auto properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
    D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_IGNORE),96*scale,96*scale);
  Check(context->CreateBitmapFromDxgiSurface(surface.Get(),&properties,&target));++targets;dpiScale=scale;
 }
 void ReleaseFrost() {
  if(drawing)throw E_ILLEGAL_METHOD_CALL;
  if(context)context->SetTarget(nullptr);
  frostTarget.Reset();frostTexture=nullptr;
 }
 // One Gaussian pass from the completed scene bitmap into the frost texture.
 HRESULT Frost(ID3D11Texture2D* texture,float sigma) noexcept {
  if(drawing||!target||!texture||!context)return E_ILLEGAL_METHOD_CALL;
  try {
   if(!blur) {
    Check(context->CreateEffect(CLSID_D2D1GaussianBlur,&blur));
    Check(blur->SetValue(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE,D2D1_BORDER_MODE_HARD));
    Check(blur->SetValue(D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION,D2D1_GAUSSIANBLUR_OPTIMIZATION_QUALITY));
   }
   if(frostTexture!=texture) {
    frostTarget.Reset();
    ComPtr<IDXGISurface> surface;Check(texture->QueryInterface(IID_PPV_ARGS(&surface)));
    auto properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
      D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_IGNORE),96*dpiScale,96*dpiScale);
    Check(context->CreateBitmapFromDxgiSurface(surface.Get(),&properties,&frostTarget));frostTexture=texture;
   }
   Check(blur->SetValue(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION,sigma)); // DIP at the scene DPI
   blur->SetInput(0,target.Get());
   context->SetTarget(frostTarget.Get());context->SetDpi(96*dpiScale,96*dpiScale);
   context->SetTransform(D2D1::Matrix3x2F::Identity());
   context->BeginDraw();context->Clear(D2D1::ColorF(0,0,0,1));context->DrawImage(blur.Get());
   auto hr=context->EndDraw();
   blur->SetInput(0,nullptr);context->SetTarget(nullptr);
   return hr;
  } catch(HRESULT hr) {
   if(blur)blur->SetInput(0,nullptr);
   context->SetTarget(nullptr);
   return hr;
  }
 }
 void Begin(float scale,UINT argb) {
  if(drawing||!target)throw E_ILLEGAL_METHOD_CALL;
  context->SetTarget(target.Get());context->SetDpi(96*scale,96*scale);
  context->SetTransform(D2D1::Matrix3x2F::Identity());
  context->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
  context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
  context->BeginDraw();drawing=true;context->Clear(Color(argb));
 }
 void Rect(float x,float y,float w,float h,UINT argb) {
  if(!drawing)throw E_ILLEGAL_METHOD_CALL;
  brush->SetColor(Color(argb));context->FillRectangle(D2D1::RectF(x,y,x+w,y+h),brush.Get());
 }
 void Text(const wchar_t* value,float x,float y,float size,UINT argb,UINT weight) {
  if(!drawing||!value)throw E_ILLEGAL_METHOD_CALL;
  ComPtr<IDWriteTextFormat> format;
  Check(textFactory->CreateTextFormat(L"Segoe UI Variable",nullptr,static_cast<DWRITE_FONT_WEIGHT>(weight),
    DWRITE_FONT_STYLE_NORMAL,DWRITE_FONT_STRETCH_NORMAL,size,L"zh-CN",&format));
  Check(format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP));
  const auto length=static_cast<UINT32>(wcslen(value));
  ComPtr<IDWriteTextLayout> layout;
  Check(textFactory->CreateTextLayout(value,length,format.Get(),1300-x,100,&layout));
  // Match the frozen Gallery's observed XAML CJK fallback. DirectWrite's
  // default WWS fallback otherwise selects a visibly heavier Chinese face.
  // This finite scene mapping does not change any application's UI font.
  for(UINT32 i=0;i<length;++i)if(value[i]>=0x4e00&&value[i]<=0x9fff)
    Check(layout->SetFontWeight(DWRITE_FONT_WEIGHT_NORMAL,DWRITE_TEXT_RANGE{i,1}));
  brush->SetColor(Color(argb));
  context->DrawTextLayout(D2D1::Point2F(x,y),layout.Get(),brush.Get());++texts;
 }
 void Image(const wchar_t* path,float x,float y,float w,float h,bool fill,float sigma=0) {
  if(!drawing||!path||frameImages.size()>=6)throw E_INVALIDARG;
  ComPtr<IWICBitmapDecoder> decoder;Check(wic->CreateDecoderFromFilename(path,nullptr,GENERIC_READ,WICDecodeMetadataCacheOnLoad,&decoder));
  ComPtr<IWICBitmapFrameDecode> frame;Check(decoder->GetFrame(0,&frame));
  ComPtr<IWICFormatConverter> converter;Check(wic->CreateFormatConverter(&converter));
  Check(converter->Initialize(frame.Get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
  ComPtr<ID2D1Bitmap1> image;
  // Effects run at the context DPI; give blurred inputs that DPI so their DIP size matches the transform.
  // The sharp path keeps the accepted default (DPI inferred from the image).
  if(sigma>0){auto props=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96*dpiScale,96*dpiScale);
   Check(context->CreateBitmapFromWicBitmap(converter.Get(),&props,&image));}
  else Check(context->CreateBitmapFromWicBitmap(converter.Get(),nullptr,&image));
  const auto size=image->GetSize();auto src=D2D1::RectF(0,0,size.width,size.height);
  if(fill) {
   const auto ratio=w/h,actual=size.width/size.height;
   if(actual>ratio){const float cut=(size.width-size.height*ratio)/2;src.left=cut;src.right=size.width-cut;}
   else {const float cut=(size.height-size.width/ratio)/2;src.top=cut;src.bottom=size.height-cut;}
  }
  if(sigma>0) {
   // Ambient artwork: blur in image space so sigma is in destination DIPs; own effect per draw
   // because D2D may evaluate the graph at EndDraw.
   const float s=w/(src.right-src.left);
   ComPtr<ID2D1Effect> effect;Check(context->CreateEffect(CLSID_D2D1GaussianBlur,&effect));
   Check(effect->SetValue(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE,D2D1_BORDER_MODE_HARD));
   Check(effect->SetValue(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION,sigma/s));
   effect->SetInput(0,image.Get());
   context->PushAxisAlignedClip(D2D1::RectF(x,y,x+w,y+h),D2D1_ANTIALIAS_MODE_ALIASED);
   context->SetTransform(D2D1::Matrix3x2F::Scale(s,s)*D2D1::Matrix3x2F::Translation(x-src.left*s,y-src.top*s));
   context->DrawImage(effect.Get());
   context->SetTransform(D2D1::Matrix3x2F::Identity());context->PopAxisAlignedClip();
   frameEffects.push_back(std::move(effect));
  }
  else context->DrawBitmap(image.Get(),D2D1::RectF(x,y,x+w,y+h),1,D2D1_INTERPOLATION_MODE_LINEAR,&src);
  frameImages.push_back(std::move(image));++images;
 }
 HRESULT End() noexcept {
  if(!drawing)return E_ILLEGAL_METHOD_CALL;
  auto hr=context->EndDraw();drawing=false;
  context->SetTarget(nullptr);frameEffects.clear();frameImages.clear();
  if(SUCCEEDED(hr))++completed;
  return hr;
 }
 ~GallerySource(){if(drawing)End();if(context)context->SetTarget(nullptr);frostTarget.Reset();blur.Reset();}
};
