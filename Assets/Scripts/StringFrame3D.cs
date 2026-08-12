using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class StringFrame3D : MonoBehaviour
{
    [Header("Modell-Konfiguration")]
    public StringFrameConfig config = new StringFrameConfig();

    [Header("Layer-Toggle")]
    public bool showSpheres = true;
    public bool showHelices = true;
    public bool showStrings = true;
    public bool showAnchor = true;
    public bool showUnison = true;
    public bool showLegs = true;

    [Header("Linien-Stärken")]
    public float helixLineWidth = 0.0020f;
    public float stringLineWidth = 0.0020f;
    public float sattelLineWidth = 0.0050f;
    public float stegLineWidth = 0.0035f;
    public float unisonLineWidth = 0.0015f;
    public float legLineWidth = 0.0030f;

    [Header("Materials (optional)")]
    public Material sphereMaterial;
    public Material lineMaterial;
    public Material unisonMaterial;

    [Header("Ball-Größe")]
    [Range(0.005f, 0.05f)] public float ballRadius = 0.025f;

    [Header("GPU Instancing")]
    public bool useInstancedSpheres = true;
    public Mesh sphereMesh;
    public bool autoCreateSphereMesh = true;
    public ShadowCastingMode sphereShadowCasting = ShadowCastingMode.Off;
    public bool sphereReceiveShadows = false;

    [Header("AR-Placement")]
    public ArUcoMarkerTracker markerTracker;
    public bool followCentralProbe = true;
    public bool hideUntilMarkerPairIsStable = true;
    public Vector3 localPlacementOffset = Vector3.zero;
    public Vector3 localRotationOffsetEuler = Vector3.zero;
    private Quaternion _lastValidPlacementRotation = Quaternion.identity;
    private bool _hasValidPlacementRotation = false;

    [Header("Height Override")]
    public bool overrideProbeHeight = true;
    public float fixedWorldHeightY = 0.75f;

    [Header("Interaction / Sphere Triggers")]
    public UdpSubscriptionClient subscribeManager;
    public Camera playerCamera;

    [SerializeField] public string playerTag = "Player";
    [SerializeField] private bool interactionDebugLogs = true;

    [Header("View Culling")]
    [SerializeField] private bool cullInstancedSpheresToPlayerView = true;
    [SerializeField, Min(0f)] private float viewportPadding = 0.05f;

    // -------------------------------------------------------------------------
    // INTERNE STATE
    // -------------------------------------------------------------------------
    private float[] frequencies;
    private float[] stringLengths;
    private float[,] rotationMatrix;
    private float Y36zMm;
    private float rot2Rad;
    private float[] cachedUValues;
    private Color[] _stringColors;

    private Vector3[] anchors;
    private Vector3[] bridgeEnds;
    private List<int[][]> unisonGroups;

    private uint[] occupiedSpheresFlags;
    private uint[] highlightedSpheresFlags;
    private SnapGrabbable[] snappedObjects;

    private Vector3[] sphereWorldPositions;
    private Vector3[] sphereLocalPositions;
    private Matrix4x4[] sphereMatrices;
    private bool spherePositionsDirty = true;

    private Transform trackedContentRoot;
    private Transform spheresParent, helicesParent, stringsParent;
    private Transform anchorParent, unisonParent, legsParent;

    private Vector3 sphereScale;

    private readonly Dictionary<Color, Material> _lineMaterialCache = new Dictionary<Color, Material>();

    private const int MaxInstancesPerDrawCall = 1023;
    private Matrix4x4[] _sphereDrawBuffer;

    private readonly List<Matrix4x4> _normalBatch = new List<Matrix4x4>();
    private readonly List<Matrix4x4> _occupiedBatch = new List<Matrix4x4>();
    private readonly List<Matrix4x4> _highlightBatch = new List<Matrix4x4>();

    // === LINIEN-REFERENZEN (Anchor kept as individual LineRenderers — already cheap, only 2) ===
    private LineRenderer sattelLine, stegLine;

    // === LINIEN-REFERENZEN (batched — one draw call per layer instead of N) ===
    private Mesh helixMesh, stringMesh, unisonMesh, legsMesh;
    private MeshFilter helixFilter, stringFilter, unisonFilter, legsFilter;
    private MeshRenderer helixRenderer, stringRenderer, unisonRenderer, legsRenderer;

    private bool initialized = false;
    private float lastArcDeg, lastR0, lastDr;

    private Plane[] _frustumPlanes = new Plane[6];
    private bool _frustumPlanesDirty = true;
    private Vector3 _lastCameraPos;
    private Quaternion _lastCameraRot;

    private const int BezierSamples = 20;

    private static Shader _standardShader;
    private static Shader _spriteDefaultShader;
    private static Shader _vertexColorUnlitShader;

    private Material _batchVertexColorMaterial;

    private Material GetBatchVertexColorMaterial()
    {
        if (_batchVertexColorMaterial == null)
        {
            Shader shader = _vertexColorUnlitShader != null ? _vertexColorUnlitShader : _spriteDefaultShader;
            _batchVertexColorMaterial = new Material(shader) { color = Color.white };
            _batchVertexColorMaterial.name = "BatchVertexColorMaterial (Runtime)";
        }
        return _batchVertexColorMaterial;
    }

    // -------------------------------------------------------------------------
    // UNITY EVENTS
    // -------------------------------------------------------------------------
    void Start()
    {
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            // Movement here is driven entirely by transform.SetPositionAndRotation()
            // in Update(). A live non-kinematic/gravity Rigidbody fights that: gravity
            // integrates every FixedUpdate tick (0..N times per rendered frame,
            // non-deterministic under variable framerate), our Update() write gets
            // silently overridden by the next physics step -> visible jump/drop cycle.
            // This object doesn't need physics simulation, so force it off.
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;

            Debug.LogWarning($"[StringFrame3D] Rigidbody found on {gameObject.name}. " +
                              "Forced isKinematic=true, useGravity=false, interpolation=None " +
                              "to stop physics fighting the AR-placement transform writes.");
        }

        Transform current = transform.parent;
        while (current != null)
        {
            var parentRb = current.GetComponent<Rigidbody>();
            if (parentRb != null)
            {
                // Not our object — don't force-modify a parent's Rigidbody automatically,
                // just flag it: if non-kinematic/gravity-enabled it WILL reproduce the
                // same jump/drop bug on this whole hierarchy.
                Debug.LogWarning($"[StringFrame3D] Parent {current.name} has Rigidbody! " +
                                  $"isKinematic={parentRb.isKinematic}, useGravity={parentRb.useGravity}. " +
                                  "Not auto-fixed. Verify manually if jump/drop symptoms persist.");
            }
            current = current.parent;
        }

        BuildAll();
        initialized = true;
    }

    void Update()
    {
        if (!initialized) return;

        bool hasPlacement = !followCentralProbe ||
                            (markerTracker != null && markerTracker.HasMarkerPair);

        if (followCentralProbe && markerTracker != null && markerTracker.HasMarkerPair)
        {
            Pose probePose = markerTracker.CentralProbePose;

            Quaternion placementRotation = probePose.rotation;

            if (overrideProbeHeight)
            {
                Vector3 forward = probePose.rotation * Vector3.forward;
                forward.y = 0f;

                if (forward.sqrMagnitude > 1e-6f)
                {
                    placementRotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
                    _lastValidPlacementRotation = placementRotation;
                    _hasValidPlacementRotation = true;
                }
                else if (_hasValidPlacementRotation)
                {
                    // Genuinely degenerate/noisy frame — reuse last known-good yaw-only
                    // rotation instead of leaking this frame's raw (possibly corrupted)
                    // full 3D rotation through.
                    placementRotation = _lastValidPlacementRotation;
                }
                // else: no valid rotation acquired yet at all (very first frame) —
                // uses raw probePose.rotation once; self-corrects next frame.
            }

            Vector3 placementPosition = probePose.position + placementRotation * localPlacementOffset;

            if (overrideProbeHeight)
                placementPosition.y = fixedWorldHeightY - config.zNutM;

            transform.SetPositionAndRotation(
                placementPosition,
                placementRotation * Quaternion.Euler(localRotationOffsetEuler)
            );

            spherePositionsDirty = true;
            _frustumPlanesDirty = true;
        }
        /*else if (!hasPlacement)
        {
            if (Debug.isDebugBuild)
                Debug.LogWarning($"[StringFrame3D] hasPlacement FALSE at t={Time.unscaledTime:F2}s. " +
                                  $"followCentralProbe={followCentralProbe}, " +
                                  $"markerTracker null={markerTracker == null}, " +
                                  $"HasMarkerPair={(markerTracker != null ? markerTracker.HasMarkerPair.ToString() : "N/A")}. " +
                                  "Holding last transform.");
            // no-op — see fix above
        }
        */
        if (spherePositionsDirty)
        {
            UpdateSphereMatrices();
            spherePositionsDirty = false;
        }

        if (!Mathf.Approximately(config.helixArcDeg, lastArcDeg) ||
            !Mathf.Approximately(config.helixR0M, lastR0) ||
            !Mathf.Approximately(config.helixDrM, lastDr))
        {
            UpdateDynamicGeometry();
            spherePositionsDirty = true;
            lastArcDeg = config.helixArcDeg;
            lastR0 = config.helixR0M;
            lastDr = config.helixDrM;
        }

        bool visible = hasPlacement || !hideUntilMarkerPairIsStable;

        SetLayerVisibility(visible);

        if (useInstancedSpheres && visible && showSpheres)
            DrawInstancedSpheresOptimized();

        UpdateSnappedObjects();
    }

    void OnDestroy()
    {
        CleanupMaterials();
    }

    // -------------------------------------------------------------------------
    // SETUP
    // -------------------------------------------------------------------------
    void BuildAll()
    {
        EnsureDefaultMaterials();
        PreAllocateArrays();

        trackedContentRoot = new GameObject("TrackedStringFrameContent").transform;
        trackedContentRoot.SetParent(transform, false);
        trackedContentRoot.localPosition = Vector3.zero;
        trackedContentRoot.localRotation = Quaternion.identity;

        spheresParent = MakeChild("Spheres");
        helicesParent = MakeChild("Helices");
        stringsParent = MakeChild("Strings");
        anchorParent = MakeChild("Anchor");
        unisonParent = MakeChild("Unison");
        legsParent = MakeChild("Legs");

        frequencies = StringFrameDataGenerator.ComputeFrequencies(config);
        stringLengths = StringFrameDataGenerator.ComputeStringLengths(config);
        StringFrameDataGenerator.ComputeTwoRotations(config, out rot2Rad, out Y36zMm, out rotationMatrix);
        StringFrameDataGenerator.ComputeAnchorsAndBridge(config, stringLengths, rotationMatrix, Y36zMm,
                                                          out anchors, out bridgeEnds);
        unisonGroups = StringFrameDataGenerator.ComputeUnisonGroupsRaw(config);

        CacheUValues();
        CacheStringColors();

        BuildSphereData();
        BuildHelixMeshBatch();
        BuildStringMeshBatch();
        BuildAnchorAndBridgeLines();
        BuildUnisonMeshBatch();
        BuildLegsMeshBatch();

        UpdateDynamicGeometry();

        lastArcDeg = config.helixArcDeg;
        lastR0 = config.helixR0M;
        lastDr = config.helixDrM;
    }

    void EnsureDefaultMaterials()
    {
        if (_standardShader == null) _standardShader = Shader.Find("Standard");
        if (_spriteDefaultShader == null) _spriteDefaultShader = Shader.Find("Sprites/Default");
        if (_vertexColorUnlitShader == null) _vertexColorUnlitShader = Shader.Find("Custom/VertexColorUnlit"); // NEW

        if (sphereMaterial == null)
            sphereMaterial = new Material(_standardShader);
        if (lineMaterial == null)
            lineMaterial = new Material(_spriteDefaultShader);
        if (unisonMaterial == null)
        {
            unisonMaterial = new Material(_spriteDefaultShader);
            unisonMaterial.color = new Color(1f, 1f, 0.4f, 0.55f);
        }
    }

    void PreAllocateArrays()
    {
        int totalSpheres = config.nFreq * config.nPartials;

        sphereLocalPositions = new Vector3[totalSpheres];
        sphereWorldPositions = new Vector3[totalSpheres];
        sphereMatrices = new Matrix4x4[totalSpheres];

        int flagCount = (totalSpheres + 31) / 32;
        occupiedSpheresFlags = new uint[flagCount];
        highlightedSpheresFlags = new uint[flagCount];

        snappedObjects = new SnapGrabbable[totalSpheres];

        cachedUValues = new float[config.nFreq];
        _stringColors = new Color[config.nFreq];

        _sphereDrawBuffer = new Matrix4x4[Mathf.Min(totalSpheres, MaxInstancesPerDrawCall)];

        sphereScale = Vector3.one * ballRadius * 2f;
    }

    void CacheUValues()
    {
        for (int i = 0; i < config.nFreq; i++)
        {
            cachedUValues[i] = Mathf.Log(frequencies[i] / config.fStart, 2f);
        }
    }

    void CacheStringColors()
    {
        for (int i = 0; i < config.nFreq; i++)
        {
            _stringColors[i] = ViridisLookup((float)i / (config.nFreq - 1));
        }
    }

    Transform MakeChild(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(trackedContentRoot, false);
        go.transform.localPosition = Vector3.zero;
        return go.transform;
    }

    // -------------------------------------------------------------------------
    // KOORDINATEN-KONVERSION
    // -------------------------------------------------------------------------
    static Vector3 ToUnity(Vector3 dataSpace) => new Vector3(dataSpace.x, dataSpace.z, dataSpace.y);

    Vector3 BallPositionUnity(int i, int k)
    {
        float u = cachedUValues[i] + Mathf.Log(k + 1, 2f);
        float theta = StringFrameDataGenerator.HelixAngle(config, u);
        float r = config.helixR0M + i * config.helixDrM;

        float sinTheta = Mathf.Sin(theta);
        float cosTheta = Mathf.Cos(theta);

        return new Vector3(
            r * cosTheta,
            config.zNutM + config.helixZOffsetM + config.helixHOctM * u,
            r * sinTheta
        );
    }

    // -------------------------------------------------------------------------
    // GEOMETRIE-AUFBAU
    // -------------------------------------------------------------------------

    public bool IsSphereOccupied(int id)
    {
        ValidateSphereId(id);
        return (occupiedSpheresFlags[id >> 5] & (1u << (id & 31))) != 0;
    }

    public void RegisterSnap(int id, SnapGrabbable obj)
    {
        ValidateSphereId(id);
        occupiedSpheresFlags[id >> 5] |= (1u << (id & 31));
        snappedObjects[id] = obj;

        SetupSphereTriggerRelay(id, obj);
        UpdateSnappedColor(id, obj);
    }

    private void SetupSphereTriggerRelay(int id, SnapGrabbable obj)
    {
        if (obj == null) return;

        var rb = obj.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }

        GetSphereID(id, out int stringID, out int partialIndex);

        var oldRelay = obj.GetComponent<SphereTriggerRelay>();
        if (oldRelay != null) Destroy(oldRelay);
        var oldTriggers = obj.GetComponents<SphereCollider>();
        foreach (var c in oldTriggers)
            if (c.isTrigger) Destroy(c);

        var relay = obj.gameObject.AddComponent<SphereTriggerRelay>();
        relay.owner = this;
        relay.stringID = stringID;
        relay.partialIndex = partialIndex;

        var col = obj.gameObject.AddComponent<SphereCollider>();
        col.radius = ballRadius * 0.45f;
        col.isTrigger = true;

        NotifySphereSelected(obj.botIndex, stringID, partialIndex);
    }

    public void ReleaseSnap(int id)
    {
        ValidateSphereId(id);
        occupiedSpheresFlags[id >> 5] &= ~(1u << (id & 31));

        if (snappedObjects[id] != null)
        {
            var relay = snappedObjects[id].GetComponent<SphereTriggerRelay>();
            if (relay != null) Destroy(relay);

            var triggers = snappedObjects[id].GetComponents<SphereCollider>();
            foreach (var c in triggers)
                if (c.isTrigger) Destroy(c);

            snappedObjects[id] = null;
        }
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    void ValidateSphereId(int id)
    {
        int totalSpheres = config.nFreq * config.nPartials;
        if (id < 0 || id >= totalSpheres)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Sphere ID {id} out of range [0, {totalSpheres})");
    }

    void BuildSphereData()
    {
        EnsureSphereMesh();

        for (int i = 0; i < config.nFreq; i++)
        {
            for (int k = 0; k < config.nPartials; k++)
            {
                int flatIndex = i * config.nPartials + k;
                sphereLocalPositions[flatIndex] = Vector3.zero;
            }
        }

        if (spheresParent)
            spheresParent.gameObject.SetActive(!useInstancedSpheres);
    }

    void EnsureSphereMesh()
    {
        if (sphereMesh != null || !autoCreateSphereMesh)
            return;

        sphereMesh = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");

        if (sphereMesh == null)
        {
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            MeshFilter mf = temp.GetComponent<MeshFilter>();
            if (mf != null)
                sphereMesh = mf.sharedMesh;
            Destroy(temp);
        }
    }

    // -------------------------------------------------------------------------
    // OPTIMIERTES INSTANCED RENDERING
    // -------------------------------------------------------------------------

    void DrawInstancedSpheresOptimized()
    {
        if (sphereMesh == null || sphereMatrices == null || sphereMatrices.Length == 0)
            return;

        if (cullInstancedSpheresToPlayerView && playerCamera != null)
        {
            if (_frustumPlanesDirty)
            {
                GeometryUtility.CalculateFrustumPlanes(playerCamera, _frustumPlanes);
                _frustumPlanesDirty = false;
                _lastCameraPos = playerCamera.transform.position;
                _lastCameraRot = playerCamera.transform.rotation;
            }
            else if ((playerCamera.transform.position - _lastCameraPos).sqrMagnitude > 0.0001f ||
                     Quaternion.Angle(playerCamera.transform.rotation, _lastCameraRot) > 0.5f)
            {
                _frustumPlanesDirty = true;
            }
        }

        for (int i = 0; i < config.nFreq; i++)
        {
            _normalBatch.Clear();
            _occupiedBatch.Clear();
            _highlightBatch.Clear();

            Color stringColor = _stringColors[i];

            for (int k = 0; k < config.nPartials; k++)
            {
                int flatIndex = i * config.nPartials + k;

                if (cullInstancedSpheresToPlayerView && playerCamera != null)
                {
                    if (!IsPointInFrustum(sphereWorldPositions[flatIndex]))
                        continue;
                }

                if (IsSphereHighlighted(flatIndex))
                    _highlightBatch.Add(sphereMatrices[flatIndex]);
                else if (IsSphereOccupied(flatIndex))
                    _occupiedBatch.Add(sphereMatrices[flatIndex]);
                else
                    _normalBatch.Add(sphereMatrices[flatIndex]);
            }

            stringColor.a = 0.6f;

            if (_normalBatch.Count > 0)
            {
                var normalMat = GetCachedMaterial(stringColor, sphereMaterial);
                var normalRp = new RenderParams(normalMat)
                {
                    layer = gameObject.layer,
                    shadowCastingMode = sphereShadowCasting,
                    receiveShadows = sphereReceiveShadows,
                    worldBounds = new Bounds(trackedContentRoot.position, Vector3.one * 20f)
                };
                RenderMatrixBatch(normalRp, _normalBatch);
            }

            stringColor.a = 1f;

            if (_occupiedBatch.Count > 0)
            {
                var occupiedMat = GetCachedMaterial(stringColor, sphereMaterial);
                var occupiedRp = new RenderParams(occupiedMat)
                {
                    layer = gameObject.layer,
                    shadowCastingMode = sphereShadowCasting,
                    receiveShadows = sphereReceiveShadows,
                    worldBounds = new Bounds(trackedContentRoot.position, Vector3.one * 20f)
                };
                RenderMatrixBatch(occupiedRp, _occupiedBatch);
            }

            if (_highlightBatch.Count > 0)
            {
                var highlightMat = GetCachedMaterial(Color.white, sphereMaterial);
                var highlightRp = new RenderParams(highlightMat)
                {
                    layer = gameObject.layer,
                    shadowCastingMode = sphereShadowCasting,
                    receiveShadows = sphereReceiveShadows,
                    worldBounds = new Bounds(trackedContentRoot.position, Vector3.one * 20f)
                };
                RenderMatrixBatch(highlightRp, _highlightBatch);
            }
        }
    }

    void RenderMatrixBatch(RenderParams rp, List<Matrix4x4> matrices)
    {
        int count = matrices.Count;
        int offset = 0;

        while (offset < count)
        {
            int batchSize = Mathf.Min(count - offset, MaxInstancesPerDrawCall);
            matrices.CopyTo(offset, _sphereDrawBuffer, 0, batchSize);

            Graphics.RenderMeshInstanced(rp, sphereMesh, 0, _sphereDrawBuffer, batchSize);

            offset += batchSize;
        }
    }

    bool IsPointInFrustum(Vector3 worldPoint)
    {
        for (int i = 0; i < 6; i++)
        {
            if (_frustumPlanes[i].GetDistanceToPoint(worldPoint) < -viewportPadding * 10f)
                return false;
        }
        return true;
    }

    // -------------------------------------------------------------------------
    // HIGHLIGHTING
    // -------------------------------------------------------------------------

    public void HighlightSphere(int id, bool state)
    {
        ValidateSphereId(id);
        int flagIndex = id >> 5;
        uint bitMask = 1u << (id & 31);

        if (state)
            highlightedSpheresFlags[flagIndex] |= bitMask;
        else
            highlightedSpheresFlags[flagIndex] &= ~bitMask;
    }

    public bool IsSphereHighlighted(int id)
    {
        ValidateSphereId(id);
        return (highlightedSpheresFlags[id >> 5] & (1u << (id & 31))) != 0;
    }

    public void ClearSphereHighlights()
    {
        Array.Clear(highlightedSpheresFlags, 0, highlightedSpheresFlags.Length);
    }

    // -------------------------------------------------------------------------
    // BATCHED LINE RENDERING
    //
    // Replaces one LineRenderer-per-string/helix/unison-group/leg with a single
    // combined mesh per layer (ribbon quads with vertex color). Cuts draw calls
    // from ~2*nFreq + unisonGroups.Count + 5 down to 4 total. Geometry is only
    // rebuilt when UpdateDynamicGeometry() actually fires (param change), same
    // as before — no extra per-frame cost.
    //
    // Note: ribbon is built in a fixed local frame (tangent x world-up), not
    // camera-billboarded. Fine at these widths (a few mm); avoids needing a
    // per-frame CPU rebuild keyed on camera orientation.
    // -------------------------------------------------------------------------

    private struct PolylineSpec
    {
        public Vector3[] Points;
        public Color Color;
        public float Width;
    }

    private static (GameObject go, MeshFilter filter, MeshRenderer renderer, Mesh mesh) CreateBatchRenderer(
        string name, Transform parent, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var filter = go.AddComponent<MeshFilter>();
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        var mesh = new Mesh { name = name + "_BatchMesh" };
        mesh.indexFormat = IndexFormat.UInt32; // headroom for large nFreq/nPartials combos
        filter.sharedMesh = mesh;

        return (go, filter, renderer, mesh);
    }

    private static void BuildPolylineMesh(Mesh mesh, List<PolylineSpec> polylines)
    {
        int totalSegments = 0;
        for (int p = 0; p < polylines.Count; p++)
            totalSegments += Mathf.Max(0, polylines[p].Points.Length - 1);

        if (totalSegments == 0)
        {
            mesh.Clear();
            return;
        }

        var vertices = new Vector3[totalSegments * 4];
        var colors = new Color[totalSegments * 4];
        var triangles = new int[totalSegments * 6];

        int vi = 0, ti = 0;

        for (int p = 0; p < polylines.Count; p++)
        {
            var spec = polylines[p];
            var pts = spec.Points;
            float halfWidth = spec.Width * 0.5f;

            for (int s = 0; s < pts.Length - 1; s++)
            {
                Vector3 a = pts[s];
                Vector3 b = pts[s + 1];

                Vector3 tangent = b - a;
                if (tangent.sqrMagnitude < 1e-10f)
                    tangent = Vector3.forward;
                tangent.Normalize();

                Vector3 refUp = Mathf.Abs(Vector3.Dot(tangent, Vector3.up)) > 0.99f
                    ? Vector3.right
                    : Vector3.up;
                Vector3 side = Vector3.Cross(tangent, refUp).normalized * halfWidth;

                vertices[vi + 0] = a - side;
                vertices[vi + 1] = a + side;
                vertices[vi + 2] = b - side;
                vertices[vi + 3] = b + side;

                Color c = spec.Color.linear;
                colors[vi + 0] = c;
                colors[vi + 1] = c;
                colors[vi + 2] = c;
                colors[vi + 3] = c;

                triangles[ti + 0] = vi + 0;
                triangles[ti + 1] = vi + 1;
                triangles[ti + 2] = vi + 2;
                triangles[ti + 3] = vi + 1;
                triangles[ti + 4] = vi + 3;
                triangles[ti + 5] = vi + 2;

                vi += 4;
                ti += 6;
            }
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.colors = colors;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
    }

    // -------------------------------------------------------------------------
    // HELIX-LINIEN (batched)
    // -------------------------------------------------------------------------

    void BuildHelixMeshBatch()
    {
        var (go, filter, renderer, mesh) = CreateBatchRenderer("Helices_Batch", helicesParent, GetBatchVertexColorMaterial());
        helixFilter = filter;
        helixRenderer = renderer;
        helixMesh = mesh;
    }

    // -------------------------------------------------------------------------
    // STRING-LINIEN (batched)
    // -------------------------------------------------------------------------

    void BuildStringMeshBatch()
    {
        var (go, filter, renderer, mesh) = CreateBatchRenderer("Strings_Batch", stringsParent, GetBatchVertexColorMaterial());
        stringFilter = filter;
        stringRenderer = renderer;
        stringMesh = mesh;
    }

    // -------------------------------------------------------------------------
    // ANKER & BRIDGE (unchanged — already only 2 draw calls, static geometry, not worth batching further)
    // -------------------------------------------------------------------------

    void BuildAnchorAndBridgeLines()
    {
        var sg = new GameObject("Sattel");
        sg.transform.SetParent(anchorParent, false);
        sattelLine = sg.AddComponent<LineRenderer>();
        ConfigureLineRendererCached(sattelLine, Color.white, sattelLineWidth, config.nFreq);
        for (int i = 0; i < config.nFreq; i++)
            sattelLine.SetPosition(i, ToUnity(anchors[i]));

        var bg = new GameObject("Steg");
        bg.transform.SetParent(anchorParent, false);
        stegLine = bg.AddComponent<LineRenderer>();
        ConfigureLineRendererCached(stegLine, new Color(1f, 0.67f, 0.4f), stegLineWidth, config.nFreq);
        for (int i = 0; i < config.nFreq; i++)
            stegLine.SetPosition(i, ToUnity(bridgeEnds[i]));
    }

    // -------------------------------------------------------------------------
    // UNISON-LINIEN (batched)
    // -------------------------------------------------------------------------

    void BuildUnisonMeshBatch()
    {
        var (go, filter, renderer, mesh) = CreateBatchRenderer("Unison_Batch", unisonParent, GetBatchVertexColorMaterial());
        unisonFilter = filter;
        unisonRenderer = renderer;
        unisonMesh = mesh;
    }

    // -------------------------------------------------------------------------
    // BEINE (batched, built once — anchors/bridgeEnds are static after BuildAll,
    // same as the original per-LineRenderer version never rebuilt these either)
    // -------------------------------------------------------------------------

    void BuildLegsMeshBatch()
    {
        var a0 = ToUnity(anchors[0]);
        var aN = ToUnity(anchors[config.nFreq - 1]);

        Vector3 stegHighU = ToUnity(bridgeEnds[0]);
        for (int i = 1; i < config.nFreq; i++)
        {
            Vector3 b = ToUnity(bridgeEnds[i]);
            if (b.z > stegHighU.z) stegHighU = b;
        }

        Vector3[] corners = new Vector3[]
        {
            new Vector3(a0.x, 0, a0.z),
            new Vector3(aN.x, 0, aN.z),
            new Vector3(aN.x, 0, stegHighU.z),
            new Vector3(a0.x, 0, stegHighU.z),
        };

        Color legCol = new Color(0.4f, 0.4f, 0.4f, 0.7f);

        var polylines = new List<PolylineSpec>();

        for (int c = 0; c < 4; c++)
        {
            polylines.Add(new PolylineSpec
            {
                Points = new[] { corners[c], new Vector3(corners[c].x, config.zNutM, corners[c].z) },
                Color = legCol,
                Width = legLineWidth
            });
        }

        polylines.Add(new PolylineSpec
        {
            Points = new[] { corners[0], corners[1], corners[2], corners[3], corners[0] },
            Color = legCol,
            Width = legLineWidth * 0.8f
        });

        var (go, filter, renderer, mesh) = CreateBatchRenderer("Legs_Batch", legsParent, GetBatchVertexColorMaterial());
        legsFilter = filter;
        legsRenderer = renderer;
        legsMesh = mesh;

        BuildPolylineMesh(legsMesh, polylines);
    }

    // -------------------------------------------------------------------------
    // MATERIAL-CACHING
    // -------------------------------------------------------------------------

    Material GetCachedMaterial(Color color, Material template = null)
    {
        if (template == null) template = lineMaterial;

        if (_lineMaterialCache.TryGetValue(color, out var mat) && mat != null)
            return mat;

        mat = new Material(template) { color = color };
        _lineMaterialCache[color] = mat;
        return mat;
    }

    void ConfigureLineRendererCached(LineRenderer lr, Color color, float width, int positionCount)
    {
        lr.material = GetCachedMaterial(color);
        lr.startColor = color;
        lr.endColor = color;
        lr.startWidth = width;
        lr.endWidth = width;
        lr.useWorldSpace = false;
        lr.positionCount = positionCount;
        lr.numCapVertices = 0; // caps invisible at these widths, cuts extra geometry
    }

    void CleanupMaterials()
    {
        foreach (var mat in _lineMaterialCache.Values)
        {
            if (mat == null) continue;
            if (Application.isPlaying) Destroy(mat); else DestroyImmediate(mat);
        }
        _lineMaterialCache.Clear();

        if (_batchVertexColorMaterial != null) // NEW
        {
            if (Application.isPlaying) Destroy(_batchVertexColorMaterial); else DestroyImmediate(_batchVertexColorMaterial);
            _batchVertexColorMaterial = null;
        }
    }

    // -------------------------------------------------------------------------
    // DYNAMISCHE GEOMETRIE-UPDATE
    // -------------------------------------------------------------------------

    void UpdateDynamicGeometry()
    {
        // 1. Kugel-Positionen (lokal)
        for (int i = 0; i < config.nFreq; i++)
        {
            for (int k = 0; k < config.nPartials; k++)
            {
                int flatIndex = i * config.nPartials + k;
                sphereLocalPositions[flatIndex] = BallPositionUnity(i, k);
            }
        }

        // 2. Helix-Kurven (batched)
        var helixPolylines = new List<PolylineSpec>(config.nFreq);
        for (int i = 0; i < config.nFreq; i++)
        {
            float uMin = cachedUValues[i];
            float uMax = cachedUValues[i] + Mathf.Log(config.nPartials, 2f);

            var points = new Vector3[config.helixSamples];
            for (int s = 0; s < config.helixSamples; s++)
            {
                float t = (float)s / (config.helixSamples - 1);
                float u = Mathf.Lerp(uMin, uMax, t);
                float theta = StringFrameDataGenerator.HelixAngle(config, u);
                float r = config.helixR0M + i * config.helixDrM;

                float sinTheta = Mathf.Sin(theta);
                float cosTheta = Mathf.Cos(theta);

                points[s] = new Vector3(
                    r * cosTheta,
                    config.zNutM + config.helixZOffsetM + config.helixHOctM * u,
                    r * sinTheta
                );
            }

            helixPolylines.Add(new PolylineSpec
            {
                Points = points,
                Color = _stringColors[i],
                Width = helixLineWidth
            });
        }
        BuildPolylineMesh(helixMesh, helixPolylines);

        // 3. Saiten-Linien (batched)
        var stringPolylines = new List<PolylineSpec>(config.nFreq);
        for (int i = 0; i < config.nFreq; i++)
        {
            Vector3 steg = ToUnity(bridgeEnds[i]);
            Vector3 sat = ToUnity(anchors[i]);
            Vector3 k1 = BallPositionUnity(i, 0);
            Vector3 ctrl = new Vector3((sat.x + k1.x) * 0.5f,
                                        config.zNutM + 0.02f,
                                        (sat.z + k1.z) * 0.5f);

            var points = new Vector3[2 + BezierSamples];
            int idx = 0;
            points[idx++] = steg;
            points[idx++] = sat;

            for (int s = 1; s <= BezierSamples; s++)
            {
                float t = (float)s / BezierSamples;
                points[idx++] = QuadBezier(sat, ctrl, k1, t);
            }

            stringPolylines.Add(new PolylineSpec
            {
                Points = points,
                Color = _stringColors[i],
                Width = stringLineWidth
            });
        }
        BuildPolylineMesh(stringMesh, stringPolylines);

        // 4. Unisono-Linien (batched)
        var unisonColor = unisonMaterial != null ? unisonMaterial.color : new Color(1f, 1f, 0.4f, 0.55f);
        var unisonPolylines = new List<PolylineSpec>(unisonGroups.Count);
        for (int g = 0; g < unisonGroups.Count; g++)
        {
            var members = unisonGroups[g];
            var points = new Vector3[members.Length];
            for (int m = 0; m < members.Length; m++)
                points[m] = BallPositionUnity(members[m][0], members[m][1]);

            unisonPolylines.Add(new PolylineSpec
            {
                Points = points,
                Color = unisonColor,
                Width = unisonLineWidth
            });
        }
        BuildPolylineMesh(unisonMesh, unisonPolylines);
    }

    // -------------------------------------------------------------------------
    // KUGEL-POSITIONEN UPDATE — Fused Single-Pass
    // -------------------------------------------------------------------------

    void UpdateSphereMatrices()
    {
        Matrix4x4 root = trackedContentRoot.localToWorldMatrix;
        int totalSpheres = config.nFreq * config.nPartials;

        // Pre-compute scale matrix once
        Matrix4x4 scaleMatrix = Matrix4x4.Scale(sphereScale);

        for (int i = 0; i < totalSpheres; i++)
        {
            Vector3 worldPos = root.MultiplyPoint3x4(sphereLocalPositions[i]);
            sphereWorldPositions[i] = worldPos;

            // sphereMatrices[i] = root * Translate(localPos) * Scale
            // Since spheres have no rotation, we can build directly
            sphereMatrices[i] = root * Matrix4x4.TRS(sphereLocalPositions[i], Quaternion.identity, sphereScale);
        }
    }

    // -------------------------------------------------------------------------
    // LAYER-SICHTBARKEIT
    // -------------------------------------------------------------------------

    void SetLayerVisibility(bool visible)
    {
        if (spheresParent) spheresParent.gameObject.SetActive(!useInstancedSpheres && visible && showSpheres);
        if (helicesParent) helicesParent.gameObject.SetActive(visible && showHelices);
        if (stringsParent) stringsParent.gameObject.SetActive(visible && showStrings);
        if (anchorParent) anchorParent.gameObject.SetActive(visible && showAnchor);
        if (unisonParent) unisonParent.gameObject.SetActive(visible && showUnison);
        if (legsParent) legsParent.gameObject.SetActive(visible && showLegs);
    }

    // -------------------------------------------------------------------------
    // INTERACTION
    // -------------------------------------------------------------------------

    void UpdateSnappedObjects()
    {
        if (snappedObjects == null) return;

        int totalSpheres = config.nFreq * config.nPartials;
        for (int i = 0; i < totalSpheres; i++)
        {
            if (snappedObjects[i] == null) continue;

            GetSphereID(i, out int stringID, out int partialIndex);
            Vector3 pos = GetSphereWorldPosition(stringID, partialIndex);
            snappedObjects[i].UpdateSnapPosition(pos);
        }
    }

    public void NotifySphereSelected(int botID, int stringID, int partialIndex)
    {
        if (stringID < 0 || stringID >= config.nFreq)
            return;

        if (partialIndex < 0 || partialIndex >= config.nPartials)
            return;

        int flatIndex = stringID * config.nPartials + partialIndex;
        Vector3 worldPosition = sphereWorldPositions[flatIndex];

        float frequencyHz = frequencies[stringID] * (partialIndex + 1);
        int harmonic = partialIndex + 1;

        if (subscribeManager != null)
        {
            subscribeManager.SendSphereSelected(
                botID,
                stringID,
                partialIndex
            );
        }

        if (interactionDebugLogs)
        {
            Debug.Log(
                $"[StringFrame3D] Sphere selected: stringID={stringID}, " +
                $"partialIndex={partialIndex}, harmonic={harmonic}, " +
                $"frequencyHz={frequencyHz:F3}"
            );
        }
    }

    public void NotifySphereTriggered(int stringID, int partialIndex, Collider other = null)
    {
        if (!string.IsNullOrEmpty(playerTag) && (other == null || !other.CompareTag(playerTag)))
            return;

        if (stringID < 0 || stringID >= config.nFreq)
            return;

        if (partialIndex < 0 || partialIndex >= config.nPartials)
            return;

        int flatIndex = stringID * config.nPartials + partialIndex;
        Vector3 worldPosition = sphereWorldPositions[flatIndex];

        float frequencyHz = frequencies[stringID] * (partialIndex + 1);
        int harmonic = partialIndex + 1;

        if (subscribeManager != null)
        {
            subscribeManager.SendSphereTriggered(
                stringID,
                partialIndex,
                frequencyHz,
                harmonic,
                worldPosition
            );
        }

        if (interactionDebugLogs)
        {
            Debug.Log(
                $"[StringFrame3D] Sphere triggered: stringID={stringID}, " +
                $"partialIndex={partialIndex}, harmonic={harmonic}, " +
                $"frequencyHz={frequencyHz:F3}"
            );
        }
    }

    public bool CanTriggerSphere(int stringID, int partialIndex)
    {
        int flatIndex = stringID * config.nPartials + partialIndex;
        return IsSphereOccupied(flatIndex);
    }

    public Color GetSphereColor(int id)
    {
        ValidateSphereId(id);
        int stringID = id / config.nPartials;
        return _stringColors[stringID];
    }

    public void UpdateSnappedColor(int id, SnapGrabbable obj)
    {
        if (obj == null) return;

        Color color = GetSphereColor(id);
        color.a = 0.45f;
        obj.SetTransparent(0.45f);
        obj.SetSnapColor(color);
    }

    // -------------------------------------------------------------------------
    // HILFSMETHODEN
    // -------------------------------------------------------------------------

    public Vector3 GetSphereWorldPosition(int stringID, int partialIndex)
    {
        if (sphereWorldPositions == null || stringID < 0 || stringID >= config.nFreq ||
            partialIndex < 0 || partialIndex >= config.nPartials)
            return transform.position;

        return sphereWorldPositions[stringID * config.nPartials + partialIndex];
    }

    public int SphereCount()
    {
        return config.nFreq * config.nPartials;
    }

    public void GetSphereID(int index, out int stringID, out int partialIndex)
    {
        stringID = index / config.nPartials;
        partialIndex = index % config.nPartials;
    }

    static Vector3 QuadBezier(Vector3 a, Vector3 b, Vector3 c, float t)
    {
        float u = 1f - t;
        float u2 = u * u;
        float t2 = t * t;
        float ut2 = 2f * u * t;
        return u2 * a + ut2 * b + t2 * c;
    }

    // -------------------------------------------------------------------------
    // FARBE: Viridis Lookup-Table
    // -------------------------------------------------------------------------

    private static readonly Color[] _viridisTable = GenerateViridisTable();

    private static Color[] GenerateViridisTable()
    {
        var table = new Color[256];
        for (int i = 0; i < 256; i++)
        {
            float t = i / 255f;
            table[i] = ViridisCalculate(t);
        }
        return table;
    }

    static Color ViridisLookup(float t)
    {
        t = Mathf.Clamp01(t);
        int index = Mathf.RoundToInt(t * 255f);
        return _viridisTable[index];
    }

    static Color ViridisCalculate(float t)
    {
        float r = 0.267f + t * (-0.4f + t * (2.6f - t * 1.5f));
        float g = 0.005f + t * (1.4f - t * 0.5f);
        float b = 0.329f + t * (0.7f - t * 0.95f);
        return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
    }

    public void SetSphereTriggerCooldown(int sphereIndex)
    {
        GetSphereID(sphereIndex, out int stringID, out int partialIndex);

        SphereTriggerRelay relay = GetSphereTriggerRelay(stringID, partialIndex);
        if (relay != null)
        {
            relay.lastTriggerTime = Time.time;
        }
    }

    private SphereTriggerRelay GetSphereTriggerRelay(int stringID, int partialIndex)
    {
        int flatIndex = stringID * config.nPartials + partialIndex;

        if (flatIndex < 0 || flatIndex >= snappedObjects.Length)
            return null;

        SnapGrabbable obj = snappedObjects[flatIndex];
        if (obj == null)
            return null;

        return obj.GetComponent<SphereTriggerRelay>();
    }
}