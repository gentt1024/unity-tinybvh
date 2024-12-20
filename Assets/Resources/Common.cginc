#ifndef __UNITY_TINYBVH_COMMON__
#define __UNITY_TINYBVH_COMMON__

struct Ray
{
    float3 origin;
    float3 direction;
};

struct RayHit
{
    float t;
    float2 barycentric;
    uint triIndex;
    uint steps;
};

struct RayHit2
{
    float t;
    float3 hitNormal;
    uint triIndex;
};

// NOTE: These could be quantized and packed better
struct TriangleAttributes
{
    float3 normal0;
    float3 normal1;
    float3 normal2;
    
    float2 uv0;
    float2 uv1;
    float2 uv2;
};

uint TotalRays;
RWStructuredBuffer<Ray> RayBuffer;
RWStructuredBuffer<RayHit> RayHitBuffer;
RWStructuredBuffer<RayHit2> RayHitBuffer2;
RWStructuredBuffer<TriangleAttributes> TriangleAttributesBuffer;

float FarPlane;
uint OutputWidth;
uint OutputHeight;
RWTexture2D<float4> Output;

#endif