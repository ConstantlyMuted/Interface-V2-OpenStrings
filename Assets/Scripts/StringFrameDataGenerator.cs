// =============================================================================
// StringFrameDataGenerator.cs
//
// Berechnet alle Geometrie- und Akustik-Daten für das StringFrame3D-Modell und
// schreibt sie als string_frame_data.json. Identisch zum Python-Generator.
//
// AUFBAU:
//   - StringFrameConfig: einfache POCO-Klasse mit allen Parametern
//   - StringFrameDataGenerator: statische Bibliothek mit allen Berechnungen
//     und JSON-Ausgabe — keine MonoBehaviour, kein Unity-spezifisches Setup
//   - StringFrameDataGeneratorRunner: optionale MonoBehaviour-Komponente,
//     die im Inspector Parameter zeigt und im ContextMenu "Generate" anbietet
//   - Editor-Menü "Tools / StringFrame3D / Generate JSON" (verwendet Defaults)
//
// VERWENDUNG:
//   1. Editor-Menü:    Tools / StringFrame3D / Generate JSON
//   2. Komponente:     leeres GameObject + StringFrameDataGeneratorRunner
//                      → ContextMenu-Eintrag "Generate JSON now"
//   3. Aus eigenem Code:
//        var cfg = new StringFrameConfig();
//        StringFrameDataGenerator.GenerateAndWrite(cfg, "Assets/Resources/string_frame_data.json");
//
// AUSGABE:
//   string_frame_data.json mit identischer Struktur zum Python-Generator,
//   kompatibel zum Three.js-Viewer und allen anderen Konsumenten.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif


// =============================================================================
// KONFIGURATION
// =============================================================================
[Serializable]
public class StringFrameConfig
{
    // Frequenz & Akustik
    public float fStart      = 32.7f;
    public int   nFreq       = 37;
    public int   nPartials   = 25;

    // Saiten-Geometrie (Instrument)
    public float strLenMaxMm    = 1300f;
    public float strLenMinMm    =  938f;
    public float stringPerpMm   =   10f;
    public float bridgeAngleDeg = 105f;
    public float rot1Deg        =  22f;     // Kippung nach hinten
    public float zNutM          = 0.77f;    // Sattel-Höhe

    // Helix-Parameter
    public float helixR0M       = 1.80f;
    public float helixDrM       = 0.080f;
    public float helixHOctM     = 0.18f;
    public float helixZOffsetM  = 0.10f;
    public float helixAlpha     = 1.07f;
    public float helixArcDeg    = 360f;

    // Sampling
    public int   helixSamples   = 200;
}


// =============================================================================
// HAUPTGENERATOR (statisch, unabhängig von MonoBehaviour)
// =============================================================================
public static class StringFrameDataGenerator
{
    public const float ArcCenterRad = -Mathf.PI / 2f;
    public const float DegToRad     = Mathf.PI / 180f;
    public const float RadToDeg     = 180f / Mathf.PI;

