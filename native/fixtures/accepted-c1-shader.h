// Frozen verbatim copy of the accepted C1 pixel shader (baseline 7d74e24 = ad59c6e + handoff snapshot,
// native/TheOrder.WinUI/Materials/Native/OpticalRenderer.cpp). Reference for bit-exact regression only.
#pragma once
const char* acceptedC1Shader = R"shader(
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
