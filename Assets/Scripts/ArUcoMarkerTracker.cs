using Meta.XR;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using System;
using Unity.VisualScripting;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(MeshRenderer))]
public class ArUcoMarkerTracker : MonoBehaviour
{
    [Header("Camera Access")]
    [SerializeField] private PassthroughCameraAccess cameraAccess; 

    [Header("ArUco Settings")]
    [SerializeField] private float markerSize = 0.18f;
    [SerializeField] private int targetMarkerId = 0;

    [Header("Tracking Settings")]
    [SerializeField] private float cubeHeightOffset = 0.05f;
    [SerializeField] private float positionLerp = 0.3f;
    [SerializeField] private float rotationLerp = 0.3f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;
    [SerializeField] private RawImage debugDisplay;

    // OpenCV objects
    private Mat rgbMat;
    private Mat grayMat;
    private Dictionary dictionary;
    private DetectorParameters detectorParams;
    private Mat cameraMatrix;
    private Mat distCoeffs;
    private Mat markerObjMat;   // 3D corners of one marker (for SolvePnP)

    // Processing
    private Texture2D processingTexture;
    private byte[] textureBytes; // Reused buffer for Texture2D → Mat copy

    // Tracking state
    private bool isTracking = false;
    private Vector3 targetPosition;
    private Quaternion targetRotation;

        private bool cameraMatrixReady = false;
    void Start()
    {
        InitializeOpenCV();
        InitializeCameraMatrix();
    }

    void InitializeOpenCV()
    {
        rgbMat = new Mat();
        grayMat = new Mat();

        dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
        detectorParams = DetectorParameters.Create();

        if (showDebugLogs) Debug.Log("[ArUco] OpenCvSharp initialized");

        // Pre-compute 3D object points for a single ArUco marker (square in XY plane, Z=0)
        float h = markerSize * 0.5f;
        Point3f[] objPts = new Point3f[]
        {
            new Point3f(-h,  h, 0f),
            new Point3f( h,  h, 0f),
            new Point3f( h, -h, 0f),
            new Point3f(-h, -h, 0f)
        };
        // Create Mat header directly over the blittable struct array (OpenCvSharp copies/pins internally)
        markerObjMat = new Mat(4, 1, MatType.CV_32FC3, objPts).Clone();
    }

    void InitializeCameraMatrix()
    {
        if (cameraAccess == null)
        {
            Debug.LogError("[ArUco] CameraAccess not assigned! Add MRUK and assign PassthroughCameraAccess.");
            return;
        }

        var intrinsics = cameraAccess.Intrinsics;

        // Build camera matrix from a flat double array (avoids Mat.Set ambiguity)
        double[] camData = new double[]
        {
            intrinsics.FocalLength.x, 0.0,                      intrinsics.PrincipalPoint.x,
            0.0,                      intrinsics.FocalLength.y, intrinsics.PrincipalPoint.y,
            0.0,                      0.0,                      1.0
        };
        cameraMatrix = new Mat(3, 3, MatType.CV_64FC1, camData).Clone();

        // Quest cameras are well-calibrated; distortion is near zero
        double[] distData = new double[] { 0, 0, 0, 0, 0 };
        distCoeffs = new Mat(1, 5, MatType.CV_64FC1, distData).Clone();

        if (showDebugLogs)
        {
            Debug.Log($"[ArUco] Camera ready. Res: {cameraAccess.CurrentResolution}, " +
                      $"Focal: {intrinsics.FocalLength}, Principal: {intrinsics.PrincipalPoint}");
        }
    }

