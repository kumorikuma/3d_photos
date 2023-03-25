using System.Collections.Generic;
using UnityEngine;

// Recursively transforms flatter and less detailed regions of the mesh into simpler quads.
// "Detail in the mesh" will be considered the amount of change in the depth values of the vertices.
// See page for diagram and videos: https://kumorikuma.dev/3d_photos/#walkthrough
//
// The algorithm works as follows... Take a rectangular region (start with the entire image) and compute the maximum deviation in distance (i.e. max - min) inside that region. 
// If the deviation is below the specified threshold, simplify the region by creating a new vertex at each corner and replacing that entire region with two triangles. 
// Otherwise, we will split the region into four quadrants, and then recursively repeat this check on each of those four rectangular regions. 
// If the region becomes smaller than a certain size, then it could not be simplified and we keep all of the old vertices and triangles.
// 
// This algorithm is quite fast since it can leverage dynamic programming. The distances for each vertex can be calculated in advance, and the min/max bounds for each region can be cached. 
// There's one problem with this method, which is that it can create gaps because the lower resolution regions' edges are put against higher resolution regions' edges. 
// It can be fixed by iterating through all the regions in order of descending size (i.e. low res first), and for all of the vertices that fall on the region's edges, 
// move them such that they lie on the edge (which is simply a linear interpolation of the two vertices that form the edge). 
// Additionally, each vertex should only be moved at most one time (from a higher resolution edge to a lower resolution edge).
public class MeshSimplification {
    public struct Region {
        public int x1;
        public int x2;
        public int y1;
        public int y2;

        public Region(int x1, int x2, int y1, int y2) {
            this.x1 = x1;
            this.x2 = x2;
            this.y1 = y1;
            this.y2 = y2;
        }
    }

