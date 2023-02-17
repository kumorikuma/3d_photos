using System;
using System.Linq;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Unity.EditorCoroutines.Editor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

public class MeshGeneration : EditorWindow {
    Texture2D ColorImage;
    Texture2D DepthImage;
    Texture2D ForegroundImage;
    MeshFilter InputMesh;
    bool RunMedianFilter = false;
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
        RunMedianFilter = EditorGUILayout.Toggle("Enable Median Filter", RunMedianFilter);

        if(GUILayout.Button("Generate V1")) {
            this.StartCoroutine(Generate3DPhotoV1(ColorImage, DepthImage));
        }

        if(GUILayout.Button("Generate V2")) {
            this.StartCoroutine(Generate3DPhotoV2(ColorImage, DepthImage));
        }

        if(GUILayout.Button("Generate V3")) {
            this.StartCoroutine(Generate3DPhotoV3(ColorImage, DepthImage));
        }

        if(GUILayout.Button("Generate V4")) {
            this.StartCoroutine(Generate3DPhotoV4(ColorImage, DepthImage, ForegroundImage));
        }
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

    Vector3[] FilterVertices(Vector3[] vertices, bool[] isBg, int width, int height, int filterRadius = 4) {
        int filterWidth = filterRadius * 2 + 1;
        Vector3[] filteredVerts = new Vector3[vertices.Length];
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                List<float> xValues = new List<float>(filterWidth * filterWidth);
                List<float> yValues = new List<float>(filterWidth * filterWidth);
                List<float> zValues = new List<float>(filterWidth * filterWidth);
                List<float> distances = new List<float>(filterWidth * filterWidth);
                Vector3 vertex = vertices[vertIdx];
                bool isVertBg = isBg[vertIdx];
                // Compute Centroid
                for (int j = -filterRadius; j <= filterRadius; j++) {
                    for (int i = -filterRadius; i <= filterRadius; i++) {
                        // if (j == 0 && i == 0) { continue; } // Skip self
                        int sampleRow = row + j;
                        if (sampleRow < 0) { 
                            sampleRow = 0; 
                            continue;
                        }
                        else if (sampleRow >= height) { 
                            sampleRow = height - 1; 
                            continue;
                        }
                        int sampleCol = col + i;
                        if (sampleCol < 0) { 
                            sampleCol = 0; 
                            continue;
                        }
                        else if (sampleCol >= width) { 
                            sampleCol = width - 1; 
                            continue;
                        }
                        int sampleVertIdx = sampleRow * width + sampleCol;
                        bool isSampledVertBg = isBg[sampleVertIdx];
                        if (isVertBg != isSampledVertBg) { 
                            sampleVertIdx = vertIdx;
                            continue;
                        }
                        Vector3 sampledVert = vertices[sampleVertIdx];
                        distances.Add((sampledVert - Vector3.zero).magnitude);
                    }
                }
                filteredVerts[vertIdx] = Vector3.zero + vertex.normalized * distances.Average();
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
                        int sampleRow = row + j;
                        if (sampleRow < 0) { continue; }
                        else if (sampleRow >= height) { continue; }
                        int sampleCol = col + i;
                        if (sampleCol < 0) { continue; }
                        else if (sampleCol >= width) { continue; }
                        int sampleVertIdx = sampleRow * width + sampleCol;
                        if (isBg[sampleVertIdx]) { continue; }
                        if (!IsBorderVertex(sampleRow, sampleCol, width, height, isBg)) { continue; }
                        Vector3 sampledVertex = vertices[sampleVertIdx];
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

    void _duplicateVert(int vertIdx, int?[] newVertIdx, List<Vector3> newVerts, List<Vector2> newUvs, Vector3[] verts, Vector2[] uvs, bool[] isBg, Color[] colors, float distance) {
        newVertIdx[vertIdx] = newVerts.Count;
        if (isBg[vertIdx]) {
            newVerts.Add(verts[vertIdx]);
        } else {
            Vector3 shiftedVert = Vector3.zero + verts[vertIdx].normalized * distance;
            newVerts.Add(shiftedVert);
            colors[vertIdx] = new Color(0, 0, 0, 0);
        }
        newUvs.Add(uvs[vertIdx]);
    }

    IEnumerator Generate3DPhotoV4(Texture2D _colorImage, Texture2D _depthImage, Texture2D _foregroundImage) {
        Texture2D depthImage = GetReadableTexture(_depthImage);
        Texture2D fgImage = GetReadableTexture(_foregroundImage, false);
        Texture2D colorImage = GetReadableTexture(_colorImage, false);

        GameObject obj = new GameObject("3D Photo");
        MeshFilter meshFilter = obj.AddComponent<MeshFilter>() as MeshFilter;
        MeshRenderer meshRenderer = obj.AddComponent<MeshRenderer>() as MeshRenderer;
        Mesh mesh = new Mesh();
        mesh.indexFormat = IndexFormat.UInt32;
        mesh_ = mesh;

        // Number of vertices on each side is equal to pixels + 1
        int width = depthImage.width + 1;
        int height = depthImage.height + 1;
        int numVerts = width * height;
        Vector3[] vertices = new Vector3[numVerts];
        bool[] isBg = new bool[numVerts];
        Vector2[] uvs = new Vector2[numVerts];
        List<int> triangles = new List<int>(); // # of tris is variable

        Vector3 origin = Vector3.zero;
        Color32[] depthPixels = depthImage.GetPixels32(0);
        // Color[] depthPixels = FilterImage(depthImage.GetPixels32(0), depthImage.width, depthImage.height);
        Color32[] fgPixels = fgImage.GetPixels32(0);
        // Create vertices
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                Vector2 uv = new Vector2(col / (float)(width), row / (float)(height)); // Range [0, 1]

                float depth = 1.0f - SampleTexture(uv, depthPixels, depthImage.width, depthImage.height).r; // 1 minus depth to flip it since we want white = closer
                Vector2 angles = new Vector2(degToRad((uv.x - 0.5f) * cameraHorizontalFov), degToRad((uv.y - 0.5f) * cameraVerticalFov)); 
                Vector3 viewingAngle = new Vector3((float)Math.Sin(angles.x), (float)Math.Sin(angles.y), (float)Math.Cos(angles.x));

                // Simply just using depth as Z value
                // float depth = SampleTexture(uv, depthPixels, depthImage.width, depthImage.height).r;

                // Apply a conversion from disparity depth to actual z value
                // Change in foreground has little effect. Change in background has a lot of effect.
                // As a result, need to apply distance thresholding.
                float maxDepth = 10;
                float z = 1 / (depth + (1 / maxDepth)); // Range [0.5, maxDepth]
                z = (z - 0.5f) / (maxDepth - 0.5f); // Range [0.0, 1.0]

                // Trying different curves
                // z = (1 - Mathf.Log(depth * 100 + 1) + Mathf.Log(101)) / (Mathf.Log(101) + 1);
                // z = (1 - Mathf.Pow(10, depth) + 10) / 10;

                bool isBackground = SampleTexture(uv, fgPixels, fgImage.width, fgImage.height).a < 0.01f;

                // Without using projection
                // Vector3 vertex = new Vector3(uv.x * 2 - 1, uv.y * 2 - 1, z); 

                // Project from virtual camera position out 
                Vector3 vertex = viewingAngle * (depth + 1);

                vertices[vertIdx] = vertex;
                uvs[vertIdx] = uv;
                isBg[vertIdx] = isBackground;
            }
        }
       
