using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using Unity.EditorCoroutines.Editor;

public class MeshGeneration : EditorWindow {
    Texture2D ColorImage;
    Texture2D DepthImage;
    Texture2D ForegroundImage;
    MeshFilter InputMesh;
    bool RunFiltering = true;
    // Field of view that the photo was taken in.
    // Viewing FOV actually doesn't matter.
    float cameraHorizontalFov = 45.0f;
    float cameraVerticalFov = 58.0f;
    float deltaThreshold = 0.05f;

    Mesh mesh_;

    [MenuItem("Custom/Mesh Generation")]
    public static void OpenWindow() {
       GetWindow<MeshGeneration>();
    }
 
    void OnEnable() {
        // cache any data you need here.
        // if you want to persist values used in the inspector, you can use eg. EditorPrefs
        if (ColorImage == null && DepthImage == null && ForegroundImage == null) {
            ColorImage.LoadImage(System.IO.File.ReadAllBytes("Assets/shiba.jpg"));
            DepthImage.LoadImage(System.IO.File.ReadAllBytes("Assets/shiba_depth.jpg"));
            ForegroundImage.LoadImage(System.IO.File.ReadAllBytes("Assets/shiba_foreground.PNG"));
        }
    }
 
    float degToRad(float angle) {
        return angle / 180.0f * Mathf.PI;
    }

    float radToDeg(float angle) {
        return angle / Mathf.PI * 180.0f;
    }

    private static Texture2D TextureField(string name, Texture2D texture)
    {
        GUILayout.BeginVertical();
        var style = new GUIStyle(GUI.skin.label);
        style.alignment = TextAnchor.UpperCenter;
        style.fixedWidth = 70;
        GUILayout.Label(name, style);
        var result = (Texture2D)EditorGUILayout.ObjectField(texture, typeof(Texture2D), false, GUILayout.Width(70), GUILayout.Height(70));
        GUILayout.EndVertical();
        return result;
    }

    void OnGUI() {
        EditorGUILayout.BeginHorizontal();
        ColorImage = TextureField("Color", ColorImage);
        DepthImage = TextureField("Depth", DepthImage);
        ForegroundImage = TextureField("Foreground", ForegroundImage);
        EditorGUILayout.EndHorizontal();

        // Exiftool can get the FOV if some metadata is still attached.
        // If only one number, usually refers to the diagnol FOV.
        // To caluculate the hFOV and vFov...
        cameraHorizontalFov = EditorGUILayout.Slider("Camera Horizontal FOV", cameraHorizontalFov, 30.0f, 120.0f);
        cameraVerticalFov = EditorGUILayout.Slider("Camera Vertical FOV", cameraVerticalFov, 30.0f, 120.0f);
        deltaThreshold = EditorGUILayout.Slider("Delta Threshold", deltaThreshold, 0, 1);
        InputMesh = EditorGUILayout.ObjectField("Mesh to Process", InputMesh, typeof(MeshFilter), true) as MeshFilter;
        RunFiltering = EditorGUILayout.Toggle("Enable Filtering", RunFiltering);
        // For making tooltips
        // new GUIContent("Test Float", "Here is a tooltip")

        if(GUILayout.Button("Generate 3D Photo")) {
            this.StartCoroutine(Generate3DPhotoV4(ColorImage, DepthImage, ForegroundImage));
        }
    }

    public static int ToNextNearestPowerOf2(int x) {
        if (x < 2) { return 1; }
        return (int) Mathf.Pow(2, (int) Mathf.Log(x-1, 2) + 1);
    }

    // See: https://support.unity.com/hc/en-us/articles/206486626-How-can-I-get-pixels-from-unreadable-textures-
    Texture2D GetReadableTexture(Texture2D texture, bool isLinear = true) {
        // Create a temporary RenderTexture of the same size as the texture
        RenderTexture tmp = RenderTexture.GetTemporary( 
                            texture.width,
                            texture.height,
                            0,
                            RenderTextureFormat.Default,
                            isLinear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);

        // Blit the pixels on texture to the RenderTexture
        Graphics.Blit(texture, tmp);

        // Backup the currently set RenderTexture
        RenderTexture previous = RenderTexture.active;

        // Set the current RenderTexture to the temporary one we created
        RenderTexture.active = tmp;

        // Create a new readable Texture2D to copy the pixels to it
        Texture2D myTexture2D = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, true, isLinear);

        // Copy the pixels from the RenderTexture to the new Texture
        myTexture2D.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
        myTexture2D.Apply();

        // Reset the active RenderTexture
        RenderTexture.active = previous;

        // Release the temporary RenderTexture
        RenderTexture.ReleaseTemporary(tmp);

