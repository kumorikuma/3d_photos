using System;
using System.IO;
using System.Collections;
using Unity.EditorCoroutines.Editor;
using UnityEngine;
using UnityEditor;

// Authored by Francis Ge: https://github.com/kumorikuma
// UnityEditor Window that provides controls to animate both SceneView and scene cameras in Edit Mode.
// Kinda hacked together to generate videos for demonstrations on website.
public class SceneCameraControlsWindow : EditorWindow {
    // General animation options
    public float AnimationLength = 2;
    public int FPS = 60;
    public int Loops = 3;
    public bool Boomerang = true;
    public bool ResetOnEnd = true;
    // Recording options
    public bool SaveFrames = false;
    public bool SaveSceneView = false;
    // Specific animation options
    public float StartAngle = 0;
    public float EndAngle = 180;
    public bool OrbitClockwise = true;
    public float ZoomAmount = 0.5f;
    public float ResetCameraDistance = 2.5f;
    public float CircleLookRadius = 0.3f;
    // Scene references
    public Camera camera = null; // If null, will use the SceneView camera instead
    public GameObject pivot = null;
    public SkinnedMeshRenderer skinnedMeshRenderer = null;
    public SkinnedMeshRenderer skinnedMeshRenderer2 = null;
    public MeshRenderer materialRenderer = null;

    // Animation state
    float t = 0.0f; // Value between [0, 1] that indicates progress of animation
    double animationTime = 0;
    int framesRendered = 0;
    double previousTime = 0;
    int playbackDirection = 1;

    // Original state of scene
    Quaternion originalRotation;
    Quaternion originalPivotRotation;
    float originalSize;
    Vector3 originalPivot;
    Vector3 originalCameraPosition;
    float originalCameraDistance;
    float fov;
    Quaternion lookRotation;

    // Screen Recording
    Rect captureRect;
    RenderTexture renderTexture;
    Texture2D screenShot;

    [MenuItem("Custom/Scene Camera Controls")]
    public static void OpenWindow() {
       GetWindow<SceneCameraControlsWindow>();
    }
 
    void OnEnable() {
        camera = Camera.main;
        pivot = GameObject.Find("Pivot");
        // cache any data you need here.
        // if you want to persist values used in the inspector, you can use eg. EditorPrefs
        if (camera) {
            originalCameraPosition = camera.transform.position;
            originalCameraDistance = (pivot.transform.position - originalCameraPosition).magnitude;
            originalRotation = camera.transform.rotation;
            originalPivotRotation = pivot.transform.rotation;
            originalPivot = pivot.transform.position;
            fov = camera.fieldOfView / 360.0f * 2 * Mathf.PI;
        }
    }
 

    // configure with raw, jpg, png, or ppm (simple raw format)
    public enum Format { RAW, JPG, PNG, PPM };
    public Format format = Format.JPG;
    // create a unique filename using a one-up variable
    private string uniqueFilename()
    {
         // if folder not specified by now use a good default
        string folder = Application.dataPath;
        if (Application.isEditor)
        {
            // put screenshots in folder above asset path so unity doesn't index the files
            var stringPath = folder + "/..";
            folder = Path.GetFullPath(stringPath);
        }
        folder += "/Screenshots";

        // make sure directoroy exists
        System.IO.Directory.CreateDirectory(folder);

        // count number of files of specified format in folder
        // string mask = string.Format("screenshot*.{0}", format.ToString().ToLower());
        // counter = Directory.GetFiles(folder, mask, SearchOption.TopDirectoryOnly).Length;
 
        // use width, height, and counter for unique file name
        var filename = string.Format("{0}/screenshot_{1}.{2}", folder, framesRendered, format.ToString().ToLower());

        // up counter for next call
        // ++counter;

        // return unique filename
        return filename;
    }

    // You can compile these images into a video using ffmpeg:
    // ffmpeg -i screenshot_%d.jpg -y recording.mp4
    void SaveFrame() {
        // get camera and manually render scene into rt
        if (SaveSceneView) {
            SceneView.lastActiveSceneView.camera.targetTexture = renderTexture;
            SceneView.lastActiveSceneView.camera.Render();
        } else {
            camera.targetTexture = renderTexture;
            camera.Render();
        }

        // read pixels will read from the currently active render texture so make our offscreen 
        // render texture active and then read the pixels
        RenderTexture.active = renderTexture;
        screenShot.ReadPixels(captureRect, 0, 0);

        // reset active camera texture and render texture
        if (SaveSceneView) {
            SceneView.lastActiveSceneView.camera.targetTexture = null;
        } else {
            camera.targetTexture = null;
            RenderTexture.active = null;
        }

        // get our unique filename
        string filename = uniqueFilename();

        // pull in our file header/data bytes for the specified image format (has to be done from main thread)
        byte[] fileHeader = null;
        byte[] fileData = null;
        if (format == Format.RAW) {
            fileData = screenShot.GetRawTextureData();
        } else if (format == Format.PNG) {
            fileData = screenShot.EncodeToPNG();
        } else if (format == Format.JPG) {
            fileData = screenShot.EncodeToJPG();
        } else { // PPM
            // create a file header for ppm formatted file
            string headerStr = string.Format("P6\n{0} {1}\n255\n", captureRect.width, captureRect.height);
            fileHeader = System.Text.Encoding.ASCII.GetBytes(headerStr);
            fileData = screenShot.GetRawTextureData();
        }

        // create file and write optional header with image bytes
        var f = System.IO.File.Create(filename);
        if (fileHeader != null) f.Write(fileHeader, 0, fileHeader.Length);
        f.Write(fileData, 0, fileData.Length);
        f.Close();
        Debug.Log(string.Format("Wrote screenshot {0} of size {1}", filename, fileData.Length));
    }

