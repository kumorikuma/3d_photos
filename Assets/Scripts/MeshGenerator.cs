using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

// Generates 3D Photos from images. Requires:
// - Image
// - Depth Map for Image
// - Separate Foreground for Image
// Code used for demonstration at: https://kumorikuma.dev/3d_photos/
// 
// Potential further work (TODO):
// - Tatebanko generation (multiple layers)
// - Elaborate on "double circle obstruction" problem
public class MeshGenerator {
    public struct Setting<ValueType> {
        public ValueType value;
        public string tooltip;

        public Setting(ValueType _value, string _tooltip) {
            value = _value;
            tooltip = _tooltip;
        }
    }

    public struct Settings {
        public Setting<bool> ProjectFromOrigin;
        public Setting<bool> ConvertDepthValuesFromDisparity;
        public Setting<bool> RemoveOutliers;
        public Setting<bool> RunMeshSmoothingFilter;
        public Setting<bool> SmoothForegroundEdges;
        public Setting<bool> SeparateFgBg;
        public Setting<bool> GenerateForegroundMesh;
        public Setting<bool> GenerateBackgroundMesh;
        public Setting<bool> GenerateInpaintedRegion;
        public Setting<bool> GenerateOutpaintedRegion;
        public Setting<bool> PerformMeshSimplification;
        public Setting<int> LargestSimplifiedRegionSize;
        public Setting<float> MaximumDeltaDistance;
        public Setting<bool> ForegroundFeathering;
        public Setting<float> MaxDepth;
        public Setting<float> MaxDistance;
        public Setting<bool> OverrideDepth;
        public Setting<float> DepthOverride;

        // Most settings should be left on default. 
        // This is mostly here to allow tweaking and playing with the algorithm, generating different visualizations.
        public static Settings DefaultSettings() {
            Settings defaultSettings = new Settings();

            defaultSettings.ProjectFromOrigin = new Setting<bool>(true, "Project out vertices using perspective projection instead of using depth as Z value");
            defaultSettings.ConvertDepthValuesFromDisparity = new Setting<bool>(true, "Input depth map is stored as a disparity map, convert disparity to depth before using");
            defaultSettings.RemoveOutliers = new Setting<bool>(true, "Remove outliers in mesh depth values using median filtering");
            defaultSettings.RunMeshSmoothingFilter = new Setting<bool>(true, "Smooths out mesh using averaging filter over depth values of vertices");
            defaultSettings.SmoothForegroundEdges = new Setting<bool>(true, "Smooth jagged edges of foreground mesh by averaging absolute XYZ coordinates");
            defaultSettings.SeparateFgBg = new Setting<bool>(true, "Separate foreground and background meshes as opposed to generating one big mesh");
            defaultSettings.GenerateForegroundMesh = new Setting<bool>(true, "Generate the foreground mesh?");
            defaultSettings.GenerateBackgroundMesh = new Setting<bool>(true, "Generate the background mesh?");
            defaultSettings.GenerateInpaintedRegion = new Setting<bool>(true, "Fill in the gaps in the background mesh");
            defaultSettings.GenerateOutpaintedRegion = new Setting<bool>(true, "Extend the background mesh outwards to extend FOV");
            defaultSettings.PerformMeshSimplification = new Setting<bool>(true, "Reduces number of triangles and vertices using mesh simplification");
            defaultSettings.LargestSimplifiedRegionSize = new Setting<int>(256, "Puts a limit on the amount of simplification done by specifying a bound for the size of the region being simplified. Increasing it will decrease the density of the mesh.");
            defaultSettings.MaximumDeltaDistance = new Setting<float>(0.025f, "How much difference in depth value before being considered flat enough for simplification");
            defaultSettings.ForegroundFeathering = new Setting<bool>(true, "Use alpha feathering on foreground mesh to soften edges");
            defaultSettings.MaxDepth = new Setting<float>(1f, "This value affects how flat the foreground ends up being");
            defaultSettings.MaxDistance = new Setting<float>(1f, "This value affects how much difference there is between fg and bg, or how much 'depth' there is in the scene. Increasing it may increase visual effect but introduce warping.");
            defaultSettings.OverrideDepth = new Setting<bool>(false, "Override the depth map with a constant value");
            defaultSettings.DepthOverride = new Setting<float>(1f, "Override the depth map with a constant value");

            return defaultSettings;
        }
    }

