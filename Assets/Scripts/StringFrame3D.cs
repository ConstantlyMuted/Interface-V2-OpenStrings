// =============================================================================
// StringFrame3D.cs
// 
// Unity-Version der StringFrame3D-Visualisierung (V18: dünne Linien,
// sinusoidale Helix-Reflexion bei reduziertem Bogen).
//
// Teilt sich `StringFrameConfig` mit `StringFrameDataGenerator.cs`, sodass
// beide Skripte identische Berechnungen durchführen.
//
// EINRICHTUNG IN UNITY:
//   1. `StringFrameDataGenerator.cs` und `StringFrame3D.cs` und 
//      `OrbitCamera.cs` in Assets/Scripts/ kopieren.
//   2. Leeres GameObject "StringFrame3D" erstellen, dieses Skript daran hängen.
//   3. Hauptkamera auswählen, `OrbitCamera.cs` als Komponente daran hängen,
//      `Target` auf das StringFrame3D-GameObject ziehen.
//   4. Play drücken — Helix erscheint vor der Kamera.
//
// STEUERUNG (Inspector im Play-Mode anpassbar):
//   - config.helixArcDeg: 30°–360° Bogen-Winkel
//   - config.helixR0M:    Innenradius
//   - config.helixDrM:    Radius-Schritt zwischen Saiten
//   - showSpheres / showHelices / showStrings / showAnchor / showUnison / showLegs
// =============================================================================

using System.Collections.Generic;
using UnityEngine;

