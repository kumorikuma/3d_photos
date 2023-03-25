using System.Collections;
using UnityEngine;
using UnityEditor;
using Unity.EditorCoroutines.Editor;

// Authored by Francis Ge: https://github.com/kumorikuma
// UnityEditor Window for generating 3D Photos from images.
// Code used for demonstration at: https://kumorikuma.dev/3d_photos/
// Access from toolbar: Custom -> Mesh Generation
public class MeshGeneration : EditorWindow {
    // Algorithm Inputs
    Texture2D ColorImage;
    Texture2D DepthImage; // Monochrome (grayscale) image where each pixel represents the depth value of that pixel
    Texture2D ForegroundImage; // RGBA image with just the foreground set to opaque, and background is transparent
    // Field of view that the photo was taken in.
    // Viewing FOV doesn't need to match this (depends on the viewing experience).
    float cameraHorizontalFov = 45.0f;
    float cameraVerticalFov = 58.0f;
    // Algorithm settings
    MeshGenerator.Settings settings = MeshGenerator.Settings.DefaultSettings();

    // Hack: For mesh simplification animation.
    MeshGenerator.CachedDenseMeshData __cachedMeshData;

    [MenuItem("Custom/Mesh Generation")]
    public static void OpenWindow() {
       GetWindow<MeshGeneration>();
    }
 
    void OnEnable() {
        // When window is opened, try to load default images
        if (ColorImage == null && DepthImage == null && ForegroundImage == null) {
            ColorImage = Resources.Load<Texture2D>("Images/shiba");
            DepthImage = Resources.Load<Texture2D>("Images/shiba_depth");
            ForegroundImage = Resources.Load<Texture2D>("Images/shiba_foreground");
        }
    }

    void OnGUI() {
        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Inputs");
            GUILayout.BeginVertical("GroupBox");
                EditorGUILayout.BeginHorizontal();
                ColorImage = EditorUIComponents.TextureField("Color", ColorImage);
                DepthImage = EditorUIComponents.TextureField("Depth", DepthImage);
                ForegroundImage = EditorUIComponents.TextureField("Foreground", ForegroundImage);
                EditorGUILayout.EndHorizontal();
            cameraHorizontalFov = EditorGUILayout.Slider("Camera Horizontal FOV", cameraHorizontalFov, 30.0f, 120.0f);
            cameraVerticalFov = EditorGUILayout.Slider("Camera Vertical FOV", cameraVerticalFov, 30.0f, 120.0f);
            if(GUILayout.Button("Generate 3D Photo")) {
                this.StartCoroutine(Generate3DPhoto());
            }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Algorithm Tweaks");
            GUILayout.BeginVertical("GroupBox");
                settings.ProjectFromOrigin.value = EditorGUILayout.Toggle(new GUIContent("Project Mesh from Origin", settings.ProjectFromOrigin.tooltip), settings.ProjectFromOrigin.value);
                settings.ConvertDepthValuesFromDisparity.value = EditorGUILayout.Toggle(new GUIContent("Depth stored as Disparity", settings.ConvertDepthValuesFromDisparity.tooltip), settings.ConvertDepthValuesFromDisparity.value);
                settings.MaxDepth.value = EditorGUILayout.Slider(new GUIContent("Foreground Flatness", settings.MaxDepth.tooltip), settings.MaxDepth.value, 1, 20);
                settings.MaxDistance.value = EditorGUILayout.Slider(new GUIContent("Maximum Distance", settings.MaxDistance.tooltip), settings.MaxDistance.value, 1, 20);                
                settings.RemoveOutliers.value = EditorGUILayout.Toggle(new GUIContent("Remove Outliers", settings.RemoveOutliers.tooltip), settings.RemoveOutliers.value);
                settings.RunMeshSmoothingFilter.value = EditorGUILayout.Toggle(new GUIContent("Smooth Mesh", settings.RunMeshSmoothingFilter.tooltip), settings.RunMeshSmoothingFilter.value);
                settings.SmoothForegroundEdges.value = EditorGUILayout.Toggle(new GUIContent("Smooth Foreground Edges", settings.SmoothForegroundEdges.tooltip), settings.SmoothForegroundEdges.value);
                settings.ForegroundFeathering.value = EditorGUILayout.Toggle(new GUIContent("Feather Foreground Edges", settings.ForegroundFeathering.tooltip), settings.ForegroundFeathering.value);
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Mesh Generation Options");
            GUILayout.BeginVertical("GroupBox");
                settings.SeparateFgBg.value = EditorGUILayout.Toggle(new GUIContent("Separate FG/BG", settings.SeparateFgBg.tooltip), settings.SeparateFgBg.value);
                settings.GenerateForegroundMesh.value = EditorGUILayout.Toggle(new GUIContent("Generate Foreground", settings.GenerateForegroundMesh.tooltip), settings.GenerateForegroundMesh.value);
                settings.GenerateBackgroundMesh.value = EditorGUILayout.Toggle(new GUIContent("Generate Background", settings.GenerateBackgroundMesh.tooltip), settings.GenerateBackgroundMesh.value);
                settings.GenerateInpaintedRegion.value = EditorGUILayout.Toggle(new GUIContent("Fill Occluded Regions", settings.GenerateInpaintedRegion.tooltip), settings.GenerateInpaintedRegion.value);
                settings.GenerateOutpaintedRegion.value = EditorGUILayout.Toggle(new GUIContent("Extend Mesh Outwards", settings.GenerateOutpaintedRegion.tooltip), settings.GenerateOutpaintedRegion.value);
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Mesh Simplification Options");
            GUILayout.BeginVertical("GroupBox");
                settings.PerformMeshSimplification.value = EditorGUILayout.Toggle(new GUIContent("Simplify Mesh", settings.PerformMeshSimplification.tooltip), settings.PerformMeshSimplification.value);
                settings.LargestSimplifiedRegionSize.value = EditorGUILayout.IntSlider(new GUIContent("Largest Region Size", settings.LargestSimplifiedRegionSize.tooltip), settings.LargestSimplifiedRegionSize.value, 128, 1024);
                settings.MaximumDeltaDistance.value = EditorGUILayout.Slider(new GUIContent("Maximum Delta Distance", settings.MaximumDeltaDistance.tooltip), settings.MaximumDeltaDistance.value, 0, 0.1f);
                settings.OverrideDepth.value = EditorGUILayout.Toggle(new GUIContent("Override Depth (debug)", settings.OverrideDepth.tooltip), settings.OverrideDepth.value);
                if (settings.OverrideDepth.value) {
                    settings.DepthOverride.value = EditorGUILayout.Slider(new GUIContent("Depth Override", settings.DepthOverride.tooltip), settings.DepthOverride.value, 0, 1);
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Demo Animations");
            GUILayout.BeginVertical("GroupBox");
                GUILayout.Label("This animation requires first generating a 3D Photo with 'Simplify Mesh' turned off.");
                if(GUILayout.Button("Simplify Last Generated Mesh")) {
                    this.StartCoroutine(SimplifyMeshAnimation());
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();
    }

    IEnumerator Generate3DPhoto() {
        string identifier = System.Guid.NewGuid().ToString();

        __cachedMeshData = MeshGenerator.Generate3DPhoto(identifier, ColorImage, DepthImage, ForegroundImage, cameraHorizontalFov, cameraVerticalFov, settings);

        yield return null;
    }

    IEnumerator SimplifyMeshAnimation(
    ) {
        this.StartCoroutine(MeshSimplificationIncremental.SimplifyMeshAnimation(this, settings, __cachedMeshData));   
        yield return null;
    }
}