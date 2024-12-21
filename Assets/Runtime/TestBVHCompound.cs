using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace tinybvh
{
    public class TestBVHCompound : MonoBehaviour
    {
        private IBVHProvider _bvhProvider;

        public IBVHProvider BvhProvider => _bvhProvider;

        private void Start()
        {
            _bvhProvider = new BVHMeshCompound(
                gameObject.GetComponentsInChildren<MeshFilter>()
                    .Select(mf => (mf.sharedMesh, mf.transform.localToWorldMatrix)).ToList());
        }

        private void OnDestroy()
        {
            if (_bvhProvider is IDisposable disposableBvhScene)
                disposableBvhScene.Dispose();
        }

        [ContextMenu("Debug/Cast Ray GPU")]
        private void DebugCastRayGPU()
        {
            var rays = new List<Ray>();
            for (int i = 0; i < 10; i++)
            {
                var ray = new Ray();
                // ray.origin = new Vector3(Random.Range(-10f, 10f), Random.Range(-10f, 10f), Random.Range(-10f, 10f));
                ray.direction = Random.onUnitSphere;
                rays.Add(ray);
            }
            var rayHits = _bvhProvider.CastRayGPU(rays, 1000);
            for (var i = 0; i < rayHits.Length; i++)
            {
                var ray = rays[i];
                var rayHit = rayHits[i];
                Debug.Log($"rayHit: t: {rayHit.t} barycentric: {rayHit.barycentric} " +
                          $"normal: {_bvhProvider.GetNormal((int)rayHit.triIndex, rayHit.barycentric)}");
                if (rayHit.t is > 0 and < 1000)
                {
                    Debug.DrawLine(ray.origin, ray.GetPoint(rayHit.t), Color.red, 10);
                }
            }
        }
        
    }
}