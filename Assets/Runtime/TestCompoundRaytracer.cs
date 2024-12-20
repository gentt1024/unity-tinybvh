using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace tinybvh
{
    [RequireComponent(typeof(Camera))]
    public class TestCompoundRaytracer : MonoBehaviour
    {
        public enum DisplayMode
        {
            NDotL,
            RayDistance,
            BvhSteps,
            Barycentrics,
            Normals,
            UV,
        }

        [SerializeField] private DisplayMode _display = DisplayMode.NDotL;
        [SerializeField] private TestBVHCompound _testTarget;

        private Camera _sourceCamera;
        
        private CommandBuffer _cmd;

        private ComputeShader _rayGenerationShader;
        private ComputeShader _rayIntersectionShader;
        private ComputeShader _rayShadingShader;

        private int _outputWidth;
        private int _outputHeight;
        private int _totalRays;
        private RenderTexture _outputRT;
        private ComputeBuffer _rayBuffer;
        private ComputeBuffer _rayHitBuffer;

        // Shading options
        private LocalKeyword _outputNDotL;
        private LocalKeyword _outputBvhSteps;
        private LocalKeyword _outputBarycentrics;
        private LocalKeyword _outputNormals;
        private LocalKeyword _outputUVs;

        // Sun for NDotL
        private Vector3 _lightDirection = new Vector3(1.0f, 1.0f, 1.0f).normalized;
        private Vector4 _lightColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);

        // Struct sizes in bytes
        private const int RAY_STRUCT_SIZE = 24;
        private const int RAY_HIT_STRUCT_SIZE = 20;

        private void Start()
        {
            _sourceCamera = GetComponent<Camera>();
            _cmd = new CommandBuffer();

            if (_testTarget == null)
            {
                Debug.LogError("BVHManager was not found in the scene!");
            }

            _rayGenerationShader   = Resources.Load<ComputeShader>("RayGeneration");
            _rayIntersectionShader = Resources.Load<ComputeShader>("RayIntersection");
            _rayShadingShader      = Resources.Load<ComputeShader>("RayShading");

            _outputNDotL         = _rayShadingShader.keywordSpace.FindKeyword("OUTPUT_NDOTL");
            _outputBvhSteps      = _rayShadingShader.keywordSpace.FindKeyword("OUTPUT_BVH_STEPS");
            _outputBarycentrics  = _rayShadingShader.keywordSpace.FindKeyword("OUTPUT_BARYCENTRICS");
            _outputNormals       = _rayShadingShader.keywordSpace.FindKeyword("OUTPUT_NORMALS");
            _outputUVs           = _rayShadingShader.keywordSpace.FindKeyword("OUTPUT_UVS");

            // Find the directional light in the scene for NDotL
            Light[] lights = FindObjectsOfType<Light>();
            foreach (Light light in lights)
            {
                if (light.type == LightType.Directional)
                {
                    _lightDirection = -light.transform.forward;
                    _lightColor = light.color * light.intensity;
                    break;
                }
            }
        }

        private void OnDestroy()
        {
            _outputRT?.Release();
            _rayBuffer?.Release();
            _rayHitBuffer?.Release();
            _cmd?.Release();
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (_testTarget == null || !_testTarget.BvhProvider.IsReady)
            {
                Graphics.Blit(source, destination);
                return;
            }

            var bvhScene = _testTarget.BvhProvider;

            _outputWidth   = _sourceCamera.scaledPixelWidth;
            _outputHeight  = _sourceCamera.scaledPixelHeight;
            _totalRays     = _outputWidth * _outputHeight;
            int dispatchX = Mathf.CeilToInt(_totalRays / 64.0f);

            // Prepare buffers and output texture
            Utilities.PrepareBuffer(ref _rayBuffer, _totalRays, RAY_STRUCT_SIZE);
            Utilities.PrepareBuffer(ref _rayHitBuffer, _totalRays, RAY_HIT_STRUCT_SIZE);
            Utilities.PrepareRenderTexture(ref _outputRT, _outputWidth, _outputHeight, RenderTextureFormat.ARGB32);

            // Generate Rays
            _cmd.BeginSample("Ray Generation");
            {
                PrepareShader(_cmd, _rayGenerationShader, 0);

                _cmd.SetComputeMatrixParam(_rayGenerationShader, "CamInvProj", _sourceCamera.projectionMatrix.inverse);
                _cmd.SetComputeMatrixParam(_rayGenerationShader, "CamToWorld", _sourceCamera.cameraToWorldMatrix);

                _cmd.DispatchCompute(_rayGenerationShader, 0, dispatchX, 1, 1);

            }
            _cmd.EndSample("Ray Generation");

            // Perform Intersection
            _cmd.BeginSample("Ray Intersection");
            {
                PrepareShader(_cmd, _rayIntersectionShader, 0);
                bvhScene.PrepareShader(_cmd, _rayIntersectionShader, 0);

                _cmd.DispatchCompute(_rayIntersectionShader, 0, dispatchX, 1, 1);

            }
            _cmd.EndSample("Ray Intersection");

            // Shade the results
            _cmd.BeginSample("Ray Shading");
            {
                PrepareShader(_cmd, _rayShadingShader, 0);
                bvhScene.PrepareShader(_cmd, _rayShadingShader, 0);

                // Set shader keywords based on which display mode is enabled
                _rayShadingShader.SetKeyword(_outputNDotL,        _display == DisplayMode.NDotL);
                _rayShadingShader.SetKeyword(_outputBvhSteps,     _display == DisplayMode.BvhSteps);
                _rayShadingShader.SetKeyword(_outputBarycentrics, _display == DisplayMode.Barycentrics);
                _rayShadingShader.SetKeyword(_outputNormals,      _display == DisplayMode.Normals);
                _rayShadingShader.SetKeyword(_outputUVs,          _display == DisplayMode.UV);

                _cmd.DispatchCompute(_rayShadingShader, 0, dispatchX, 1, 1);
            }
            _cmd.EndSample("Ray Shading");

            // Overwrite image with output from raytracer
            _cmd.Blit(_outputRT, destination);

            Graphics.ExecuteCommandBuffer(_cmd);
            _cmd.Clear();
        }

        private void PrepareShader(CommandBuffer cmd, ComputeShader shader, int kernelIndex)
        {
            cmd.SetComputeVectorParam(shader, "LightDirection", _lightDirection);
            cmd.SetComputeVectorParam(shader, "LightColor", _lightColor);
            cmd.SetComputeFloatParam(shader, "FarPlane", _sourceCamera.farClipPlane);
            cmd.SetComputeIntParam(shader, "OutputWidth", _outputWidth);
            cmd.SetComputeIntParam(shader, "OutputHeight", _outputHeight);
            cmd.SetComputeIntParam(shader, "TotalRays", _totalRays);
            cmd.SetComputeBufferParam(shader, kernelIndex, "RayBuffer", _rayBuffer);
            cmd.SetComputeBufferParam(shader, kernelIndex, "RayHitBuffer", _rayHitBuffer);
            cmd.SetComputeTextureParam(shader, kernelIndex, "Output", _outputRT);
        }

        // -- Debug Functions --

        [ContextMenu("Debug/Cast Ray GPU")]
        private void DebugCastRayGPU()
        {
            CommandBuffer debugCmd = new CommandBuffer();

            ComputeBuffer debugRayBuffer = new ComputeBuffer(1, RAY_STRUCT_SIZE, ComputeBufferType.Structured);
            ComputeBuffer debugRayHitBuffer = new ComputeBuffer(1, RAY_HIT_STRUCT_SIZE, ComputeBufferType.Structured);

            Vector3 pos = _sourceCamera.transform.position;
            Vector3 dir = _sourceCamera.transform.forward;

            // Create and upload ray
            List<Utilities.DebugRay> debugRays = new List<Utilities.DebugRay>();
            Utilities.DebugRay ray = new Utilities.DebugRay();
            ray.origin = pos;
            ray.direction = dir;
            debugRays.Add(ray);
            debugRayBuffer.SetData(debugRays);

            // Execute ray intersection shader
            debugCmd.SetComputeFloatParam(_rayIntersectionShader, "FarPlane", _sourceCamera.farClipPlane);
            debugCmd.SetComputeIntParam(_rayIntersectionShader, "OutputWidth", 1);
            debugCmd.SetComputeIntParam(_rayIntersectionShader, "OutputHeight", 1);
            debugCmd.SetComputeBufferParam(_rayIntersectionShader, 0, "RayBuffer", debugRayBuffer);
            debugCmd.SetComputeBufferParam(_rayIntersectionShader, 0, "RayHitBuffer", debugRayHitBuffer);
            debugCmd.DispatchCompute(_rayIntersectionShader, 0, 1, 1, 1);

            Graphics.ExecuteCommandBuffer(debugCmd);
            debugCmd.Release();

            // Read back hit
            Utilities.DebugRayHit[] debugRayHits = new Utilities.DebugRayHit[1];
            debugRayHitBuffer.GetData(debugRayHits);

            Debug.Log("Ray Hit Distance: " + debugRayHits[0].t + ", Triangle Index: " + debugRayHits[0].triIndex);
            Debug.DrawLine(pos, pos + (dir * debugRayHits[0].t), Color.red, 10.0f);

            debugRayBuffer.Release();
            debugRayHitBuffer.Release();
        }
    }
}