    // Simplifies a mesh that has a vertex layout in the shape of a grid.
    // - Vertices / UVs needs to be a 2D array of width * height.
    // - VertexMask can specify vertices to ignore.
    // - vertexMaskFlag determines how to interpret the values of the VertexMask: 
    //   - If true, then vertexMask ignores any vertices that are "false" in vertexMask
    // - shouldSkipBorderVertices:
    //   - Do not perform simplification on border vertices (vertices that have a neighbor with a different vertexMask value).
    //   - Should be set to true when processing foreground mesh to not distort the silhouette.
    public static void SimplifyGridMesh(
        Vector3[] vertices, Vector2[] uvs, int[] triangles, 
        bool[] vertexMask, bool vertexMaskFlag, // Filters out certain vertices from being used
        int width, int height, 
        out Vector3[] _newVerts, out Vector2[] _newUvs, out int[] _newTriangles,
        MeshGenerator.Settings settings,
        bool shouldSkipBorderVertices = false
    ) {
        // Compute distances.
        float[] distances = new float[width * height];
        for (int i = 0; i < distances.Length; i++) { distances[i] = float.MaxValue; } // Use float.MaxValue as a marker for this was not set
        for (int vertIdx = 0; vertIdx < vertices.Length; vertIdx++) {
            bool isBorderVertex = false;
            if (shouldSkipBorderVertices) {
                int col = vertIdx % width;
                int row = (vertIdx - col) / width;
                isBorderVertex = MeshGenerator.IsFgVertexOnBorder(row, col, width, height, vertexMask);
            }
            if (vertexMaskFlag != vertexMask[vertIdx] || isBorderVertex) {
                continue;
            } else {
                distances[vertIdx] = (Vector3.zero - vertices[vertIdx]).magnitude;
            }
        }

        List<Vector3> newVertices = new List<Vector3>();
        List<Vector2> newUvs = new List<Vector2>();
        List<int> newTriangles = new List<int>();
        int[] newVertexIdx = new int[vertices.Length];
        Dictionary<string, Vector2> regionBoundsCache = new Dictionary<string, Vector2>();
        for (int i = 0; i < newVertexIdx.Length; i++) { newVertexIdx[i] = -1; }
        List<RectInt> regions = new List<RectInt>();

        SimplifyMeshRegion(
            0, width - 1, 0, height - 1, 
            distances, width, height, 
            vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx,
            regionBoundsCache,
            regions,
            settings);

        // Loop through all the old triangles.
        // Determine a triangle can be skipped if any of its vertices are removed.
        // Otherwise the triangle needs to be reconstructed with new vertex buffer.
        for (int i = 0; i < triangles.Length; i += 3) {
            int newVertA = newVertexIdx[triangles[i]];
            int newVertB = newVertexIdx[triangles[i + 1]];
            int newVertC = newVertexIdx[triangles[i + 2]];
            if (newVertA >= 0 && newVertB >= 0 && newVertC >= 0) {
                newTriangles.Add(newVertA);
                newTriangles.Add(newVertB);
                newTriangles.Add(newVertC);
            }
        }

        // Fix the gaps introduced by the simplification. This happens because of the lower resolution of the larger regions,
        // causing the edges to be misaligned against the smaller, more detailed regions.
        // For each regions edges, find all of the new vertices that fall on those edges.
        // If there's a vertex that lies on this edge, need to push it to the lower resolution edge.
        // This will just be a linear interpolation between the two points that form the endpoints of the edge.

        // Vertices should only be moved once. We don't want it to be moved, then moved again later.
        bool[] hasNewVertexBeenModified = new bool[newVertices.Count];
        for (int i = 0; i < hasNewVertexBeenModified.Length; i++) { hasNewVertexBeenModified[i] = false; }
        // This only works if we use the larger regions first, to move the smaller regions to align to their edges.
        regions.Sort((RectInt rectA, RectInt rectB) => {
            int rectAArea = rectA.width * rectA.height;
            int rectBArea = rectB.width * rectB.height;
            return rectBArea.CompareTo(rectAArea); // Descending order
        });

        for (int i = 0; i < regions.Count; i++) {
            int x1 = regions[i].x;
            int x2 = regions[i].xMax;
            int y1 = regions[i].y;
            int y2 = regions[i].yMax;

            // Read vertex data from the newVertices buffer because it's possible
            // that the corners of the more detailed regions will move.
            Vector3 vertA = newVertices[newVertexIdx[y1 * width + x1]];
            Vector3 vertB = newVertices[newVertexIdx[y1 * width + x2]];
            Vector3 vertC = newVertices[newVertexIdx[y2 * width + x1]];
            Vector3 vertD = newVertices[newVertexIdx[y2 * width + x2]];

            // Top Edge
            for (int col = x1; col <= x2; col++) {
                int oldVertIdx = y1 * width + col;
                int newIdx = newVertexIdx[oldVertIdx];
                if (newIdx < 0 || hasNewVertexBeenModified[newIdx]) { continue; }
                float t = (col - x1) / (float)regions[i].width;
                newVertices[newIdx] = Vector3.Lerp(vertA, vertB, t);
                hasNewVertexBeenModified[newIdx] = true;
            }
            // Bottom Edge
            for (int col = x1; col <= x2; col++) {
                int oldVertIdx = y2 * width + col;
                int newIdx = newVertexIdx[oldVertIdx];
                if (newIdx < 0 || hasNewVertexBeenModified[newIdx]) { continue; }
                float t = (col - x1) / (float)regions[i].width;
                newVertices[newIdx] = Vector3.Lerp(vertC, vertD, t);
                hasNewVertexBeenModified[newIdx] = true;
            }
            // Left Edge
            for (int row = y1; row <= y2; row++) {
                int oldVertIdx = row * width + x1;
                int newIdx = newVertexIdx[oldVertIdx];
                if (newIdx < 0 || hasNewVertexBeenModified[newIdx]) { continue; }
                float t = (row - y1) / (float)regions[i].height;
                newVertices[newIdx] = Vector3.Lerp(vertA, vertC, t);
                hasNewVertexBeenModified[newIdx] = true;
            }
            // Right Edge
            for (int row = y1; row <= y2; row++) {
                int oldVertIdx = row * width + x2;
                int newIdx = newVertexIdx[oldVertIdx];
                if (newIdx < 0 || hasNewVertexBeenModified[newIdx]) { continue; }
                float t = (row - y1) / (float)regions[i].height;
                newVertices[newIdx] = Vector3.Lerp(vertB, vertD, t);
                hasNewVertexBeenModified[newIdx] = true;
            }
        }

        // Cull any un-used vertices.
        // TODO: It's unclear why there are so many extra verts...
        int[] vertConversionIdxMap = new int[newVertices.Count];
        for (int i = 0; i < vertConversionIdxMap.Length; i++) { vertConversionIdxMap[i] = -1; }
        List<Vector3> reducedVerts = new List<Vector3>();
        List<Vector2> reducedUvs = new List<Vector2>();
        for (int i = 0; i < newTriangles.Count; i++) {
            if (vertConversionIdxMap[newTriangles[i]] < 0) {
                vertConversionIdxMap[newTriangles[i]] = reducedVerts.Count;
                reducedVerts.Add(newVertices[newTriangles[i]]);
                reducedUvs.Add(newUvs[newTriangles[i]]);
            }
        }
        for (int i = 0; i < newTriangles.Count; i++) {
            newTriangles[i] = vertConversionIdxMap[newTriangles[i]];
        }

        _newVerts = reducedVerts.ToArray();
        _newUvs = reducedUvs.ToArray();
        _newTriangles = newTriangles.ToArray();
    }

