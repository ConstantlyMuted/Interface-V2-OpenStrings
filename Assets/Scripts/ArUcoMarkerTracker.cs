using Meta.XR;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

public class ArUcoMarkerTracker : MonoBehaviour
{
    private const int Marker0Id = 0;
    private const int Marker1Id = 1;
    private const int MaxInstancesPerDrawCall = 1023;

    [Header("Camera Access")]
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    [Header("ArUco Settings")]
    [SerializeField] private float markerSize = 0.18f;
    [SerializeField, Min(0.05f)] private float markerScanIntervalSeconds = 2f;

    [Header("Stationary Marker Stabilization")]
    [SerializeField, Min(1)] private int poseHistoryLength = 8;
    [SerializeField, Min(1)] private int acceptedSamplesBeforePlacement = 3;
    [SerializeField, Min(0)] private int samplesBeforeOutlierRejection = 3;
    [SerializeField, Min(0.001f)] private float maxPositionDeviationMeters = 0.08f;
    [SerializeField, Range(0.1f, 180f)] private float maxRotationDeviationDegrees = 10f;
    [SerializeField, Min(0.01f)] private float anchorSmoothingSeconds = 4f;
    [SerializeField] private bool logRejectedPoseSamples = true;

    [Header("Instanced Cube Field")]
    [SerializeField] private Mesh cubeMesh;
    [SerializeField] private Material cubeMaterial;
    [SerializeField, Min(2)] private int cubesOnMarkerLine = 37;
    [SerializeField, Min(1)] private int cubesPerRow = 25;
    [SerializeField, Min(0.001f)] private float rowCubeSpacing = 0.02f;
    [SerializeField] private Vector3 cubeScale = new Vector3(0.01f, 0.01f, 0.01f);
    [SerializeField] private float cubeVerticalOffset = 0.005f;
    [SerializeField] private ShadowCastingMode shadowCastingMode = ShadowCastingMode.Off;
    [SerializeField] private bool receiveShadows = false;

    [Header("Tracking Visualization")]
    [SerializeField] private bool showTrackingVisualization = true;

    [Header("Instancing Diagnostics")]
    [SerializeField] private bool useDebugCubeMeshIfGridMeshMissing = true;
    [SerializeField] private bool useVisibleDebugCubeMaterialForGrid = true;
    [SerializeField] private bool drawLargeInstancingProbeCube = true;
    [SerializeField, Min(0.001f)] private float instancingProbeCubeSize = 0.06f;
    [SerializeField] private float instancingProbeCubeHeightOffset = 0.08f;

    [Header("Debug Output")]
    [SerializeField] private bool showDebugLogs = true;
    [SerializeField] private RawImage debugDisplay;

    [Header("Debug Marker Cubes")]
    [SerializeField] private bool createDebugMarkerCubes = true;
    [SerializeField, Min(0.001f)] private float debugMarkerCubeSize = 0.04f;
    [SerializeField] private float debugMarkerCubeVerticalOffset = 0.02f;
    [SerializeField] private Material marker0DebugMaterial;
    [SerializeField] private Material marker1DebugMaterial;

    [Header("Reprojection Gate")]
    [SerializeField] private bool useReprojectionGate = true;
    [SerializeField, Min(0.1f)] private float maxReprojectionRmsPx = 2.0f;

    [Header("Poisoned Average Recovery")]
    [SerializeField, Min(1)] private int maxConsecutiveRejectionsBeforeReset = 4;

    // ---------------------------------------------------------------------
    // NEW: Tracking Lock — freeze scanning + smoothing once stable, to cut
    // ongoing CPU cost (OpenCV scan pipeline + per-frame anchor lerp).
    // Periodically re-validates a single raw sample against the frozen
    // anchor; only un-freezes if the marker actually moved.
    // ---------------------------------------------------------------------
    [Header("Tracking Lock (Performance)")]
    [SerializeField] private bool enableTrackingLock = true;
    [SerializeField, Min(1)] private int stableFramesBeforeLock = 30;
    [SerializeField, Min(1f)] private float lockedSanityCheckIntervalSeconds = 10f;
    [SerializeField, Min(0.001f)] private float lockedSanityDeviationMeters = 0.05f;

    private int stableAcceptedStreak = 0;
    private bool trackingLocked = false;
    private float nextLockedSanityCheckTime = 0f;
    public bool IsTrackingLocked => trackingLocked;

    public Transform Marker0Anchor { get; private set; }
    public Transform Marker1Anchor { get; private set; }
    public Vector3 MarkerMidpoint { get; private set; }
    public bool HasMarkerPair => hasMarker0Pose && hasMarker1Pose;

    public Pose CentralProbePose =>
        new Pose(
            Vector3.Lerp(Marker0Anchor.position, Marker1Anchor.position, 0.5f)
                + Vector3.up * instancingProbeCubeHeightOffset,
            GetPlacementRotation()
        );

