using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
public class FingerHitboxesFromHandMesh : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private SkinnedMeshRenderer handRenderer;

    [Header("Hitbox")]
    [SerializeField] private string hitboxTag = "Player";
    [SerializeField] private int hitboxLayer = -1;
    [SerializeField] private bool addKinematicRigidbody = true;
    [SerializeField] private bool createOnlyFingerBones = true;

    [Header("Size")]
    [SerializeField, Min(0.001f)] private float jointRadius = 0.018f;
    [SerializeField, Min(0.001f)] private float tipRadius = 0.028f;

    [Header("Debug")]
    [SerializeField] private bool showDebugMeshes = true;
    [SerializeField] private bool logBoneNames = true;
    [SerializeField] private Material debugMaterial;

    private bool built;

    private IEnumerator Start()
    {
        // Building Blocks often initialize hand visuals after scene start.
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
            Debug.LogError("[FingerHitboxesFromHandMesh] No SkinnedMeshRenderer found. Add/enable a Hand Visual first.");
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
        if (built)
            return;

        built = true;

        Transform[] bones = handRenderer.bones;
        int created = 0;

        if (logBoneNames)
        {
            Debug.Log("[FingerHitboxesFromHandMesh] Bones found: " + bones.Length);
            foreach (Transform bone in bones)
            {
                if (bone != null)
                    Debug.Log("[FingerHitboxesFromHandMesh] Bone: " + bone.name);
            }
        }

        foreach (Transform bone in bones)
        {
            if (bone == null)
                continue;

            if (createOnlyFingerBones && !LooksLikeFingerBone(bone.name))
                continue;

            CreateHitbox(bone);
            created++;
        }

        Debug.Log("[FingerHitboxesFromHandMesh] Finger hitboxes created: " + created);
    }

    private void CreateHitbox(Transform bone)
    {
        GameObject go = new GameObject("FingerHitbox_" + bone.name);

        go.transform.SetParent(bone, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        TrySetTag(go, hitboxTag);

        if (hitboxLayer >= 0 && hitboxLayer <= 31)
            go.layer = hitboxLayer;

        float desiredWorldRadius = LooksLikeTipBone(bone.name) ? tipRadius : jointRadius;

        // Compensate parent/bone scaling.
        float scale = MaxAbsScale(go.transform.lossyScale);
        float localRadius = desiredWorldRadius / scale;

        SphereCollider collider = go.AddComponent<SphereCollider>();
        collider.isTrigger = false;
        collider.radius = localRadius;

        if (showDebugMeshes)
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "Visual";
            visual.transform.SetParent(go.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * (localRadius * 2f);

            Collider visualCollider = visual.GetComponent<Collider>();
            if (visualCollider != null)
                Destroy(visualCollider);

            Renderer renderer = visual.GetComponent<Renderer>();
            if (renderer != null && debugMaterial != null)
                renderer.material = debugMaterial;
        }

        if (addKinematicRigidbody)
        {
            Rigidbody rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }
    }

    private static float MaxAbsScale(Vector3 scale)
    {
        return Mathf.Max(
            Mathf.Abs(scale.x),
            Mathf.Abs(scale.y),
            Mathf.Abs(scale.z),
            0.0001f
        );
    }

    private static bool LooksLikeFingerBone(string boneName)
    {
        if (string.IsNullOrEmpty(boneName))
            return false;

        string n = boneName.ToLowerInvariant();

        return n.Contains("thumb") ||
               n.Contains("index") ||
               n.Contains("middle") ||
               n.Contains("ring") ||
               n.Contains("pinky") ||
               n.Contains("little");
    }

    private static bool LooksLikeTipBone(string boneName)
    {
        if (string.IsNullOrEmpty(boneName))
            return false;

        string n = boneName.ToLowerInvariant();

        return n.Contains("tip") ||
               n.Contains("distal") ||
               n.EndsWith("3") ||
               n.EndsWith("_3");
    }

    private static void TrySetTag(GameObject go, string tagName)
    {
        if (string.IsNullOrEmpty(tagName))
            return;

        try
        {
            go.tag = tagName;
        }
        catch
        {
            Debug.LogWarning("[FingerHitboxesFromHandMesh] Tag does not exist: " + tagName +
                             ". Create tag or clear hitboxTag.");
        }
    }
}