    static void SimplifyMeshRegion(
        int x1, int x2, int y1, int y2, 
        float[] distances, int width, int height, 
        Vector3[] vertices, Vector2[] uvs, 
        List<Vector3> newVertices, List<Vector2> newUvs, List<int> newTriangles, int[] newVertexIdx,
        Dictionary<string, Vector2> regionBoundsCache,
        List<RectInt> regions,
        MeshGenerator.Settings settings
    ) {
        int regionWidth = x2 - x1;
        int regionHeight = y2 - y1;
        int regionArea = regionWidth * regionHeight;
        if (regionArea < 4) {
            // Region could not be simplified anymore.
            // Add all the old verts to new verts
            for (int j = y1; j <= y2; j++) {
                for (int i = x1; i <= x2; i++) {
                    int oldVertIdx = j * width + i;
                    // Add the vertex if it hasn't been added before
                    if (newVertexIdx[oldVertIdx] < 0) {
                        newVertexIdx[oldVertIdx] = newVertices.Count;
                        newVertices.Add(vertices[oldVertIdx]);
                        newUvs.Add(uvs[oldVertIdx]);
                    }
                }
            }
            return;
        }

        int xMidpoint = (x1 + x2) / 2;
        int yMidPoint = (y1 + y2) / 2;
        Vector2 bounds = ComputeRegionBounds(x1, x2, y1, y2, distances, width, regionBoundsCache);
        int largestRegionArea = settings.LargestSimplifiedRegionSize.value * settings.LargestSimplifiedRegionSize.value;
        if ((bounds.y - bounds.x) < settings.MaximumDeltaDistance.value && regionArea < largestRegionArea) {
            // Simplify the region into two triangles.
            // Only add the corner vertices back (if they haven't been added already)
            int vertAIdx = y1 * width + x1;
            int vertBIdx = y1 * width + x2;
            int vertCIdx = y2 * width + x1;
            int vertDIdx = y2 * width + x2;

            // Vert A
            if (newVertexIdx[vertAIdx] < 0) {
                newVertexIdx[vertAIdx] = newVertices.Count;
                newVertices.Add(vertices[vertAIdx]);
                newUvs.Add(uvs[vertAIdx]);
            }
            // Vert B
            if (newVertexIdx[vertBIdx] < 0) {
                newVertexIdx[vertBIdx] = newVertices.Count;
                newVertices.Add(vertices[vertBIdx]);
                newUvs.Add(uvs[vertBIdx]);
            }
            // Vert C
            if (newVertexIdx[vertCIdx] < 0) {
                newVertexIdx[vertCIdx] = newVertices.Count;
                newVertices.Add(vertices[vertCIdx]);
                newUvs.Add(uvs[vertCIdx]);
            }
            // Vert D
            if (newVertexIdx[vertDIdx] < 0) {
                newVertexIdx[vertDIdx] = newVertices.Count;
                newVertices.Add(vertices[vertDIdx]);
                newUvs.Add(uvs[vertDIdx]);
            }
            
            // Triangle ABC -> CBA
            newTriangles.Add(newVertexIdx[vertCIdx]);
            newTriangles.Add(newVertexIdx[vertBIdx]);
            newTriangles.Add(newVertexIdx[vertAIdx]);
            // Triangle BDC -> CDB
            newTriangles.Add(newVertexIdx[vertCIdx]);
            newTriangles.Add(newVertexIdx[vertDIdx]);
            newTriangles.Add(newVertexIdx[vertBIdx]);

            regions.Add(new RectInt(x1, y1, x2 - x1, y2 - y1));
        } else {
            SimplifyMeshRegion(x1, xMidpoint, y1, yMidPoint, distances, width, height, vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx, regionBoundsCache, regions, settings);
            SimplifyMeshRegion(xMidpoint, x2, y1, yMidPoint, distances, width, height, vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx, regionBoundsCache, regions, settings);
            SimplifyMeshRegion(x1, xMidpoint, yMidPoint, y2, distances, width, height, vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx, regionBoundsCache, regions, settings);
            SimplifyMeshRegion(xMidpoint, x2, yMidPoint, y2, distances, width, height, vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx, regionBoundsCache, regions, settings);
        }
    }