        // Generate triangles
        int numTrianglesGenerated = 0;
        for (int row = 1; row < height; row++) {
            for (int col = 1; col < width; col++) {
                int vertIdx = row * width + col;
                int triangleIdx = numTrianglesGenerated * 3;
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
                    if (isBackgroundTriangle || isForegroundTriangle) {
                        triangles.Add(vertC);
                        triangles.Add(vertB);
                        triangles.Add(vertA);
                        numTrianglesGenerated += 1;
                    } else {
                        // Try doing a triangle ADC -> CDA.
                        // Reduces blockiness by adding more triangles that form 'half squares'
                        isBackgroundTriangle = isBg[vertC] && isBg[vertD] && isBg[vertA];
                        isForegroundTriangle = !isBg[vertC] && !isBg[vertD] && !isBg[vertA];
                        if (isBackgroundTriangle || isForegroundTriangle) {
                            triangles.Add(vertC);
                            triangles.Add(vertD);
                            triangles.Add(vertA);
                            numTrianglesGenerated += 1;
                        }
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
                    if (isBackgroundTriangle || isForegroundTriangle) {
                        triangles.Add(vertC);
                        triangles.Add(vertD);
                        triangles.Add(vertB);
                        numTrianglesGenerated += 1;
                    } else {
                        // Try doing a triangle ABD -> DBA.
                        // Reduces blockiness by adding more triangles that form 'half squares'
                        isBackgroundTriangle = isBg[vertD] && isBg[vertB] && isBg[vertA];
                        isForegroundTriangle = !isBg[vertD] && !isBg[vertB] && !isBg[vertA];
                        if (isBackgroundTriangle || isForegroundTriangle) {
                            triangles.Add(vertD);
                            triangles.Add(vertB);
                            triangles.Add(vertA);
                            numTrianglesGenerated += 1;
                        }
                    }
                }
            }
        }

