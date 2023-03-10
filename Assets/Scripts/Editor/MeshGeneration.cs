using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using Unity.EditorCoroutines.Editor;

// TODO: Tatebanko
// TODO: What about double circle obstruction? aka what about the middleground? Could just say avoid doing this.
public class MeshGeneration : EditorWindow {
    Texture2D ColorImage;
    Texture2D DepthImage;
    Texture2D ForegroundImage;
    MeshFilter InputMesh;
    bool RunMedianFilter = true;
    bool RunMeshSmoothingFilter = true;
    bool SmoothForegroundEdges = true;
    bool ProjectFromOrigin = true;
    bool ConvertDepthValuesFromDisparity = true;
    bool SeparateFgBg = true;
    bool GenerateForegroundMesh = true;
    bool GenerateBackgroundMesh = true;
    bool GenerateInpaintedRegion = true;
    bool GenerateOutpaintedRegion = true;
    bool PerformMeshSimplification = true;
    int LargestSimplifiedRegionSize = 256;
    float MaximumDeltaDistance = 0.025f;
    bool ForegroundFeathering = true;
    float MaxDepth = 1;
    float MaxDistance = 1;
    bool OverrideDepth = false;
    float DepthOverride = 1f;
    string PhotoIdentifier = "";
    
    // Field of view that the photo was taken in.
    // Viewing FOV actually doesn't matter.
    float cameraHorizontalFov = 45.0f;
    float cameraVerticalFov = 58.0f;

    // For mesh simplification animation
    bool[] __cachedBgVertexMask;
    bool[] __cachedExtendedBgVertexMask;
    MeshFilter __cachedFgMesh;
    MeshFilter __cachedBgMesh;
    int __cachedWidth;
    int __cachedHeight;
    int __cachedExtendedBgWidth;
    int __cachedExtendedBgHeight;


    [MenuItem("Custom/Mesh Generation")]
    public static void OpenWindow() {
       GetWindow<MeshGeneration>();
    }
 
