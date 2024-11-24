#include <deque>
#include <mutex>

#define TINYBVH_IMPLEMENTATION
#include "tinybvh/tiny_bvh.h"
#include "plugin.h"

// Global container for BVHs and a mutex for thread safety
std::deque<tinybvh::BVH*> gBVHs;
std::mutex gBVHMutex;

// Adds a bvh to the global list either reusing an empty slot or making a new one.
int AddBVH(tinybvh::BVH* newBVH) 
{
    std::lock_guard<std::mutex> lock(gBVHMutex);

    // Look for a free entry to reuse
    for (size_t i = 0; i < gBVHs.size(); ++i) 
    {
        if (gBVHs[i] == nullptr) 
        {
            gBVHs[i] = newBVH;
            return static_cast<int>(i);
        }
    }

    // If no free entry is found, append a new one
    gBVHs.push_back(newBVH);
    return static_cast<int>(gBVHs.size() - 1);
}

// Fetch a pointer to a BVH by index, or nullptr if the index is invalid
tinybvh::BVH* GetBVH(int index)
{
    std::lock_guard<std::mutex> lock(gBVHMutex);
    if (index >= 0 && index < static_cast<int>(gBVHs.size())) 
    {
        return gBVHs[index];
    }
    return nullptr;
}

int BuildBVH(tinybvh::bvhvec4* vertices, int triangleCount, bool buildCWBVH)
{
    tinybvh::BVH* bvh = new tinybvh::BVH();
    bvh->Build(vertices, triangleCount);

    if (buildCWBVH)
    {
        // HACK: cwbvh building currently can end up with > 3 triangles in a node
        // which breaks traversal. We use SplitLeafs to make it one tri per node.
        bvh->Convert(tinybvh::BVH::WALD_32BYTE, tinybvh::BVH::VERBOSE);
        bvh->SplitLeafs();
        bvh->Convert(tinybvh::BVH::VERBOSE, tinybvh::BVH::WALD_32BYTE);
        bvh->Convert(tinybvh::BVH::WALD_32BYTE, tinybvh::BVH::BASIC_BVH8);
        bvh->Convert(tinybvh::BVH::BASIC_BVH8, tinybvh::BVH::CWBVH);
    }
    
    return AddBVH(bvh);
}

void DestroyBVH(int index) 
{
    std::lock_guard<std::mutex> lock(gBVHMutex);
    if (index >= 0 && index < static_cast<int>(gBVHs.size())) 
    {
        if (gBVHs[index] != nullptr)
        {
            delete gBVHs[index];
            gBVHs[index] = nullptr;
        }
    }
}

bool IsBVHReady(int index)
{
    tinybvh::BVH* bvh = GetBVH(index);
    return (bvh != nullptr);
}

tinybvh::Intersection Intersect(int index, tinybvh::bvhvec3 origin, tinybvh::bvhvec3 direction, bool useCWBVH)
{
    tinybvh::BVH* bvh = GetBVH(index);
    if (bvh != nullptr)
    {
        tinybvh::Ray ray(origin, direction);
        bvh->Intersect(ray, useCWBVH ? tinybvh::BVH::CWBVH : tinybvh::BVH::WALD_32BYTE);
        return ray.hit;
    }
    return tinybvh::Intersection();
}

int GetCWBVHNodesSize(int index)
{
    tinybvh::BVH* bvh = GetBVH(index);
    return (bvh != nullptr) ? bvh->usedCWBVHBlocks * 16 : 0;
}

int GetCWBVHTrisSize(int index) 
{
    tinybvh::BVH* bvh = GetBVH(index);
    return (bvh != nullptr) ? bvh->idxCount * 3 * 16 : 0;
}

bool GetCWBVHData(int index, tinybvh::bvhvec4** bvhNodes, tinybvh::bvhvec4** bvhTris) 
{
    tinybvh::BVH* bvh = GetBVH(index);
    if (bvh != nullptr && bvh->bvh8Compact != nullptr && bvh->bvh8Tris != nullptr) 
    {
        *bvhNodes = bvh->bvh8Compact;
        *bvhTris  = bvh->bvh8Tris;
        return true;
    }
    return false;
}