    // Returns <min, max> depth values for a given region.
    // Taking the difference between the min, max bounds that are computed yields the "amount of detail" or how 'flat' the region is.
    public static Vector2 ComputeRegionBounds(int x1, int x2, int y1, int y2, float[] values, int valuesWidth, Dictionary<string, Vector2> regionBoundsCache) {
        // Width / Height of region must be even
        int width = x2 - x1;
        int height = y2 - y1;
        int area = width * height;
        if (area < 32) {
            // Check to see if the bounds are cached
            string cacheKey = x1+","+x2+","+y1+","+y2;
            if (regionBoundsCache.ContainsKey(cacheKey)) {
                return regionBoundsCache[cacheKey];
            }

            // Compute the min/max of the region
            float min = float.MaxValue;
            float max = 0;
            for (int j = y1; j <= y2; j++) {
                for (int i = x1; i <= x2; i++) {
                    float value = values[j * valuesWidth + i];
                    min = Mathf.Min(min, value);
                    max = Mathf.Max(max, value);
                }
            }

            Vector2 bounds = new Vector2(min, max);
            // If one of the distances is float.MaxValue, set min to be float.MinValue.
            // This makes it so this region will not be simplified.
            if (bounds.y == float.MaxValue) {
                bounds = new Vector2(float.MinValue, float.MaxValue);
            }

            regionBoundsCache[cacheKey] = bounds;
            return bounds;
        } else {
            // Subdivide region into four quadrants
            int xMidpoint = (x1 + x2) / 2;
            int yMidPoint = (y1 + y2) / 2;
            Vector2 topLeftBounds = ComputeRegionBounds(x1, xMidpoint, y1, yMidPoint, values, valuesWidth, regionBoundsCache);
            Vector2 topRightBounds = ComputeRegionBounds(xMidpoint, x2, y1, yMidPoint, values, valuesWidth, regionBoundsCache);
            Vector2 bottomLeftBounds = ComputeRegionBounds(x1, xMidpoint, yMidPoint, y2, values, valuesWidth, regionBoundsCache);
            Vector2 bottomRightBounds = ComputeRegionBounds(xMidpoint, x2, yMidPoint, y2, values, valuesWidth, regionBoundsCache);
            Vector2 bounds = new Vector2(
                Mathf.Min(Mathf.Min(topLeftBounds.x, topRightBounds.x), Mathf.Min(bottomLeftBounds.x, bottomRightBounds.x)), 
                Mathf.Max(Mathf.Max(topLeftBounds.y, topRightBounds.y), Mathf.Max(bottomLeftBounds.y, bottomRightBounds.y))
            );
            
            return bounds;
        }
    }
}