    // Hack: For mesh simplification animation.
    // Cache intermediate outputs of algorithm in order to animate later for screen recording.
    public struct CachedDenseMeshData {
        public bool[] bgVertexMask;
        public bool[] extendedBgVertexMask;
        public MeshFilter fgMesh;
        public MeshFilter bgMesh;
        public int width;
        public int height;
        public int extendedBgWidth;
        public int extendedBgHeight;
    }

    // Spawns a GameObject in the scene "3D Photo" with a 3D photo mesh.
    public static CachedDenseMeshData Generate3DPhoto(string identifier, Texture2D _colorImage, Texture2D _depthImage, Texture2D _foregroundImage, float imageHFov, float imageVFov, Settings settings) {
        CachedDenseMeshData cachedMeshData = new CachedDenseMeshData();
        bool UseDistanceThresholding = false;

        // When importing a texture, "Read/Write" is disabled.
        // This creates a copy of the texture so user doesn't need to worry about it.
        Texture2D depthImage = Utils.GetReadableTexture(_depthImage, true);
        Texture2D fgImage = Utils.GetReadableTexture(_foregroundImage, false);
        Texture2D colorImage = Utils.GetReadableTexture(_colorImage, false);

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
                bool isBackground = Utils.SampleTexture(uv, fgPixels, fgImage.width, fgImage.height).a < 0.01f;
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
                float disparityDepth = Utils.SampleTexture(uv, depthPixels, depthImage.width, depthImage.height).r;
                if (settings.OverrideDepth.value) {
                    disparityDepth = settings.DepthOverride.value;
                }
                float depth = disparityDepth;
                if (settings.ConvertDepthValuesFromDisparity.value) {
                    // Apply a conversion from disparity depth to actual z value
                    // Change in foreground has little effect. Change in background has a lot of effect.
                    float maxDepth = settings.MaxDepth.value;
                    depth = 1 / (disparityDepth + (1 / maxDepth)); // Range [0.5, MaxDepth]
                    depth = (depth - 0.5f) / (maxDepth - 0.5f) * settings.MaxDistance.value; // Range [0.0, MaxDistance]
                } else {
                    depth = 1 - depth;
                }

                bool isBackground;
                if (UseDistanceThresholding) {
                    isBackground = true;
                    // TODO:
                } else {
                    // Any transparent pixels in the foreground image are considered part of the background.
                    isBackground = Utils.SampleTexture(uv, fgPixels, fgImage.width, fgImage.height).a < 0.01f;
                }

                const float DEPTH_OFFSET = 1;
                Vector3 vertex;
                if (settings.ProjectFromOrigin.value) {
                    // Project from the virtual camera position out in a direction. Use the depth as the distance along this projection.
                    Vector2 angles = new Vector2((uv.x - 0.5f) * imageHFov * Mathf.Deg2Rad, (uv.y - 0.5f) * imageVFov * Mathf.Deg2Rad); 
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
        if (settings.RemoveOutliers.value) {
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
                if (!settings.SeparateFgBg.value) {
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
        if (settings.RemoveOutliers.value) {
            // Run a median filter to remove any outliers
            vertices = FilterVertices(vertices, bgVertexMask, false, width, height, 4, true); // Background
            vertices = FilterVertices(vertices, bgVertexMask, true, width, height, 8, true); // Foreground
            vertices = FilterVertices(vertices, bgVertexMask, true, width, height, 8, true); // Foreground
        }
        if (settings.RunMeshSmoothingFilter.value) {
            // Then run an averaging (or blurring) filter to smooth out the mesh
            vertices = FilterVertices(vertices, bgVertexMask, false, width, height, 8); // Background
            vertices = FilterVertices(vertices, bgVertexMask, true, width, height, 8); // Foreground
        }
        if (settings.SmoothForegroundEdges.value) {
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
        if (!settings.GenerateOutpaintedRegion.value) {
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
                    colors[newVertIdx] = Utils.SampleTexture(uvs[originalVertIdx], colorImagePixels, colorImage.width, colorImage.height);

                    // Add any original vertices from the background to the new buffers.
                    if (bgVertexMask[originalVertIdx]) {
                        extendedBgVerts[newVertIdx] = vertices[originalVertIdx];
                        extendedBgUVs[newVertIdx] = uv;
                        extendedBgVertexMask[newVertIdx] = true;
                        continue;
                    }

                    // Skip generating the inpainted region if desired
                    if (!settings.GenerateInpaintedRegion.value) {
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
                    float degreesPerPixelHFov = imageHFov / width;
                    float degreesPerPixelVFov = imageVFov / height;
                    Vector2 angles;
                    if (col < originalStartColumn) {
                        angles.x = (originalStartColumn - col) * -degreesPerPixelHFov - imageHFov / 2;
                    } else {
                        angles.x = (col - originalEndColumn) * degreesPerPixelHFov + imageHFov / 2;
                    }
                    if (row < originalStartRow) {
                        angles.y = (originalStartRow - row) * -degreesPerPixelVFov - imageVFov / 2;
                    } else {
                        angles.y = (row - originalEndRow) * degreesPerPixelVFov + imageVFov / 2;
                    }
                    Vector3 viewingDirection = (new Vector3((float)Math.Sin(angles.x * Mathf.Deg2Rad), (float)Math.Sin(angles.y * Mathf.Deg2Rad), (float)Math.Cos(angles.x * Mathf.Deg2Rad))).normalized;

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
        string filename = "Assets/Resources/"+identifier+"_extendedBgTexture.png";
        File.WriteAllBytes(filename, extendedBgTexture.EncodeToPNG());
        AssetDatabase.ImportAsset(filename);
        extendedBgTexture = Resources.Load<Texture2D>(identifier+"_extendedBgTexture");

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
        if (settings.ForegroundFeathering.value) {
            fgMat = new Material(Shader.Find("Unlit/FeatherShader"));
            fgMat.SetTexture("_MainTex", _colorImage);
            fgMat.SetTexture("_FeatherTex", _foregroundImage);
            fgMat.renderQueue = 3000; // For transparency
            AssetDatabase.CreateAsset(fgMat, "Assets/Resources/"+identifier+"_fgMaterial.mat");
        }

        if (!settings.SeparateFgBg.value) {
            // This results in severe artifacting, only used for demonstration purposes
            Utils.SpawnMesh("Mesh", vertices, uvs, allTriangles.ToArray(), _colorImage, identifier, rootObj.transform);
        } else {
            // Simplify the foreground mesh and generate output
            if (settings.GenerateForegroundMesh.value) {
                if (settings.PerformMeshSimplification.value) {
                    Vector3[] fgVertsSimplified; Vector2[] fgUvsSimplified; int[] fgTrianglesSimplified;
                    const bool SkipBorderVertices = true;
                    MeshSimplification.SimplifyGridMesh(vertices, uvs, fgTriangles.ToArray(), bgVertexMask, false, width, height, out fgVertsSimplified, out fgUvsSimplified, out fgTrianglesSimplified, settings, SkipBorderVertices);
                    Utils.SpawnMesh("Foreground", fgVertsSimplified, fgUvsSimplified, fgTrianglesSimplified, _colorImage, identifier, rootObj.transform, fgMat);
                    Debug.Log("Foreground Mesh Vert #: " + fgVertsSimplified.Length);
                    Debug.Log("Foreground Mesh Tri #: " + fgTrianglesSimplified.Length);
                } else {
                    cachedMeshData.fgMesh = Utils.SpawnMesh("Foreground", vertices, uvs, fgTriangles.ToArray(), _colorImage, identifier, rootObj.transform, fgMat);
                    Debug.Log("Foreground Mesh Vert #: " + vertices.Length);
                    Debug.Log("Foreground Mesh Tri #: " + fgTriangles.Count);
                }
            }

            // Simplify the extended background mesh and generate output
            if (settings.GenerateBackgroundMesh.value) {
                if (settings.PerformMeshSimplification.value) {
                    Vector3[] extBgVertsSimplified; Vector2[] extBgUvsSimplified; int[] extBgTrianglesSimplified;
                    MeshSimplification.SimplifyGridMesh(extendedBgVerts, extendedBgUVs, extendedBgTriangles.ToArray(), extendedBgVertexMask, true, extendedBgWidth, extendedBgHeight, out extBgVertsSimplified, out extBgUvsSimplified, out extBgTrianglesSimplified, settings);
                    Utils.SpawnMesh("Background", extBgVertsSimplified, extBgUvsSimplified, extBgTrianglesSimplified, extendedBgTexture, identifier, rootObj.transform);
                    Debug.Log("Background Mesh Vert #: " + extBgVertsSimplified.Length);
                    Debug.Log("Background Mesh Tri #: " + extBgTrianglesSimplified.Length);
                } else {
                    cachedMeshData.bgMesh = Utils.SpawnMesh("Background", extendedBgVerts, extendedBgUVs, extendedBgTriangles.ToArray(), extendedBgTexture, identifier, rootObj.transform);
                    Debug.Log("Background Mesh Vert #: " + extendedBgVerts.Length);
                    Debug.Log("Background Mesh Tri #: " + extendedBgTriangles.Count);
                }
            }
        }

        // For mesh simplification animation
        cachedMeshData.bgVertexMask = bgVertexMask;
        cachedMeshData.width = width;
        cachedMeshData.height = height;
        cachedMeshData.extendedBgVertexMask = extendedBgVertexMask;
        cachedMeshData.extendedBgWidth = extendedBgWidth;
        cachedMeshData.extendedBgHeight = extendedBgHeight;
        return cachedMeshData;
    }

    // Runs either an averaging filter or median filter on the grid of vertices provided.
    // - Vertices to be a 2D array of width * height.
    // - VertexMask can specify vertices to ignore. Default is false to ignore.
    // - invertMask flips the values of the VertexMask: true is now ignore.
    // - shouldSkipBorderVertices:
    //   - Do not perform simplification on border vertices (vertices that have a neighbor with a different vertexMask value).
    //   - Should be set to true when processing foreground mesh to not distort the silhouette.
    static Vector3[] FilterVertices(Vector3[] vertices, bool[] vertexMask, bool invertMask, int width, int height, int filterRadius = 4, bool useMedianInsteadOfAverage = false) {
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

    // Fixes problem with foreground mesh edges looking jagged by averaging the XYZ positions.
    // - vertices is vertex grid of size width*height
    // - isBg is an array of size width*height that is set to true if that vertex in the grid.
    // - useMedianInsteadOfAverage: by default does average filter, but if true will do median filtering.
    static Vector3[] FilterFgBorderVertices(Vector3[] vertices, bool[] isBg, int width, int height, int filterRadius = 1, bool useMedianInsteadOfAverage = false) {
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

    // Determines if a foreground vertex is bordering a background vertex in the vertex grid.
    // - isBg is an array of size width*height that is set to true if that vertex in the grid.
    public static bool IsFgVertexOnBorder(int row, int col, int width, int height, bool[] isBg) {
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
}