    // OpenCV for Unity objects.
    private Mat rgbaMat;
    private Mat rgbMat;
    private Mat grayMat;
    private OpenCVForUnity.ObjdetectModule.Dictionary arucoDictionary;
    private DetectorParameters detectorParameters;
    private RefineParameters refineParameters;
    private ArucoDetector arucoDetector;
    private Mat cameraMatrix;
    private MatOfDouble distCoeffs;
    private MatOfPoint3f markerObjectPoints;
    private Mat ids;
    private readonly List<Mat> corners = new List<Mat>();
    private readonly List<Mat> rejectedCorners = new List<Mat>();

    private Texture2D processingTexture;
    private bool _asyncReadbackPending;
    private Pose _pendingCapturePose;
    private int _pendingWidth;
    private int _pendingHeight;
    private bool _appFocused = true;

    private bool cameraMatrixReady;
    private bool hasMarker0Pose;
    private bool hasMarker1Pose;
    private float nextMarkerScanTime;
    private float nextWaitingLogTime;
    private PoseHistory marker0PoseHistory;
    private PoseHistory marker1PoseHistory;

    private GameObject marker0DebugCube;
    private GameObject marker1DebugCube;

    private readonly List<Matrix4x4> instanceMatrices = new List<Matrix4x4>();
    private readonly Matrix4x4[] drawBatch = new Matrix4x4[MaxInstancesPerDrawCall];
    private Material runtimeGridMaterial;
    private bool instancedDrawFailureLogged;
    private float nextGridStatusLogTime;

    private sealed class PoseHistory
    {
        private readonly Pose[] samples;
        private int count;
        private int nextIndex;
        private int consecutiveRejections;

        public int Count => count;
        public Pose AveragePose { get; private set; }
        public int ConsecutiveRejections => consecutiveRejections;

        public PoseHistory(int capacity)
        {
            samples = new Pose[Mathf.Max(1, capacity)];
        }

        public void Reset()
        {
            count = 0;
            nextIndex = 0;
            consecutiveRejections = 0;
            AveragePose = default;
        }

        public bool TryAdd(
            Pose sample,
            int warmupSamplesWithoutOutlierRejection,
            float maxPositionDeviation,
            float maxRotationDeviation,
            int maxConsecutiveRejections,
            out float positionDeviation,
            out float rotationDeviation)
        {
            positionDeviation = 0f;
            rotationDeviation = 0f;

            if (count > 0)
            {
                positionDeviation = Vector3.Distance(sample.position, AveragePose.position);
                rotationDeviation = Quaternion.Angle(sample.rotation, AveragePose.rotation);

                bool outlierRejectionActive = count >= warmupSamplesWithoutOutlierRejection;

                if (outlierRejectionActive &&
                    (positionDeviation > maxPositionDeviation ||
                     rotationDeviation > maxRotationDeviation))
                {
                    consecutiveRejections++;

                    if (consecutiveRejections >= maxConsecutiveRejections)
                    {
                        // FIX: don't promote the outlier that triggered the reset into
                        // AveragePose. Just seed a fresh run — caller must wait for a
                        // full new warmup batch (same rigor as initial acquisition)
                        // before this history is trusted again.
                        Reset();
                        samples[nextIndex] = sample;
                        nextIndex = (nextIndex + 1) % samples.Length;
                        count = 1;
                        // AveragePose stays default/invalid on purpose.
                    }

                    return false;
                }
            }

            consecutiveRejections = 0;
            samples[nextIndex] = sample;
            nextIndex = (nextIndex + 1) % samples.Length;
            count = Mathf.Min(count + 1, samples.Length);
            AveragePose = CalculateAveragePose();
            return true;
        }

        private Pose CalculateAveragePose()
        {
            Vector3 positionSum = Vector3.zero;
            Quaternion reference = samples[0].rotation;
            Vector4 rotationSum = Vector4.zero;

            for (int i = 0; i < count; i++)
            {
                Pose pose = samples[i];
                positionSum += pose.position;

                Quaternion rotation = pose.rotation;
                if (Quaternion.Dot(rotation, reference) < 0f)
                {
                    rotation = new Quaternion(
                        -rotation.x,
                        -rotation.y,
                        -rotation.z,
                        -rotation.w
                    );
                }

                rotationSum += new Vector4(
                    rotation.x,
                    rotation.y,
                    rotation.z,
                    rotation.w
                );
            }

            Vector3 averagePosition = positionSum / count;
            Quaternion averageRotation = new Quaternion(
                rotationSum.x,
                rotationSum.y,
                rotationSum.z,
                rotationSum.w
            ).normalized;

            return new Pose(averagePosition, averageRotation);
        }
    }

