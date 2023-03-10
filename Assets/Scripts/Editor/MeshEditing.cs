using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Unity.EditorCoroutines.Editor;
using UnityEngine;
using UnityEditor;

public class MeshEditing : EditorWindow {
    MeshFilter SourceMesh;
    MeshFilter TargetMesh;
    int MeshWidth;
    int MeshHeight;
    int LargestSimplifiedRegionSize = 256;
    float MaximumDeltaDistance = 0.025f;
    bool ShouldSkipBorderVertices = false;

    [MenuItem("Custom/Mesh Editing")]
    public static void OpenWindow() {
       GetWindow<MeshEditing>();
    }
 
    void OnEnable() {

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