        Debug.Log(numVerts);
        Debug.Log(numTrianglesGenerated);

        // Filter the mesh
        // Run a median filter on the positions to smooth it out.
        // Border behavior: clamp
        if (RunMedianFilter) {
            // This is by far the slowest part of the algorithm
            vertices = FilterVertices(vertices, isBg, width, height);
            vertices = FilterVertices(vertices, isBg, width, height);
            vertices = FilterVertices(vertices, isBg, width, height);
            vertices = FilterFgBorderVertices(vertices, isBg, width, height);
            vertices = FilterFgBorderVertices(vertices, isBg, width, height);
        }

        // Background Mesh Object
        GameObject bgObj = new GameObject("Background");
        bgObj.transform.SetParent(obj.transform);
        MeshFilter perimeterMeshFilter = bgObj.AddComponent<MeshFilter>() as MeshFilter;
        MeshRenderer perimeterMeshRenderer = bgObj.AddComponent<MeshRenderer>() as MeshRenderer;
        Mesh perimeterMesh = new Mesh();
        perimeterMesh.indexFormat = IndexFormat.UInt32;

        List<Vector3> perimeterVertices = new List<Vector3>();
        List<Vector2> perimeterUvs = new List<Vector2>();
        List<int> perimeterTriangles = new List<int>();

        // Generate a background mesh
        // Make a new texture that uses alpha = 0 for the empty spots. Goal is to infill those spots using DallE.
        Texture2D bgTexture = new Texture2D(width, height);
        Color[] colors = new Color[width * height];
        Color32[] colorImagePixels = colorImage.GetPixels32(0);
        int?[] newVertIdxs = new int?[width * height];
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;

                // Fill in the color
                colors[vertIdx] = SampleTexture(uvs[vertIdx], colorImagePixels, colorImage.width, colorImage.height);

                // If this vertex is a FG vert, or if any of its neighbors are FG verts, we'll need to duplicate it.
                bool hasFgVert = false;

