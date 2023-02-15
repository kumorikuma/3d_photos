using System;
using System.Collections;
using System.Collections.Generic;
using Unity.EditorCoroutines.Editor;
using UnityEngine;
using UnityEditor;

public class SceneCameraControls : EditorWindow {
    public float AnimationLength = 2;
    public int FPS = 60;
    public float EndAngle = 180;
    public bool Boomerang = true;
    public bool OrbitClockwise = true;
    public bool ResetOnEnd = true;
    public float ZoomAmount = 2.0f;
    public float ResetCameraDistance = 2.5f;
    public Camera camera = null;
    public GameObject pivot = null;

    float t = 0.0f;
    double animationTime = 0;
    int framesRendered = 0;
    double previousTime = 0;
    int playbackDirection = 1;
    Quaternion originalRotation;
    float originalSize;
    Vector3 originalPivot;
    Vector3 originalCameraPosition;
    float originalCameraDistance;
    float fov;
    Quaternion lookRotation;

    [MenuItem("Custom/Scene Camera Controls")]
    public static void OpenWindow() {
       GetWindow<SceneCameraControls>();
    }
 
    void OnEnable() {
        // cache any data you need here.
        // if you want to persist values used in the inspector, you can use eg. EditorPrefs
        if (camera) {
            originalCameraPosition = camera.transform.position;
            originalCameraDistance = (pivot.transform.position - originalCameraPosition).magnitude;
            originalRotation = camera.transform.rotation;
            fov = camera.fieldOfView / 360.0f * 2 * Mathf.PI;
        }
    }
 
    void OnGUI() {
        //Draw things here. Same as custom inspectors, EditorGUILayout and GUILayout has most of the things you need
        AnimationLength = EditorGUILayout.FloatField("Animation Length (s)", AnimationLength);
        if (AnimationLength < 0) {
            AnimationLength = 0.01f;
        }

        FPS = EditorGUILayout.IntSlider("FPS", FPS, 1, 240);
        Boomerang = EditorGUILayout.Toggle("Boomerang", Boomerang);
        ResetOnEnd = EditorGUILayout.Toggle("Reset On End", ResetOnEnd);
        EndAngle = EditorGUILayout.Slider("End Angle", EndAngle, 0, 360);
        OrbitClockwise = EditorGUILayout.Toggle("Orbit Clockwise", OrbitClockwise);
        camera = EditorGUILayout.ObjectField("Camera Object", camera, typeof(Camera), true) as Camera;
        pivot = EditorGUILayout.ObjectField("Pivot", pivot, typeof(GameObject), true) as GameObject;

        if(GUILayout.Button("Demo Animation")) {
            AnimationLength = 4;
            Boomerang = true;
            EndAngle = 180;
            this.StartCoroutine(Animate(AnimationLength, Boomerang, OrbitAnimationUpdate, () => {
                AnimationLength = 1;
                Boomerang = false;
                ResetOnEnd = false;
                ZoomAmount = 2.0f;
                this.StartCoroutine(Animate(AnimationLength, Boomerang, ZoomAnimationUpdate, () => {
                    AnimationLength = 6;
                    Boomerang = false;
                    this.StartCoroutine(Animate(AnimationLength, Boomerang, LookAt2AnimationUpdate, () => {
                        AnimationLength = 1;
                        Boomerang = false;
                        ResetOnEnd = false;
                        ZoomAmount = -ZoomAmount;
                        this.StartCoroutine(Animate(AnimationLength, Boomerang, ZoomAnimationUpdate));
                    }));
                    
                }));
            }));
        }

        if(GUILayout.Button("Orbit Animation")) {
            this.StartCoroutine(Animate(AnimationLength, Boomerang, OrbitAnimationUpdate));
        }

        if(GUILayout.Button("Zoom Animation")) {
            this.StartCoroutine(Animate(AnimationLength, Boomerang, ZoomAnimationUpdate));
        }

        if(GUILayout.Button("Look Animation")) {
            lookRotation = Quaternion.LookRotation(Vector3.forward);
            this.StartCoroutine(Animate(1, false, LookAtAnimationUpdate, () => {
                lookRotation = Quaternion.LookRotation(Vector3.right + Vector3.forward);
                this.StartCoroutine(Animate(1, false, LookAtAnimationUpdate, () => {
                    lookRotation = Quaternion.LookRotation(Vector3.right + Vector3.up);
                    this.StartCoroutine(Animate(1, false, LookAtAnimationUpdate, () => {
                        lookRotation = Quaternion.LookRotation(Vector3.back);
                        this.StartCoroutine(Animate(1, false, LookAtAnimationUpdate, () => {
                            EndAngle = 180;
                            this.StartCoroutine(Animate(2, false, OrbitAnimationUpdate));
                        }));
                    }));
                }));
            }));
        }

        if(GUILayout.Button("Look2 Animation")) {
            this.StartCoroutine(Animate(AnimationLength, Boomerang, LookAt2AnimationUpdate));
        }

        if(GUILayout.Button("Reset Camera")) {
            if (camera) {
                camera.transform.rotation = originalRotation;
                camera.transform.position = originalCameraPosition;
            } else {
                fov = SceneView.lastActiveSceneView.camera.fieldOfView / 360.0f * 2 * Mathf.PI;
                SceneView.lastActiveSceneView.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
                SceneView.lastActiveSceneView.size = SizeFromCameraDistance(ResetCameraDistance, fov);
                SceneView.lastActiveSceneView.pivot = Vector3.zero;
                SceneView.lastActiveSceneView.Repaint();
            }
        }
    }

