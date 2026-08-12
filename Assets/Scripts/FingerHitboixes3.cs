using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
public class OpenXRFingerHitboxes : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Drag the OpenXR hand visual's SkinnedMeshRenderer here. If empty, auto-finds the richest one in children.")]
    [SerializeField] private SkinnedMeshRenderer handRenderer;

    [Header("Hitbox")]
    [SerializeField] private string hitboxTag = "Player";
    [SerializeField] private int hitboxLayer = -1;
    [SerializeField] private bool addKinematicRigidbody = false;

    [Header("Bone Filter")]
    [SerializeField] private bool includeMetacarpals = false;
    [SerializeField] private bool includeWrist = false;

    [Header("Size")]
    [SerializeField, Min(0.001f)] private float jointRadius = 0.01f;
    [SerializeField, Min(0.001f)] private float tipRadius = 0.006f;

    [Header("Debug")]
    [SerializeField] private bool showDebugMeshes = false;
    [SerializeField] private bool logBoneNames = false;
    [SerializeField] private Material debugMaterial;

    private bool built;

    private IEnumerator Start()
    {
        // OpenXR hand visuals often spawn after scene start; wait up to 120 frames
        for (int i = 0; i < 120; i++)
        {
            if (handRenderer == null)
                handRenderer = FindBestHandRenderer();

            if (handRenderer != null && handRenderer.bones != null && handRenderer.bones.Length > 0)
                break;

            yield return null;
        }

        if (handRenderer == null)
        {
            Debug.LogError("[OpenXRFingerHitboxes] No SkinnedMeshRenderer found. Assign the hand visual's SkinnedMeshRenderer.");
            yield break;
        }

        Build();
    }

    private SkinnedMeshRenderer FindBestHandRenderer()
    {
        SkinnedMeshRenderer[] renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);

        SkinnedMeshRenderer best = null;
        int bestBoneCount = 0;

        foreach (SkinnedMeshRenderer renderer in renderers)
        {
            if (renderer == null || renderer.bones == null)
                continue;

            if (renderer.bones.Length > bestBoneCount)
            {
                best = renderer;
                bestBoneCount = renderer.bones.Length;
            }
        }

        return best;
    }

    private void Build()
    {
        if (built) return;
        built = true;

        Transform[] bones = handRenderer.bones;
        int created = 0;

        if (logBoneNames)
        {
            Debug.Log("[OpenXRFingerHitboxes] Bones found: " + bones.Length);
            foreach (Transform bone in bones)
            {
                if (bone != null)
                    Debug.Log("[OpenXRFingerHitboxes] Bone: " + bone.name);
            }
        }

        foreach (Transform bone in bones)
        {
            if (bone == null) continue;
            if (!ShouldCreateHitbox(bone.name)) continue;

            CreateHitbox(bone);
            created++;
        }

        Debug.Log("[OpenXRFingerHitboxes] Finger hitboxes created: " + created);
    }

    private bool ShouldCreateHitbox(string boneName)
    {
        if (string.IsNullOrEmpty(boneName)) return false;

        string n = boneName.ToLowerInvariant();

        // Skip palm always
        if (n.Contains("palm")) return false;

        // Wrist optional
        if (n.Contains("wrist") && !includeWrist) return false;

        // Must be a finger bone
        bool isFinger = n.Contains("thumb") ||
                        n.Contains("index") ||
                        n.Contains("middle") ||
                        n.Contains("ring") ||
                        n.Contains("little") ||
                        n.Contains("pinky");

        if (!isFinger) return false;

        // Metacarpals optional
        if (n.Contains("metacarpal") && !includeMetacarpals)
            return false;

        return true;
    }

    private void CreateHitbox(Transform bone)
    {
        GameObject go = new GameObject("Hitbox_" + bone.name);

        go.transform.SetParent(bone, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        TrySetTag(go, hitboxTag);

        if (hitboxLayer >= 0 && hitboxLayer <= 31)
            go.layer = hitboxLayer;

        float desiredWorldRadius = IsTipBone(bone.name) ? tipRadius : jointRadius;

        // Compensate for any parent scaling
        float scale = MaxAbsScale(go.transform.lossyScale);
        float localRadius = desiredWorldRadius / scale;

        SphereCollider col = go.AddComponent<SphereCollider>();
        col.isTrigger = true;
        col.radius = localRadius;

        if (showDebugMeshes)
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "Visual";
            visual.transform.SetParent(go.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * (localRadius * 2f);

            Collider visualCol = visual.GetComponent<Collider>();
            if (visualCol != null) Destroy(visualCol);

            Renderer rend = visual.GetComponent<Renderer>();
            if (rend != null && debugMaterial != null)
                rend.material = debugMaterial;
        }

        if (addKinematicRigidbody)
        {
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }
    }

    private static bool IsTipBone(string boneName)
    {
        if (string.IsNullOrEmpty(boneName)) return false;
        return boneName.EndsWith("Tip", System.StringComparison.OrdinalIgnoreCase);
    }

    private static float MaxAbsScale(Vector3 scale)
    {
        return Mathf.Max(
            Mathf.Abs(scale.x),
            Mathf.Abs(scale.y),
            Mathf.Abs(scale.z),
            0.00001f
        );
    }

    private static void TrySetTag(GameObject go, string tagName)
    {
        if (string.IsNullOrEmpty(tagName)) return;

        try
        {
            go.tag = tagName;
        }
        catch
        {
            Debug.LogWarning("[OpenXRFingerHitboxes] Tag '" + tagName + "' does not exist. " +
                "Create it in Edit > Project Settings > Tags and Layers.");
        }
    }
}