    void OnGUI() {
        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Common Settings");
            GUILayout.BeginVertical("GroupBox");
                AnimationLength = EditorGUILayout.FloatField("Animation Length (s)", AnimationLength);
                if (AnimationLength < 0) {
                    AnimationLength = 0.01f;
                }
                FPS = EditorGUILayout.IntSlider("FPS", FPS, 1, 240);
                Loops = EditorGUILayout.IntSlider("# of Loops", Loops, 1, 10);
                SaveFrames = EditorGUILayout.Toggle("Save Frames", SaveFrames);
                SaveSceneView = EditorGUILayout.Toggle("Save Frames from Scene View", SaveSceneView);
                Boomerang = EditorGUILayout.Toggle("Boomerang", Boomerang);
                ResetOnEnd = EditorGUILayout.Toggle("Reset On End", ResetOnEnd);
            GUILayout.EndVertical();
        GUILayout.EndVertical();
        
        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Camera");
            GUILayout.BeginVertical("GroupBox");
                camera = EditorGUILayout.ObjectField("Camera Object", camera, typeof(Camera), true) as Camera;
                pivot = EditorGUILayout.ObjectField("Pivot", pivot, typeof(GameObject), true) as GameObject;
                if(GUILayout.Button("Reset Camera")) {
                    if (camera) {
                        camera.transform.rotation = originalRotation;
                        camera.transform.position = originalCameraPosition;
                        pivot.transform.rotation = originalPivotRotation;
                        pivot.transform.position = originalPivot;
                    } else {
                        fov = SceneView.lastActiveSceneView.camera.fieldOfView / 360.0f * 2 * Mathf.PI;
                        SceneView.lastActiveSceneView.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
                        SceneView.lastActiveSceneView.size = SizeFromCameraDistance(ResetCameraDistance, fov);
                        SceneView.lastActiveSceneView.pivot = Vector3.zero;
                        SceneView.lastActiveSceneView.Repaint();
                    }

                    if (skinnedMeshRenderer) {
                        skinnedMeshRenderer.SetBlendShapeWeight(0, 0);
                    }
                    if (skinnedMeshRenderer2) {
                        skinnedMeshRenderer2.SetBlendShapeWeight(0, 0);
                    }
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Blendshape Animation");
            GUILayout.BeginVertical("GroupBox");
                skinnedMeshRenderer = EditorGUILayout.ObjectField("Blendshape Object", skinnedMeshRenderer, typeof(SkinnedMeshRenderer), true) as SkinnedMeshRenderer;
                skinnedMeshRenderer2 = EditorGUILayout.ObjectField("Blendshape Object 2", skinnedMeshRenderer2, typeof(SkinnedMeshRenderer), true) as SkinnedMeshRenderer;
                if(GUILayout.Button("Animate Blend Shape")) {
                    this.StartCoroutine(Animate(AnimationLength, Boomerang, BlendshapeAnimationUpdate, null, Loops));
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Material Animation");
            GUILayout.BeginVertical("GroupBox");
                materialRenderer = EditorGUILayout.ObjectField("Material Object", materialRenderer, typeof(MeshRenderer), true) as MeshRenderer;
                // material = EditorGUILayout.ObjectField("Material Object", material, typeof(Material), true) as Material;
                if(GUILayout.Button("Animate Material 'Blend' Property")) {
                    this.StartCoroutine(Animate(AnimationLength, Boomerang, MaterialAnimationUpdate, null, Loops));
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Orbit Animation");
            GUILayout.BeginVertical("GroupBox");
                StartAngle = EditorGUILayout.Slider("Start Angle", StartAngle, -360, 360);
                EndAngle = EditorGUILayout.Slider("End Angle", EndAngle, -360, 360);
                OrbitClockwise = EditorGUILayout.Toggle("Orbit Clockwise", OrbitClockwise);
                if(GUILayout.Button("Orbit Animation")) {
                    this.StartCoroutine(Animate(AnimationLength, Boomerang, OrbitAnimationUpdate, null, Loops));
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("CircleLook Animation");
            GUILayout.BeginVertical("GroupBox");
                CircleLookRadius = EditorGUILayout.Slider("CircleLookRadius", CircleLookRadius, 0, 2.0f);
                if(GUILayout.Button("CircleLook Animation")) {
                    this.StartCoroutine(Animate(AnimationLength, Boomerang, CircleLookAnimationUpdate, null, Loops));
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("HelpBox");
            GUILayout.Label("Blendshape Animation");
            GUILayout.BeginVertical("GroupBox");
                ZoomAmount = EditorGUILayout.Slider("Zoom Amount", ZoomAmount, 0, 10.0f);
                if(GUILayout.Button("Zoom Animation")) {
                    this.StartCoroutine(Animate(AnimationLength, Boomerang, ZoomAnimationUpdate, null, Loops));
                }
            GUILayout.EndVertical();
        GUILayout.EndVertical();
    }

    // SceneView's camera must be set using SceneView's pivot, rotation, size.
    // Setting camera transform directly does not work, and AlignViewToObject is an async method.
    // See: https://forum.unity.com/threads/moving-scene-view-camera-from-editor-script.64920/
    // And: https://docs.unity3d.com/ScriptReference/SceneView-size.html

    void BlendshapeAnimationUpdate(float t) {
        float modifiedT = EasingFunction.EaseInOutSine(0, 1, t);

        if (skinnedMeshRenderer) {
            skinnedMeshRenderer.SetBlendShapeWeight(0, modifiedT * 100.0f);
        }

        if (skinnedMeshRenderer2) {
            skinnedMeshRenderer2.SetBlendShapeWeight(0, modifiedT * 100.0f);
        }
    }

    void MaterialAnimationUpdate(float t) {
        float modifiedT = EasingFunction.EaseInOutSine(0, 1, t);

        if (materialRenderer) {
            materialRenderer.sharedMaterial.SetFloat("_Blend", modifiedT);
        }
    }

    void OrbitAnimationUpdate(float t) {
        float angle = EasingFunction.EaseInOutSine(0, 1, t) * EndAngle;
        if (!OrbitClockwise) {
            angle = 360 - angle;
        }

        if (camera) {
            pivot.transform.rotation = Quaternion.AngleAxis(angle, Vector3.up) * originalRotation;
        } else {
            SceneView.lastActiveSceneView.pivot = Vector3.zero;
            SceneView.lastActiveSceneView.rotation = Quaternion.AngleAxis(angle, Vector3.up) * originalRotation;
            SceneView.lastActiveSceneView.size = SizeFromCameraDistance(originalCameraDistance, fov);
        }
    }

    void CircleLookAnimationUpdate(float t) {
        float angle = EasingFunction.EaseInOutSine(0, 1, t) * EndAngle;
        if (!OrbitClockwise) {
            angle = 360 - angle;
        }

        if (camera) {
            angle = angle / 180.0f * Mathf.PI;
            camera.transform.position = originalCameraPosition + CircleLookRadius * new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
            camera.transform.rotation = Quaternion.LookRotation(pivot.transform.position - camera.transform.position);
        }
    }

    void ZoomAnimationUpdate(float t) {
        float cameraDistance = EasingFunction.EaseInOutSine(originalCameraDistance, originalCameraDistance - ZoomAmount, t);

        if (camera) {
            camera.transform.position = pivot.transform.position + (originalCameraPosition - pivot.transform.position).normalized * cameraDistance;
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

    IEnumerator RepeatFor(int loops, IEnumerator coroutine) {
        int loopCount = 0;
        while (loopCount < loops) {
            Debug.Log("Loop: " + loopCount);
            yield return this.StartCoroutine(coroutine);
            loopCount++;
        }

        yield return null;
    }

    // Enabling boomerang will playback the animation twice as fast, and reverse the animation after its complete.
    IEnumerator Animate(float duration, bool boomerang, Action<float> update, Action callback = null, int loops = 0) {
        int loopCount = 0;
        while (loopCount < loops) {
            int numFrames = (int)(duration * FPS);
            float timePerFrame = 1.0f / FPS;

            // Init
            if (camera) {
                originalRotation = camera.transform.rotation;
                originalPivot = pivot.transform.position;
                originalPivotRotation = pivot.transform.rotation;
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

            // Prep screen recording
            // creates off-screen render texture that can rendered into
            captureRect = camera.pixelRect;
            int captureWidth = (int)captureRect.width;
            int captureHeight = (int)captureRect.height;
            renderTexture = new RenderTexture(captureWidth, captureHeight, 24);
            screenShot = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);

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

                // Save screenshot if needed
                if (SaveFrames) {
                    SaveFrame();
                }

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
            loopCount++;
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