    void Update()
    {
        MeshRenderer mesh = GetComponent<MeshRenderer>();

        if (cameraAccess == null || !cameraAccess.enabled)
        {
            if (mesh != null && mesh.enabled)
                mesh.enabled = false;

            isTracking = false;
            return;
        }

        Texture cameraTexture = cameraAccess.GetTexture();
        if (cameraTexture == null)
        {
            if (showDebugLogs && Time.frameCount % 120 == 0)
                Debug.LogWarning("[ArUco] GetTexture() returned null");
            return;
        }
        if (showDebugLogs && Time.frameCount % 120 == 0)
        {
            Debug.Log($"[ArUco] Texture: {cameraTexture.width}x{cameraTexture.height}, format: {cameraTexture.GetType()}");
        }
        if (!cameraMatrixReady)
        {
            var intrinsics = cameraAccess.Intrinsics;
            if (intrinsics.FocalLength.x > 0 && intrinsics.FocalLength.y > 0)
            {
                InitializeCameraMatrix();
                cameraMatrixReady = true;
            }
            else return;
        }

        // Camera available → ensure cube is visible
        if (mesh != null && !mesh.enabled)
            mesh.enabled = true;

        if (cameraTexture == null) return;

        ProcessFrame(cameraTexture);

        if (isTracking)
        {
            transform.position = Vector3.Lerp(transform.position, targetPosition, positionLerp);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationLerp);
        }
    }

    void ProcessFrame(Texture cameraTexture)
    {
        try
        {
            Vector2Int res = cameraAccess.CurrentResolution;

            if (processingTexture == null || processingTexture.width != res.x || processingTexture.height != res.y)
            {
                processingTexture = new Texture2D(res.x, res.y, TextureFormat.RGBA32, false);
                textureBytes = new byte[res.x * res.y * 4];
                rgbMat = new Mat(res.y, res.x, MatType.CV_8UC3);
                grayMat = new Mat(res.y, res.x, MatType.CV_8UC1);
            }

            // --- Camera texture → Texture2D ---
            RenderTexture rt = RenderTexture.GetTemporary(res.x, res.y);
            Graphics.Blit(cameraTexture, rt);
            RenderTexture.active = rt;
            processingTexture.ReadPixels(new UnityEngine.Rect(0, 0, res.x, res.y), 0, 0);
            processingTexture.Apply();
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            // --- Texture2D → OpenCV Mat (RGBA) ---
            NativeArray<byte> rawData = processingTexture.GetRawTextureData<byte>();
            rawData.CopyTo(textureBytes);
            using (Mat rgbaMat = new Mat(res.y, res.x, MatType.CV_8UC4, textureBytes))
            {
                Cv2.CvtColor(rgbaMat, rgbMat, ColorConversionCodes.RGBA2RGB);
            }

            // --- Grayscale for detection ---
            Cv2.CvtColor(rgbMat, grayMat, ColorConversionCodes.RGB2GRAY);
            Cv2.Flip(grayMat, grayMat, FlipMode.X);

            // --- Detect markers ---
            Point2f[][] corners;
            int[] ids;
            Point2f[][] rejected;
            CvAruco.DetectMarkers(grayMat, dictionary, out corners, out ids, detectorParams, out rejected);

            if (ids != null && ids.Length > 0)
            {
                CvAruco.DrawDetectedMarkers(rgbMat, corners, ids, new Scalar(0, 255, 0));

                for (int i = 0; i < ids.Length; i++)
                {
                    if (targetMarkerId != -1 && ids[i] != targetMarkerId) continue;

                    Point2f[] imgPts = corners[i];

                    // FIX CS0117: Use Cv2.SolvePnP instead of missing EstimatePoseSingleMarkers
                    using (Mat rvec = new Mat())
                    using (Mat tvec = new Mat())
                    using (Mat imgMat = new Mat(4, 1, MatType.CV_32FC2, imgPts))
                    {
                        Cv2.SolvePnP(markerObjMat, imgMat, cameraMatrix, distCoeffs, rvec, tvec);

                        Pose markerPose = ConvertOpenCVToUnity(rvec, tvec);
                        targetPosition = markerPose.position + Vector3.up * cubeHeightOffset;
                        targetRotation = markerPose.rotation;
                        isTracking = true;

                        if (showDebugLogs && Time.frameCount % 60 == 0)
                            Debug.Log($"[ArUco] Marker {ids[i]} @ {targetPosition}, rot: {targetRotation.eulerAngles}");
                    }
                }
            }

            // --- Debug display ---
            if (debugDisplay != null)
            {
                MatToTexture2D(rgbMat, processingTexture);
                debugDisplay.texture = processingTexture;
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[ArUco] Processing error: " + e.Message + "\n" + e.StackTrace);
        }
    }

    /// <summary>
    /// Converts RGB Mat → Texture2D for the UI RawImage.
    /// </summary>
    void MatToTexture2D(Mat mat, Texture2D tex)
    {
        using (Mat rgba = new Mat())
        {
            Cv2.CvtColor(mat, rgba, ColorConversionCodes.RGB2RGBA);
            Cv2.Flip(rgbMat, rgbMat, FlipMode.X);
            Color32[] px = new Color32[rgba.Width * rgba.Height];
            for (int y = 0; y < rgba.Height; y++)
            {
                for (int x = 0; x < rgba.Width; x++)
                {
                    Vec4b v = rgba.At<Vec4b>(y, x);
                    px[y * rgba.Width + x] = new Color32(v.Item0, v.Item1, v.Item2, v.Item3);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
        }
    }

    /// <summary>
    /// Converts OpenCV rvec/tvec (camera-relative) to Unity world-space Pose.
    /// </summary>
    Pose ConvertOpenCVToUnity(Mat rvec, Mat tvec)
    {
        using (Mat rotMat = new Mat())
        {
            Cv2.Rodrigues(rvec, rotMat);
            double[] r = new double[9];
            rotMat.GetArray(0, 0, r);

            // OpenCV:  Right-handed (X right, Y down, Z forward)
            // Unity:   Left-handed  (X right, Y up,   Z forward)
            Matrix4x4 R = Matrix4x4.identity;
            R.m00 = (float)r[0]; R.m01 = -(float)r[1]; R.m02 = (float)r[2];
            R.m10 = -(float)r[3]; R.m11 = (float)r[4]; R.m12 = -(float)r[5];
            R.m20 = (float)r[6]; R.m21 = -(float)r[7]; R.m22 = (float)r[8];

            Quaternion q = Quaternion.LookRotation(R.GetColumn(2), R.GetColumn(1));

            double[] t = new double[3];
            tvec.GetArray(0, 0, t);
            Vector3 pos = new Vector3((float)t[0], -(float)t[1], (float)t[2]);

            // Transform from camera-local to world space
            Pose camPose = cameraAccess.GetCameraPose();
            pos = camPose.position + camPose.rotation * pos;
            q = camPose.rotation * q;

            return new Pose(pos, q);
        }
    }

    void OnDestroy()
    {
        rgbMat?.Dispose();
        grayMat?.Dispose();
        cameraMatrix?.Dispose();
        distCoeffs?.Dispose();
        markerObjMat?.Dispose();
    }
}