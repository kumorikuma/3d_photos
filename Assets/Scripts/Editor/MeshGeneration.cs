using System;
using System.Collections;
using System.Collections.Generic;
using Unity.EditorCoroutines.Editor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;

public class MeshGeneration : EditorWindow {
    Texture2D colorImage;
    Texture2D depthImage;
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
        colorImage = TextureField("Color", colorImage);
        depthImage = TextureField("Depth", depthImage);
        EditorGUILayout.EndHorizontal();

        // Exiftool can get the FOV if some metadata is still attached.
        // If only one number, usually refers to the diagnol FOV.
        // To caluculate the hFOV and vFov...
        cameraHorizontalFov = EditorGUILayout.Slider("Camera Horizontal FOV", cameraHorizontalFov, 30.0f, 120.0f);
        cameraVerticalFov = EditorGUILayout.Slider("Camera Vertical FOV", cameraVerticalFov, 30.0f, 120.0f);
        deltaThreshold = EditorGUILayout.Slider("Delta Threshold", deltaThreshold, 0, 1);

        if(GUILayout.Button("Generate V1")) {
            this.StartCoroutine(Generate3DPhotoV1(colorImage, depthImage));
        }

        if(GUILayout.Button("Generate V2")) {
            this.StartCoroutine(Generate3DPhotoV2(colorImage, depthImage));
        }
    }

    // See: https://support.unity.com/hc/en-us/articles/206486626-How-can-I-get-pixels-from-unreadable-textures-
    Texture2D GetReadableTexture(Texture2D texture) {
        // Create a temporary RenderTexture of the same size as the texture
        RenderTexture tmp = RenderTexture.GetTemporary( 
                            texture.width,
                            texture.height,
                            0,
                            RenderTextureFormat.Default,
                            RenderTextureReadWrite.Linear);

        // Blit the pixels on texture to the RenderTexture
        Graphics.Blit(texture, tmp);

        // Backup the currently set RenderTexture
        RenderTexture previous = RenderTexture.active;

        // Set the current RenderTexture to the temporary one we created
        RenderTexture.active = tmp;

        // Create a new readable Texture2D to copy the pixels to it
        Texture2D myTexture2D = new Texture2D(texture.width, texture.height);

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

        return new Color(r, g, b);
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

        // Generate triangles
        int numTrianglesGenerated = 0;
        for (int col = 1; col < height; col++) {
            for (int row = 1; row < width; row++) {
                int vertIdx = col * width + row;
                int triangleIdx = numTrianglesGenerated * 3;
                int vertA = vertIdx - 1 - width;
                int vertB = vertIdx - width;
                int vertC = vertIdx - 1;
                int vertD = vertIdx;
                // Triangle ABC -> CBA
                float deltaAB = (vertices[vertA] - vertices[vertB]).magnitude;
                float deltaBC = (vertices[vertB] - vertices[vertC]).magnitude;
                float deltaAC = (vertices[vertA] - vertices[vertC]).magnitude;
                if (deltaAB < deltaThreshold && deltaBC < deltaThreshold && deltaAC < deltaThreshold) {
                    triangles[triangleIdx] = vertC;
                    triangles[triangleIdx + 1] = vertB;
                    triangles[triangleIdx + 2] = vertA;
                    numTrianglesGenerated += 1;
                }
                // Triangle BDC -> CDB
                float deltaBD = (vertices[vertB] - vertices[vertD]).magnitude;
                float deltaDC = (vertices[vertD] - vertices[vertC]).magnitude;
                if (deltaBD < deltaThreshold && deltaBC < deltaThreshold && deltaDC < deltaThreshold) {
                    triangles[triangleIdx + 3] = vertC;
                    triangles[triangleIdx + 4] = vertD;
                    triangles[triangleIdx + 5] = vertB;
                    numTrianglesGenerated += 1;
                }
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
                int vertIdx = col * width + row;
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
}