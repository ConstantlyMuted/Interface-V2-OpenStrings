using System;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// Diagnostic logger for the "jump/drop/overshoot" AR-placement bug.
///
/// Logs, every frame, timestamped and to CSV:
///   - StringFrame3D's world-space Y (should be perfectly constant — this is
///     the value that mathematically CANNOT move given fixedWorldHeightY)
///   - The headset camera's world position/rotation (candidate: SLAM
///     relocalization / tracking-loss recovery)
///   - The StringFrame's position AS SEEN FROM THE CAMERA (camera-local space)
///     — this is what the user actually perceives. If world Y is constant but
///     camera-local Y oscillates, the frame never moved; the camera did.
///   - Marker anchor world positions (rules in/out ArUco-side movement)
///   - ArUcoMarkerTracker's tracking-lock state
///   - GC collection counts per generation + allocated heap size (candidate:
///     GC stop-the-world pause long enough to cause a frame-budget overrun /
///     runtime session demotion)
///   - Frame time in ms (candidate: any frame-budget overrun, GC or otherwise)
///   - Application focus state + transition timestamps, tracked independently
///     of ArUcoMarkerTracker's own handler, for cross-correlation
///   - Proximity sensor state (OVRPlugin.userPresent), if available (candidate:
///     headset fit drift triggering face-off/face-on focus cycling)
///
/// Also flags large frame-to-frame deltas AND GC/frame-time spikes directly in
/// the console so you don't have to eyeball the CSV to find the event.
///
/// Usage: attach to any GameObject, assign references, hit Play. Reproduce
/// the jump. Stop. Open the CSV from Application.persistentDataPath.
/// </summary>
public class JumpBugDiagnosticLogger : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private StringFrame3D stringFrame;
    [SerializeField] private ArUcoMarkerTracker markerTracker;
    [SerializeField] private Camera vrCamera; // assign the actual headset/CenterEye camera

    [Header("Logging")]
    [SerializeField] private bool logToFile = true;
    [SerializeField] private bool logToConsoleOnJump = true;
    [SerializeField, Min(0f)] private float logIntervalSeconds = 0f;
    [SerializeField, Min(0.001f)] private float jumpFlagThresholdMeters = 0.03f;
    [SerializeField, Min(1)] private int fileFlushEveryNFrames = 60; // NEW — batch disk writes

    private System.Text.StringBuilder _pendingLines = new System.Text.StringBuilder(); // NEW
    private int _framesSinceFlush = 0; // NEW

    [Header("Performance / GC Thresholds")]
    [SerializeField, Min(0f)] private float frameTimeSpikeMs = 30f; // flag any frame over this
    [SerializeField] private bool trackUserPresence = true; // requires OVRPlugin — see note below

    private StreamWriter _writer;
    private float _nextLogTime;

    private bool _hasPrevious;
    private float _prevFrameWorldY;
    private Vector3 _prevFrameCameraLocalPos;
    private Vector3 _prevCameraWorldPos;
    private Quaternion _prevCameraWorldRot;

    // GC / perf tracking
    private int _prevGen0Count;
    private int _prevGen1Count;
    private int _prevGen2Count;
    private bool _prevUserPresent = true;
    private bool _hasPrevUserPresent;

    // Focus tracking, independent of ArUcoMarkerTracker's own handler —
    // lets you cross-check the two logs agree on timing.
    private bool _appFocused = true;

    private void Start()
    {
        if (stringFrame == null)
            Debug.LogWarning("[JumpBugDiagnosticLogger] No StringFrame3D assigned — frame Y / camera-local data will be skipped.");

        if (vrCamera == null)
        {
            vrCamera = Camera.main;
            if (vrCamera == null)
                Debug.LogWarning("[JumpBugDiagnosticLogger] No camera assigned and Camera.main is null — camera-side data will be skipped.");
        }

        _prevGen0Count = GC.CollectionCount(0);
        _prevGen1Count = GC.CollectionCount(1);
        _prevGen2Count = GC.CollectionCount(2);
        /*
        if (logToFile)
        {
            string fileName = $"jump_bug_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string path = Path.Combine(Application.persistentDataPath, fileName);

            try
            {
                _writer = new StreamWriter(path, false);
                _writer.WriteLine(
                    "time,frameWorldY,frameWorldX,frameWorldZ," +
                    "cameraWorldX,cameraWorldY,cameraWorldZ," +
                    "cameraEulerX,cameraEulerY,cameraEulerZ," +
                    "frameInCameraLocalX,frameInCameraLocalY,frameInCameraLocalZ," +
                    "marker0X,marker0Y,marker0Z," +
                    "marker1X,marker1Y,marker1Z," +
                    "trackingLocked," +
                    "frameYDeltaThisFrame,cameraPosDeltaThisFrame,frameInCameraLocalYDeltaThisFrame," +
                    "frameTimeMs,gen0CollectionsThisFrame,gen1CollectionsThisFrame,gen2CollectionsThisFrame," +
                    "totalAllocatedMB,monoHeapMB," +
                    "appFocused,userPresent"
                );
                Debug.Log($"[JumpBugDiagnosticLogger] Logging to: {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[JumpBugDiagnosticLogger] Failed to open log file: {e.Message}");
                logToFile = false;
            }
        }
    */
    }
    /*
    private void OnApplicationFocus(bool hasFocus)
    {
        _appFocused = hasFocus;

        if (logToConsoleOnJump)
        {
            Debug.LogWarning($"[JumpBugDiagnosticLogger] t={Time.unscaledTime:F2}s: " +
                              $"OnApplicationFocus({hasFocus}). Cross-check this timestamp against " +
                              "ArUcoMarkerTracker's own focus log line — should match exactly.");
        }
    }
    */
    private void Update()
    {
        if (logIntervalSeconds > 0f && Time.unscaledTime < _nextLogTime)
            return;

        _nextLogTime = Time.unscaledTime + logIntervalSeconds;

        float t = Time.unscaledTime;
        float frameTimeMs = Time.unscaledDeltaTime * 1000f;

        Vector3 frameWorldPos = stringFrame != null ? stringFrame.transform.position : Vector3.zero;
        float frameWorldY = frameWorldPos.y;

        Vector3 cameraWorldPos = vrCamera != null ? vrCamera.transform.position : Vector3.zero;
        Quaternion cameraWorldRot = vrCamera != null ? vrCamera.transform.rotation : Quaternion.identity;
        Vector3 cameraEuler = cameraWorldRot.eulerAngles;

        // What the user actually sees: frame position relative to the camera.
        // If this oscillates while frameWorldY stays flat, the camera is the culprit.
        Vector3 frameInCameraLocal = vrCamera != null
            ? vrCamera.transform.InverseTransformPoint(frameWorldPos)
            : Vector3.zero;

        Vector3 marker0Pos = (markerTracker != null && markerTracker.Marker0Anchor != null)
            ? markerTracker.Marker0Anchor.position
            : Vector3.zero;
        Vector3 marker1Pos = (markerTracker != null && markerTracker.Marker1Anchor != null)
            ? markerTracker.Marker1Anchor.position
            : Vector3.zero;

        bool trackingLocked = markerTracker != null && markerTracker.IsTrackingLocked;

        // --- GC tracking ---
        int gen0Count = GC.CollectionCount(0);
        int gen1Count = GC.CollectionCount(1);
        int gen2Count = GC.CollectionCount(2);

        int gen0Delta = gen0Count - _prevGen0Count;
        int gen1Delta = gen1Count - _prevGen1Count;
        int gen2Delta = gen2Count - _prevGen2Count;

        long totalAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
        long monoHeapBytes = Profiler.GetMonoHeapSizeLong();
        float totalAllocatedMB = totalAllocatedBytes / (1024f * 1024f);
        float monoHeapMB = monoHeapBytes / (1024f * 1024f);

        // --- Presence tracking (optional, requires Oculus Integration / Meta XR) ---
        bool userPresent = true;
        bool userPresentKnown = false;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (trackUserPresence)
        {
            try
            {
                userPresent = OVRPlugin.userPresent;
                userPresentKnown = true;
            }
            catch
            {
                // OVRPlugin not available/initialized — skip silently, don't spam every frame.
                userPresentKnown = false;
            }
        }
#endif

        float frameYDelta = 0f;
        float cameraPosDelta = 0f;
        float frameInCameraLocalYDelta = 0f;
        /*
        if (_hasPrevious)
        {
            frameYDelta = frameWorldY - _prevFrameWorldY;
            cameraPosDelta = Vector3.Distance(cameraWorldPos, _prevCameraWorldPos);
            frameInCameraLocalYDelta = frameInCameraLocal.y - _prevFrameCameraLocalPos.y;
            
            if (logToConsoleOnJump)
            {
                if (Mathf.Abs(frameYDelta) > jumpFlagThresholdMeters)
                {
                    Debug.LogWarning($"[JumpBugDiagnosticLogger] t={t:F2}s: StringFrame3D world Y jumped " +
                                      $"{frameYDelta:F4}m in one frame. This should be IMPOSSIBLE with " +
                                      $"overrideProbeHeight active — if this fires, the frame's own transform " +
                                      $"really is moving, not just the camera.");
                }

                if (Mathf.Abs(frameInCameraLocalYDelta) > jumpFlagThresholdMeters &&
                    Mathf.Abs(frameYDelta) < jumpFlagThresholdMeters * 0.5f)
                {
                    Debug.LogWarning($"[JumpBugDiagnosticLogger] t={t:F2}s: Frame's WORLD Y stayed flat " +
                                      $"(delta={frameYDelta:F4}m) but its position IN CAMERA SPACE jumped " +
                                      $"{frameInCameraLocalYDelta:F4}m. This points at the camera/headset " +
                                      $"pose moving, not the StringFrame3D transform.");
                }

                if (cameraPosDelta > jumpFlagThresholdMeters)
                {
                    Debug.LogWarning($"[JumpBugDiagnosticLogger] t={t:F2}s: Camera world position jumped " +
                                      $"{cameraPosDelta:F4}m in one frame ({_prevCameraWorldPos} -> {cameraWorldPos}). " +
                                      $"Possible SLAM relocalization / tracking-loss recovery event.");
                }

                if (gen0Delta > 0 || gen1Delta > 0 || gen2Delta > 0)
                {
                    Debug.LogWarning($"[JumpBugDiagnosticLogger] t={t:F2}s: GC collection occurred " +
                                      $"(gen0+{gen0Delta}, gen1+{gen1Delta}, gen2+{gen2Delta}), " +
                                      $"frameTime={frameTimeMs:F1}ms, heap={monoHeapMB:F1}MB. " +
                                      $"Correlate against tracking-jump/focus-loss timestamps — " +
                                      "a gen1/gen2 (or any large) collection can stall the frame long " +
                                      "enough for the OpenXR runtime to demote session state on its own.");
                }
                else if (frameTimeMs > frameTimeSpikeMs)
                {
                    Debug.LogWarning($"[JumpBugDiagnosticLogger] t={t:F2}s: Frame time spike " +
                                      $"{frameTimeMs:F1}ms (threshold {frameTimeSpikeMs:F1}ms), no GC collection " +
                                      "this frame — non-GC stall (CV scan pipeline, async readback callback, " +
                                      "or something else on main thread).");
                }

                if (userPresentKnown && _hasPrevUserPresent && userPresent != _prevUserPresent)
                {
                    Debug.LogWarning($"[JumpBugDiagnosticLogger] t={t:F2}s: Proximity sensor state changed: " +
                                      $"userPresent {_prevUserPresent} -> {userPresent}. " +
                                      "Cross-check against focus-loss timestamps — if these always coincide, " +
                                      "it's headset fit/proximity, not GC or tracking.");
                }
            }
        }

        if (logToFile && _writer != null)
        {
            _pendingLines.AppendLine(string.Join(",",
                t.ToString("F3"),
                frameWorldPos.y.ToString("F5"), frameWorldPos.x.ToString("F5"), frameWorldPos.z.ToString("F5"),
                cameraWorldPos.x.ToString("F5"), cameraWorldPos.y.ToString("F5"), cameraWorldPos.z.ToString("F5"),
                cameraEuler.x.ToString("F3"), cameraEuler.y.ToString("F3"), cameraEuler.z.ToString("F3"),
                frameInCameraLocal.x.ToString("F5"), frameInCameraLocal.y.ToString("F5"), frameInCameraLocal.z.ToString("F5"),
                marker0Pos.x.ToString("F5"), marker0Pos.y.ToString("F5"), marker0Pos.z.ToString("F5"),
                marker1Pos.x.ToString("F5"), marker1Pos.y.ToString("F5"), marker1Pos.z.ToString("F5"),
                trackingLocked ? "1" : "0",
                frameYDelta.ToString("F5"), cameraPosDelta.ToString("F5"), frameInCameraLocalYDelta.ToString("F5"),
                frameTimeMs.ToString("F2"), gen0Delta.ToString(), gen1Delta.ToString(), gen2Delta.ToString(),
                totalAllocatedMB.ToString("F2"), monoHeapMB.ToString("F2"),
                _appFocused ? "1" : "0", userPresentKnown ? (userPresent ? "1" : "0") : "NA"
            ));
            _framesSinceFlush++;

            if (_framesSinceFlush >= fileFlushEveryNFrames)
            {
                _writer.Write(_pendingLines.ToString());
                _writer.Flush();
                _pendingLines.Clear();
                _framesSinceFlush = 0;
            }
        }
        */
        _prevFrameWorldY = frameWorldY;
        _prevFrameCameraLocalPos = frameInCameraLocal;
        _prevCameraWorldPos = cameraWorldPos;
        _prevCameraWorldRot = cameraWorldRot;
        _prevGen0Count = gen0Count;
        _prevGen1Count = gen1Count;
        _prevGen2Count = gen2Count;

        if (userPresentKnown)
        {
            _prevUserPresent = userPresent;
            _hasPrevUserPresent = true;
        }

        _hasPrevious = true;
    }

    private void OnDestroy()
    {
        CloseWriter();
    }

    private void OnApplicationQuit()
    {
        CloseWriter();
    }

    private void CloseWriter()
    {
        if (_writer == null) return;
        try
        {
            if (_pendingLines.Length > 0)
            {
                _writer.Write(_pendingLines.ToString());
                _pendingLines.Clear();
            }
            _writer.Flush();
            _writer.Close();
        }
        catch (Exception e) { Debug.LogWarning($"[JumpBugDiagnosticLogger] Error closing log file: {e.Message}"); }
        finally { _writer = null; }
    }
}