                // Check current Row
                if (!isBg[vertIdx]) { hasFgVert = true; }
                if (col > 0 && !isBg[vertIdx - 1]) { hasFgVert = true; } // Check previous column
                if (col < width - 1 && !isBg[vertIdx + 1]) { hasFgVert = true; } // Check next column
                // Check previous row
                if (row > 0) {
                    int baseIdx = vertIdx - width;
                    if (!isBg[baseIdx]) { hasFgVert = true; }
                    if (col > 0 && !isBg[baseIdx - 1]) { hasFgVert = true; } // Check previous column
                    if (col < width - 1 && !isBg[baseIdx + 1]) { hasFgVert = true; } // Check next column
                }
                // Check next row
                if (row < height - 1) {
                    int baseIdx = vertIdx + width;
                    if (!isBg[baseIdx]) { hasFgVert = true; }
                    if (col > 0 && !isBg[baseIdx - 1]) { hasFgVert = true; } // Check previous column
                    if (col < width - 1 && !isBg[baseIdx + 1]) { hasFgVert = true; } // Check next column
                }
                
                // If there's no FG verts, just skip
                if (!hasFgVert) {
                    continue;
                }

                // If it's a FG vert, we will also shift it such that it's in the background.
                // Take the depth of the left BG pixel and right BG pixel.
                float lastBgDistance = 1;
                int distanceToLast = 0;
                for (int x = col - 1; x > 0; x--) {
                    int searchVertIdx = row * width + x;
                    distanceToLast++;
                    if (isBg[searchVertIdx]) {
                        lastBgDistance = (vertices[searchVertIdx] - Vector3.zero).magnitude;
                        break;
                    }
                }
                float nextBgDistance = 1;
                int distanceToNext = 0;
                for (int x = col + 1; x < width; x++) {
                    int searchVertIdx = row * width + x;
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
                float fgDistance = (vertices[vertIdx] - Vector3.zero).magnitude;
                if (distance < fgDistance) {
                    distance = fgDistance + 0.001f;
                }

                _duplicateVert(vertIdx, newVertIdxs, perimeterVertices, perimeterUvs, vertices, uvs, isBg, colors, distance);            
            }
        }

        // Form the triangles
        for (int row = 1; row < height; row++) {
            for (int col = 1; col < width; col++) {
                Vector2 uv = new Vector2(col / (float)(width), row / (float)(height)); // Range [0, 1]
                int vertIdx = row * width + col;
                int triangleIdx = numTrianglesGenerated * 3;

                // Previous Row
                int vertA = vertIdx - 1 - width;
                int vertB = vertIdx - width;
                // Current Row
                int vertC = vertIdx - 1;
                int vertD = vertIdx;

                // If this vertex is a FG vert, or if any of its neighbors are FG verts, we'll need to form triangles.
                bool hasFgVert = false;

                // Check previous row
                if (row > 0) {
                    if (col > 0) {
                        // Check previous column
                        if (!isBg[vertA]) { hasFgVert = true; }
                    }
                    if (!isBg[vertB]) { hasFgVert = true; }
                }
                // Check current Row
                if (col > 0) {
                    // Check previous column
                    if (!isBg[vertC]) { hasFgVert = true; }
                }
                if (!isBg[vertD]) { hasFgVert = true; }
                
                // If there's no FG verts, just skip
                if (!hasFgVert) {
                    continue;
                }

                int dupVertA = newVertIdxs[vertA] == null ? throw new ArgumentNullException() : (int)newVertIdxs[vertA];
                int dupVertB = newVertIdxs[vertB] == null ? throw new ArgumentNullException() : (int)newVertIdxs[vertB]; 
                int dupVertC = newVertIdxs[vertC] == null ? throw new ArgumentNullException() : (int)newVertIdxs[vertC]; 
                int dupVertD = newVertIdxs[vertD] == null ? throw new ArgumentNullException() : (int)newVertIdxs[vertD]; 

                // Triangle ABC -> CBA
                perimeterTriangles.Add(dupVertC);
                perimeterTriangles.Add(dupVertB);
                perimeterTriangles.Add(dupVertA);
                // Triangle BDC -> CDB
                perimeterTriangles.Add(dupVertC);
                perimeterTriangles.Add(dupVertD);
                perimeterTriangles.Add(dupVertB);
            }
        }

        bgTexture.SetPixels(colors);
        File.WriteAllBytes("Assets/background.png", bgTexture.EncodeToPNG());
        var rawData = System.IO.File.ReadAllBytes("Assets/background.png");
        bgTexture.LoadImage(rawData);

        perimeterMesh.vertices = perimeterVertices.ToArray();
        perimeterMesh.uv = perimeterUvs.ToArray();
        perimeterMesh.triangles = perimeterTriangles.ToArray();
        perimeterMeshFilter.mesh = perimeterMesh;

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles.ToArray();
        meshFilter.mesh = mesh;

        // Add a material onto the mesh
        Material mat = new Material(Shader.Find("Unlit/Texture"));
        mat.SetTexture("_MainTex", colorImage);
        meshRenderer.material = mat;

        // Add a material onto the background mesh
        Material bgMat = new Material(Shader.Find("Unlit/Texture"));
        bgMat.SetTexture("_MainTex", bgTexture);
        perimeterMeshRenderer.material = bgMat;

        yield return null;
    }

    IEnumerator Generate3DPhotoV3(Texture2D colorImage, Texture2D depthImage) {
        Texture2D readableDepthImage = GetReadableTexture(depthImage);

        GameObject obj = new GameObject("3D Photo");
        MeshFilter meshFilter = obj.AddComponent<MeshFilter>() as MeshFilter;
        MeshRenderer meshRenderer = obj.AddComponent<MeshRenderer>() as MeshRenderer;
        Mesh mesh = new Mesh();
        mesh.indexFormat = IndexFormat.UInt32;
        mesh_ = mesh;

        // Number of vertices on each side is equal to pixels + 1
        int width = readableDepthImage.width + 1;
        int height = readableDepthImage.height + 1;
        int numVerts = width * height;
        Vector3[] vertices = new Vector3[numVerts];
        Vector2[] uvs = new Vector2[numVerts];
        // int numTrianglesPerRow = 2 * (width - 1);
        // int numTriangles = numTrianglesPerRow * (height - 1);
        // int[] triangles = new int[numTriangles * 3];
        List<int> triangles = new List<int>();

        Vector3 origin = Vector3.zero;
        Color32[] depthPixels = readableDepthImage.GetPixels32(0);
        float max = 0;
        // Create vertices
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                Vector2 uv = new Vector2(col / (float)(width), row / (float)(height)); // Range [0, 1]
                // float depth = 1.0f - SampleTexture(uv, depthPixels, readableDepthImage.width, readableDepthImage.height).r; // 1 minus depth to flip it since we want white = closer
                // Vector2 angles = new Vector2(degToRad((uv.x - 0.5f) * cameraHorizontalFov), degToRad((uv.y - 0.5f) * cameraVerticalFov)); 
                // Vector3 viewingAngle = new Vector3((float)Math.Sin(angles.x), (float)Math.Sin(angles.y), (float)Math.Cos(angles.x));
                // if (depth > max) {
                //     max = depth;
                // }

                // Simply just using depth as Z value
                float depth = 1 - SampleTexture(uv, depthPixels, readableDepthImage.width, readableDepthImage.height).r;
                Vector3 vertex = new Vector3(uv.x * 2 - 1, uv.y * 2 - 1, depth);

                // Project from virtual camera position out 
                // Vector3 vertex = viewingAngle * (depth + 1);
                vertices[vertIdx] = vertex;
                uvs[vertIdx] = uv;
            }
        }

        Debug.Log(max);

        bool separateBackground = false;

        // Perimeter Mesh
        GameObject perimeterObj = new GameObject("Perimeter");
        perimeterObj.transform.SetParent(obj.transform);
        MeshFilter perimeterMeshFilter = perimeterObj.AddComponent<MeshFilter>() as MeshFilter;
        MeshRenderer perimeterMeshRenderer = perimeterObj.AddComponent<MeshRenderer>() as MeshRenderer;
        Mesh perimeterMesh = new Mesh();
        perimeterMesh.indexFormat = IndexFormat.UInt32;

        List<Vector3> perimeterVertices = new List<Vector3>();
        List<Vector2> perimeterUvs = new List<Vector2>();
        List<int> perimeterTriangles = new List<int>();

        // Generate triangles
        int numTrianglesGenerated = 0;
        for (int col = 1; col < height; col++) {
            for (int row = 1; row < width; row++) {
                int vertIdx = row * width + col;
                int triangleIdx = numTrianglesGenerated * 3;
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
                if (distanceThresholdExceeded) {
                    int perimeterVertexIdx = perimeterVertices.Count;
                    perimeterVertices.Add(vertices[vertC]);
                    perimeterVertices.Add(vertices[vertB]);
                    perimeterVertices.Add(vertices[vertA]);
                    // // What color should Vert C be?
                    // if (bcThresholdExceeded && vertices[vertC].z > vertices[vertB].z) {
                    //     perimeterUvs.Add(uvs[vertB]);
                    // } else if (acThresholdExceeded && vertices[vertC].z > vertices[vertA].z) {
                    //     perimeterUvs.Add(uvs[vertA]);
                    // } else {
                    //     perimeterUvs.Add(uvs[vertC]);
                    // }
                    // // What color should Vert B be?
                    // if (bcThresholdExceeded && vertices[vertB].z > vertices[vertC].z) {
                    //     perimeterUvs.Add(uvs[vertC]);
                    // } else if (abThresholdExceeded && vertices[vertB].z > vertices[vertA].z) {
                    //     perimeterUvs.Add(uvs[vertA]);
                    // } else {
                    //     perimeterUvs.Add(uvs[vertB]);
                    // }
                    // // What color should Vert A be?
                    // if (acThresholdExceeded && vertices[vertA].z > vertices[vertC].z) {
                    //     perimeterUvs.Add(uvs[vertC]);
                    // } else if (abThresholdExceeded && vertices[vertA].z > vertices[vertB].z) {
                    //     perimeterUvs.Add(uvs[vertB]);
                    // } else {
                    //     perimeterUvs.Add(uvs[vertA]);
                    // }
                    perimeterUvs.Add(uvs[vertC]);
                    perimeterUvs.Add(uvs[vertB]);
                    perimeterUvs.Add(uvs[vertA]);
                    perimeterTriangles.Add(perimeterVertexIdx);
                    perimeterTriangles.Add(perimeterVertexIdx + 1);
                    perimeterTriangles.Add(perimeterVertexIdx + 2);
                } else {
                    triangles.Add(vertC);
                    triangles.Add(vertB);
                    triangles.Add(vertA);
                    numTrianglesGenerated += 1;
                }
                // Triangle BDC -> CDB
                float deltaBD = (vertices[vertB] - vertices[vertD]).magnitude;
                float deltaDC = (vertices[vertD] - vertices[vertC]).magnitude;
                distanceThresholdExceeded = deltaBD > deltaThreshold || deltaBC > deltaThreshold || deltaDC > deltaThreshold;
                // distanceThresholdExceeded = false;
                if (distanceThresholdExceeded) {
                    int perimeterVertexIdx = perimeterVertices.Count;
                    perimeterVertices.Add(vertices[vertC]);
                    perimeterVertices.Add(vertices[vertD]);
                    perimeterVertices.Add(vertices[vertB]);
                    perimeterUvs.Add(uvs[vertC]);
                    perimeterUvs.Add(uvs[vertD]);
                    perimeterUvs.Add(uvs[vertB]);
                    perimeterTriangles.Add(perimeterVertexIdx);
                    perimeterTriangles.Add(perimeterVertexIdx + 1);
                    perimeterTriangles.Add(perimeterVertexIdx + 2);
                } else {
                    triangles.Add(vertC);
                    triangles.Add(vertD);
                    triangles.Add(vertB);
                    numTrianglesGenerated += 1;
                }
            }
        }

        Debug.Log(numVerts);
        Debug.Log(numTrianglesGenerated);

        // Filter the mesh

        perimeterMesh.vertices = perimeterVertices.ToArray();
        perimeterMesh.uv = perimeterUvs.ToArray();
        perimeterMesh.triangles = perimeterTriangles.ToArray();
        perimeterMeshFilter.mesh = perimeterMesh;

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles.ToArray();
        meshFilter.mesh = mesh;

        // Add a material onto the mesh
        Material mat = new Material(Shader.Find("Unlit/Texture"));
        mat.SetTexture("_MainTex", colorImage);
        meshRenderer.material = mat;

        perimeterMeshRenderer.material = mat;

        yield return null;
    }

    IEnumerator Generate3DPhotoV2(Texture2D colorImage, Texture2D depthImage) {
        Texture2D readableDepthImage = GetReadableTexture(depthImage);

        GameObject obj = new GameObject("3D Photo");
        MeshFilter meshFilter = obj.AddComponent<MeshFilter>() as MeshFilter;
        MeshRenderer meshRenderer = obj.AddComponent<MeshRenderer>() as MeshRenderer;
        Mesh mesh = new Mesh();
        mesh.indexFormat = IndexFormat.UInt32;
        mesh_ = mesh;

        // Number of vertices on each side is equal to pixels + 1
        int width = readableDepthImage.width + 1;
        int height = readableDepthImage.height + 1;
        int numVerts = width * height;
        Vector3[] vertices = new Vector3[numVerts];
        Vector2[] uvs = new Vector2[numVerts];
        int numTrianglesPerRow = 2 * (width - 1);
        int numTriangles = numTrianglesPerRow * (height - 1);
        int[] triangles = new int[numTriangles * 3];

        Vector3 origin = Vector3.zero;
        Color32[] depthPixels = readableDepthImage.GetPixels32(0);
        float max = 0;
        // Create vertices
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                Vector2 uv = new Vector2(col / (float)(width), row / (float)(height)); // Range [0, 1]
                float depth = 1.0f - SampleTexture(uv, depthPixels, readableDepthImage.width, readableDepthImage.height).r; // 1 minus depth to flip it since we want white = closer
                Vector2 angles = new Vector2(degToRad((uv.x - 0.5f) * cameraHorizontalFov), degToRad((uv.y - 0.5f) * cameraVerticalFov)); 
                Vector3 viewingAngle = new Vector3((float)Math.Sin(angles.x), (float)Math.Sin(angles.y), (float)Math.Cos(angles.x));
                if (depth > max) {
                    max = depth;
                }

                // Simply just using depth as Z value
                // float depth = SampleTexture(uv, depthPixels, readableDepthImage.width, readableDepthImage.height).r;
                // Vector3 vertex = new Vector3(uv.x * 2 - 1, uv.y * 2 - 1, depth);

                // Project from virtual camera position out 
                Vector3 vertex = viewingAngle * (depth + 1);
                vertices[vertIdx] = vertex;
                uvs[vertIdx] = uv;
            }
        }

        Debug.Log(max);

        bool separateBackground = false;

        // Generate triangles
        int numTrianglesGenerated = 0;
        for (int col = 1; col < height; col++) {
            for (int row = 1; row < width; row++) {
                int vertIdx = row * width + col;
                int triangleIdx = numTrianglesGenerated * 3;
                int vertA = vertIdx - 1 - width;
                int vertB = vertIdx - width;
                int vertC = vertIdx - 1;
                int vertD = vertIdx;
                // Triangle ABC -> CBA
                float deltaAB = (vertices[vertA] - vertices[vertB]).magnitude;
                float deltaBC = (vertices[vertB] - vertices[vertC]).magnitude;
                float deltaAC = (vertices[vertA] - vertices[vertC]).magnitude;
                if (!separateBackground || deltaAB < deltaThreshold && deltaBC < deltaThreshold && deltaAC < deltaThreshold) {
                    triangles[triangleIdx] = vertC;
                    triangles[triangleIdx + 1] = vertB;
                    triangles[triangleIdx + 2] = vertA;
                    numTrianglesGenerated += 1;
                }
                // Triangle BDC -> CDB
                float deltaBD = (vertices[vertB] - vertices[vertD]).magnitude;
                float deltaDC = (vertices[vertD] - vertices[vertC]).magnitude;
                if (!separateBackground || deltaBD < deltaThreshold && deltaBC < deltaThreshold && deltaDC < deltaThreshold) {
                    triangles[triangleIdx + 3] = vertC;
                    triangles[triangleIdx + 4] = vertD;
                    triangles[triangleIdx + 5] = vertB;
                    numTrianglesGenerated += 1;
                }
            }
        }

        Debug.Log(numVerts);
        Debug.Log(numTrianglesGenerated);

        // Filter the mesh

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        meshFilter.mesh = mesh;

        // Add a material onto the mesh
        Material mat = new Material(Shader.Find("Unlit/Texture"));
        mat.SetTexture("_MainTex", colorImage);
        meshRenderer.material = mat;

        yield return null;
    }

    IEnumerator Generate3DPhotoV1(Texture2D colorImage, Texture2D depthImage) {
        Texture2D readableDepthImage = GetReadableTexture(depthImage);

        GameObject obj = new GameObject("3D Photo");
        MeshFilter meshFilter = obj.AddComponent<MeshFilter>() as MeshFilter;
        MeshRenderer meshRenderer = obj.AddComponent<MeshRenderer>() as MeshRenderer;
        Mesh mesh = new Mesh();
        mesh.indexFormat = IndexFormat.UInt32;
        mesh_ = mesh;

        // Number of vertices on each side is equal to pixels + 1
        int width = readableDepthImage.width + 1;
        int height = readableDepthImage.height + 1;
        int numVerts = width * height;
        Vector3[] vertices = new Vector3[numVerts];
        Vector2[] uvs = new Vector2[numVerts];
        int numTrianglesPerRow = 2 * (width - 1);
        int numTriangles = numTrianglesPerRow * (height - 1);
        int[] triangles = new int[numTriangles * 3];

        Vector3 origin = Vector3.zero;
        Color32[] depthPixels = readableDepthImage.GetPixels32(0);
        // Create vertices
        for (int row = 0; row < height; row++) {
            for (int col = 0; col < width; col++) {
                int vertIdx = row * width + col;
                Vector2 uv = new Vector2(col / (float)(width), row / (float)(height));
                float depth = 1.0f - SampleTexture(uv, depthPixels, readableDepthImage.width, readableDepthImage.height).r; // 1 minus depth to flip it since we want white = closer
                Vector3 vertex = new Vector3(uv.x * 2 - 1, uv.y * 2 - 1, depth);
                vertices[vertIdx] = vertex;
                uvs[vertIdx] = uv;
            }
        }

        // Generate triangles
        int numTrianglesGenerated = 0;
        for (int col = 1; col < height; col++) {
            for (int row = 1; row < width; row++) {
                int vertIdx = row * width + col;
                int triangleIdx = numTrianglesGenerated * 3;
                int vertA = vertIdx - 1 - width;
                int vertB = vertIdx - width;
                int vertC = vertIdx - 1;
                int vertD = vertIdx;
                // Triangle ABC -> CBA
                triangles[triangleIdx] = vertC;
                triangles[triangleIdx + 1] = vertB;
                triangles[triangleIdx + 2] = vertA;
                // Triangle BDC -> CDB
                triangles[triangleIdx + 3] = vertC;
                triangles[triangleIdx + 4] = vertD;
                triangles[triangleIdx + 5] = vertB;
                numTrianglesGenerated += 2;
            }
        }

        Debug.Log(numVerts);
        Debug.Log(numTrianglesGenerated);

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        meshFilter.mesh = mesh;

        // Add a material onto the mesh
        Material mat = new Material(Shader.Find("Unlit/Texture"));
        mat.SetTexture("_MainTex", colorImage);
        meshRenderer.material = mat;

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
}