    private void Awake()
    {
        marker0PoseHistory = new PoseHistory(poseHistoryLength);
        marker1PoseHistory = new PoseHistory(poseHistoryLength);

        Marker0Anchor = new GameObject("Marker_0_Anchor").transform;
        Marker1Anchor = new GameObject("Marker_1_Anchor").transform;
        Marker0Anchor.SetParent(null, true);
        Marker1Anchor.SetParent(null, true);

        if (createDebugMarkerCubes)
        {
            marker0DebugCube = CreateDebugCube("DebugCube_Marker_0", marker0DebugMaterial);
            marker1DebugCube = CreateDebugCube("DebugCube_Marker_1", marker1DebugMaterial);
        }
    }

    private void Start()
    {
        InitializeOpenCV();
        ConfigureInstancedGridResources();

        if (cameraAccess == null)
        {
            Debug.LogError("[ArUco] CameraAccess not assigned. Assign PassthroughCameraAccess in Inspector.");
            return;
        }

        TryInitializeCameraMatrix();
        nextMarkerScanTime = 0f;

        if (showDebugLogs)
        {
            Debug.Log($"[ArUco] Debug cubes: {createDebugMarkerCubes}. " +
                      $"Grid: {cubesOnMarkerLine} x {cubesPerRow} = {cubesOnMarkerLine * cubesPerRow} instances. " +
                      $"Stabilization: history={poseHistoryLength}, initialSamples={acceptedSamplesBeforePlacement}, " +
                      $"warmupNoReject={samplesBeforeOutlierRejection}, maxPos={maxPositionDeviationMeters:F3}m, " +
                      $"maxRot={maxRotationDeviationDegrees:F1}deg, smoothing={anchorSmoothingSeconds:F1}s. " +
                      $"ReprojGate={useReprojectionGate}, maxRMS={maxReprojectionRmsPx:F1}px. " +
                      $"TrackingLock={enableTrackingLock}, stableFrames={stableFramesBeforeLock}, " +
                      $"sanityInterval={lockedSanityCheckIntervalSeconds:F1}s, sanityDev={lockedSanityDeviationMeters:F3}m.");
        }
    }

    private GameObject CreateDebugCube(string objectName, Material material)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = objectName;
        cube.layer = gameObject.layer;
        cube.transform.SetParent(null, true);
        cube.transform.localScale = Vector3.one * debugMarkerCubeSize;

        Collider collider = cube.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        Renderer renderer = cube.GetComponent<Renderer>();
        if (renderer != null && material != null)
            renderer.sharedMaterial = material;

