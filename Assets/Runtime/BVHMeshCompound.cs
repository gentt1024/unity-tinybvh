using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace tinybvh
{
    public class BVHMeshCompound : IDisposable, IBVHProvider
    {
        private ComputeShader _meshProcessingShader;
        private LocalKeyword _has32BitIndicesKeyword;
        private LocalKeyword _hasNormalsKeyword;
        private LocalKeyword _hasUVsKeyword;
        
        private int _totalVertexCount = 0;
        private int _totalTriangleCount = 0;
        private DateTime _readbackStartTime;

        private ComputeBuffer _vertexPositionBufferGPU;
        private NativeArray<Vector4> _vertexPositionBufferCPU;
        private ComputeBuffer _triangleAttributesBufferGPU;
        private NativeArray<Utilities.TriangleAttributes> _triangleAttributesBufferCPU;
        
        private bool _hasVertexBufferCPUReady = false;
        private bool _hasTriangleBufferCPUReady = false;

        // BVH data
        private readonly BVH _bvh = new BVH();
        private bool _hasBvhBuilt = false;
        private ComputeBuffer _bvhNodes;
        private ComputeBuffer _bvhTris;
        
        // Struct sizes in bytes
        private const int VERTEX_POSITION_SIZE = 16;
        private const int TRIANGLE_ATTRIBUTE_SIZE = 60;
        private const int BVH_NODE_SIZE = 80;
        private const int BVH_TRI_SIZE = 16;

        private readonly CancellationTokenSource _buildTaskCancellationTokenSource = new ();

        public bool IsReady => _hasBvhBuilt && _hasVertexBufferCPUReady && _hasTriangleBufferCPUReady;

        public BVHMeshCompound(IList<(Mesh mesh, Matrix4x4 matrix)> mfs)
        {
            // Load compute shader
            _meshProcessingShader   = Resources.Load<ComputeShader>("MeshProcessing");
            _has32BitIndicesKeyword = _meshProcessingShader.keywordSpace.FindKeyword("HAS_32_BIT_INDICES");
            _hasNormalsKeyword      = _meshProcessingShader.keywordSpace.FindKeyword("HAS_NORMALS");
            _hasUVsKeyword          = _meshProcessingShader.keywordSpace.FindKeyword("HAS_UVS");

            ProcessMeshes(mfs);
        }
        
        public void Dispose()
        {
            _buildTaskCancellationTokenSource.Cancel();
            _buildTaskCancellationTokenSource.Dispose();
            _vertexPositionBufferGPU?.Release();
            _triangleAttributesBufferGPU?.Release();

            if (_vertexPositionBufferCPU.IsCreated)
            {
                _vertexPositionBufferCPU.Dispose();
            }

            if (_triangleAttributesBufferCPU.IsCreated)
            {
                _triangleAttributesBufferCPU.Dispose();
            }

            _bvh.Destroy();
        }

        private void ProcessMeshes(IList<(Mesh mesh, Matrix4x4 matrix)> mfs)
        {
            _totalVertexCount = 0;
            _totalTriangleCount = 0;

            // Gather info on the mesh we'll be using
            foreach (var mesh in mfs.Select(mf => mf.mesh))
            {
                _totalVertexCount += Utilities.GetTriangleCount(mesh) * 3;
            }

            // Allocate buffers
            _vertexPositionBufferGPU  = new ComputeBuffer(_totalVertexCount, VERTEX_POSITION_SIZE);
            _vertexPositionBufferCPU  = new NativeArray<Vector4>(_totalVertexCount * VERTEX_POSITION_SIZE, Allocator.Persistent);
            _triangleAttributesBufferGPU = new ComputeBuffer(_totalVertexCount / 3, TRIANGLE_ATTRIBUTE_SIZE);
            _triangleAttributesBufferCPU = new NativeArray<Utilities.TriangleAttributes>(_totalVertexCount / 3 * TRIANGLE_ATTRIBUTE_SIZE, Allocator.Persistent);

            // Pack each mesh into global vertex buffer via compute shader
            // Note: this avoids the need for every mesh to have cpu read/write access.
            foreach (var mf in mfs)
            {
                Mesh mesh = mf.mesh;
                if (mesh == null)
                {
                    continue;
                }
                
                mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                GraphicsBuffer vertexBuffer = mesh.GetVertexBuffer(0);

                mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
                GraphicsBuffer indexBuffer = mesh.GetIndexBuffer();
                
                int triangleCount = Utilities.GetTriangleCount(mesh);

                // Determine where in the Unity vertex buffer each vertex attribute is
                int vertexStride, positionOffset, normalOffset, uvOffset;
                Utilities.FindVertexAttribute(mesh, VertexAttribute.Position, out positionOffset, out vertexStride);
                Utilities.FindVertexAttribute(mesh, VertexAttribute.Normal, out normalOffset, out vertexStride);
                Utilities.FindVertexAttribute(mesh, VertexAttribute.TexCoord0, out uvOffset, out vertexStride);

                _meshProcessingShader.SetBuffer(0, "VertexBuffer", vertexBuffer);
                _meshProcessingShader.SetBuffer(0, "IndexBuffer", indexBuffer);
                _meshProcessingShader.SetBuffer(0, "VertexPositionBuffer", _vertexPositionBufferGPU);
                _meshProcessingShader.SetBuffer(0, "TriangleAttributesBuffer", _triangleAttributesBufferGPU);
                _meshProcessingShader.SetInt("VertexStride", vertexStride);
                _meshProcessingShader.SetInt("PositionOffset", positionOffset);
                _meshProcessingShader.SetInt("NormalOffset", normalOffset);
                _meshProcessingShader.SetInt("UVOffset", uvOffset);
                _meshProcessingShader.SetInt("TriangleCount", triangleCount);
                _meshProcessingShader.SetInt("OutputTriangleStart", _totalTriangleCount);
                _meshProcessingShader.SetMatrix("LocalToWorld", mf.matrix);

                // Set keywords based on format/attributes of this mesh
                _meshProcessingShader.SetKeyword(_has32BitIndicesKeyword, (mesh.indexFormat == IndexFormat.UInt32));
                _meshProcessingShader.SetKeyword(_hasNormalsKeyword, mesh.HasVertexAttribute(VertexAttribute.Normal));
                _meshProcessingShader.SetKeyword(_hasUVsKeyword, mesh.HasVertexAttribute(VertexAttribute.TexCoord0));

                _meshProcessingShader.Dispatch(0, Mathf.CeilToInt(triangleCount / 64.0f), 1, 1);

                _totalTriangleCount += triangleCount;
            }

            Debug.Log("Meshes processed. Total triangles: " + _totalTriangleCount);

            // Initiate async readback of vertex buffer to pass to tinybvh to build
            _readbackStartTime = DateTime.UtcNow;
            AsyncGPUReadback.RequestIntoNativeArray(ref _vertexPositionBufferCPU, _vertexPositionBufferGPU, OnVertexPositionBufferCompleteReadback);
            AsyncGPUReadback.RequestIntoNativeArray(ref _triangleAttributesBufferCPU, _triangleAttributesBufferGPU, OnTriangleAttributesBufferCompleteReadback);
        }

        private unsafe void OnVertexPositionBufferCompleteReadback(AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                Debug.LogError("GPU readback error.");
                return;
            }

            TimeSpan readbackTime = DateTime.UtcNow - _readbackStartTime;
            Debug.Log("GPU readback took: " + readbackTime.TotalMilliseconds + "ms");

            // In the editor if we exit play mode before the bvh is finished building the memory will be freed
            // and tinybvh will illegal access and crash everything. 
            #if UNITY_EDITOR
                NativeArray<Vector4> persistentBuffer = new NativeArray<Vector4>(_vertexPositionBufferCPU.Length, Allocator.Persistent);
                persistentBuffer.CopyFrom(_vertexPositionBufferCPU);
                var dataPointer = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(persistentBuffer);
            #else
                var dataPointer = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(vertexPositionBufferCPU);
            #endif
            
            // Build BVH in thread.
            Thread thread = new Thread(() =>
            {
                DateTime bvhStartTime = DateTime.UtcNow;
                _bvh.Build(dataPointer, _totalTriangleCount, true);
                TimeSpan bvhTime = DateTime.UtcNow - bvhStartTime;

                Debug.Log("BVH built in: " + bvhTime.TotalMilliseconds + "ms");

                #if UNITY_EDITOR
                    persistentBuffer.Dispose();
                #endif
            });
            
            thread.Start();

            GetCWBVHData().Forget();
            
            _hasVertexBufferCPUReady = true;
        }
        
        private void OnTriangleAttributesBufferCompleteReadback(AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                Debug.LogError("GPU readback error.");
                return;
            }
            
            _hasTriangleBufferCPUReady = true;
        }

        private async UniTask GetCWBVHData()
        {
            await UniTask.WaitUntil(() => _bvh.IsReady(), cancellationToken: _buildTaskCancellationTokenSource.Token);
            
            // Check if the build was cancelled
            if (_buildTaskCancellationTokenSource.Token.IsCancellationRequested)
            {
                Debug.Log("BVH build cancelled.");
                return;
            }
            
            // Get the sizes of the arrays
            int nodesSize = _bvh.GetCWBVHNodesSize();
            int trisSize = _bvh.GetCWBVHTrisSize();

            IntPtr nodesPtr, trisPtr;
            if (_bvh.GetCWBVHData(out nodesPtr, out trisPtr))
            {
                Utilities.UploadFromPointer(ref _bvhNodes, nodesPtr, nodesSize, BVH_NODE_SIZE);
                Utilities.UploadFromPointer(ref _bvhTris, trisPtr, trisSize, BVH_TRI_SIZE);
                _hasBvhBuilt = true;
            }
            else
            {
                Debug.LogError("Failed to fetch updated BVH data.");
            }
        }

        public void PrepareShader(CommandBuffer cmd, ComputeShader shader, int kernelIndex)
        {
            cmd.SetComputeBufferParam(shader, kernelIndex, "BVHNodes", _bvhNodes);
            cmd.SetComputeBufferParam(shader, kernelIndex, "BVHTris", _bvhTris);
            cmd.SetComputeBufferParam(shader, kernelIndex, "TriangleAttributesBuffer", _triangleAttributesBufferGPU);
        }
        
        public Vector3 GetNormal(int triangleIndex, Vector2 barycentric)
        {
            var triangleAttributes = _triangleAttributesBufferCPU[triangleIndex];
            return InterpolateAttribute(barycentric, triangleAttributes.normal0, triangleAttributes.normal1, triangleAttributes.normal2).normalized;
        }
        
        private Vector3 InterpolateAttribute(Vector2 barycentric, Vector3 attr0, Vector3 attr1, Vector3 attr2)
        {
            return attr0 * (1.0f - barycentric.x - barycentric.y) + attr1 * barycentric.x + attr2 * barycentric.y;
        }
    }
}