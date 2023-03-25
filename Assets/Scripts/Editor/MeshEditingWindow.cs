using System.Collections;
using Unity.EditorCoroutines.Editor;
using UnityEngine;
using UnityEditor;

// Authored by Francis Ge: https://github.com/kumorikuma
// UnityEditor window with options for editing mesh.
// Accessed from toolbar: Custom -> Mesh Editing
public class MeshEditingWindow : EditorWindow {
    MeshFilter SourceMesh; // Mesh to edit and perform changes onto
    MeshFilter TargetMesh; // Mesh to use as data

    [MenuItem("Custom/Mesh Editing")]
    public static void OpenWindow() {
       GetWindow<MeshEditingWindow>();
    }

    void OnGUI() {
        SourceMesh = EditorGUILayout.ObjectField("Mesh", SourceMesh, typeof(MeshFilter), true) as MeshFilter;
        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Blendshape");
            GUILayout.BeginVertical("GroupBox");
                TargetMesh = EditorGUILayout.ObjectField("Target Mesh", TargetMesh, typeof(MeshFilter), true) as MeshFilter;
                if(GUILayout.Button("Add target mesh as blendshape")) {
                    this.StartCoroutine(AddMeshAsBlendshape(TargetMesh, SourceMesh));
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();
    }

    // Adds the selected mesh as a blendshape to the source mesh.
    // Only works if they have the same number of vertices.
    // Note: MeshRenderer using the Mesh needs to be changed to SkinnedMeshRenderer after.
    IEnumerator AddMeshAsBlendshape(MeshFilter sourceMesh, MeshFilter targetMesh) {
        Vector3[] sourceVerts = sourceMesh.mesh.vertices;
        Vector3[] targetVerts = targetMesh.mesh.vertices;
        if (sourceVerts.Length != targetVerts.Length) {
            Debug.LogError("Failed to add blendshape. Source mesh has different vertex count than target mesh.");
            yield break;
        }
        
        Vector3[] deltaVertices = new Vector3[sourceVerts.Length];
        for (int i = 0; i < deltaVertices.Length; i++) {
            deltaVertices[i] = sourceVerts[i] - targetVerts[i];
        }
        targetMesh.mesh.AddBlendShapeFrame("Blendshape", 100, deltaVertices, null, null);
        // Need to do this after adding blendshape.
        // See: https://forum.unity.com/threads/adding-new-blendshape-from-script-buggy-deformation-result-fixed.827187/ 
        targetMesh.mesh.RecalculateNormals();
        targetMesh.mesh.RecalculateTangents();
        yield return null;
    }
}