        cube.SetActive(false);
        return cube;
    }

    private void ConfigureInstancedGridResources()
    {
        if (cubeMesh == null && useDebugCubeMeshIfGridMeshMissing && marker0DebugCube != null)
        {
            MeshFilter meshFilter = marker0DebugCube.GetComponent<MeshFilter>();
            if (meshFilter != null)
                cubeMesh = meshFilter.sharedMesh;
        }

        Material sourceMaterial = cubeMaterial;

        if (sourceMaterial == null && useVisibleDebugCubeMaterialForGrid && marker0DebugCube != null)
        {
            Renderer debugRenderer = marker0DebugCube.GetComponent<Renderer>();
            if (debugRenderer != null && debugRenderer.sharedMaterial != null)
                sourceMaterial = debugRenderer.sharedMaterial;
        }

        if (sourceMaterial != null)
        {
            if (runtimeGridMaterial != null)
                Destroy(runtimeGridMaterial);

            runtimeGridMaterial = new Material(sourceMaterial);
            runtimeGridMaterial.name = sourceMaterial.name + " (Runtime Instanced Grid)";
            runtimeGridMaterial.enableInstancing = true;
            cubeMaterial = runtimeGridMaterial;
        }

        if (showDebugLogs)
        {
            string meshName = cubeMesh != null ? cubeMesh.name : "NULL";
            string materialName = cubeMaterial != null ? cubeMaterial.name : "NULL";
            string shaderName = cubeMaterial != null && cubeMaterial.shader != null
                ? cubeMaterial.shader.name
                : "NULL";
            bool materialInstancing = cubeMaterial != null && cubeMaterial.enableInstancing;

            Debug.Log($"[Instancing] Configuration: supportsInstancing={SystemInfo.supportsInstancing}; " +
                      $"mesh={meshName}; material={materialName}; shader={shaderName}; " +
                      $"material.enableInstancing={materialInstancing}");
        }
    }

    private void InitializeOpenCV()
    {
        arucoDictionary = Objdetect.getPredefinedDictionary(Objdetect.DICT_4X4_50);

        detectorParameters = new DetectorParameters();

        detectorParameters.set_adaptiveThreshWinSizeMin(3);
        detectorParameters.set_adaptiveThreshWinSizeMax(23);
        detectorParameters.set_adaptiveThreshWinSizeStep(10);
        detectorParameters.set_minMarkerPerimeterRate(0.02);
        detectorParameters.set_maxMarkerPerimeterRate(4.0);
        detectorParameters.set_minCornerDistanceRate(0.05);
        detectorParameters.set_minDistanceToBorder(3);
        detectorParameters.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);

        refineParameters = new RefineParameters(10f, 3f, true);
        arucoDetector = new ArucoDetector(arucoDictionary, detectorParameters, refineParameters);
        ids = new Mat();

        float h = markerSize * 0.5f;
        markerObjectPoints = new MatOfPoint3f(
            new Point3(-h, h, 0),
            new Point3(h, h, 0),
            new Point3(h, -h, 0),
            new Point3(-h, -h, 0)
        );

        if (showDebugLogs)
            Debug.Log("[ArUco] OpenCV for Unity initialized. Detecting DICT_4X4_50 IDs 0 and 1.");
    }

    private void TryInitializeCameraMatrix()
    {
        if (cameraAccess == null)
            return;

        var intrinsics = cameraAccess.Intrinsics;

        if (intrinsics.FocalLength.x <= 0f || intrinsics.FocalLength.y <= 0f)
        {
            LogWaiting("[ArUco] Waiting for valid camera intrinsics.");
            return;
        }

        cameraMatrix?.Dispose();
        distCoeffs?.Dispose();

        cameraMatrix = new Mat(3, 3, CvType.CV_64FC1);
        cameraMatrix.put(0, 0, new double[]
        {
            intrinsics.FocalLength.x, 0.0,                      intrinsics.PrincipalPoint.x,
            0.0,                      intrinsics.FocalLength.y, intrinsics.PrincipalPoint.y,
            0.0,                      0.0,                      1.0
        });

        distCoeffs = new MatOfDouble(0, 0, 0, 0, 0);
        cameraMatrixReady = true;

        if (showDebugLogs)
        {
            Debug.Log($"[ArUco] Intrinsics ready. Resolution: {cameraAccess.CurrentResolution}; " +
                      $"focal: {intrinsics.FocalLength}; principal: {intrinsics.PrincipalPoint}");
        }
    }

    private void Update()
    {
        // Sanity-check window: fires while locked, on its own schedule, independent
        // of whether tracking is currently locked-out of the normal scan cadence.
        bool dueForSanityCheck = enableTrackingLock && trackingLocked &&
                                  Time.unscaledTime >= nextLockedSanityCheckTime;

        bool shouldScan = !enableTrackingLock || !trackingLocked || dueForSanityCheck;

        if (cameraAccess == null)
        {
            LogWaiting("[ArUco] No PassthroughCameraAccess assigned.");
        }
        else if (!cameraAccess.enabled)
        {
            LogWaiting("[ArUco] PassthroughCameraAccess component disabled.");
        }
        else if (!cameraAccess.IsPlaying)
        {
            LogWaiting("[ArUco] Waiting for camera. IsPlaying=false.");
        }
        else
        {
            if (!cameraMatrixReady)
                TryInitializeCameraMatrix();

            if (_appFocused && shouldScan && cameraMatrixReady && Time.unscaledTime >= nextMarkerScanTime && !_asyncReadbackPending)
            {
                nextMarkerScanTime = Time.unscaledTime + markerScanIntervalSeconds;

                if (dueForSanityCheck)
                    nextLockedSanityCheckTime = Time.unscaledTime + lockedSanityCheckIntervalSeconds;

                Texture cameraTexture = cameraAccess.GetTexture();
                Pose capturePose = cameraAccess.GetCameraPose();

                if (cameraTexture != null)
                {
                    StartAsyncMarkerScan(cameraTexture, capturePose);
                }
                else
                {
                    LogWaiting("[ArUco] Marker scan skipped. GetTexture() returned null.");
                }
            }
        }

        // Locked: anchors are frozen, skip the per-frame lerp/slerp entirely.
        if (!enableTrackingLock || !trackingLocked)
            SmoothAnchorsTowardStablePoses();

        if (showTrackingVisualization)
            UpdateDebugMarkerCubes();
        else
            HideDebugMarkerCubes();

        if (!HasMarkerPair)
        {
            if (showTrackingVisualization)
            {
                LogGridStatus($"Waiting for marker pair. stored0={hasMarker0Pose}; stored1={hasMarker1Pose}; " +
                              $"cube0Visible={(marker0DebugCube != null && marker0DebugCube.activeSelf)}; " +
                              $"cube1Visible={(marker1DebugCube != null && marker1DebugCube.activeSelf)}");
            }
            return;
        }

        MarkerMidpoint = Vector3.Lerp(Marker0Anchor.position, Marker1Anchor.position, 0.5f);

        if (showTrackingVisualization)
        {
            BuildInstanceMatrices();
            DrawInstancedCubes();
        }
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        _appFocused = hasFocus;

        if (showDebugLogs)
        {
            Debug.Log($"[ArUco] Focus changed to {hasFocus} at t={Time.unscaledTime:F2}s. " +
                      $"UserPresent={OVRPlugin.userPresent}, " +
                      $"Battery={SystemInfo.batteryLevel:P0}, " +
                      $"ThermalState={(int)SystemInfo.batteryStatus}");
        }

        if (!hasFocus)
            _asyncReadbackPending = false;
    }

    private void LogGridStatus(string message)
    {
        if (!showDebugLogs || Time.unscaledTime < nextGridStatusLogTime)
            return;

        nextGridStatusLogTime = Time.unscaledTime + 2f;
        Debug.Log("[Grid] " + message);
    }

    private void LogWaiting(string message)
    {
        if (!showDebugLogs || Time.unscaledTime < nextWaitingLogTime)
            return;

        nextWaitingLogTime = Time.unscaledTime + 2f;
        Debug.LogWarning(message);
    }

    private void StartAsyncMarkerScan(Texture cameraTexture, Pose capturePose)
    {
        Vector2Int resolution = cameraAccess.CurrentResolution;
        int width = resolution.x > 0 ? resolution.x : cameraTexture.width;
        int height = resolution.y > 0 ? resolution.y : cameraTexture.height;

        EnsureFrameResources(width, height);

        RenderTexture previous = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);

        try
        {
            Graphics.Blit(cameraTexture, temporary);

            _asyncReadbackPending = true;
            _pendingCapturePose = capturePose;
            _pendingWidth = width;
            _pendingHeight = height;

            AsyncGPUReadback.Request(temporary, 0, TextureFormat.RGBA32, (request) =>
            {
                _asyncReadbackPending = false;

                if (!_appFocused)
                {
                    // Focus dropped while this readback was in flight — the capture
                    // pose/frame may be stale or mid-relocalization. Discard rather
                    // than feed it into tracking.
                    return;
                }

                if (request.hasError)
                {
                    Debug.LogWarning("[ArUco] AsyncGPUReadback failed.");
                    return;
                }

                var data = request.GetData<byte>();
                processingTexture.LoadRawTextureData(data);
                processingTexture.Apply(false, false);

                ProcessMarkerFrame(_pendingCapturePose);
            });
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    private void ProcessMarkerFrame(Pose capturePose)
    {
        UnityEngine.Profiling.Profiler.BeginSample("ArUco.ProcessMarkerFrame");
        try
        {
            OpenCVMatUtils.Texture2DToMat(processingTexture, rgbaMat);
            Imgproc.cvtColor(rgbaMat, rgbMat, Imgproc.COLOR_RGBA2RGB);
            Imgproc.cvtColor(rgbaMat, grayMat, Imgproc.COLOR_RGBA2GRAY);

            Imgproc.equalizeHist(grayMat, grayMat);

            ids.release();
            arucoDetector.detectMarkers(grayMat, corners, ids, rejectedCorners);

            int[] detectedIds = Array.Empty<int>();
            if (ids.total() > 0)
            {
                detectedIds = new int[(int)(ids.total() * ids.channels())];
                ids.get(0, 0, detectedIds);
            }

            if (showDebugLogs)
            {
                Scalar grayMean = Core.mean(grayMat);
                string idList = detectedIds.Length == 0 ? "none" : string.Join(", ", detectedIds);
                Debug.Log($"[ArUco] Scan: detected={detectedIds.Length} ids=[{idList}] " +
                          $"rejected={rejectedCorners.Count} grayMean={grayMean.val[0]:F1} " +
                          $"stored0={hasMarker0Pose} stored1={hasMarker1Pose} locked={trackingLocked}");
            }

            if (detectedIds.Length > 0)
            {
                Objdetect.drawDetectedMarkers(rgbMat, corners, ids, new Scalar(0, 255, 0));

                for (int i = 0; i < detectedIds.Length; i++)
                {
                    if (detectedIds[i] != Marker0Id && detectedIds[i] != Marker1Id)
                        continue;

                    using (Mat corner4x1 = corners[i].reshape(2, 4))
                    using (MatOfPoint2f imagePoints = new MatOfPoint2f(corner4x1))
                    using (Mat rvec = new Mat())
                    using (Mat tvec = new Mat())
                    {
                        bool solved;
                        try
                        {
                            solved = Calib3d.solvePnP(
                                markerObjectPoints,
                                imagePoints,
                                cameraMatrix,
                                distCoeffs,
                                rvec,
                                tvec,
                                false,
                                Calib3d.SOLVEPNP_IPPE_SQUARE
                            );
                        }
                        catch (Exception pnpException)
                        {
                            Debug.LogWarning("[ArUco] SolvePnP failed for marker " +
                                             detectedIds[i] + ": " + pnpException.Message);
                            continue;
                        }

                        if (!solved)
                        {
                            Debug.LogWarning("[ArUco] SolvePnP returned false for marker " + detectedIds[i] + ".");
                            continue;
                        }

                        if (useReprojectionGate)
                        {
                            using (MatOfPoint2f reprojected = new MatOfPoint2f())
                            {
                                Calib3d.projectPoints(markerObjectPoints, rvec, tvec, cameraMatrix, distCoeffs, reprojected);
                                double rmsPx = Core.norm(imagePoints, reprojected, Core.NORM_L2) / 2.0;

                                if (rmsPx > maxReprojectionRmsPx)
                                {
                                    Debug.LogWarning($"[ArUco] Marker {detectedIds[i]} rejected, reproj RMS={rmsPx:F2}px");
                                    continue;
                                }
                            }
                        }

                        Pose markerPose = ConvertOpenCVToUnity(rvec, tvec, capturePose);
                        UpdateMarkerAnchor(detectedIds[i], markerPose);
                    }
                }
            }

            if (debugDisplay != null)
            {
                OpenCVMatUtils.MatToTexture2D(rgbMat, processingTexture);
                debugDisplay.texture = processingTexture;
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[ArUco] Processing error: " + e.Message + "\n" + e.StackTrace);
        }
        finally
        {
            UnityEngine.Profiling.Profiler.EndSample();
        }
    }

    private void UpdateMarkerAnchor(int markerId, Pose rawPose)
    {
        PoseHistory history = markerId == Marker0Id ? marker0PoseHistory : marker1PoseHistory;
        Transform anchor = markerId == Marker0Id ? Marker0Anchor : Marker1Anchor;
        bool wasPlaced = markerId == Marker0Id ? hasMarker0Pose : hasMarker1Pose;

        if (enableTrackingLock && trackingLocked)
        {
            // Locked: don't feed PoseHistory or move the anchor from raw samples.
            // Just compare this single scan against the frozen anchor position.
            float deviation = Vector3.Distance(rawPose.position, anchor.position);

            if (deviation > lockedSanityDeviationMeters)
            {
                if (showDebugLogs)
                    Debug.LogWarning($"[Stabilizer] Marker {markerId} sanity check FAILED " +
                                      $"(deviation={deviation:F3}m > {lockedSanityDeviationMeters:F3}m). " +
                                      "Unlocking, resuming full tracking.");

                trackingLocked = false;
                stableAcceptedStreak = 0;
                history.Reset();
            }
            else if (showDebugLogs)
            {
                Debug.Log($"[Stabilizer] Marker {markerId} sanity check OK (deviation={deviation:F3}m). Staying locked.");
            }

            return;
        }

        bool accepted = history.TryAdd(
            rawPose,
            Mathf.Max(0, samplesBeforeOutlierRejection),
            maxPositionDeviationMeters,
            maxRotationDeviationDegrees,
            maxConsecutiveRejectionsBeforeReset,
            out float positionDeviation,
            out float rotationDeviation
        );

        if (!accepted)
        {
            stableAcceptedStreak = 0;

            if (showDebugLogs && logRejectedPoseSamples)
            {
                Debug.LogWarning($"[Stabilizer] Marker {markerId} REJECTED. " +
                                 $"positionDelta={positionDeviation:F3}m; " +
                                 $"rotationDelta={rotationDeviation:F1}deg; " +
                                 $"samples={history.Count}; consecutiveRejections={history.ConsecutiveRejections}.");
            }
            return;
        }

        if (!wasPlaced && history.Count >= Mathf.Max(1, acceptedSamplesBeforePlacement))
        {
            anchor.SetPositionAndRotation(history.AveragePose.position, history.AveragePose.rotation);

            if (markerId == Marker0Id)
                hasMarker0Pose = true;
            else
                hasMarker1Pose = true;

            if (showDebugLogs)
            {
                Debug.Log($"[Stabilizer] Marker {markerId} ACQUIRED after {history.Count} accepted samples. " +
                          $"Position={history.AveragePose.position}; yaw={history.AveragePose.rotation.eulerAngles.y:F1}.");
            }

            return;
        }

        if (enableTrackingLock && hasMarker0Pose && hasMarker1Pose && !trackingLocked)
        {
            stableAcceptedStreak++;

            if (stableAcceptedStreak >= stableFramesBeforeLock)
            {
                trackingLocked = true;
                nextLockedSanityCheckTime = Time.unscaledTime + lockedSanityCheckIntervalSeconds;

                if (showDebugLogs)
                    Debug.Log($"[Stabilizer] Tracking LOCKED after {stableAcceptedStreak} stable accepted samples. " +
                              $"Scans + smoothing paused; sanity re-check every {lockedSanityCheckIntervalSeconds:F1}s.");
            }
        }

        if (showDebugLogs && !wasPlaced)
        {
            Debug.Log($"[Stabilizer] Marker {markerId} calibrating: " +
                      $"{history.Count}/{Mathf.Max(1, acceptedSamplesBeforePlacement)} accepted samples.");
        }
    }

    private void SmoothAnchorsTowardStablePoses()
    {
        float seconds = Mathf.Max(0.01f, anchorSmoothingSeconds);
        float alpha = 1f - Mathf.Exp(-Time.unscaledDeltaTime / seconds);
        int warmup = Mathf.Max(1, samplesBeforeOutlierRejection);

        if (hasMarker0Pose && marker0PoseHistory.Count >= warmup)
        {
            Pose target = marker0PoseHistory.AveragePose;
            Marker0Anchor.position = Vector3.Lerp(Marker0Anchor.position, target.position, alpha);
            Marker0Anchor.rotation = Quaternion.Slerp(Marker0Anchor.rotation, target.rotation, alpha);
        }

        if (hasMarker1Pose && marker1PoseHistory.Count >= warmup)
        {
            Pose target = marker1PoseHistory.AveragePose;
            Marker1Anchor.position = Vector3.Lerp(Marker1Anchor.position, target.position, alpha);
            Marker1Anchor.rotation = Quaternion.Slerp(Marker1Anchor.rotation, target.rotation, alpha);
        }
    }

    private Quaternion GetPlacementRotation()
    {
        Vector3 baseline = Marker1Anchor.position - Marker0Anchor.position;
        baseline.y = 0f;

        if (baseline.sqrMagnitude > 1e-6f)
            return Quaternion.LookRotation(baseline.normalized, Vector3.up);

        return Quaternion.Euler(0f, Marker0Anchor.eulerAngles.y - 180f, 0f);
    }

    private void HideDebugMarkerCubes()
    {
        if (marker0DebugCube != null && marker0DebugCube.activeSelf)
            marker0DebugCube.SetActive(false);

        if (marker1DebugCube != null && marker1DebugCube.activeSelf)
            marker1DebugCube.SetActive(false);
    }

    private void UpdateDebugMarkerCubes()
    {
        if (!createDebugMarkerCubes)
            return;

        Quaternion placementRotation = GetPlacementRotation();
        Vector3 offset = Vector3.up * debugMarkerCubeVerticalOffset;

        if (hasMarker0Pose && marker0DebugCube != null)
        {
            if (!marker0DebugCube.activeSelf)
                marker0DebugCube.SetActive(true);

            marker0DebugCube.transform.SetPositionAndRotation(Marker0Anchor.position + offset, placementRotation);
            marker0DebugCube.transform.localScale = Vector3.one * debugMarkerCubeSize;
        }

        if (hasMarker1Pose && marker1DebugCube != null)
        {
            if (!marker1DebugCube.activeSelf)
                marker1DebugCube.SetActive(true);

            marker1DebugCube.transform.SetPositionAndRotation(Marker1Anchor.position + offset, placementRotation);
            marker1DebugCube.transform.localScale = Vector3.one * debugMarkerCubeSize;
        }
    }

    private void BuildInstanceMatrices()
    {
        instanceMatrices.Clear();

        Quaternion cubeRotation = GetPlacementRotation();
        Vector3 rowDirection = cubeRotation * Vector3.forward;
        Vector3 verticalOffset = Vector3.up * cubeVerticalOffset;

        for (int lineIndex = 0; lineIndex < cubesOnMarkerLine; lineIndex++)
        {
            float t = lineIndex / (float)(cubesOnMarkerLine - 1);

            Vector3 rowStart =
                Vector3.Lerp(Marker0Anchor.position, Marker1Anchor.position, t) +
                verticalOffset;

            for (int rowIndex = 0; rowIndex < cubesPerRow; rowIndex++)
            {
                Vector3 position = rowStart + rowDirection * (rowIndex * rowCubeSpacing);
                instanceMatrices.Add(Matrix4x4.TRS(position, cubeRotation, cubeScale));
            }
        }

        if (drawLargeInstancingProbeCube)
        {
            Vector3 probePosition = MarkerMidpoint + Vector3.up * instancingProbeCubeHeightOffset;
            Vector3 probeScale = Vector3.one * instancingProbeCubeSize;
            instanceMatrices.Add(Matrix4x4.TRS(probePosition, cubeRotation, probeScale));
        }
    }

    private void DrawInstancedCubes()
    {
        if (cubeMesh == null || cubeMaterial == null)
        {
            ConfigureInstancedGridResources();
        }

        if (cubeMesh == null || cubeMaterial == null)
        {
            LogWaiting("[Instancing] Grid hidden. Cube Mesh or renderable Material is still null.");
            return;
        }

        if (!SystemInfo.supportsInstancing)
        {
            LogWaiting("[ArUco] Grid hidden. GPU instancing unsupported on current device.");
            return;
        }

        cubeMaterial.enableInstancing = true;

        try
        {
            RenderParams renderParams = new RenderParams(cubeMaterial)
            {
                layer = gameObject.layer,
                shadowCastingMode = shadowCastingMode,
                receiveShadows = receiveShadows,
                worldBounds = new Bounds(MarkerMidpoint, Vector3.one * 20f)
            };

            int start = 0;
            int drawCalls = 0;
            while (start < instanceMatrices.Count)
            {
                int count = Mathf.Min(MaxInstancesPerDrawCall, instanceMatrices.Count - start);
                instanceMatrices.CopyTo(start, drawBatch, 0, count);

                Graphics.RenderMeshInstanced(
                    renderParams,
                    cubeMesh,
                    0,
                    drawBatch,
                    count
                );

                drawCalls++;
                start += count;
            }

            string shaderName = cubeMaterial.shader != null ? cubeMaterial.shader.name : "NULL";
            LogGridStatus($"Rendered instances={instanceMatrices.Count}; calls={drawCalls}; " +
                          $"mesh={cubeMesh.name}; shader={shaderName}; " +
                          $"instancing={cubeMaterial.enableInstancing}; probe={drawLargeInstancingProbeCube}");
        }
        catch (Exception e)
        {
            if (!instancedDrawFailureLogged)
            {
                instancedDrawFailureLogged = true;
                Debug.LogError("[Grid] RenderMeshInstanced failed: " + e.Message + "\n" + e.StackTrace);
            }
        }
    }

    private void EnsureFrameResources(int width, int height)
    {
        if (processingTexture != null &&
            processingTexture.width == width &&
            processingTexture.height == height)
        {
            return;
        }

        rgbaMat?.Dispose();
        rgbMat?.Dispose();
        grayMat?.Dispose();

        if (processingTexture != null)
            Destroy(processingTexture);

        processingTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        rgbaMat = new Mat(height, width, CvType.CV_8UC4);
        rgbMat = new Mat(height, width, CvType.CV_8UC3);
        grayMat = new Mat(height, width, CvType.CV_8UC1);

        if (showDebugLogs)
            Debug.Log($"[ArUco] Frame buffers created: {width}x{height}.");
    }

    private Pose ConvertOpenCVToUnity(Mat rvec, Mat tvec, Pose capturePose)
    {
        using (Mat rotationMatrix = new Mat())
        {
            Calib3d.Rodrigues(rvec, rotationMatrix);

            double[] r = new double[9];
            rotationMatrix.get(0, 0, r);

            Matrix4x4 unityRotationMatrix = Matrix4x4.identity;
            unityRotationMatrix.m00 = (float)r[0];
            unityRotationMatrix.m01 = -(float)r[1];
            unityRotationMatrix.m02 = (float)r[2];
            unityRotationMatrix.m10 = -(float)r[3];
            unityRotationMatrix.m11 = (float)r[4];
            unityRotationMatrix.m12 = -(float)r[5];
            unityRotationMatrix.m20 = (float)r[6];
            unityRotationMatrix.m21 = -(float)r[7];
            unityRotationMatrix.m22 = (float)r[8];

            Quaternion rotation = Quaternion.LookRotation(
                unityRotationMatrix.GetColumn(2),
                unityRotationMatrix.GetColumn(1)
            );

            double[] t = new double[3];
            tvec.get(0, 0, t);
            Vector3 position = new Vector3((float)t[0], -(float)t[1], (float)t[2]);

            position = capturePose.position + capturePose.rotation * position;
            rotation = capturePose.rotation * rotation;

            return new Pose(position, rotation);
        }
    }

    private void DisposeDetectionResults()
    {
        foreach (Mat corner in corners)
            corner.Dispose();
        corners.Clear();

        foreach (Mat rejectedCorner in rejectedCorners)
            rejectedCorner.Dispose();
        rejectedCorners.Clear();
    }

    private void OnDestroy()
    {
        DisposeDetectionResults();

        rgbaMat?.Dispose();
        rgbMat?.Dispose();
        grayMat?.Dispose();
        ids?.Dispose();
        markerObjectPoints?.Dispose();
        cameraMatrix?.Dispose();
        distCoeffs?.Dispose();
        arucoDetector?.Dispose();
        refineParameters?.Dispose();
        detectorParameters?.Dispose();
        arucoDictionary?.Dispose();

        if (processingTexture != null)
            Destroy(processingTexture);

        if (runtimeGridMaterial != null)
            Destroy(runtimeGridMaterial);

        if (marker0DebugCube != null)
            Destroy(marker0DebugCube);

        if (marker1DebugCube != null)
            Destroy(marker1DebugCube);

        if (Marker0Anchor != null)
            Destroy(Marker0Anchor.gameObject);

        if (Marker1Anchor != null)
            Destroy(Marker1Anchor.gameObject);
    }
}