        return myTexture2D;
    }

    Color SampleTexture(Vector2 uv, Color32[] texturePixels, int width, int height) {
        int x_1 = (int)Mathf.Floor(uv.x * (width - 1));
        int x_2 = (int)Mathf.Ceil(uv.x * (width - 1));
        int y_1 = (int)Mathf.Floor(uv.y * (height - 1));
        int y_2 = (int)Mathf.Ceil(uv.y * (height - 1));

        Color A = texturePixels[y_1 * width + x_1];
        Color B = texturePixels[y_1 * width + x_2];
        Color C = texturePixels[y_2 * width + x_1];
        Color D = texturePixels[y_2 * width + x_2];

        float r = (A.r + B.r + C.r + D.r) / 4.0f;
        float g = (A.g + B.g + C.g + D.g) / 4.0f;
        float b = (A.b + B.b + C.b + D.b) / 4.0f;
        float a = (A.a + B.a + C.a + D.a) / 4.0f;

        return new Color(r, g, b, a);
    }

    Color SampleTexture(Vector2 uv, Color[] texturePixels, int width, int height) {
        int x_1 = (int)Mathf.Floor(uv.x * (width - 1));
        int x_2 = (int)Mathf.Ceil(uv.x * (width - 1));
        int y_1 = (int)Mathf.Floor(uv.y * (height - 1));
        int y_2 = (int)Mathf.Ceil(uv.y * (height - 1));

        Color A = texturePixels[y_1 * width + x_1];
        Color B = texturePixels[y_1 * width + x_2];
        Color C = texturePixels[y_2 * width + x_1];
        Color D = texturePixels[y_2 * width + x_2];

        float r = (A.r + B.r + C.r + D.r) / 4.0f;
        float g = (A.g + B.g + C.g + D.g) / 4.0f;
        float b = (A.b + B.b + C.b + D.b) / 4.0f;
        float a = (A.a + B.a + C.a + D.a) / 4.0f;

        return new Color(r, g, b, a);
    }

    Vector3[] FilterVertices(Vector3[] vertices, bool[] isBg, int width, int height, int filterRadius = 4, bool useMedianInsteadOfAverage = false) {
        int filterWidth = filterRadius * 2 + 1;
        Vector3[] filteredVerts = new Vector3[vertices.Length];
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                List<float> distances = new List<float>(filterWidth * filterWidth);
                Vector3 vertex = vertices[vertIdx];
                bool isVertBg = isBg[vertIdx];
                // Compute Centroid
                for (int j = -filterRadius; j <= filterRadius; j++) {
                    for (int i = -filterRadius; i <= filterRadius; i++) {
                        // if (j == 0 && i == 0) { continue; } // Skip self
                        int neighborRow = row + j;
                        if (neighborRow < 0) { 
                            neighborRow = 0; 
                            continue;
                        }
                        else if (neighborRow >= height) { 
                            neighborRow = height - 1; 
                            continue;
                        }
                        int neighborCol = col + i;
                        if (neighborCol < 0) { 
                            neighborCol = 0; 
                            continue;
                        }
                        else if (neighborCol >= width) { 
                            neighborCol = width - 1; 
                            continue;
                        }
                        int neighborVertIdx = neighborRow * width + neighborCol;
                        bool isSampledVertBg = isBg[neighborVertIdx];
                        if (isVertBg != isSampledVertBg) { 
                            neighborVertIdx = vertIdx;
                            continue;
                        }
                        Vector3 sampledVert = vertices[neighborVertIdx];
                        distances.Add((sampledVert - Vector3.zero).magnitude);
                    }
                }
                if (useMedianInsteadOfAverage) {
                    distances.Sort();
                    filteredVerts[vertIdx] = Vector3.zero + vertex.normalized * distances[distances.Count / 2];
                } else {
                    filteredVerts[vertIdx] = Vector3.zero + vertex.normalized * distances.Average();
                }
            }
        }
        return filteredVerts;
    }

    bool IsBorderVertex(int row, int col, int width, int height, bool[] isBg) {
        int vertIdx = row * width + col;
        // Check current Row
        if (col > 0 && isBg[vertIdx - 1]) { return true; } // Check previous column
        if (col < width - 1 && isBg[vertIdx + 1]) { return true; } // Check next column
        // Check previous row
        if (row > 0) {
            int baseIdx = vertIdx - width;
            if (isBg[baseIdx]) { return true; }
            if (col > 0 && isBg[baseIdx - 1]) { return true; } // Check previous column
            if (col < width - 1 && isBg[baseIdx + 1]) { return true; } // Check next column
        }
        // Check next row
        if (row < height - 1) {
            int baseIdx = vertIdx + width;
            if (isBg[baseIdx]) { return true; }
            if (col > 0 && isBg[baseIdx - 1]) { return true; } // Check previous column
            if (col < width - 1 && isBg[baseIdx + 1]) { return true; } // Check next column
        }
        return false;
    }

    Vector3[] FilterFgBorderVertices(Vector3[] vertices, bool[] isBg, int width, int height) {
        int filterRadius = 1;
        int filterWidth = filterRadius * 2 + 1;
        Vector3[] filteredVerts = new Vector3[vertices.Length];
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                if (isBg[vertIdx]) { 
                    filteredVerts[vertIdx] = vertices[vertIdx];
                    continue; 
                }
                List<float> xValues = new List<float>(filterWidth * filterWidth);
                List<float> yValues = new List<float>(filterWidth * filterWidth);
                List<float> zValues = new List<float>(filterWidth * filterWidth);
                Vector3 vert = vertices[vertIdx];
                // If this is a border vertex...
                if (!IsBorderVertex(row, col, width, height, isBg)) {
                    filteredVerts[vertIdx] = vertices[vertIdx];
                    continue;
                }
                // Average between the neighbor border vertices
                for (int j = -filterRadius; j <= filterRadius; j++) {
                    for (int i = -filterRadius; i <= filterRadius; i++) {
                        int neighborRow = row + j;
                        if (neighborRow < 0) { continue; }
                        else if (neighborRow >= height) { continue; }
                        int neighborCol = col + i;
                        if (neighborCol < 0) { continue; }
                        else if (neighborCol >= width) { continue; }
                        int neighborVertIdx = neighborRow * width + neighborCol;
                        if (isBg[neighborVertIdx]) { continue; }
                        if (!IsBorderVertex(neighborRow, neighborCol, width, height, isBg)) { continue; }
                        Vector3 sampledVertex = vertices[neighborVertIdx];
                        xValues.Add(sampledVertex.x);
                        yValues.Add(sampledVertex.y);
                        zValues.Add(sampledVertex.z);
                    }
                }

                Vector3 centroid = new Vector3(xValues.Average(), yValues.Average(), zValues.Average());
                filteredVerts[vertIdx] = centroid;
            }
        }
        return filteredVerts;
    }

    // Returns <min, max>
    Vector2 ComputeRegionBounds(int x1, int x2, int y1, int y2, float[] values, int valuesWidth, Dictionary<string, Vector2> regionBoundsCache) {
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

    void SimplifyMeshV2(
        Vector3[] vertices, Vector2[] uvs, int[] triangles, 
        bool[]? isBg, bool meshIsBg, 
        int width, int height, 
        out Vector3[] _newVerts, out Vector2[] _newUvs, out int[] _newTriangles
    ) {
        // Compute distances.
        float[] distances = new float[width * height];
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                if (isBg != null && meshIsBg != isBg[vertIdx]) { // Kind of a hack since we re-use the same buffer for FG/BG meshes
                    distances[vertIdx] = float.MaxValue;
                } else {
                    distances[vertIdx] = (Vector3.zero - vertices[vertIdx]).magnitude;
                }
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
            regions);

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

            Vector3 vertA = vertices[y1 * width + x1];
            Vector3 vertB = vertices[y1 * width + x2];
            Vector3 vertC = vertices[y2 * width + x1];
            Vector3 vertD = vertices[y2 * width + x2];

            // Top Edge
            // for (int col = x1; col <= x2; col++) {
            //     int oldVertIdx = y1 * width + col;
            //     int newIdx = newVertexIdx[oldVertIdx];
            //     if (newIdx < 0 || hasNewVertexBeenModified[newIdx]) { continue; }
            //     float t = (col - x1) / (float)regions[i].width;
            //     newVertices[newIdx] = Vector3.Lerp(vertA, vertB, t);
            //     hasNewVertexBeenModified[newIdx] = true;
            // }
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

        _newVerts = newVertices.ToArray();
        _newUvs = newUvs.ToArray();
        _newTriangles = newTriangles.ToArray();
        Debug.Log("Simplified tris count: " + _newTriangles.Length);
    }

    void SimplifyMeshRegion(
        int x1, int x2, int y1, int y2, 
        float[] distances, int width, int height, 
        Vector3[] vertices, Vector2[] uvs, 
        List<Vector3> newVertices, List<Vector2> newUvs, List<int> newTriangles, int[] newVertexIdx,
        Dictionary<string, Vector2> regionBoundsCache,
        List<RectInt> regions
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
        // const int minimumArea = 128;
        if ((bounds.y - bounds.x) < 0.01f /* && regionArea < minimumArea */) {
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
            SimplifyMeshRegion(x1, xMidpoint, y1, yMidPoint, distances, width, height, vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx, regionBoundsCache, regions);
            SimplifyMeshRegion(xMidpoint, x2, y1, yMidPoint, distances, width, height, vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx, regionBoundsCache, regions);
            SimplifyMeshRegion(x1, xMidpoint, yMidPoint, y2, distances, width, height, vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx, regionBoundsCache, regions);
            SimplifyMeshRegion(xMidpoint, x2, yMidPoint, y2, distances, width, height, vertices, uvs, newVertices, newUvs, newTriangles, newVertexIdx, regionBoundsCache, regions);
        }
    }

    void SimplifyMesh(Vector3[] vertices, Vector2[] uvs, int[] triangles, bool[] isBg, bool meshIsBg, int width, int height, out Vector3[] _newVerts, out Vector2[] _newUvs, out int[] _newTriangles) {
        int neighborhoodRadius = 2;
        int neighborhoodSize = neighborhoodRadius * 2 + 1;
        int numNeighbors = neighborhoodSize * neighborhoodSize;

        List<Vector3> newVertices = new List<Vector3>();
        List<Vector2> newUvs = new List<Vector2>();
        List<int> newTriangles = new List<int>();
        int[] newVertexIdx = new int[vertices.Length];
        for (int i = 0; i < newVertexIdx.Length; i++) { newVertexIdx[i] = -1; }

        for (int row = 1; row < height - 1; row += 2) {
            for (int col = 1; col < width - 1; col += 2) {
                int vertIdx = row * width + col;
                // Find neighbors and compute the flatness.
                // The flatness measurement will be how much the vertices deviate from the average distance from origin.
                List<int> neighbors = new List<int>();
                List<float> distances = new List<float>();
                for (int j = -neighborhoodRadius; j <= neighborhoodRadius; j++) {
                    for (int i = -neighborhoodRadius; i <= neighborhoodRadius; i++) {
                        int neighborRow = row + j;
                        if (neighborRow < 0) { continue; }
                        else if (neighborRow >= height) { continue; }
                        int neighborCol = col + i;
                        if (neighborCol < 0) { continue; }
                        else if (neighborCol >= width) { continue; }
                        int neighborVertIdx = neighborRow * width + neighborCol;
                        if (meshIsBg != isBg[neighborVertIdx]) { continue; }
                        // if (!IsBorderVertex(neighborRow, neighborCol, width, height, isBg)) { continue; }
                        // // TODO: Check if vertex has been processed already.
                        neighbors.Add(neighborVertIdx);
                        distances.Add((Vector3.zero - vertices[neighborVertIdx]).magnitude);
                    }
                }

                // Only do simplification with a complete neighborhood
                float maxDeltaDistance = 0;
                if (neighbors.Count == numNeighbors) {
                    float avgDistance = distances.Average();
                    for (int i = 0; i < neighbors.Count; i++) {
                        float deltaDistance = avgDistance - distances[i];
                        if (deltaDistance > maxDeltaDistance) {
                            maxDeltaDistance = deltaDistance;
                        }
                    }
                }
                bool shouldPerformSimplification = neighbors.Count == numNeighbors && maxDeltaDistance < 0.001f;
                // bool shouldPerformSimplification = false;
                int newVertIdxBase = newVertices.Count;
                if (!shouldPerformSimplification) {
                    // Add all vertices back
                    for (int i = 0; i < neighbors.Count; i++) {
                        int newVertIdx = newVertIdxBase + i;
                        newVertexIdx[neighbors[i]] = newVertIdx;
                        newVertices.Add(vertices[neighbors[i]]);
                        newUvs.Add(uvs[neighbors[i]]);
                    }
                    continue;
                }

                // Simplify the neighborhood into smaller triangles.
                // Only add the corner vertices back.
                int vertAIdx = vertIdx - width - 1;
                int newVertAIdx = newVertIdxBase;
                int vertBIdx = vertIdx - width + 1;
                int newVertBIdx = newVertIdxBase + 1;
                int vertCIdx = vertIdx + width - 1;
                int newVertCIdx = newVertIdxBase + 2;
                int vertDIdx = vertIdx + width + 1;
                int newVertDIdx = newVertIdxBase + 3;
                newVertexIdx[vertAIdx] = newVertAIdx;
                newVertices.Add(vertices[vertAIdx]);
                newUvs.Add(uvs[vertAIdx]);
                newVertexIdx[vertBIdx] = newVertBIdx;
                newVertices.Add(vertices[vertBIdx]);
                newUvs.Add(uvs[vertBIdx]);
                newVertexIdx[vertCIdx] = newVertCIdx;
                newVertices.Add(vertices[vertCIdx]);
                newUvs.Add(uvs[vertCIdx]);
                newVertexIdx[vertDIdx] = newVertDIdx;
                newVertices.Add(vertices[vertDIdx]);
                newUvs.Add(uvs[vertDIdx]);
                
                newTriangles.Add(newVertCIdx);
                newTriangles.Add(newVertBIdx);
                newTriangles.Add(newVertAIdx);

                newTriangles.Add(newVertCIdx);
                newTriangles.Add(newVertDIdx);
                newTriangles.Add(newVertBIdx);
            }
        }

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

        _newVerts = newVertices.ToArray();
        _newUvs = newUvs.ToArray();
        _newTriangles = newTriangles.ToArray();
        Debug.Log("Simplified tris count: " + _newTriangles.Length);
    }

    void GenerateMesh(string name, Vector3[] vertices, Vector2[] uvs, int[] triangles, Texture2D texture, Transform parent = null) {
        GameObject meshObject = new GameObject(name);
        MeshFilter meshFilter = meshObject.AddComponent<MeshFilter>() as MeshFilter;
        MeshRenderer meshRenderer = meshObject.AddComponent<MeshRenderer>() as MeshRenderer;
        Mesh mesh = new Mesh();
        mesh.indexFormat = IndexFormat.UInt32;

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        meshFilter.mesh = mesh;

        // Add a material onto the mesh
        Material mat = new Material(Shader.Find("Unlit/Texture"));
        mat.SetTexture("_MainTex", texture);
        meshRenderer.material = mat;

        if (parent != null) {
            meshObject.transform.SetParent(parent);
        }
    }

    IEnumerator Generate3DPhotoV4(Texture2D _colorImage, Texture2D _depthImage, Texture2D _foregroundImage) {
        bool projectFromOrigin = true;
        bool convertDepthValuesFromDisparity = true;

        Texture2D depthImage = GetReadableTexture(_depthImage);
        Texture2D fgImage = GetReadableTexture(_foregroundImage, false);
        Texture2D colorImage = GetReadableTexture(_colorImage, false);

        GameObject rootObj = new GameObject("3D Photo");

        // Number of vertices on each side is equal to pixels + 1
        int width = depthImage.width + 1;
        int height = depthImage.height + 1;
        int numVerts = width * height;
        Vector3[] vertices = new Vector3[numVerts];
        bool[] isBg = new bool[numVerts];
        Vector2[] uvs = new Vector2[numVerts];
        List<int> bgTriangles = new List<int>(); // # of tris is variable
        List<int> fgTriangles = new List<int>(); // # of tris is variable

        Vector3 origin = Vector3.zero;
        Color32[] depthPixels = depthImage.GetPixels32(0);
        // Color[] depthPixels = FilterImage(depthImage.GetPixels32(0), depthImage.width, depthImage.height);
        Color32[] fgPixels = fgImage.GetPixels32(0);
        // Create vertices
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                Vector2 uv = new Vector2(col / (float)(width), row / (float)(height)); // Range [0, 1]

                float disparityDepth = SampleTexture(uv, depthPixels, depthImage.width, depthImage.height).r;
                Vector2 angles = new Vector2(degToRad((uv.x - 0.5f) * cameraHorizontalFov), degToRad((uv.y - 0.5f) * cameraVerticalFov)); 
                Vector3 viewingDirection = (new Vector3((float)Math.Sin(angles.x), (float)Math.Sin(angles.y), (float)Math.Cos(angles.x))).normalized;

                float depth = disparityDepth;
                if (convertDepthValuesFromDisparity) {
                    // Apply a conversion from disparity depth to actual z value
                    // Change in foreground has little effect. Change in background has a lot of effect.
                    // As a result, need to apply distance thresholding.
                    float maxDepth = 1;
                    depth = 1 / (disparityDepth + (1 / maxDepth)); // Range [0.5, maxDepth]
                    depth = (depth - 0.5f) / (maxDepth - 0.5f); // Range [0.0, 1.0]
                }

                bool isBackground = SampleTexture(uv, fgPixels, fgImage.width, fgImage.height).a < 0.01f;

                Vector3 vertex;
                if (projectFromOrigin) {
                    // Project from virtual camera position out 
                    vertex = viewingDirection * (depth + 1);
                } else {
                    // Without using projection
                    vertex = new Vector3(uv.x * 2 - 1, uv.y * 2 - 1, depth); 
                }

                vertices[vertIdx] = vertex;
                uvs[vertIdx] = uv;
                isBg[vertIdx] = isBackground;
            }
        }
       
        // Generate triangles. We only generate triangles in pairs to ensure there are only quads in our topology.
        for (int row = 1; row < height; row++) {
            for (int col = 1; col < width; col++) {
                int vertIdx = row * width + col;
                int vertA = vertIdx - 1 - width;
                int vertB = vertIdx - width;
                int vertC = vertIdx - 1;
                int vertD = vertIdx;
                // Triangle ABC -> CBA
                float deltaAB = (vertices[vertA] - vertices[vertB]).magnitude;
                float deltaBC = (vertices[vertB] - vertices[vertC]).magnitude;
                float deltaAC = (vertices[vertA] - vertices[vertC]).magnitude;
                bool abThresholdExceeded = deltaAB > deltaThreshold;
                bool bcThresholdExceeded = deltaBC > deltaThreshold;
                bool acThresholdExceeded = deltaAC > deltaThreshold;
                bool distanceThresholdExceeded = abThresholdExceeded || bcThresholdExceeded || acThresholdExceeded;
                distanceThresholdExceeded = false;
                if (!distanceThresholdExceeded) {
                    bool isBackgroundTriangle = isBg[vertC] && isBg[vertB] && isBg[vertA];
                    bool isForegroundTriangle = !isBg[vertC] && !isBg[vertB] && !isBg[vertA];
                    if (isBackgroundTriangle) {
                        bgTriangles.Add(vertC);
                        bgTriangles.Add(vertB);
                        bgTriangles.Add(vertA);
                    }
                    if (isForegroundTriangle) {
                        fgTriangles.Add(vertC);
                        fgTriangles.Add(vertB);
                        fgTriangles.Add(vertA);
                    }
                }
                // Triangle BDC -> CDB
                float deltaBD = (vertices[vertB] - vertices[vertD]).magnitude;
                float deltaDC = (vertices[vertD] - vertices[vertC]).magnitude;
                distanceThresholdExceeded = deltaBD > deltaThreshold || deltaBC > deltaThreshold || deltaDC > deltaThreshold;
                distanceThresholdExceeded = false;
                if (!distanceThresholdExceeded) {
                    bool isBackgroundTriangle = isBg[vertC] && isBg[vertD] && isBg[vertB];
                    bool isForegroundTriangle = !isBg[vertC] && !isBg[vertD] && !isBg[vertB];
                    if (isBackgroundTriangle) {
                        bgTriangles.Add(vertC);
                        bgTriangles.Add(vertD);
                        bgTriangles.Add(vertB);
                    }
                    if (isForegroundTriangle) {
                        fgTriangles.Add(vertC);
                        fgTriangles.Add(vertD);
                        fgTriangles.Add(vertB);
                    }
                }
            }
        }

        Debug.Log("Vertex count: " + numVerts);
        Debug.Log("Triangle count in background mesh: " + bgTriangles.Count);
        Debug.Log("Triangle count in foreground mesh: " + fgTriangles.Count);

        // Filter the mesh
        // Run a median filter on the positions to smooth it out.
        // Border behavior: clamp
        if (RunFiltering) {
            // This is by far the slowest part of the algorithm
            vertices = FilterVertices(vertices, isBg, width, height, 4, true); // Median Filter
            vertices = FilterVertices(vertices, isBg, width, height); // Average
            vertices = FilterFgBorderVertices(vertices, isBg, width, height); // Average
            vertices = FilterFgBorderVertices(vertices, isBg, width, height); // Average
        }

        // Generate Mesh that needs to be inpainted.
        // This mesh doesn't exist in the original POV, hence "hallucinated."
        List<Vector3> hallucinatedVertices = new List<Vector3>();
        List<Vector2> hallucinatedUvs = new List<Vector2>();
        List<int> hallucinatedTriangles = new List<int>();

        // Make a new texture that uses alpha = 0 for the empty spots. Goal is to infill those spots using DallE.
        // We'll also do outpainting. The output texture will be square.
        int bgTextureSize = Mathf.Max(ToNextNearestPowerOf2(width), ToNextNearestPowerOf2(height));
        Texture2D bgTexture = new Texture2D(bgTextureSize, bgTextureSize);
        Color[] colors = new Color[bgTextureSize * bgTextureSize];
        Color32[] colorImagePixels = colorImage.GetPixels32(0);
        int?[] newVertIdxs = new int?[bgTextureSize * bgTextureSize];
        // Center the original inside of the larger square
        int originalStartRow = (int)((bgTextureSize - height) / 2.0f);
        int originalEndRow = originalStartRow + height;
        int originalStartColumn = (int)((bgTextureSize - width) / 2.0f);
        int originalEndColumn = originalStartColumn + width;
        for (int row = 0; row < bgTextureSize; row++) {
            for (int col = 0; col < bgTextureSize; col++) {
                int newVertIdx = row * bgTextureSize + col;
                Vector2 uv = new Vector2(col / (float)(bgTextureSize), row / (float)(bgTextureSize)); // Range [0, 1]

                // If this is part of the original...
                // We'll inset it by 1 row/col in order to give the outpainted frame something to connect to.
                if (row >= originalStartRow + 1 && row < originalEndRow - 1 && col >= originalStartColumn + 1 && col < originalEndColumn - 1) {
                    int originalRow = row - originalStartRow;
                    int originalCol = col - originalStartColumn;
                    int originalVertIdx = originalRow * width + originalCol;

                    // Fill in the color
                    colors[newVertIdx] = SampleTexture(uvs[originalVertIdx], colorImagePixels, colorImage.width, colorImage.height);

                    // If this vertex is a FG vert, or if any of its neighbors are FG verts, we'll need to duplicate it.
                    bool hasFgVert = false;

                    // Check current Row
                    if (!isBg[originalVertIdx]) { hasFgVert = true; }
                    if (originalCol > 0 && !isBg[originalVertIdx - 1]) { hasFgVert = true; } // Check previous column
                    if (originalCol < width - 1 && !isBg[originalVertIdx + 1]) { hasFgVert = true; } // Check next column
                    // Check previous row
                    if (originalRow > 0) {
                        int baseIdx = originalVertIdx - width;
                        if (!isBg[baseIdx]) { hasFgVert = true; }
                        if (originalCol > 0 && !isBg[baseIdx - 1]) { hasFgVert = true; } // Check previous column
                        if (originalCol < width - 1 && !isBg[baseIdx + 1]) { hasFgVert = true; } // Check next column
                    }
                    // Check next row
                    if (originalRow < height - 1) {
                        int baseIdx = originalVertIdx + width;
                        if (!isBg[baseIdx]) { hasFgVert = true; }
                        if (originalCol > 0 && !isBg[baseIdx - 1]) { hasFgVert = true; } // Check previous column
                        if (originalCol < width - 1 && !isBg[baseIdx + 1]) { hasFgVert = true; } // Check next column
                    }
                    
                    // If there's no FG verts, just skip
                    if (!hasFgVert) {
                        continue;
                    }

                    // If it's a FG vert, we will also shift it such that it's in the background.
                    // Take the depth of the left BG pixel and right BG pixel.
                    float lastBgDistance = 1;
                    int distanceToLast = 0;
                    for (int x = originalCol - 1; x > 0; x--) {
                        int searchVertIdx = originalRow * width + x;
                        distanceToLast++;
                        if (isBg[searchVertIdx]) {
                            lastBgDistance = (vertices[searchVertIdx] - Vector3.zero).magnitude;
                            break;
                        }
                    }
                    float nextBgDistance = 1;
                    int distanceToNext = 0;
                    for (int x = originalCol + 1; x < width; x++) {
                        int searchVertIdx = originalRow * width + x;
                        distanceToNext++;
                        if (isBg[searchVertIdx]) {
                            nextBgDistance = (vertices[searchVertIdx] - Vector3.zero).magnitude;
                            break;
                        }
                    }
                    // Get the weighted distance
                    float lastWeight = 1 / (float)distanceToLast;
                    float nextWeight = 1 / (float)distanceToNext;
                    float distance = (lastBgDistance * lastWeight  + nextBgDistance * nextWeight) / (lastWeight + nextWeight);

                    // If the distance is less than the FG vertex, then push it back behind it.
                    float fgDistance = (vertices[originalVertIdx] - Vector3.zero).magnitude;
                    if (distance < fgDistance) {
                        distance = fgDistance + 0.001f;
                    }

                    // Duplicate the vertex
                    newVertIdxs[newVertIdx] = hallucinatedVertices.Count;
                    if (isBg[originalVertIdx]) {
                        hallucinatedVertices.Add(vertices[originalVertIdx]);
                    } else {
                        Vector3 shiftedVert = Vector3.zero + vertices[originalVertIdx].normalized * distance;
                        hallucinatedVertices.Add(shiftedVert);
                        colors[newVertIdx] = new Color(0, 0, 0, 0);
                    }
                    hallucinatedUvs.Add(uv);
                } else { // This needs to be outpainted
                    colors[newVertIdx] = new Color(0, 0, 0, 0);

                    // Use clamping behavior to use the depth from the nearest border vertex
                    int neighborCol = col;
                    if (neighborCol < originalStartColumn) { neighborCol = originalStartColumn; }
                    else if (neighborCol >= originalEndColumn) { neighborCol = originalEndColumn - 1; }
                    neighborCol = neighborCol - originalStartColumn;
                    int neighborRow = row;
                    if (neighborRow < originalStartRow) { neighborRow = originalStartRow; }
                    else if (neighborRow >= originalEndRow) { neighborRow = originalEndRow - 1; }
                    neighborRow = neighborRow - originalStartRow;
                    float distanceFromOrigin = (vertices[neighborRow * width + neighborCol] - Vector3.zero).magnitude;

                    // Expand the FOV outside of the original FOV
                    float degreesPerPixelHFov = cameraHorizontalFov / width;
                    float degreesPerPixelVFov = cameraVerticalFov / height;
                    Vector2 angles;
                    if (col < originalStartColumn) {
                        angles.x = (originalStartColumn - col) * -degreesPerPixelHFov - cameraHorizontalFov / 2;
                    } else {
                        angles.x = (col - originalEndColumn) * degreesPerPixelHFov + cameraHorizontalFov / 2;
                    }
                    if (row < originalStartRow) {
                        angles.y = (originalStartRow - row) * -degreesPerPixelVFov - cameraVerticalFov / 2;
                    } else {
                        angles.y = (row - originalEndRow) * degreesPerPixelVFov + cameraVerticalFov / 2;
                    }
                    Vector3 viewingDirection = (new Vector3((float)Math.Sin(degToRad(angles.x)), (float)Math.Sin(degToRad(angles.y)), (float)Math.Cos(degToRad(angles.x)))).normalized;

                    newVertIdxs[newVertIdx] = hallucinatedVertices.Count;
                    hallucinatedVertices.Add(Vector3.zero + viewingDirection * distanceFromOrigin);
                    hallucinatedUvs.Add(uv);
                }    
            }
        }

        // Form the triangles
        for (int row = 1; row < bgTextureSize; row++) {
            for (int col = 1; col < bgTextureSize; col++) {
                int newVertIdx = row * bgTextureSize + col;
                // Previous Row
                int vertA = newVertIdx - 1 - bgTextureSize;
                int vertB = newVertIdx - bgTextureSize;
                // Current Row
                int vertC = newVertIdx - 1;
                int vertD = newVertIdx;

                // If this is part of the original, need to check to see if we want to form triangles.
                // Otherwise just form triangles.
                // Inset the start row/col by 1 to allow the frame to connect.
                if (row >= originalStartRow + 1 && row < originalEndRow && col >= originalStartColumn + 1 && col < originalEndColumn) {
                    int originalRow = row - originalStartRow;
                    int originalCol = col - originalStartColumn;
                    int originalVertIdx = originalRow * width + originalCol;

                    // Previous Row
                    int ogVertA = originalVertIdx - 1 - width;
                    int ogVertB = originalVertIdx - width;
                    // Current Row
                    int ogVertC = originalVertIdx - 1;
                    int ogVertD = originalVertIdx;

                    // If this vertex is a FG vert, or if any of its neighbors are FG verts, we'll need to form triangles.
                    bool hasFgVert = false;

                    // Check previous row
                    if (originalRow > 0) {
                        if (originalCol > 0) {
                            // Check previous column
                            if (!isBg[ogVertA]) { hasFgVert = true; }
                        }
                        if (!isBg[ogVertB]) { hasFgVert = true; }
                    }
                    // Check current Row
                    if (originalCol > 0) {
                        // Check previous column
                        if (!isBg[ogVertC]) { hasFgVert = true; }
                    }
                    if (!isBg[ogVertD]) { hasFgVert = true; }
                    
                    // If there's no FG verts, just skip
                    if (!hasFgVert) {
                        continue;
                    }
                }

                try {
                    int newVertA = newVertIdxs[vertA] == null ? throw new ArgumentNullException() : (int)newVertIdxs[vertA];
                    int newVertB = newVertIdxs[vertB] == null ? throw new ArgumentNullException() : (int)newVertIdxs[vertB]; 
                    int newVertC = newVertIdxs[vertC] == null ? throw new ArgumentNullException() : (int)newVertIdxs[vertC]; 
                    int newVertD = newVertIdxs[vertD] == null ? throw new ArgumentNullException() : (int)newVertIdxs[vertD]; 

                    // Triangle ABC -> CBA
                    hallucinatedTriangles.Add(newVertC);
                    hallucinatedTriangles.Add(newVertB);
                    hallucinatedTriangles.Add(newVertA);
                    // Triangle BDC -> CDB
                    hallucinatedTriangles.Add(newVertC);
                    hallucinatedTriangles.Add(newVertD);
                    hallucinatedTriangles.Add(newVertB);
                } catch (ArgumentNullException) {
                    Debug.LogError("Vertex was null for x="+col+" y="+row);
                    continue;
                }
            }
        }

        // Save texture to disk to allow inpainting with third party tool
        bgTexture.SetPixels(colors);
        File.WriteAllBytes("Assets/background.png", bgTexture.EncodeToPNG());
        bgTexture.LoadImage(System.IO.File.ReadAllBytes("Assets/background.png"));

        Vector3[] fgVertsSimplified; Vector2[] fgUvsSimplified; int[] fgTrianglesSimplified;
        SimplifyMeshV2(vertices, uvs, fgTriangles.ToArray(), isBg, true, width, height, out fgVertsSimplified, out fgUvsSimplified, out fgTrianglesSimplified);
        GenerateMesh("Foreground", fgVertsSimplified, fgUvsSimplified, fgTrianglesSimplified, colorImage, rootObj.transform);

        // For the background, we want it to use the larger, generated background texture that the hallucinated mesh will also use.
        // This will make sure there aren't any seams between the hallucinated region and the background mesh.
        // When using Dall-E to hallucinate the texture, it may alter parts of the original image slightly.
        Vector2[] transformedUvs = new Vector2[uvs.Length];
        for (int i = 0; i < uvs.Length; i++) {
            float originalStartU = originalStartColumn / (float)bgTextureSize;
            float originalEndU = originalEndColumn / (float)bgTextureSize;
            float originalStartV = originalStartRow / (float)bgTextureSize;
            float originalEndV = originalEndRow / (float)bgTextureSize;
            // Compress the range from [0, 1] to [originalStart, originalEnd]
            transformedUvs[i] = new Vector2(uvs[i].x * (originalEndU - originalStartU) + originalStartU, uvs[i].y * (originalEndV - originalStartV) + originalStartV);
        }
        Vector3[] bgVertsSimplified; Vector2[] bgUvsSimplified; int[] bgTrianglesSimplified;
        SimplifyMeshV2(vertices, transformedUvs, bgTriangles.ToArray(), isBg, true, width, height, out bgVertsSimplified, out bgUvsSimplified, out bgTrianglesSimplified);
        GenerateMesh("Background", bgVertsSimplified, bgUvsSimplified, bgTrianglesSimplified, bgTexture, rootObj.transform);

        // TODO: Need to weld the hallucinated mesh to the background mesh
        // or: maybe simpler to just combine the bg and the hallucinated meshes
        GenerateMesh("Hallucinated", hallucinatedVertices.ToArray(), hallucinatedUvs.ToArray(), hallucinatedTriangles.ToArray(), bgTexture, rootObj.transform);

        yield return null;
    }

    // V3
    // TODO: Tatebanko
    // TODO: What if you added a plane behind the original...
    // TODO: Disconnect background from foreground. Any "abrupt" changes are cut.
    // Take a camera projection of *just* the background, and then missing pieces. Then we inpaint the missing pieces.
    // What about double circle obstruction? aka what about the middleground? Could just say avoid doing this.
    // TODO: Mesh Simplification
    // TODO: What if you doubled up on the vertices at the "disconnects" and specified the colors as the background?
    // TODO: Maybe skirts fore multiple layers that are not BG, then for BG layer add the inpainted plane
    // TODO: What if depth was exponentially scaled instead of linearly?

    // Depth vs Disparity

    // Challenges:
    // - How to let users "see behind" the foreground? Need to separate bg from fg.
    // - How to spearate foreground from background? Very difficult problem to solve. First might think to use the depth information...
    //   - Blurred edges make edges not so clear, might need edge filter for this
    //   - How to handle gradients? 
    //   - With a lot of different heuristics and edge cases, could try to make it work, but honestly best to just have the ground truth, OR use AI bg/fg separation.
    //     - Can use Apple's subject isolation feature (handling islands)
    // - What should be shown "behind" the foreground object? Blur the background, hallucinate the background, provide user with method to supply background info
    //   - What about the case where there should be multiple "layers"? Like boz' baby arm.
    // - Mesh density
    //   - Basically generating a LOD. Plenty of algorithms out there, but one obvious optimization is that only areas with lots of detail require high triangle density
    // - Jagged borders. Fix by blurring edge only
    // TODO: Can also outpaint using DallE. Make background image a square.
    // Problem: DallE alters the original image slightly.
    // Have FG and BG mesh use separate texture.

    // Mesh simplification
    // Foreach triangle, 
}