using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Unity.EditorCoroutines.Editor;

using Region = MeshSimplification.Region;

// Different version of simplification algorithm written for making an animation.
// Main difference is that it operates in-place, and is non-recursive so it can be paused for rendering.
// See MeshSimplification.cs
public class MeshSimplificationIncremental {
    public static IEnumerator SimplifyMeshAnimation(EditorWindow window, MeshGenerator.Settings settings, MeshGenerator.CachedDenseMeshData meshData) {
        if (meshData.fgMesh == null || meshData.bgMesh == null) {
            Debug.LogError("Cannot run mesh simplification without a mesh to simplify. Run 3D Photo generation first with 'Simplify Mesh' turned off.");
        } else {
            window.StartCoroutine(SimplifyGridMeshIncrementally(window, meshData.bgMesh, meshData.extendedBgVertexMask, true, meshData.extendedBgWidth, meshData.extendedBgHeight, settings));
            window.StartCoroutine(SimplifyGridMeshIncrementally(window, meshData.fgMesh, meshData.bgVertexMask, false, meshData.width, meshData.height, settings));        
        }

        yield return null;
    }

    // Similar to SimplifyGridMesh, but performs mesh simplification incrementally over time for animation visualization.
    static IEnumerator SimplifyGridMeshIncrementally(
        EditorWindow window, 
        MeshFilter sourceMesh,
        bool[] vertexMask, bool vertexMaskFlag, // Filters out certain vertices from being used
        int width, int height,
        MeshGenerator.Settings settings,
        bool shouldSkipBorderVertices = false
    ) {
        Vector3[] vertices = sourceMesh.sharedMesh.vertices;
        List<int> triangles = new List<int>(sourceMesh.sharedMesh.triangles);

        // Compute distances
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

        Dictionary<string, Vector2> regionBoundsCache = new Dictionary<string, Vector2>();
        List<RectInt> regions = new List<RectInt>();

        yield return window.StartCoroutine(SimplifyMeshGridRegionIncrementally(
            0, width - 1, 0, height - 1, 
            distances, width, height, 
            vertices, triangles,
            regionBoundsCache,
            regions, 
            settings, 
            (List<int> triangles) => {
                sourceMesh.sharedMesh.triangles = triangles.ToArray();
                EditorWindow.GetWindow<SceneView>().Repaint();
        }));

        yield return null;
    }

    // Similar to MeshSimplification.SimplifyMeshGridRegion, however it's unrolled to be non-recursive.
    // That way, it can be yielded and made into a generator so we can pause to give Unity time to render.
    static IEnumerator SimplifyMeshGridRegionIncrementally(
        int _x1, int _x2, int _y1, int _y2, 
        float[] distances, int width, int height, 
        Vector3[] vertices, List<int> triangles,
        Dictionary<string, Vector2> regionBoundsCache,
        List<RectInt> regions,
        MeshGenerator.Settings settings,
        Action<List<int>> renderCallback
    ) {
        Stack<Region> regionsToProcess = new Stack<Region>();

        regionsToProcess.Push(new Region(_x1, _x2, _y1, _y2));
        while (regionsToProcess.Count > 0) {
            Region currentRegion = regionsToProcess.Pop();
            int x1 = currentRegion.x1;
            int x2 = currentRegion.x2;
            int y1 = currentRegion.y1;
            int y2 = currentRegion.y2;

            // If Region is too small, move on
            int regionWidth = x2 - x1;
            int regionHeight = y2 - y1;
            int regionArea = regionWidth * regionHeight;
            if (regionArea < 4) {
                continue;
            }

            int xMidpoint = (x1 + x2) / 2;
            int yMidPoint = (y1 + y2) / 2;
            Vector2 bounds = MeshSimplification.ComputeRegionBounds(x1, x2, y1, y2, distances, width, regionBoundsCache);
            int largestRegionArea = settings.LargestSimplifiedRegionSize.value * settings.LargestSimplifiedRegionSize.value;
            bool regionCanBeSimplified = (bounds.y - bounds.x) < settings.MaximumDeltaDistance.value && regionArea < largestRegionArea;
            if (regionCanBeSimplified) {
                // Simplify the region into two triangles.
                int vertAIdx = y1 * width + x1;
                int vertBIdx = y1 * width + x2;
                int vertCIdx = y2 * width + x1;
                int vertDIdx = y2 * width + x2;

                // Take any triangles that are in the region and remove them
                List<int> newTriangles = new List<int>();
                for (int i = 0; i < triangles.Count; i += 3) {
                    int vertA = triangles[i];
                    int col = vertA % width;
                    int row = (vertA - col) / width;
                    bool vertAContained = col >= x1 && col <= x2 && row >= y1 && row <= y2;
                    int vertB = triangles[i + 1];
                    col = vertB % width;
                    row = (vertB - col) / width;
                    bool vertBContained = col >= x1 && col <= x2 && row >= y1 && row <= y2;
                    int vertC = triangles[i + 2];
                    col = vertC % width;
                    row = (vertC - col) / width;
                    bool vertCContained = col >= x1 && col <= x2 && row >= y1 && row <= y2;
                    if (vertAContained && vertBContained && vertCContained) {
                        continue;
                    }
                    newTriangles.Add(vertA);
                    newTriangles.Add(vertB);
                    newTriangles.Add(vertC);
                }
                int trianglesRemoved = triangles.Count - newTriangles.Count;
                
                // Triangle ABC -> CBA
                newTriangles.Add(vertCIdx);
                newTriangles.Add(vertBIdx);
                newTriangles.Add(vertAIdx);
                // Triangle BDC -> CDB
                newTriangles.Add(vertCIdx);
                newTriangles.Add(vertDIdx);
                newTriangles.Add(vertBIdx);

                triangles.Clear();
                for (int i = 0; i < newTriangles.Count; i++) {
                    triangles.Add(newTriangles[i]);
                }

                regions.Add(new RectInt(x1, y1, x2 - x1, y2 - y1));
                renderCallback(triangles);
                yield return new WaitForSeconds(0.0166f);
            } else {
                regionsToProcess.Push(new Region(x1, xMidpoint, y1, yMidPoint));
                regionsToProcess.Push(new Region(xMidpoint, x2, y1, yMidPoint));
                regionsToProcess.Push(new Region(x1, xMidpoint, yMidPoint, y2));
                regionsToProcess.Push(new Region(xMidpoint, x2, yMidPoint, y2));
            }
        }

        yield return null;
    }
}