    void OnEnable() {
        // cache any data you need here.
        // if you want to persist values used in the inspector, you can use eg. EditorPrefs
        if (ColorImage == null && DepthImage == null && ForegroundImage == null) {
            ColorImage = Resources.Load<Texture2D>("Images/shiba");
            DepthImage = Resources.Load<Texture2D>("Images/shiba_depth");
            ForegroundImage = Resources.Load<Texture2D>("Images/shiba_foreground");
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
        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Inputs");
            GUILayout.BeginVertical("GroupBox");
                EditorGUILayout.BeginHorizontal();
                ColorImage = TextureField("Color", ColorImage);
                DepthImage = TextureField("Depth", DepthImage);
                ForegroundImage = TextureField("Foreground", ForegroundImage);
                EditorGUILayout.EndHorizontal();
            cameraHorizontalFov = EditorGUILayout.Slider("Camera Horizontal FOV", cameraHorizontalFov, 30.0f, 120.0f);
            cameraVerticalFov = EditorGUILayout.Slider("Camera Vertical FOV", cameraVerticalFov, 30.0f, 120.0f);
            if(GUILayout.Button("Generate 3D Photo")) {
                this.StartCoroutine(Generate3DPhotoV4(ColorImage, DepthImage, ForegroundImage));
            }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Algorithm Tweaks");
            GUILayout.BeginVertical("GroupBox");
                ProjectFromOrigin = EditorGUILayout.Toggle("Project Mesh from Origin", ProjectFromOrigin);
                ConvertDepthValuesFromDisparity = EditorGUILayout.Toggle("Depth stored as Disparity", ConvertDepthValuesFromDisparity);
                MaxDepth = EditorGUILayout.Slider("Foreground Flatness", MaxDepth, 1, 20);
                MaxDistance = EditorGUILayout.Slider("Maximum Distance", MaxDistance, 1, 20);
                RunMedianFilter = EditorGUILayout.Toggle("Remove Outliers", RunMedianFilter);
                RunMeshSmoothingFilter = EditorGUILayout.Toggle("Smooth Mesh", RunMeshSmoothingFilter);
                SmoothForegroundEdges = EditorGUILayout.Toggle("Smooth Foreground Edges", SmoothForegroundEdges);
                ForegroundFeathering = EditorGUILayout.Toggle("Feather Foreground Edges", ForegroundFeathering);
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Mesh Generation Options");
            GUILayout.BeginVertical("GroupBox");
                SeparateFgBg = EditorGUILayout.Toggle("Separate FG/BG", SeparateFgBg);
                GenerateForegroundMesh = EditorGUILayout.Toggle("Generate Foreground", GenerateForegroundMesh);
                GenerateBackgroundMesh = EditorGUILayout.Toggle("Generate Background", GenerateBackgroundMesh);
                GenerateInpaintedRegion = EditorGUILayout.Toggle("Fill Occluded Regions", GenerateInpaintedRegion);
                GenerateOutpaintedRegion = EditorGUILayout.Toggle("Extend Mesh Outwards", GenerateOutpaintedRegion);
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Mesh Simplification Options");
            GUILayout.BeginVertical("GroupBox");
                PerformMeshSimplification = EditorGUILayout.Toggle("Simplify Mesh", PerformMeshSimplification);
                LargestSimplifiedRegionSize = EditorGUILayout.IntSlider("Largest Region Size", LargestSimplifiedRegionSize, 128, 1024);
                MaximumDeltaDistance = EditorGUILayout.Slider("Maximum Delta Distance", MaximumDeltaDistance, 0, 0.1f);
                OverrideDepth = EditorGUILayout.Toggle("Override Depth (debug)", OverrideDepth);
                if (OverrideDepth) {
                    DepthOverride = EditorGUILayout.Slider("Depth Override", DepthOverride, 0, 1);
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Demo Animations");
            GUILayout.BeginVertical("GroupBox");
                if(GUILayout.Button("Simplify Last Generated Mesh")) {
                    this.StartCoroutine(SimplifyMeshAnimation());
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();
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

Color[] FilterImage(Color32[] image, int width, int height, bool[]? mask = null, bool invertMask = false, int filterRadius = 4, bool useMedianInsteadOfAverage = false) {
    int filterWidth = filterRadius * 2 + 1;
    Color[] filteredColors = new Color[image.Length];
    for (int row = 0; row < height; row++) {
        for (int col = 0; col < width; col++) {
            int idx = row * width + col;
            // Mask out pixels
            if (mask != null && ((!mask[idx] && !invertMask) || (mask[idx] && invertMask))) {
                filteredColors[idx] = image[idx];
                continue;
            }
            List<float> rValues = new List<float>(filterWidth * filterWidth);
            List<float> gValues = new List<float>(filterWidth * filterWidth);
            List<float> bValues = new List<float>(filterWidth * filterWidth);
            List<float> aValues = new List<float>(filterWidth * filterWidth);
            for (int j = -filterRadius; j < filterRadius; j++) {
                for (int i = -filterRadius; i < filterRadius; i++) {
                    int neighborRow = row + j;
                    if (neighborRow < 0) { 
                        neighborRow = 0;
                    }
                    else if (neighborRow >= height) { 
                        neighborRow = height - 1;
                    }
                    int neighborCol = col + i;
                    if (neighborCol < 0) { 
                        neighborCol = 0;
                    }
                    else if (neighborCol >= width) { 
                        neighborCol = width - 1;
                    }
                    // Mask out pixels
                    int neighborIdx = neighborRow * width + neighborCol;
                    if (mask != null && ((!mask[neighborIdx] && !invertMask) || (mask[neighborIdx] && invertMask))) {
                        neighborCol = col;
                        neighborRow = row;
                    }
                    Color32 sampledColor = image[neighborIdx];
                    rValues.Add(sampledColor.r / 255.0f);
                    gValues.Add(sampledColor.g / 255.0f);
                    bValues.Add(sampledColor.b / 255.0f);
                    // aValues.Add(sampledColor.a / 255.0f);
                }
            }
            // filteredColors[idx] = Color.red;
            if (useMedianInsteadOfAverage) {
                rValues.Sort();
                gValues.Sort();
                bValues.Sort();
                filteredColors[idx] = new Color(rValues[rValues.Count / 2], gValues[gValues.Count / 2], bValues[bValues.Count / 2], 1);
            } else {
                filteredColors[idx] = new Color(rValues.Average(), gValues.Average(), bValues.Average(), 1);
            }
        }
    }
    return filteredColors;
}

    Vector3[] FilterVertices(Vector3[] vertices, bool[] vertexMask, bool invertMask, int width, int height, int filterRadius = 4, bool useMedianInsteadOfAverage = false) {
        int filterWidth = filterRadius * 2 + 1;
        Vector3[] filteredVerts = new Vector3[vertices.Length];
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                List<float> distances = new List<float>(filterWidth * filterWidth);
                Vector3 vertex = vertices[vertIdx];
                if ((!vertexMask[vertIdx] && !invertMask) || (vertexMask[vertIdx] && invertMask)) { 
                    // Don't do any filtering
                    filteredVerts[vertIdx] = vertices[vertIdx];
                    continue;
                }
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
                        if ((!vertexMask[neighborVertIdx] && !invertMask) || (vertexMask[neighborVertIdx] && invertMask)) { 
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

    // Determines if a foreground vertex is bordering a background vertex.
    bool IsFgVertexOnBorder(int row, int col, int width, int height, bool[] isBg) {
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

    Vector3[] FilterFgBorderVertices(Vector3[] vertices, bool[] isBg, int width, int height, int filterRadius = 1, bool useMedianInsteadOfAverage = false) {
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
                if (!IsFgVertexOnBorder(row, col, width, height, isBg)) {
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
                        if (!IsFgVertexOnBorder(neighborRow, neighborCol, width, height, isBg)) { continue; }
                        Vector3 sampledVertex = vertices[neighborVertIdx];
                        xValues.Add(sampledVertex.x);
                        yValues.Add(sampledVertex.y);
                        zValues.Add(sampledVertex.z);
                    }
                }

                Vector3 centroid;
                if (useMedianInsteadOfAverage) {
                    xValues.Sort();
                    yValues.Sort();
                    zValues.Sort();
                    centroid = new Vector3(xValues[xValues.Count / 2], yValues[yValues.Count / 2], zValues[zValues.Count / 2]);
                } else {
                    centroid = new Vector3(xValues.Average(), yValues.Average(), zValues.Average());
                }
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

    // Vertices / UVs needs to be a 2D array of width * height.
    void SimplifyMeshV2(
        Vector3[] vertices, Vector2[] uvs, int[] triangles, 
        bool[] vertexMask, bool vertexMaskFlag, // Filters out certain vertices from being used
        int width, int height, 
        out Vector3[] _newVerts, out Vector2[] _newUvs, out int[] _newTriangles,
        bool shouldSkipBorderVertices = false // Do not perform simplification on foreground border vertices. Should not be used for background mesh.
    ) {
        // Compute distances.
        float[] distances = new float[width * height];
        for (int i = 0; i < distances.Length; i++) { distances[i] = float.MaxValue; } // Use float.MaxValue as a marker for this was not set
        for (int vertIdx = 0; vertIdx < vertices.Length; vertIdx++) {
            bool isBorderVertex = false;
            if (shouldSkipBorderVertices) {
                int col = vertIdx % width;
                int row = (vertIdx - col) / width;
                isBorderVertex = IsFgVertexOnBorder(row, col, width, height, vertexMask);
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
        int largestRegionArea = LargestSimplifiedRegionSize * LargestSimplifiedRegionSize;
        if ((bounds.y - bounds.x) < MaximumDeltaDistance && regionArea < largestRegionArea) {
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

    MeshFilter GenerateMesh(string name, Vector3[] vertices, Vector2[] uvs, int[] triangles, Texture2D texture, Transform parent = null, Material material = null) {
        GameObject meshObject = new GameObject(name);
        MeshFilter meshFilter = meshObject.AddComponent<MeshFilter>() as MeshFilter;
        MeshRenderer meshRenderer = meshObject.AddComponent<MeshRenderer>() as MeshRenderer;
        Mesh mesh = new Mesh();
        mesh.indexFormat = IndexFormat.UInt32;

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        meshFilter.mesh = mesh;

        if (material == null) {
            // Add a material onto the mesh
            Material mat = new Material(Shader.Find("Unlit/Texture"));
            mat.SetTexture("_MainTex", texture);
            AssetDatabase.CreateAsset(mat, "Assets/Resources/"+PhotoIdentifier+"_bgMaterial.mat");
            meshRenderer.material = mat;
        } else {
            meshRenderer.material = material;
        }

        if (parent != null) {
            meshObject.transform.SetParent(parent);
        }

        return meshFilter;
    }

    int ConvertVertexCoordinates(int index, int width, int colOffset, int rowOffset, int newWidth) {
        int col = index % width;
        int row = (index - col) / width;
        int newIndex = (row + rowOffset) * newWidth + (col + colOffset);
        return newIndex;
    }

    IEnumerator Generate3DPhotoV4(Texture2D _colorImage, Texture2D _depthImage, Texture2D _foregroundImage) {
        PhotoIdentifier = System.Guid.NewGuid().ToString();
        bool UseDistanceThresholding = false;

        // When importing a texture, "Read/Write" is disabled.
        // This creates a copy of the texture that has read enabled.
        Texture2D depthImage = GetReadableTexture(_depthImage, true);
        Texture2D fgImage = GetReadableTexture(_foregroundImage, false);
        Texture2D colorImage = GetReadableTexture(_colorImage, false);
        Color32[] depthPixels = depthImage.GetPixels32(0);
        Color32[] fgPixels = fgImage.GetPixels32(0);
        Color32[] colorImagePixels = colorImage.GetPixels32(0);

        GameObject rootObj = new GameObject("3D Photo");

        // For each pixel, we'll have four vertices, one for each corner.
        // Then we will form two triangles covering the square that is the pixel.
        // Total number of vertices on each side is equal to pixels + 1.
        // Diagram: https://github.com/kumorikuma/3d_photos/blob/main/Assets/Diagrams/vertex_layout.jpg
        int width = depthImage.width + 1;
        int height = depthImage.height + 1;
        int numVerts = width * height;
        Vector3[] vertices = new Vector3[numVerts];
        Vector2[] uvs = new Vector2[numVerts];
        List<float> distances = new List<float>(numVerts);

        // We're going to re-use this same vertex buffer for different meshes,
        // so we will also establish a vertexMask to mark which ones to use.
        bool[] bgVertexMask = new bool[numVerts]; // fgVertexMask is just the opposite of this
        List<int> bgTriangles = new List<int>(); // # of tris is variable
        List<int> fgTriangles = new List<int>(); // # of tris is variable
        List<int> allTriangles = new List<int>(); // For demonstration purposes

        // First construct a mask of all the background vertices.
        bool[] bgPxMask = new bool[depthImage.width * depthImage.height]; // fgVertexMask is just the opposite of this
        for (int row = 0; row < depthImage.height; row++) {
            for (int col = 0; col < depthImage.width; col++) {
                int idx = row * depthImage.width + col;
                Vector2 uv = new Vector2(col / (float)(depthImage.width), row / (float)(depthImage.height)); // Range [0, 1]
                bool isBackground = SampleTexture(uv, fgPixels, fgImage.width, fgImage.height).a < 0.01f;
                bgPxMask[idx] = isBackground;
            }
        }

        // Create vertices
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                Vector2 uv = new Vector2(col / (float)(width), row / (float)(height)); // Range [0, 1]

                // The "depth" value stored in many depth maps such as those from iOS are actually storing a "disparity" value,
                // and not the actual distance from the pixel to the camera.
                // More about disparity: https://stackoverflow.com/questions/17607312/what-is-the-difference-between-a-disparity-map-and-a-disparity-image-in-stereo-m
                float disparityDepth = SampleTexture(uv, depthPixels, depthImage.width, depthImage.height).r;
                if (OverrideDepth) {
                    disparityDepth = DepthOverride;
                }
                float depth = disparityDepth;
                if (ConvertDepthValuesFromDisparity) {
                    // Apply a conversion from disparity depth to actual z value
                    // Change in foreground has little effect. Change in background has a lot of effect.
                    float maxDepth = MaxDepth; // This value affects how much difference there is between fg and bg, or how much 'depth' there is in the scene.
                    depth = 1 / (disparityDepth + (1 / maxDepth)); // Range [0.5, maxDepth]
                    depth = (depth - 0.5f) / (maxDepth - 0.5f) * MaxDistance; // Range [0.0, MaxDistance]
                } else {
                    depth = 1 - depth;
                }

                bool isBackground;
                if (UseDistanceThresholding) {
                    isBackground = true;
                    // TODO:
                } else {
                    // Any transparent pixels in the foreground image are considered part of the background.
                    isBackground = SampleTexture(uv, fgPixels, fgImage.width, fgImage.height).a < 0.01f;
                }

                const float DEPTH_OFFSET = 1;
                Vector3 vertex;
                if (ProjectFromOrigin) {
                    // Project from the virtual camera position out in a direction. Use the depth as the distance along this projection.
                    Vector2 angles = new Vector2(degToRad((uv.x - 0.5f) * cameraHorizontalFov), degToRad((uv.y - 0.5f) * cameraVerticalFov)); 
                    Vector3 viewingDirection = (new Vector3((float)Math.Sin(angles.x), (float)Math.Sin(angles.y), (float)Math.Cos(angles.x))).normalized;
                    vertex = viewingDirection * (depth + DEPTH_OFFSET); // Add offset here because otherwise some vertices will be at the projection origin when depth = 0
                } else {
                    // Naive method that just straight up uses the depth as the z-value without using projection
                    vertex = new Vector3(uv.x * 2 - 1, uv.y * 2 - 1, depth + DEPTH_OFFSET); 
                }

                vertices[vertIdx] = vertex;
                uvs[vertIdx] = uv;
                bgVertexMask[vertIdx] = isBackground;
            }
        }
       
        // Outlier removal for BG: if distance value is less than 90% of other values, or average, then set it to the average.
        if (RunMedianFilter) {
            List<Vector3> bgVertices = new List<Vector3>();
            for (int i = 0; i < vertices.Length; i++) {
                if (bgVertexMask[i]) { bgVertices.Add(vertices[i]); }
            }
            bgVertices.Sort((Vector3 A, Vector3 B) => {
                float distanceA = (A - Vector3.zero).magnitude;
                float distanceB = (B - Vector3.zero).magnitude;
                return distanceB.CompareTo(distanceA); // Descending order
            });
            float distanceThreshold = (bgVertices[(int)(bgVertices.Count * 0.9f)] - Vector3.zero).magnitude;
            for (int row = 0; row < height; row++) {
                for (int col = 0; col < width; col++) {
                    int vertIdx = row * width + col;
                    if (!bgVertexMask[vertIdx]) { continue; }
                    // Reproject the vertex
                    float distance = (vertices[vertIdx] - Vector3.zero).magnitude;
                    if (distance < distanceThreshold) {
                        vertices[vertIdx] = vertices[vertIdx].normalized * distanceThreshold;
                    }
                }
            }
        }
        

        // Generate triangles. We only generate triangles in pairs to ensure there are only quads in our topology.
        // The quad has:
        // - Top left corner A
        // - Top right corner B
        // - Bottom left corner C
        // - Bottom right corner D
        // This loops over every "D" vertex. So we start from row = 1, col = 1 to avoid out of bounds.
        for (int row = 1; row < height; row++) {
            for (int col = 1; col < width; col++) {
                int vertIdx = row * width + col;
                int vertA = vertIdx - 1 - width;
                int vertB = vertIdx - width;
                int vertC = vertIdx - 1;
                int vertD = vertIdx;
                if (!SeparateFgBg) {
                    allTriangles.Add(vertC); allTriangles.Add(vertB); allTriangles.Add(vertA);
                    allTriangles.Add(vertC); allTriangles.Add(vertD); allTriangles.Add(vertB);
                    continue;
                }
                // Triangle ABC -> CBA
                bool isBackgroundTriangle = bgVertexMask[vertC] && bgVertexMask[vertB] && bgVertexMask[vertA];
                bool isForegroundTriangle = !bgVertexMask[vertC] && !bgVertexMask[vertB] && !bgVertexMask[vertA];
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
                // Triangle BDC -> CDB
                isBackgroundTriangle = bgVertexMask[vertC] && bgVertexMask[vertD] && bgVertexMask[vertB];
                isForegroundTriangle = !bgVertexMask[vertC] && !bgVertexMask[vertD] && !bgVertexMask[vertB];
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

        // Resulting mesh ends up having a lot of imperfections which can be mitigated using filters.
        // This is by far the slowest part of the algorithm.
        // Use border behavior: clamp.
        if (RunMedianFilter) {
            // Run a median filter to remove any outliers
            vertices = FilterVertices(vertices, bgVertexMask, false, width, height, 4, true); // Background
            vertices = FilterVertices(vertices, bgVertexMask, true, width, height, 8, true); // Foreground
            vertices = FilterVertices(vertices, bgVertexMask, true, width, height, 8, true); // Foreground
        }
        if (RunMeshSmoothingFilter) {
            // Then run an averaging (or blurring) filter to smooth out the mesh
            vertices = FilterVertices(vertices, bgVertexMask, false, width, height, 8); // Background
            vertices = FilterVertices(vertices, bgVertexMask, true, width, height, 8); // Foreground
        }
        if (SmoothForegroundEdges) {
            // The edges in the foreground can be very jagged. 
            // Run averaging filter on only the foreground edge vertices to smooth out the jaggies.
            vertices = FilterFgBorderVertices(vertices, bgVertexMask, width, height); // Average
            vertices = FilterFgBorderVertices(vertices, bgVertexMask, width, height); // Average
        }

        // To handle viewing of the occluded regions, we will extend the background mesh by filling in the holes left by the foreground mesh.
        // To handle viewing of regions that are slightly outside the original viewing constraints, we will extend the background mesh outwards slightly.
        // The extended background mesh will use a new texture with the foreground removed, that can be inpainted and outpainted using an external inpainting system like Dall-E.
        // The extended mesh will need to be bigger, and since Dall-E likes its textures square, we'll simply just round up the nearest power of two.
        // Note: Originally the original background and hallucinated mesh areas were separate. However they are best kept together for two reasons:
        // - The original background should use the same texture as the hallucinated mesh. Dall-E changes the original image pixels slightly when inpainting,
        // which can cause seams between the hallucinated regions and the background.
        // - Mesh simplification needs to weld the hallucinated region to the original background, and this is a lot simpler if they are one mesh.
        int paddingWidth = (int)(0.1f * width);
        int paddingHeight = (int)(0.1f * height);
        int extendedBgWidth = width + paddingWidth;
        int extendedBgHeight = height + paddingHeight;
        int extendedBgTextureSize = Mathf.Max(extendedBgWidth, extendedBgHeight);
        extendedBgWidth = extendedBgTextureSize; // Technically this should be +1 but that makes the color indexing off by 1 and it's kind of a pain
        extendedBgHeight = extendedBgTextureSize; // Technically this should be +1 but that makes the color indexing off by 1 and it's kind of a pain
        if (!GenerateOutpaintedRegion) {
            extendedBgWidth = width;
            extendedBgHeight = height;
        }
        int numVertsForExtendedBg = extendedBgWidth * extendedBgHeight;
        // New vertex / element buffers
        Vector3[] extendedBgVerts = new Vector3[numVertsForExtendedBg];
        Vector2[] extendedBgUVs = new Vector2[numVertsForExtendedBg];
        bool[] extendedBgVertexMask = new bool[numVertsForExtendedBg];
        bool[] hallucinatedMeshVertexMask = new bool[numVertsForExtendedBg];
        List<int> extendedBgTriangles = new List<int>();

        // Make a new texture that uses alpha = 0 for the empty spots. Goal is to infill those spots using DallE.
        // We'll also do outpainting. The output texture will be square.
        Texture2D extendedBgTexture = new Texture2D(extendedBgWidth, extendedBgHeight);
        Color[] colors = new Color[extendedBgWidth * extendedBgHeight];

        // Center the original image inside of the larger square
        int originalStartRow = (int)((extendedBgHeight - height) / 2.0f);
        int originalEndRow = originalStartRow + height;
        int originalStartColumn = (int)((extendedBgWidth - width) / 2.0f);
        int originalEndColumn = originalStartColumn + width;
        for (int row = 0; row < extendedBgHeight; row++) {
            for (int col = 0; col < extendedBgWidth; col++) {
                int newVertIdx = row * extendedBgWidth + col;
                Vector2 uv = new Vector2(col / (float)(extendedBgWidth), row / (float)(extendedBgHeight)); // Range [0, 1]

                // If this is part of the original...
                if (row >= originalStartRow && row < originalEndRow  && col >= originalStartColumn && col < originalEndColumn) {
                    int originalRow = row - originalStartRow;
                    int originalCol = col - originalStartColumn;
                    int originalVertIdx = originalRow * width + originalCol;

                    // Fill in the color
                    colors[newVertIdx] = SampleTexture(uvs[originalVertIdx], colorImagePixels, colorImage.width, colorImage.height);

                    // Add any original vertices from the background to the new buffers.
                    if (bgVertexMask[originalVertIdx]) {
                        extendedBgVerts[newVertIdx] = vertices[originalVertIdx];
                        extendedBgUVs[newVertIdx] = uv;
                        extendedBgVertexMask[newVertIdx] = true;
                        continue;
                    }

                    // Skip generating the inpainted region if desired
                    if (!GenerateInpaintedRegion) {
                        continue;
                    }

                    // This is a foreground vertex, we'll duplicate it and shift it such that it's in the background.
                    // Take the depth of the left BG pixel and right BG pixel.
                    float lastBgDistance = 1;
                    int distanceToLast = 0;
                    for (int x = originalCol - 1; x > 0; x--) {
                        int searchVertIdx = originalRow * width + x;
                        distanceToLast++;
                        if (bgVertexMask[searchVertIdx]) {
                            lastBgDistance = (vertices[searchVertIdx] - Vector3.zero).magnitude;
                            break;
                        }
                    }
                    float nextBgDistance = 1;
                    int distanceToNext = 0;
                    for (int x = originalCol + 1; x < width; x++) {
                        int searchVertIdx = originalRow * width + x;
                        distanceToNext++;
                        if (bgVertexMask[searchVertIdx]) {
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
                        distance = fgDistance + 0.01f;
                    }

                    // Create hallucinated vertex for inpainted (occluded) region
                    Vector3 hallucinatedVert = Vector3.zero + vertices[originalVertIdx].normalized * distance;
                    colors[newVertIdx] = new Color(0, 0, 0, 0);
                    extendedBgVerts[newVertIdx] = hallucinatedVert;
                    extendedBgUVs[newVertIdx] = uv;
                    extendedBgVertexMask[newVertIdx] = true;
                    hallucinatedMeshVertexMask[newVertIdx] = true;
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
                    int neighborIdx = neighborRow * width + neighborCol;
                    float distanceFromOrigin = (vertices[neighborIdx] - Vector3.zero).magnitude;
                    // TODO: If it's a foreground vertex, probably do something different

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

                    // Create hallucinated vertex for outpainted region
                    Vector3 hallucinatedVert = Vector3.zero + viewingDirection * distanceFromOrigin;
                    extendedBgVerts[newVertIdx] = hallucinatedVert;
                    extendedBgUVs[newVertIdx] = uv;
                    extendedBgVertexMask[newVertIdx] = true;
                    hallucinatedMeshVertexMask[newVertIdx] = true;
                }    
            }
        }

        // Save texture to disk to allow inpainting with third party tool.
        // Dall-E's max resolution is 1024x1024, so resize it if it's bigger.
        
        extendedBgTexture.SetPixels(colors);
        if (extendedBgTextureSize != 1024) {
            GPUTextureScaler.Scale(extendedBgTexture, 1024, 1024);
        }
        string filename = "Assets/Resources/"+PhotoIdentifier+"_extendedBgTexture.png";
        File.WriteAllBytes(filename, extendedBgTexture.EncodeToPNG());
        AssetDatabase.ImportAsset(filename);
        extendedBgTexture = Resources.Load<Texture2D>(PhotoIdentifier+"_extendedBgTexture");

        // Form the triangles.
        // Because the background mesh and hallucinated mesh regions are combined, it makes this logic quite simple.
        for (int row = 1; row < extendedBgHeight; row++) {
            for (int col = 1; col < extendedBgWidth; col++) {
                int newVertIdx = row * extendedBgWidth + col;
                // Previous Row
                int vertA = newVertIdx - 1 - extendedBgWidth;
                int vertB = newVertIdx - extendedBgWidth;
                // Current Row
                int vertC = newVertIdx - 1;
                int vertD = newVertIdx;

                // Triangle ABC -> CBA
                if (extendedBgVertexMask[vertC] && extendedBgVertexMask[vertB] && extendedBgVertexMask[vertA]) {
                    extendedBgTriangles.Add(vertC);
                    extendedBgTriangles.Add(vertB);
                    extendedBgTriangles.Add(vertA);
                }
                // Triangle BDC -> CDB
                if (extendedBgVertexMask[vertC] && extendedBgVertexMask[vertD] && extendedBgVertexMask[vertB]) {
                    extendedBgTriangles.Add(vertC);
                    extendedBgTriangles.Add(vertD);
                    extendedBgTriangles.Add(vertB);
                }
            }
        }

        // Use a separate material for the foreground mesh that feathers the edges.
        // Texture2D featherTexture = GenerateFeatherMask(1024, 1024, fgPixels, fgImage.width, fgImage.height);
        // featherTexture.SetPixels(FilterImage(featherTexture.GetPixels32(), featherTexture.width, featherTexture.height, null, false, 8));
        Material fgMat = null;
        if (ForegroundFeathering) {
            fgMat = new Material(Shader.Find("Unlit/FeatherShader"));
            fgMat.SetTexture("_MainTex", _colorImage);
            fgMat.SetTexture("_FeatherTex", _foregroundImage);
            fgMat.renderQueue = 3000; // For transparency
            AssetDatabase.CreateAsset(fgMat, "Assets/Resources/"+PhotoIdentifier+"_fgMaterial.mat");
        }

        if (!SeparateFgBg) {
            GenerateMesh("Mesh", vertices, uvs, allTriangles.ToArray(), _colorImage, rootObj.transform);
            yield break;
        }

        // Simplify the foreground mesh and generate output
        if (GenerateForegroundMesh) {
            if (PerformMeshSimplification) {
                Vector3[] fgVertsSimplified; Vector2[] fgUvsSimplified; int[] fgTrianglesSimplified;
                const bool SkipBorderVertices = true;
                SimplifyMeshV2(vertices, uvs, fgTriangles.ToArray(), bgVertexMask, false, width, height, out fgVertsSimplified, out fgUvsSimplified, out fgTrianglesSimplified, SkipBorderVertices);
                GenerateMesh("Foreground", fgVertsSimplified, fgUvsSimplified, fgTrianglesSimplified, _colorImage, rootObj.transform, fgMat);
                Debug.Log("Foreground Mesh Vert #: " + fgVertsSimplified.Length);
                Debug.Log("Foreground Mesh Tri #: " + fgTrianglesSimplified.Length);
            } else {
                __cachedFgMesh = GenerateMesh("Foreground", vertices, uvs, fgTriangles.ToArray(), _colorImage, rootObj.transform, fgMat);
                Debug.Log("Foreground Mesh Vert #: " + vertices.Length);
                Debug.Log("Foreground Mesh Tri #: " + fgTriangles.Count);
            }
        }

        // Simplify the extended background mesh and generate output
        if (GenerateBackgroundMesh) {
            if (PerformMeshSimplification) {
                Vector3[] extBgVertsSimplified; Vector2[] extBgUvsSimplified; int[] extBgTrianglesSimplified;
                SimplifyMeshV2(extendedBgVerts, extendedBgUVs, extendedBgTriangles.ToArray(), extendedBgVertexMask, true, extendedBgWidth, extendedBgHeight, out extBgVertsSimplified, out extBgUvsSimplified, out extBgTrianglesSimplified);
                GenerateMesh("Background", extBgVertsSimplified, extBgUvsSimplified, extBgTrianglesSimplified, extendedBgTexture, rootObj.transform);
                Debug.Log("Background Mesh Vert #: " + extBgVertsSimplified.Length);
                Debug.Log("Background Mesh Tri #: " + extBgTrianglesSimplified.Length);
            } else {
                __cachedBgMesh = GenerateMesh("Background", extendedBgVerts, extendedBgUVs, extendedBgTriangles.ToArray(), extendedBgTexture, rootObj.transform);
                Debug.Log("Background Mesh Vert #: " + extendedBgVerts.Length);
                Debug.Log("Background Mesh Tri #: " + extendedBgTriangles.Count);
            }
        }

        // For mesh simplification animation
        __cachedBgVertexMask = bgVertexMask;
        __cachedWidth = width;
        __cachedHeight = height;
        __cachedExtendedBgVertexMask = extendedBgVertexMask;
        __cachedExtendedBgWidth = extendedBgWidth;
        __cachedExtendedBgHeight = extendedBgHeight;

        yield return null;
    }

    IEnumerator SimplifyMeshAnimation(
    ) {
        this.StartCoroutine(__SimplifyMeshAnimation(__cachedBgMesh, __cachedExtendedBgVertexMask, true, __cachedExtendedBgWidth, __cachedExtendedBgHeight));
        this.StartCoroutine(__SimplifyMeshAnimation(__cachedFgMesh, __cachedBgVertexMask, false, __cachedWidth, __cachedHeight));

        yield return null;
    }

    struct Region {
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

    // Different version of simplification algorithm written for making an animation.
    // Main difference is that it operates in-place, and is non-recursive so it can be paused for rendering.
    IEnumerator __SimplifyMeshRegion(
        int _x1, int _x2, int _y1, int _y2, 
        float[] distances, int width, int height, 
        Vector3[] vertices, List<int> triangles,
        Dictionary<string, Vector2> regionBoundsCache,
        List<RectInt> regions,
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
            Vector2 bounds = ComputeRegionBounds(x1, x2, y1, y2, distances, width, regionBoundsCache);
            int largestRegionArea = LargestSimplifiedRegionSize * LargestSimplifiedRegionSize;
            bool regionCanBeSimplified = (bounds.y - bounds.x) < MaximumDeltaDistance && regionArea < largestRegionArea;
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

    IEnumerator __SimplifyMeshAnimation(
        MeshFilter sourceMesh,
        bool[] vertexMask, bool vertexMaskFlag, // Filters out certain vertices from being used
        int width, int height, 
        bool shouldSkipBorderVertices = false // Do not perform simplification on foreground border vertices. Should not be used for background mesh.) 
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
                isBorderVertex = IsFgVertexOnBorder(row, col, width, height, vertexMask);
            }
            if (vertexMaskFlag != vertexMask[vertIdx] || isBorderVertex) {
                continue;
            } else {
                distances[vertIdx] = (Vector3.zero - vertices[vertIdx]).magnitude;
            }
        }

        Dictionary<string, Vector2> regionBoundsCache = new Dictionary<string, Vector2>();
        List<RectInt> regions = new List<RectInt>();

        yield return this.StartCoroutine(__SimplifyMeshRegion(
            0, width - 1, 0, height - 1, 
            distances, width, height, 
            vertices, triangles,
            regionBoundsCache,
            regions, (List<int> triangles) => {
                sourceMesh.sharedMesh.triangles = triangles.ToArray();
                EditorWindow.GetWindow<SceneView>().Repaint();
        }));

        yield return null;
    }

    Texture2D GenerateFeatherMask(int width, int height, Color32[] fgPixels, int fgWidth, int fgHeight) {
        // Create a texture that represents distance from nearest inpainted region
        Texture2D featherMask = new Texture2D(width, height);
        Color[] featherMaskColors = new Color[width * height];
        int filterRadius = 3;
        int filterSize = filterRadius * 2 + 1;
        float maxDistance = filterRadius;
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int idx = row * width + col;
                // If it's background region, then fill with black.
                Vector2 uv = new Vector2(col / (float)(width), row / (float)(height)); // Range [0, 1]
                if (SampleTexture(uv, fgPixels, fgWidth, fgHeight).a < 0.01f) {
                    featherMaskColors[idx] = Color.black;
                    continue;
                }

                // Get distance to background region
                float minDistance = float.MaxValue;
                Vector2 coordinate = new Vector2(col, row);
                for (int j = -filterRadius; j <= filterRadius; j++) {
                    for (int i = -filterRadius; i <= filterRadius; i++) {
                        if (j == 0 && i == 0) { continue; } // Skip self
                        int neighborRow = row + j;
                        if (neighborRow < 0) { continue; }
                        else if (neighborRow >= height) { continue; }
                        int neighborCol = col + i;
                        if (neighborCol < 0) { continue; }
                        else if (neighborCol >= width) { continue; }

                        Vector2 neighborUv = new Vector2(neighborCol / (float)(width), neighborRow / (float)(height)); // Range [0, 1]
                        if (SampleTexture(uv, fgPixels, fgWidth, fgHeight).a < 0.01f) {
                            // Compute distance to it
                            float distance = (new Vector2(neighborCol, neighborRow) - coordinate).magnitude;
                            if (distance < minDistance) {
                                minDistance = distance;
                            }
                        }
                    }
                }
                if (minDistance == float.MaxValue) {
                    featherMaskColors[idx] = Color.white;
                } else {
                    float t = minDistance / filterRadius;
                    featherMaskColors[idx] = Color.Lerp(Color.black, Color.white, t);
                }
            }
        }
        featherMask.SetPixels(featherMaskColors);
        File.WriteAllBytes("Assets/feather_mask.png", featherMask.EncodeToPNG());
        featherMask.LoadImage(System.IO.File.ReadAllBytes("Assets/feather_mask.png"));
        return featherMask;
    }
}