public class StringFrame3D : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // KONFIGURATION (geteilt mit StringFrameDataGenerator)
    // -------------------------------------------------------------------------
    [Header("Modell-Konfiguration")]
    public StringFrameConfig config = new StringFrameConfig();

    [Header("Layer-Toggle")]
    public bool showSpheres = true;
    public bool showHelices = true;
    public bool showStrings = true;     // physische Saiten + Bezier
    public bool showAnchor  = true;     // Sattel + Steg
    public bool showUnison  = true;
    public bool showLegs    = true;

    [Header("Linien-Stärken (V18 = dünn)")]
    public float helixLineWidth   = 0.0020f;   // 2 mm in Welt-Einheiten
    public float stringLineWidth  = 0.0020f;
    public float sattelLineWidth  = 0.0050f;
    public float stegLineWidth    = 0.0035f;
    public float unisonLineWidth  = 0.0015f;
    public float legLineWidth     = 0.0030f;

    [Header("Materials (optional)")]
    public Material sphereMaterial;
    public Material lineMaterial;
    public Material unisonMaterial;

    [Header("Ball-Größe")]
    [Range(0.005f, 0.05f)] public float ballRadius = 0.025f;

    // -------------------------------------------------------------------------
    // INTERNE STATE
    // -------------------------------------------------------------------------
    private float[] frequencies;
    private float[] stringLengths;
    private float[,] rotationMatrix;
    private float Y36zMm;
    private float rot2Rad;
    private Vector3[] anchors;        // Sattel-Punkte (Daten-Konvention: x,y=Tiefe,z=Höhe)
    private Vector3[] bridgeEnds;     // Steg-Endpunkte
    private List<int[][]> unisonGroups;

    private Transform spheresParent, helicesParent, stringsParent;
    private Transform anchorParent, unisonParent, legsParent;

    private Transform[,] sphereMeshes;
    private LineRenderer[] helixLineRenderers;
    private LineRenderer[] stringLineRenderers;
    private LineRenderer sattelLine, stegLine;
    private List<LineRenderer> unisonLineRenderers;

    private bool initialized = false;
    private float lastArcDeg, lastR0, lastDr;

    // -------------------------------------------------------------------------
    // UNITY EVENTS
    // -------------------------------------------------------------------------
    void Start()
    {
        BuildAll();
        initialized = true;
    }

    void Update()
    {
        if (!initialized) return;

        // Live-Update bei Slider-Bewegung im Inspector (Play-Mode)
        if (config.helixArcDeg != lastArcDeg
         || config.helixR0M    != lastR0
         || config.helixDrM    != lastDr)
        {
            UpdateDynamicGeometry();
            lastArcDeg = config.helixArcDeg;
            lastR0    = config.helixR0M;
            lastDr    = config.helixDrM;
        }

        // Layer-Sichtbarkeit
        if (spheresParent) spheresParent.gameObject.SetActive(showSpheres);
        if (helicesParent) helicesParent.gameObject.SetActive(showHelices);
        if (stringsParent) stringsParent.gameObject.SetActive(showStrings);
        if (anchorParent)  anchorParent.gameObject.SetActive(showAnchor);
        if (unisonParent)  unisonParent.gameObject.SetActive(showUnison);
        if (legsParent)    legsParent.gameObject.SetActive(showLegs);
    }

    // -------------------------------------------------------------------------
    // SETUP
    // -------------------------------------------------------------------------
    void BuildAll()
    {
        EnsureDefaultMaterials();

        spheresParent = MakeChild("Spheres");
        helicesParent = MakeChild("Helices");
        stringsParent = MakeChild("Strings");
        anchorParent  = MakeChild("Anchor");
        unisonParent  = MakeChild("Unison");
        legsParent    = MakeChild("Legs");

        // === BERECHNUNGEN (über StringFrameDataGenerator-Methoden) ===
        // Wir nutzen die exakt gleichen Formeln wie der JSON-Generator.
        frequencies   = StringFrameDataGenerator.ComputeFrequencies(config);
        stringLengths = StringFrameDataGenerator.ComputeStringLengths(config);
        StringFrameDataGenerator.ComputeTwoRotations(config, out rot2Rad, out Y36zMm, out rotationMatrix);
        StringFrameDataGenerator.ComputeAnchorsAndBridge(config, stringLengths, rotationMatrix, Y36zMm,
                                                          out anchors, out bridgeEnds);
        unisonGroups = StringFrameDataGenerator.ComputeUnisonGroupsRaw(config);

        // === GAMEOBJECTS ERSTELLEN ===
        BuildSphereMeshes();
        BuildHelixLineRenderers();
        BuildStringLineRenderers();
        BuildAnchorAndBridgeLines();
        BuildUnisonLines();
        BuildLegs();

        UpdateDynamicGeometry();

        lastArcDeg = config.helixArcDeg;
        lastR0    = config.helixR0M;
        lastDr    = config.helixDrM;
    }

    void EnsureDefaultMaterials()
    {
        if (sphereMaterial == null)
            sphereMaterial = new Material(Shader.Find("Standard"));
        if (lineMaterial == null)
            lineMaterial = new Material(Shader.Find("Sprites/Default"));
        if (unisonMaterial == null)
        {
            unisonMaterial = new Material(Shader.Find("Sprites/Default"));
            unisonMaterial.color = new Color(1f, 1f, 0.4f, 0.55f);
        }
    }

    Transform MakeChild(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform);
        go.transform.localPosition = Vector3.zero;
        return go.transform;
    }

    // -------------------------------------------------------------------------
    // KOORDINATEN-KONVERSION
    // 
    // Daten-Konvention (Generator/JSON): x=horizontal, y=Tiefe, z=Höhe
    // Unity-Konvention:                  x=horizontal, y=Höhe (oben), z=Tiefe
    // 
    // Wir tauschen y und z beim Übergang.
    // -------------------------------------------------------------------------
    static Vector3 ToUnity(Vector3 dataSpace) => new Vector3(dataSpace.x, dataSpace.z, dataSpace.y);

    Vector3 BallPositionUnity(int i, int k)
    {
        float f = frequencies[i] * (k + 1);
        float u = Mathf.Log(f / config.fStart, 2f);
        float theta = StringFrameDataGenerator.HelixAngle(config, u);
        float r = config.helixR0M + i * config.helixDrM;
        // Daten: (r·cos, r·sin, z_nut + offset + H_oct·u)
        return new Vector3(
            r * Mathf.Cos(theta),
            config.zNutM + config.helixZOffsetM + config.helixHOctM * u,
            r * Mathf.Sin(theta)
        );
    }

    // -------------------------------------------------------------------------
    // GEOMETRIE-AUFBAU
    // -------------------------------------------------------------------------
    void BuildSphereMeshes()
    {
        sphereMeshes = new Transform[config.nFreq, config.nPartials];
        for (int i = 0; i < config.nFreq; i++)
        {
            Color c = Viridis((float)i / (config.nFreq - 1));
            for (int k = 0; k < config.nPartials; k++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Ball_{i:D2}_{k:D2}";
                go.transform.SetParent(spheresParent);
                go.transform.localScale = Vector3.one * ballRadius * 2f;
                var mat = new Material(sphereMaterial);
                mat.color = c;
                go.GetComponent<Renderer>().material = mat;
                // Collider entfernen — wir brauchen sie nicht
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                sphereMeshes[i, k] = go.transform;
            }
        }
    }

    void BuildHelixLineRenderers()
    {
        helixLineRenderers = new LineRenderer[config.nFreq];
        for (int i = 0; i < config.nFreq; i++)
        {
            var go = new GameObject($"Helix_{i:D2}");
            go.transform.SetParent(helicesParent);
            var lr = go.AddComponent<LineRenderer>();
            ConfigureLineRenderer(lr, Viridis((float)i / (config.nFreq - 1)),
                                   helixLineWidth, config.helixSamples);
            helixLineRenderers[i] = lr;
        }
    }

    void BuildStringLineRenderers()
    {
        // Pro Saite: Steg → Sattel → Bezier (zu k=1)
        const int BezierSamples = 20;
        stringLineRenderers = new LineRenderer[config.nFreq];
        for (int i = 0; i < config.nFreq; i++)
        {
            var go = new GameObject($"String_{i:D2}");
            go.transform.SetParent(stringsParent);
            var lr = go.AddComponent<LineRenderer>();
            ConfigureLineRenderer(lr, Viridis((float)i / (config.nFreq - 1)),
                                   stringLineWidth, 2 + BezierSamples);
            stringLineRenderers[i] = lr;
        }
    }

    void BuildAnchorAndBridgeLines()
    {
        // Sattel
        var sg = new GameObject("Sattel");
        sg.transform.SetParent(anchorParent);
        sattelLine = sg.AddComponent<LineRenderer>();
        ConfigureLineRenderer(sattelLine, Color.white, sattelLineWidth, config.nFreq);
        for (int i = 0; i < config.nFreq; i++)
            sattelLine.SetPosition(i, ToUnity(anchors[i]));

        // Steg
        var bg = new GameObject("Steg");
        bg.transform.SetParent(anchorParent);
        stegLine = bg.AddComponent<LineRenderer>();
        ConfigureLineRenderer(stegLine, new Color(1f, 0.67f, 0.4f), stegLineWidth, config.nFreq);
        for (int i = 0; i < config.nFreq; i++)
            stegLine.SetPosition(i, ToUnity(bridgeEnds[i]));
    }

    void BuildUnisonLines()
    {
        unisonLineRenderers = new List<LineRenderer>();
        for (int g = 0; g < unisonGroups.Count; g++)
        {
            var go = new GameObject($"Unison_{g:D3}");
            go.transform.SetParent(unisonParent);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = unisonMaterial;
            lr.startWidth = unisonLineWidth;
            lr.endWidth   = unisonLineWidth;
            lr.useWorldSpace = true;
            lr.positionCount = unisonGroups[g].Length;
            unisonLineRenderers.Add(lr);
        }
    }

    void BuildLegs()
    {
        var a0 = ToUnity(anchors[0]);
        var aN = ToUnity(anchors[config.nFreq - 1]);

        // Höchste Steg-Position (Tiefe) finden
        Vector3 stegHighU = ToUnity(bridgeEnds[0]);
        for (int i = 1; i < config.nFreq; i++)
        {
            Vector3 b = ToUnity(bridgeEnds[i]);
            if (b.z > stegHighU.z) stegHighU = b;
        }

        Vector3[] corners =
        {
            new Vector3(a0.x, 0, a0.z),
            new Vector3(aN.x, 0, aN.z),
            new Vector3(aN.x, 0, stegHighU.z),
            new Vector3(a0.x, 0, stegHighU.z),
        };

        Color legCol = new Color(0.4f, 0.4f, 0.4f, 0.7f);
        for (int c = 0; c < 4; c++)
        {
            var go = new GameObject($"Leg_{c}");
            go.transform.SetParent(legsParent);
            var lr = go.AddComponent<LineRenderer>();
            ConfigureLineRenderer(lr, legCol, legLineWidth, 2);
            lr.SetPosition(0, corners[c]);
            lr.SetPosition(1, new Vector3(corners[c].x, config.zNutM, corners[c].z));
        }

        // Bodenrahmen
        var fr = new GameObject("FloorFrame");
        fr.transform.SetParent(legsParent);
        var lf = fr.AddComponent<LineRenderer>();
        ConfigureLineRenderer(lf, legCol, legLineWidth * 0.8f, 5);
        for (int i = 0; i < 4; i++) lf.SetPosition(i, corners[i]);
        lf.SetPosition(4, corners[0]);
    }

    void ConfigureLineRenderer(LineRenderer lr, Color color, float width, int positionCount)
    {
        var mat = new Material(lineMaterial);
        mat.color = color;
        lr.material = mat;
        lr.startColor = color;
        lr.endColor = color;
        lr.startWidth = width;
        lr.endWidth = width;
        lr.useWorldSpace = true;
        lr.positionCount = positionCount;
        lr.numCapVertices = 2;
    }

    // -------------------------------------------------------------------------
    // UPDATE: Positionen neu berechnen, wenn Parameter geändert
    // -------------------------------------------------------------------------
    void UpdateDynamicGeometry()
    {
        // 1. Bälle
        for (int i = 0; i < config.nFreq; i++)
            for (int k = 0; k < config.nPartials; k++)
                sphereMeshes[i, k].position = BallPositionUnity(i, k);

        // 2. Helix-Kurven
        for (int i = 0; i < config.nFreq; i++)
        {
            float uMin = Mathf.Log(frequencies[i] / config.fStart, 2f);
            float uMax = Mathf.Log(config.nPartials * frequencies[i] / config.fStart, 2f);
            var lr = helixLineRenderers[i];
            if (lr.positionCount != config.helixSamples)
                lr.positionCount = config.helixSamples;
            for (int s = 0; s < config.helixSamples; s++)
            {
                float u = uMin + (uMax - uMin) * s / (config.helixSamples - 1);
                float theta = StringFrameDataGenerator.HelixAngle(config, u);
                float r = config.helixR0M + i * config.helixDrM;
                lr.SetPosition(s, new Vector3(
                    r * Mathf.Cos(theta),
                    config.zNutM + config.helixZOffsetM + config.helixHOctM * u,
                    r * Mathf.Sin(theta)
                ));
            }
        }

        // 3. Saiten-Linien (Steg → Sattel → Bezier → k=1)
        const int BezierSamples = 20;
        for (int i = 0; i < config.nFreq; i++)
        {
            var lr = stringLineRenderers[i];
            Vector3 steg = ToUnity(bridgeEnds[i]);
            Vector3 sat  = ToUnity(anchors[i]);
            Vector3 k1   = BallPositionUnity(i, 0);
            Vector3 ctrl = new Vector3((sat.x + k1.x) / 2f,
                                        config.zNutM + 0.02f,
                                        (sat.z + k1.z) / 2f);

            int idx = 0;
            lr.SetPosition(idx++, steg);
            lr.SetPosition(idx++, sat);
            for (int s = 1; s <= BezierSamples; s++)
            {
                float t = (float)s / BezierSamples;
                Vector3 p = QuadBezier(sat, ctrl, k1, t);
                lr.SetPosition(idx++, p);
            }
        }

        // 4. Unisono-Linien (radiale Polylinien)
        for (int g = 0; g < unisonGroups.Count; g++)
        {
            var lr = unisonLineRenderers[g];
            var members = unisonGroups[g];
            for (int m = 0; m < members.Length; m++)
                lr.SetPosition(m, BallPositionUnity(members[m][0], members[m][1]));
        }
    }

    static Vector3 QuadBezier(Vector3 a, Vector3 b, Vector3 c, float t)
    {
        float u = 1 - t;
        return u * u * a + 2 * u * t * b + t * t * c;
    }

    // -------------------------------------------------------------------------
    // FARBVERLAUF (Viridis-Approximation)
    // -------------------------------------------------------------------------
    static Color Viridis(float t)
    {
        t = Mathf.Clamp01(t);
        float r = 0.267f + t * (-0.4f + t * (2.6f - t * 1.5f));
        float g = 0.005f + t * (1.4f - t * 0.5f);
        float b = 0.329f + t * (0.7f - t * 0.95f);
        return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
    }
}