    // SceneView's camera must be set using SceneView's pivot, rotation, size.
    // Setting camera transform directly does not work, and AlignViewToObject is an async method.
    // See: https://forum.unity.com/threads/moving-scene-view-camera-from-editor-script.64920/
    // And: https://docs.unity3d.com/ScriptReference/SceneView-size.html

    void OrbitAnimationUpdate(float t) {
        float angle = EasingFunction.EaseInOutSine(0, 1, t) * EndAngle;
        if (!OrbitClockwise) {
            angle = 360 - angle;
        }
        angle = angle / 180.0f * Mathf.PI;

        if (camera) {
            camera.transform.position = originalCameraPosition + 0.1f * new Vector3((float)Math.Cos(angle) - 1, (float)Math.Sin(angle), 0);
            camera.transform.rotation = Quaternion.LookRotation(pivot.transform.position - camera.transform.position);
        } else {
            SceneView.lastActiveSceneView.pivot = Vector3.zero;
            SceneView.lastActiveSceneView.rotation = Quaternion.AngleAxis(angle, Vector3.up) * originalRotation;
            SceneView.lastActiveSceneView.size = SizeFromCameraDistance(originalCameraDistance, fov);
        }
    }

    void ZoomAnimationUpdate(float t) {
        float cameraDistance = EasingFunction.EaseInOutSine(originalCameraDistance, originalCameraDistance - ZoomAmount, t);

        if (camera) {
            camera.transform.position = cameraDistance * (pivot.transform.position - originalCameraPosition);
        } else {
            SceneView.lastActiveSceneView.pivot = Vector3.zero;
            SceneView.lastActiveSceneView.rotation = originalRotation;
            SceneView.lastActiveSceneView.size = SizeFromCameraDistance(cameraDistance, fov);
        }
    }

    void LookAtAnimationUpdate(float t) {
        t = EasingFunction.Linear(0, 1, t);
        SceneView.lastActiveSceneView.rotation = Quaternion.Slerp(originalRotation, lookRotation, t);
    }
    
    void LookAt2AnimationUpdate(float t) {
        t = EasingFunction.EaseInOutSine(0, 1, t);
        Vector3 lookatTarget = originalCameraPosition + originalRotation * (new Vector3(Mathf.Cos(t * 2 * Mathf.PI) - 1, Mathf.Sin(t * 2 * Mathf.PI), 1));
        SceneView.lastActiveSceneView.rotation = Quaternion.LookRotation((lookatTarget - originalCameraPosition));
    }

    // Enabling boomerang will playback the animation twice as fast, and reverse the animation after its complete.
    IEnumerator Animate(float duration, bool boomerang, Action<float> update, Action callback = null) {
        int numFrames = (int)(duration * FPS);
        float timePerFrame = 1.0f / FPS;

        // Init
        if (camera) {
            originalRotation = camera.transform.rotation;
            originalCameraPosition = camera.transform.position;
            originalCameraDistance = (pivot.transform.position - originalCameraPosition).magnitude;
            fov = camera.fieldOfView / 360.0f * 2 * Mathf.PI;
        } else {
            originalRotation = SceneView.lastActiveSceneView.rotation;
            originalSize = SceneView.lastActiveSceneView.size;
            originalPivot = SceneView.lastActiveSceneView.pivot;
            originalCameraPosition = SceneView.lastActiveSceneView.camera.transform.position;
            originalCameraDistance = (SceneView.lastActiveSceneView.camera.transform.position - originalPivot).magnitude;
            fov = SceneView.lastActiveSceneView.camera.fieldOfView / 360.0f * 2 * Mathf.PI;
        }

        previousTime = EditorApplication.timeSinceStartup;
        playbackDirection = 1;
        t = 0;
        animationTime = 0;
        framesRendered = 0;

        float stepSize = 1.0f / (numFrames - 1);
        if (boomerang) {
            stepSize *= 2;
        }

        while (framesRendered < numFrames) {
            double deltaTime = EditorApplication.timeSinceStartup - previousTime;
            previousTime = EditorApplication.timeSinceStartup;

            animationTime += deltaTime; // amount of time elapsed during this animation

            // If not enough time has elapsed, skip this update.
            if (animationTime < framesRendered * timePerFrame) {
                yield return null;
                continue;
            }

            update(t);

            // Advance time
            t += stepSize * playbackDirection;
            framesRendered += 1;
            if (playbackDirection > 0 && t > 1) {
                t = 1;
                if (boomerang) {
                    playbackDirection = -1;
                }
            } else if (playbackDirection < 0 && t < 0) {
                t = 0;
            }

            yield return new WaitForSeconds(timePerFrame);
        }

        if (ResetOnEnd) {
            // Reset camera
            if (camera) {
                camera.transform.rotation = originalRotation;
                camera.transform.position = originalCameraPosition;
            } else {
                SceneView.lastActiveSceneView.rotation = originalRotation;
                SceneView.lastActiveSceneView.size = originalSize;
                SceneView.lastActiveSceneView.pivot = originalPivot;
                SceneView.lastActiveSceneView.Repaint();
            }
        }

        if (callback != null) callback();
        yield return null;
    }

    float SizeFromCameraDistance(float cameraDistance, float fov) {
        return cameraDistance * Mathf.Sin(fov / 2.0f);
    }

    void Update() {

    }
}