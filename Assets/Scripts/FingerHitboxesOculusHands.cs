using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
public class FingerHitboxesFromHandMeshOculus : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private SkinnedMeshRenderer handRenderer;

    [Header("Hitbox")]
    [SerializeField] private string hitboxTag = "Player";
    [SerializeField] private int hitboxLayer = -1;
    [SerializeField] private bool addKinematicRigidbody = true;

    [Header("Bone Filter")]
    [SerializeField] private bool createOnlyFingerBones = true;

    [Header("Size")]
    [SerializeField, Min(0.001f)]
    private float jointRadius = 0.018f;

    [SerializeField, Min(0.001f)]
    private float tipRadius = 0.028f;

    [Header("Debug")]
    [SerializeField] private bool showDebugMeshes = true;
    [SerializeField] private bool logBoneNames = false;
    [SerializeField] private Material debugMaterial;


    private bool built;


    private IEnumerator Start()
    {
        for (int i = 0; i < 120; i++)
        {
            if (handRenderer == null)
                handRenderer = FindHandRenderer();

            if (handRenderer != null &&
                handRenderer.bones != null &&
                handRenderer.bones.Length > 0)
                break;

            yield return null;
        }


        if (handRenderer == null)
        {
            Debug.LogError(
                "[FingerHitboxesFromHandMesh] Oculus hand renderer not found."
            );
            yield break;
        }


        Build();
    }


    private SkinnedMeshRenderer FindHandRenderer()
    {
        SkinnedMeshRenderer[] renderers =
            GetComponentsInChildren<SkinnedMeshRenderer>(true);


        SkinnedMeshRenderer best = null;
        int count = 0;


        foreach (var r in renderers)
        {
            if (r == null || r.bones == null)
                continue;


            if (r.bones.Length > count)
            {
                count = r.bones.Length;
                best = r;
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
            Debug.Log(
                "[FingerHitboxesFromHandMesh] Bones: " + bones.Length
            );

            foreach (Transform b in bones)
            {
                if (b)
                    Debug.Log(b.name);
            }
        }


        foreach (Transform bone in bones)
        {
            if (bone == null)
                continue;


            if (createOnlyFingerBones &&
                !IsOculusFingerBone(bone.name))
                continue;


            CreateHitbox(bone);
            created++;
        }


        Debug.Log(
            "[FingerHitboxesFromHandMesh] Created: " + created
        );
    }


    private void CreateHitbox(Transform bone)
    {
        GameObject go = new GameObject(
            "FingerHitbox_" + bone.name
        );


        go.transform.SetParent(
            bone,
            false
        );


        TrySetTag(
            go,
            hitboxTag
        );


        if (hitboxLayer >= 0 &&
            hitboxLayer <= 31)
        {
            go.layer = hitboxLayer;
        }


        float radius =
            IsTipBone(bone.name)
            ? tipRadius
            : jointRadius;


        float scale =
            Mathf.Max(
                Mathf.Abs(go.transform.lossyScale.x),
                Mathf.Abs(go.transform.lossyScale.y),
                Mathf.Abs(go.transform.lossyScale.z),
                0.0001f
            );


        SphereCollider collider =
            go.AddComponent<SphereCollider>();

        collider.radius =
            radius / scale;

        collider.isTrigger = false;



        if (showDebugMeshes)
        {
            GameObject sphere =
                GameObject.CreatePrimitive(
                    PrimitiveType.Sphere
                );


            sphere.name = "DebugSphere";

            sphere.transform.SetParent(
                go.transform,
                false
            );

            sphere.transform.localScale =
                Vector3.one *
                collider.radius *
                2f;


            Destroy(
                sphere.GetComponent<Collider>()
            );


            if (debugMaterial)
            {
                sphere.GetComponent<Renderer>()
                      .material = debugMaterial;
            }
        }



        if (addKinematicRigidbody)
        {
            Rigidbody rb =
                go.AddComponent<Rigidbody>();

            rb.isKinematic = true;
            rb.useGravity = false;

            rb.collisionDetectionMode =
                CollisionDetectionMode.ContinuousSpeculative;
        }
    }



    private static bool IsOculusFingerBone(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;


        string n =
            name.ToLowerInvariant();


        if (n.Contains("marker"))
            return false;


        return
            n.Contains("thumb") ||
            n.Contains("index") ||
            n.Contains("middle") ||
            n.Contains("ring") ||
            n.Contains("pinky") ||
            n.Contains("little");
    }



    private static bool IsTipBone(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;


        string n =
            name.ToLowerInvariant();


        return
            n.Contains("tip") ||
            n.Contains("distal") ||
            n.EndsWith("3") ||
            n.Contains("_3");
    }



    private static void TrySetTag(
        GameObject go,
        string tagName)
    {
        if (string.IsNullOrEmpty(tagName))
            return;


        try
        {
            go.tag = tagName;
        }
        catch
        {
            Debug.LogWarning(
                "[FingerHitboxesFromHandMesh] Missing tag: "
                + tagName
            );
        }
    }
}