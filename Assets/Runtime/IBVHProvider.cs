using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace tinybvh
{
    public interface IBVHProvider
    {
        bool IsReady { get; }
        void PrepareShader(CommandBuffer cmd, ComputeShader shader, int kernelIndex);
        Vector3 GetNormal(int triangleIndex, Vector2 barycentric);
    }

    public static class BVHProviderEx
    {
        private static readonly ComputeShader RayIntersectionShader = Resources.Load<ComputeShader>("RayIntersection");

        public static unsafe Utilities.DebugRayHit[] CastRayGPU(this IBVHProvider bvhProvider, List<Ray> rays, float farPlane)
        {
            if (bvhProvider is not { IsReady: true })
            {
                return null;
            }
            
            var raysCount = rays.Count;
            int dispatchX = Mathf.CeilToInt(raysCount / 64.0f);
            
            // Prepare buffers and output texture
            ComputeBuffer debugRayBuffer = new ComputeBuffer(raysCount, sizeof(Utilities.DebugRay), ComputeBufferType.Structured);
            ComputeBuffer debugRayHitBuffer = new ComputeBuffer(raysCount, sizeof(Utilities.DebugRayHit), ComputeBufferType.Structured);
            
            // Create and upload ray
            List<Utilities.DebugRay> debugRays = rays.Select(unityRay => 
                new Utilities.DebugRay { origin = unityRay.origin, direction = unityRay.direction }).ToList();
            debugRayBuffer.SetData(debugRays);

            // Execute ray intersection shader
            CommandBuffer debugCmd = new CommandBuffer();
            debugCmd.BeginSample("Ray Intersection");
            var kernelIndex = RayIntersectionShader.FindKernel("RayIntersection");
            bvhProvider.PrepareShader(debugCmd, RayIntersectionShader, kernelIndex);
            debugCmd.SetComputeIntParam(RayIntersectionShader, "TotalRays", raysCount);
            debugCmd.SetComputeFloatParam(RayIntersectionShader, "FarPlane", farPlane);
            debugCmd.SetComputeBufferParam(RayIntersectionShader, kernelIndex, "RayBuffer", debugRayBuffer);
            debugCmd.SetComputeBufferParam(RayIntersectionShader, kernelIndex, "RayHitBuffer", debugRayHitBuffer);
            debugCmd.DispatchCompute(RayIntersectionShader, kernelIndex, dispatchX, 1, 1);
            debugCmd.EndSample("Ray Intersection");
            
            Graphics.ExecuteCommandBuffer(debugCmd);

            // Read back hit
            var debugRayHits = new Utilities.DebugRayHit[raysCount];
            debugRayHitBuffer.GetData(debugRayHits);

            debugCmd.Release();
            debugRayBuffer.Release();
            debugRayHitBuffer.Release();

            return debugRayHits;
        }
        
        public static unsafe Utilities.DebugRayHit2[] CastRayGPU2(this IBVHProvider bvhProvider, List<Ray> rays, float farPlane)
        {
            if (bvhProvider is not { IsReady: true })
            {
                return null;
            }
            
            var raysCount = rays.Count;
            int dispatchX = Mathf.CeilToInt(raysCount / 64.0f);
            
            // Prepare buffers and output texture
            ComputeBuffer debugRayBuffer = new ComputeBuffer(raysCount, sizeof(Utilities.DebugRay), ComputeBufferType.Structured);
            ComputeBuffer debugRayHitBuffer = new ComputeBuffer(raysCount, sizeof(Utilities.DebugRayHit2), ComputeBufferType.Structured);
            
            // Create and upload ray
            List<Utilities.DebugRay> debugRays = rays.Select(unityRay => 
                new Utilities.DebugRay { origin = unityRay.origin, direction = unityRay.direction }).ToList();
            debugRayBuffer.SetData(debugRays);

            // Execute ray intersection shader
            CommandBuffer debugCmd = new CommandBuffer();
            debugCmd.BeginSample("Ray Intersection");
            var kernelIndex = RayIntersectionShader.FindKernel("RayIntersection2");
            bvhProvider.PrepareShader(debugCmd, RayIntersectionShader, kernelIndex);
            debugCmd.SetComputeIntParam(RayIntersectionShader, "TotalRays", raysCount);
            debugCmd.SetComputeFloatParam(RayIntersectionShader, "FarPlane", farPlane);
            debugCmd.SetComputeBufferParam(RayIntersectionShader, kernelIndex, "RayBuffer", debugRayBuffer);
            debugCmd.SetComputeBufferParam(RayIntersectionShader, kernelIndex, "RayHitBuffer2", debugRayHitBuffer);
            debugCmd.DispatchCompute(RayIntersectionShader, kernelIndex, dispatchX, 1, 1);
            debugCmd.EndSample("Ray Intersection");
            
            Graphics.ExecuteCommandBuffer(debugCmd);

            // Read back hit
            var debugRayHits = new Utilities.DebugRayHit2[raysCount];
            debugRayHitBuffer.GetData(debugRayHits);

            debugCmd.Release();
            debugRayBuffer.Release();
            debugRayHitBuffer.Release();

            return debugRayHits;
        }
    }
}