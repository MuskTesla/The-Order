#include <windows.h>
#include <windows.graphics.effects.interop.h>
#include <windows.foundation.h>
#include <d2d1effects.h>
#include <d2d1_1.h>
#include <wrl.h>
#include <wrl/wrappers/corewrappers.h>
#include <array>

using namespace Microsoft::WRL;
using namespace Microsoft::WRL::Wrappers;
using namespace ABI::Windows::Graphics::Effects;
using ABI::Windows::Foundation::IPropertyValue;
using ABI::Windows::Foundation::IPropertyValueStatics;

// Public effect metadata only: no device, shader, textures, capture or render loop.
// The compositor compiles GammaTransfer channel isolation + Composite/PLUS.
class ChannelEffect final : public RuntimeClass<RuntimeClassFlags<WinRtClassicComMix>,
    IGraphicsEffect, IGraphicsEffectSource, IGraphicsEffectD2D1Interop>
{
    InspectableClass(L"TheOrder.Test.ChannelEffect", BaseTrust);
public:
    bool composite = false;
    unsigned channel = 0;
    std::array<ComPtr<IGraphicsEffectSource>, 3> sources;
    ComPtr<IPropertyValueStatics> values;
    HString effectName;
    HRESULT Initialize() noexcept {
        return RoGetActivationFactory(HStringReference(RuntimeClass_Windows_Foundation_PropertyValue).Get(), IID_PPV_ARGS(&values));
    }
    IFACEMETHODIMP get_Name(HSTRING* value) override { return effectName.CopyTo(value); }
    IFACEMETHODIMP put_Name(HSTRING value) override { return effectName.Set(value); }
    IFACEMETHODIMP GetEffectId(GUID* id) override {
        if (!id) return E_POINTER;
        *id = composite ? CLSID_D2D1Composite : CLSID_D2D1GammaTransfer; return S_OK;
    }
    IFACEMETHODIMP GetNamedPropertyMapping(LPCWSTR, UINT*, GRAPHICS_EFFECT_PROPERTY_MAPPING*) override { return E_INVALIDARG; }
    IFACEMETHODIMP GetPropertyCount(UINT* count) override { if (!count) return E_POINTER; *count = composite ? 1 : 17; return S_OK; }
    IFACEMETHODIMP GetProperty(UINT index, IPropertyValue** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        ComPtr<IInspectable> boxed;
        HRESULT hr;
        if (composite) {
            if (index != 0) return E_INVALIDARG;
            hr = values->CreateUInt32(D2D1_COMPOSITE_MODE_PLUS, &boxed);
        } else {
            if (index > 16) return E_INVALIDARG;
            if (index == 16 || index % 4 == 3) hr = values->CreateBoolean(false, &boxed);
            else {
                // Gamma works in straight alpha. Share alpha among the three
                // branches and compensate each selected RGB amplitude BEFORE
                // premultiplication. PLUS then restores RGB and alpha to 1x.
                // Leaving alpha=1 in all branches caused an observed dark ring.
                float v = index % 4 == 1 ? 1.0f : index % 4 == 0 ? (index / 4 == 3 ? 1.0f / 3 : index / 4 == channel ? 3.0f : 0.0f) : 0.0f;
                hr = values->CreateSingle(v, &boxed);
            }
        }
        return FAILED(hr) ? hr : boxed->QueryInterface(IID_PPV_ARGS(value));
    }
    IFACEMETHODIMP GetSourceCount(UINT* count) override { if (!count) return E_POINTER; *count = composite ? 3 : 1; return S_OK; }
    IFACEMETHODIMP GetSource(UINT index, IGraphicsEffectSource** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        return index >= (composite ? 3u : 1u) ? E_INVALIDARG : sources[index].CopyTo(value);
    }
};

extern "C" __declspec(dllexport) HRESULT __stdcall CreateChannelEffect(
    IGraphicsEffectSource* red, IGraphicsEffectSource* green, IGraphicsEffectSource* blue, IGraphicsEffect** result) noexcept
{
    if (!red || !green || !blue || !result) return E_POINTER;
    *result = nullptr;
    auto combined = Make<ChannelEffect>();
    if (!combined) return E_OUTOFMEMORY;
    HRESULT hr = combined->Initialize(); if (FAILED(hr)) return hr;
    combined->composite = true;
    IGraphicsEffectSource* inputs[] = { red, green, blue };
    for (unsigned i = 0; i < 3; ++i) {
        auto isolated = Make<ChannelEffect>(); if (!isolated) return E_OUTOFMEMORY;
        hr = isolated->Initialize(); if (FAILED(hr)) return hr;
        isolated->channel = i; isolated->sources[0] = inputs[i];
        hr = isolated.As(&combined->sources[i]); if (FAILED(hr)) return hr;
    }
    return combined.CopyTo(result);
}