    // -------------------------------------------------------------------------
    // EINSPRUNGSPUNKT
    // -------------------------------------------------------------------------
    public static void GenerateAndWrite(StringFrameConfig cfg, string path)
    {
        Debug.Log("[StringFrameDataGenerator] Starte Berechnung…");

        var frequencies   = ComputeFrequencies(cfg);
        var stringLengths = ComputeStringLengths(cfg);

        ComputeTwoRotations(cfg, out float rot2Rad, out float Y36zMm, out float[,] R);
        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[StringFrameDataGenerator] Y36_z = {0:F2}mm, Rot1 = {1:F2}°, Rot2 = {2:F4}°",
            Y36zMm, cfg.rot1Deg, rot2Rad * RadToDeg));

        ComputeAnchorsAndBridge(cfg, stringLengths, R, Y36zMm, out var anchors, out var bridgeEnds);

        var spheres      = ComputeBalls(cfg, frequencies);
        var helixCurves  = ComputeHelixCurves(cfg, frequencies);
        var unisonLines  = ComputeUnisonLines(cfg, frequencies);

        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[StringFrameDataGenerator] {0} Unisono-Gruppen ({1} Bälle)",
            unisonLines.Count, CountTotalUnisonMembers(unisonLines)));

        VerifyGeometry(cfg, anchors, bridgeEnds);

        string json = BuildJson(cfg, frequencies, stringLengths, anchors, bridgeEnds,
                                 spheres, helixCurves, unisonLines, rot2Rad);

        // Pfad relativ zum Projekt-Wurzelverzeichnis (= Application.dataPath/..)
        string fullPath = ResolvePath(path);
        string dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, json, Encoding.UTF8);

        var info = new FileInfo(fullPath);
        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[StringFrameDataGenerator] Geschrieben: {0} ({1:F0} KB)",
            fullPath, info.Length / 1024.0));
    }

    static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        // relativ zum Projekt-Root (über Application.dataPath = "<Projekt>/Assets")
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        return Path.GetFullPath(Path.Combine(projectRoot, path));
    }

    // -------------------------------------------------------------------------
    // 1. FREQUENZEN UND SAITENLÄNGEN
    // -------------------------------------------------------------------------
    public static float[] ComputeFrequencies(StringFrameConfig cfg)
    {
        var f = new float[cfg.nFreq];
        for (int i = 0; i < cfg.nFreq; i++)
            f[i] = cfg.fStart * (16 + i) / 16f;
        return f;
    }

    public static float[] ComputeStringLengths(StringFrameConfig cfg)
    {
        var L = new float[cfg.nFreq];
        for (int i = 0; i < cfg.nFreq; i++)
        {
            float t = (float)i / (cfg.nFreq - 1);
            float Lmm = cfg.strLenMaxMm - t * (cfg.strLenMaxMm - cfg.strLenMinMm);
            L[i] = Lmm / 1000f;
        }
        return L;
    }

    // -------------------------------------------------------------------------
    // 2. ZWEI-ROTATIONS-GEOMETRIE
    // -------------------------------------------------------------------------
    public static void ComputeTwoRotations(StringFrameConfig cfg,
                                     out float rot2Rad, out float Y36zMm, out float[,] R)
    {
        float Wperp = (cfg.nFreq - 1) * cfg.stringPerpMm / 1000f;
        float Ldiff = (cfg.strLenMaxMm - cfg.strLenMinMm) / 1000f;
        float cosBridge = Mathf.Cos(cfg.bridgeAngleDeg * DegToRad);
        float cos2 = cosBridge * cosBridge;
        float Y36z = Ldiff - Wperp * Mathf.Sqrt(cos2 / (1 - cos2));
        Y36zMm = Y36z * 1000f;

        float rot1Rad = cfg.rot1Deg * DegToRad;
        rot2Rad = Mathf.Atan2(Y36z * Mathf.Cos(rot1Rad), Wperp);

        var R1 = MatrixRx(-rot1Rad);
        var R2 = MatrixRy(rot2Rad);
        R = MatMul(R2, R1);
    }

    static float[,] MatrixRx(float a)
    {
        float c = Mathf.Cos(a), s = Mathf.Sin(a);
        return new float[3, 3] { { 1, 0, 0 }, { 0, c, s }, { 0, -s, c } };
    }
    static float[,] MatrixRy(float a)
    {
        float c = Mathf.Cos(a), s = Mathf.Sin(a);
        return new float[3, 3] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
    }
    static float[,] MatMul(float[,] A, float[,] B)
    {
        var C = new float[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                float sum = 0f;
                for (int k = 0; k < 3; k++) sum += A[i, k] * B[k, j];
                C[i, j] = sum;
            }
        return C;
    }
    static Vector3 MatVec(float[,] M, Vector3 v)
    {
        return new Vector3(
            M[0, 0] * v.x + M[0, 1] * v.y + M[0, 2] * v.z,
            M[1, 0] * v.x + M[1, 1] * v.y + M[1, 2] * v.z,
            M[2, 0] * v.x + M[2, 1] * v.y + M[2, 2] * v.z
        );
    }

    // -------------------------------------------------------------------------
    // 3. SATTEL UND STEG
    // -------------------------------------------------------------------------
    public static void ComputeAnchorsAndBridge(StringFrameConfig cfg, float[] stringLengths,
                                         float[,] R, float Y36zMm,
                                         out Vector3[] anchors, out Vector3[] bridgeEnds)
    {
        int N = cfg.nFreq;
        anchors    = new Vector3[N];
        bridgeEnds = new Vector3[N];

        float spacingX = cfg.stringPerpMm / 1000f;
        float Wperp = (N - 1) * spacingX;
        float spacingZ = (Y36zMm / 1000f) / (N - 1);

        Vector3 endPre = new Vector3(Wperp, 0, Y36zMm / 1000f);
        Vector3 endRot = MatVec(R, endPre);
        float xOffset = endRot.x / 2f;

        for (int i = 0; i < N; i++)
        {
            Vector3 nutPre    = new Vector3(i * spacingX, 0, i * spacingZ);
            Vector3 bridgePre = new Vector3(nutPre.x, nutPre.y, nutPre.z + stringLengths[i]);
            Vector3 nut3d    = MatVec(R, nutPre);
            Vector3 bridge3d = MatVec(R, bridgePre);
            // Daten-Konvention: x=horizontal, y=Tiefe, z=Höhe
            anchors[i]    = new Vector3(nut3d.x - xOffset,    nut3d.y,    nut3d.z + cfg.zNutM);
            bridgeEnds[i] = new Vector3(bridge3d.x - xOffset, bridge3d.y, bridge3d.z + cfg.zNutM);
        }
    }

    // -------------------------------------------------------------------------
    // 4. HELIX
    // -------------------------------------------------------------------------
    public static float HelixAngle(StringFrameConfig cfg, float u)
    {
        float raw = cfg.helixAlpha * u * 2f * Mathf.PI;
        float arcRad = cfg.helixArcDeg * DegToRad;
        if (arcRad >= 2f * Mathf.PI - 1e-4f) return raw;
        float offset = -(arcRad / 2f) * Mathf.Cos(raw * Mathf.PI / arcRad);
        return ArcCenterRad + offset;
    }

    public struct Sphere
    {
        public float x, y, z;
        public int k;
        public float f, cents;
    }

    public static Sphere[][] ComputeBalls(StringFrameConfig cfg, float[] frequencies)
    {
        var spheres = new Sphere[cfg.nFreq][];
        for (int i = 0; i < cfg.nFreq; i++)
        {
            var row = new Sphere[cfg.nPartials];
            for (int k = 1; k <= cfg.nPartials; k++)
            {
                float f = frequencies[i] * k;
                float u = Mathf.Log(f / cfg.fStart, 2f);
                float theta = HelixAngle(cfg, u);
                float r = cfg.helixR0M + i * cfg.helixDrM;
                row[k - 1] = new Sphere {
                    x = r * Mathf.Cos(theta),
                    y = r * Mathf.Sin(theta),
                    z = cfg.zNutM + cfg.helixZOffsetM + cfg.helixHOctM * u,
                    k = k,
                    f = f,
                    cents = 1200f * Mathf.Log(f / cfg.fStart, 2f),
                };
            }
            spheres[i] = row;
        }
        return spheres;
    }

    public static Vector3[][] ComputeHelixCurves(StringFrameConfig cfg, float[] frequencies)
    {
        var curves = new Vector3[cfg.nFreq][];
        for (int i = 0; i < cfg.nFreq; i++)
        {
            float uMin = Mathf.Log(frequencies[i] / cfg.fStart, 2f);
            float uMax = Mathf.Log(cfg.nPartials * frequencies[i] / cfg.fStart, 2f);
            var pts = new Vector3[cfg.helixSamples];
            for (int j = 0; j < cfg.helixSamples; j++)
            {
                float u = uMin + (uMax - uMin) * j / (cfg.helixSamples - 1);
                float theta = HelixAngle(cfg, u);
                float r = cfg.helixR0M + i * cfg.helixDrM;
                pts[j] = new Vector3(
                    r * Mathf.Cos(theta),
                    r * Mathf.Sin(theta),
                    cfg.zNutM + cfg.helixZOffsetM + cfg.helixHOctM * u
                );
            }
            curves[i] = pts;
        }
        return curves;
    }

    // -------------------------------------------------------------------------
    // 5. EXAKTE UNISONO-GRUPPEN
    // -------------------------------------------------------------------------
    public class UnisonLine
    {
        public int M;
        public float frequency;
        public float cents;
        public int[][] members;     // jedes Element ist [i, k]
        public int count;
    }

    public static List<UnisonLine> ComputeUnisonLines(StringFrameConfig cfg, float[] frequencies)
    {
        var groups = new Dictionary<int, List<int[]>>();
        for (int i = 0; i < cfg.nFreq; i++)
        {
            for (int k = 1; k <= cfg.nPartials; k++)
            {
                int M = (16 + i) * k;
                if (!groups.TryGetValue(M, out var list))
                {
                    list = new List<int[]>();
                    groups[M] = list;
                }
                list.Add(new int[] { i, k - 1 });
            }
        }

        var lines = new List<UnisonLine>();
        foreach (var kvp in groups)
        {
            if (kvp.Value.Count < 2) continue;
            kvp.Value.Sort((a, b) => a[0].CompareTo(b[0]));
            float f = cfg.fStart * kvp.Key / 16f;
            lines.Add(new UnisonLine {
                M = kvp.Key,
                frequency = f,
                cents = 1200f * Mathf.Log(f / cfg.fStart, 2f),
                members = kvp.Value.ToArray(),
                count = kvp.Value.Count,
            });
        }
        lines.Sort((a, b) => a.frequency.CompareTo(b.frequency));
        return lines;
    }

    /// <summary>
    /// Variante, die nur die rohen Mitgliederlisten zurückgibt (ohne Frequenz-Metadaten).
    /// Für den Visualizer ausreichend; vermeidet die Frequenz-Berechnung.
    /// Returns: List of int[][] — jede Liste enthält [i, k]-Paare.
    /// </summary>
    public static List<int[][]> ComputeUnisonGroupsRaw(StringFrameConfig cfg)
    {
        var groups = new Dictionary<int, List<int[]>>();
        for (int i = 0; i < cfg.nFreq; i++)
        {
            for (int k = 1; k <= cfg.nPartials; k++)
            {
                int M = (16 + i) * k;
                if (!groups.TryGetValue(M, out var list))
                {
                    list = new List<int[]>();
                    groups[M] = list;
                }
                list.Add(new int[] { i, k - 1 });
            }
        }

        var result = new List<int[][]>();
        // Sortiere nach M für deterministische Reihenfolge
        var sortedKeys = new List<int>(groups.Keys);
        sortedKeys.Sort();
        foreach (var M in sortedKeys)
        {
            var list = groups[M];
            if (list.Count < 2) continue;
            list.Sort((a, b) => a[0].CompareTo(b[0]));
            result.Add(list.ToArray());
        }
        return result;
    }

    static int CountTotalUnisonMembers(List<UnisonLine> lines)
    {
        int total = 0;
        foreach (var l in lines) total += l.count;
        return total;
    }

    // -------------------------------------------------------------------------
    // VERIFIKATION
    // -------------------------------------------------------------------------
    static void VerifyGeometry(StringFrameConfig cfg, Vector3[] anchors, Vector3[] bridgeEnds)
    {
        // Sattel-z (Daten-Konvention: z = Höhe)
        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var a in anchors)
        {
            if (a.z < zMin) zMin = a.z;
            if (a.z > zMax) zMax = a.z;
        }
        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[Verifikation] Sattel-z: {0:F5} … {1:F5}m  (sollte konstant sein)", zMin, zMax));

        // Bridge-Winkel zur längsten Saite
        Vector3 s0 = (bridgeEnds[0] - anchors[0]).normalized;
        Vector3 br = (bridgeEnds[cfg.nFreq - 1] - bridgeEnds[0]).normalized;
        float cosA = Mathf.Clamp(Vector3.Dot(s0, br), -1f, 1f);
        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[Verifikation] Bridge-Winkel zur längsten Saite: {0:F2}° (sollte {1}°)",
            Mathf.Acos(cosA) * RadToDeg, cfg.bridgeAngleDeg));
    }

    // -------------------------------------------------------------------------
    // 6. JSON-AUSGABE (manuell, ohne externe Bibliotheken)
    // -------------------------------------------------------------------------
    static string BuildJson(StringFrameConfig cfg,
                             float[] frequencies, float[] stringLengths,
                             Vector3[] anchors, Vector3[] bridgeEnds,
                             Sphere[][] spheres, Vector3[][] helixCurves,
                             List<UnisonLine> unisonLines, float rot2Rad)
    {
        var sb = new StringBuilder(2 * 1024 * 1024);
        var ci = CultureInfo.InvariantCulture;

        sb.Append("{\n");

        // META
        sb.Append(" \"meta\": {\n");
        AppendKVStr(sb, "version",        "V18-csharpGenerator", false);
        AppendKVStr(sb, "description",
            "37 Saiten × 25 Teiltöne, sinusoidale Helix-Reflexion bei reduziertem Bogen", false);
        AppendKVInt(sb, "n_freq",          cfg.nFreq, false);
        AppendKVInt(sb, "n_partials",      cfg.nPartials, false);
        AppendKVFlt(sb, ci, "f_start",     cfg.fStart, false);
        AppendKVFlt(sb, ci, "string_length_max_mm",     cfg.strLenMaxMm, false);
        AppendKVFlt(sb, ci, "string_length_min_mm",     cfg.strLenMinMm, false);
        AppendKVFlt(sb, ci, "string_perp_distance_mm",  cfg.stringPerpMm, false);
        AppendKVFlt(sb, ci, "bridge_angle_deg",         cfg.bridgeAngleDeg, false);
        AppendKVFlt(sb, ci, "rot1_deg",                 cfg.rot1Deg, false);
        AppendKVFlt(sb, ci, "rot2_deg",                 rot2Rad * RadToDeg, false);
        AppendKVFlt(sb, ci, "z_nut",                    cfg.zNutM, false);
        AppendKVFlt(sb, ci, "helix_R0",                 cfg.helixR0M, false);
        AppendKVFlt(sb, ci, "helix_DR",                 cfg.helixDrM, false);
        AppendKVFlt(sb, ci, "helix_H_OCT",              cfg.helixHOctM, false);
        AppendKVFlt(sb, ci, "helix_z_offset",           cfg.helixZOffsetM, false);
        AppendKVFlt(sb, ci, "helix_alpha",              cfg.helixAlpha, false);
        AppendKVFlt(sb, ci, "helix_arc_deg",            cfg.helixArcDeg, false);
        AppendKVFlt(sb, ci, "helix_arc_center_rad",     ArcCenterRad, true);
        sb.Append(" },\n");

        // FREQUENCIES
        sb.Append(" \"frequencies\": [");
        for (int i = 0; i < frequencies.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(frequencies[i].ToString("R", ci));
        }
        sb.Append("],\n");

        // STRING_LENGTHS_MM
        sb.Append(" \"string_lengths_mm\": [");
        for (int i = 0; i < stringLengths.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append((stringLengths[i] * 1000f).ToString("R", ci));
        }
        sb.Append("],\n");

        // ANCHORS
        sb.Append(" \"anchors\": [\n");
        for (int i = 0; i < anchors.Length; i++)
        {
            sb.Append("  [");
            sb.Append(anchors[i].x.ToString("R", ci)); sb.Append(", ");
            sb.Append(anchors[i].y.ToString("R", ci)); sb.Append(", ");
            sb.Append(anchors[i].z.ToString("R", ci));
            sb.Append("]");
            if (i < anchors.Length - 1) sb.Append(",");
            sb.Append("\n");
        }
        sb.Append(" ],\n");

        // BRIDGE_ENDS
        sb.Append(" \"bridge_ends\": [\n");
        for (int i = 0; i < bridgeEnds.Length; i++)
        {
            sb.Append("  [");
            sb.Append(bridgeEnds[i].x.ToString("R", ci)); sb.Append(", ");
            sb.Append(bridgeEnds[i].y.ToString("R", ci)); sb.Append(", ");
            sb.Append(bridgeEnds[i].z.ToString("R", ci));
            sb.Append("]");
            if (i < bridgeEnds.Length - 1) sb.Append(",");
            sb.Append("\n");
        }
        sb.Append(" ],\n");

        // SPHERES
        sb.Append(" \"spheres\": [\n");
        for (int i = 0; i < spheres.Length; i++)
        {
            sb.Append("  [\n");
            for (int k = 0; k < spheres[i].Length; k++)
            {
                var s = spheres[i][k];
                sb.Append("   {");
                sb.Append("\"x\": ");      sb.Append(s.x.ToString("R", ci));
                sb.Append(", \"y\": ");    sb.Append(s.y.ToString("R", ci));
                sb.Append(", \"z\": ");    sb.Append(s.z.ToString("R", ci));
                sb.Append(", \"k\": ");    sb.Append(s.k);
                sb.Append(", \"f\": ");    sb.Append(s.f.ToString("R", ci));
                sb.Append(", \"cents\": "); sb.Append(s.cents.ToString("R", ci));
                sb.Append("}");
                if (k < spheres[i].Length - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]");
            if (i < spheres.Length - 1) sb.Append(",");
            sb.Append("\n");
        }
        sb.Append(" ],\n");

        // HELIX_CURVES
        sb.Append(" \"helix_curves\": [\n");
        for (int i = 0; i < helixCurves.Length; i++)
        {
            sb.Append("  [\n");
            for (int j = 0; j < helixCurves[i].Length; j++)
            {
                var p = helixCurves[i][j];
                sb.Append("   {\"x\": ");   sb.Append(p.x.ToString("R", ci));
                sb.Append(", \"y\": ");     sb.Append(p.y.ToString("R", ci));
                sb.Append(", \"z\": ");     sb.Append(p.z.ToString("R", ci));
                sb.Append("}");
                if (j < helixCurves[i].Length - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]");
            if (i < helixCurves.Length - 1) sb.Append(",");
            sb.Append("\n");
        }
        sb.Append(" ],\n");

        // UNISON_LINES
        sb.Append(" \"unison_lines\": [\n");
        for (int u = 0; u < unisonLines.Count; u++)
        {
            var L = unisonLines[u];
            sb.Append("  {");
            sb.Append("\"M\": ");           sb.Append(L.M);
            sb.Append(", \"frequency\": "); sb.Append(L.frequency.ToString("R", ci));
            sb.Append(", \"cents\": ");     sb.Append(L.cents.ToString("R", ci));
            sb.Append(", \"count\": ");     sb.Append(L.count);
            sb.Append(", \"members\": [");
            for (int m = 0; m < L.members.Length; m++)
            {
                if (m > 0) sb.Append(", ");
                sb.Append("{\"i\": "); sb.Append(L.members[m][0]);
                sb.Append(", \"k\": "); sb.Append(L.members[m][1]);
                sb.Append("}");
            }
            sb.Append("]}");
            if (u < unisonLines.Count - 1) sb.Append(",");
            sb.Append("\n");
        }
        sb.Append(" ]\n");

        sb.Append("}\n");
        return sb.ToString();
    }

    // Helper für META-Felder
    static void AppendKVStr(StringBuilder sb, string key, string value, bool isLast)
    {
        sb.Append("  \""); sb.Append(key); sb.Append("\": \"");
        sb.Append(EscapeJsonString(value));
        sb.Append("\"");
        if (!isLast) sb.Append(",");
        sb.Append("\n");
    }
    static void AppendKVInt(StringBuilder sb, string key, int value, bool isLast)
    {
        sb.Append("  \""); sb.Append(key); sb.Append("\": ");
        sb.Append(value);
        if (!isLast) sb.Append(",");
        sb.Append("\n");
    }
    static void AppendKVFlt(StringBuilder sb, CultureInfo ci, string key, float value, bool isLast)
    {
        sb.Append("  \""); sb.Append(key); sb.Append("\": ");
        sb.Append(value.ToString("R", ci));
        if (!isLast) sb.Append(",");
        sb.Append("\n");
    }

    static string EscapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < ' ') sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

#if UNITY_EDITOR
    // -------------------------------------------------------------------------
    // EDITOR-MENÜ
    // -------------------------------------------------------------------------
    [MenuItem("Tools/StringFrame3D/Generate JSON (Defaults)")]
    public static void GenerateFromMenu()
    {
        var cfg = new StringFrameConfig();
        string path = "Assets/Resources/string_frame_data.json";
        GenerateAndWrite(cfg, path);
        AssetDatabase.Refresh();
    }
#endif
}


// =============================================================================
// OPTIONALE MONOBEHAVIOUR-KOMPONENTE
// 
// Falls man die Konfiguration im Inspector anpassen und über ContextMenu
// generieren möchte. Sonst reicht das Editor-Menü oben.
// =============================================================================
public class StringFrameDataGeneratorRunner : MonoBehaviour
{
    public StringFrameConfig config = new StringFrameConfig();

    [Tooltip("Pfad relativ zum Projekt-Root (= Application.dataPath/..). " +
             "Beispiel: Assets/Resources/string_frame_data.json")]
    public string outputPath = "Assets/Resources/string_frame_data.json";

    [ContextMenu("Generate JSON now")]
    public void GenerateJsonNow()
    {
        StringFrameDataGenerator.GenerateAndWrite(config, outputPath);
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }
}
