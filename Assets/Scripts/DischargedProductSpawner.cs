using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class DischargedProductSpawner : MonoBehaviour
{
    public enum DischargeRoute
    {
        Ok,
        Ng
    }

    [Header("Visual Source")]
    [SerializeField] private GameObject accumulatedProductPrefab;
    [SerializeField] private Transform accumulatedProductsRoot;
    [SerializeField] private Transform okReleasePoint;
    [SerializeField] private Transform ngReleasePoint;

    [Header("Routes")]
    [SerializeField] private bool enableOkPhysics = true;
    [SerializeField] private bool enableNgPhysics = true;

    [Header("Pool")]
    [SerializeField, Min(1)] private int maxAccumulatedProducts = 20;
    [SerializeField] private bool reuseOldestProducts = true;

    [Header("Safe Release Search")]
    [SerializeField] private Vector2 releaseAreaSize = new Vector2(0.6f, 1f);
    [SerializeField, Min(0.01f)] private float candidateSpacing = 0.3f;
    [SerializeField, Min(1)] private int maxPositionSearchAttempts = 24;
    [SerializeField, Min(0f)] private float colliderClearance = 0.03f;
    [SerializeField, Min(0.01f)] private float additionalVerticalSpacing = 0.2f;

    [Header("Physics")]
    [SerializeField, Min(0f)] private float physicsActivationDelay = 0.1f;
    [SerializeField] private Vector3 initialLinearVelocity = Vector3.zero;
    [SerializeField] private Vector3 initialAngularVelocity = Vector3.zero;
    [SerializeField, Min(0.0001f)] private float productMass = 1f;
    [SerializeField] private string dischargedProductLayerName = "DischargedProduct";

    private readonly Queue<DischargedProductPhysics> activeProducts =
        new Queue<DischargedProductPhysics>();
    private readonly Stack<DischargedProductPhysics> inactiveProducts =
        new Stack<DischargedProductPhysics>();
    private readonly Collider[] overlapBuffer = new Collider[64];
    private int spawnSequence;

    public int ActiveProductCount => activeProducts.Count;
    public int MaxAccumulatedProducts => maxAccumulatedProducts;

    public bool TrySpawn(Transform processProduct, DischargeRoute route)
    {
        if (processProduct == null || !IsRouteEnabled(route))
        {
            return false;
        }

        int dischargedLayer = LayerMask.NameToLayer(dischargedProductLayerName);
        if (dischargedLayer < 0)
        {
            Debug.LogError(
                $"[DISCHARGED PRODUCT] Layer '{dischargedProductLayerName}' is not configured");
            return false;
        }

        Transform releasePoint = route == DischargeRoute.Ok
            ? okReleasePoint
            : ngReleasePoint;
        if (releasePoint == null)
        {
            Debug.LogError($"[DISCHARGED PRODUCT] {route} release point is missing");
            return false;
        }

        DischargedProductPhysics productPhysics = GetAvailableProduct(processProduct);
        if (productPhysics == null)
        {
            return false;
        }

        GameObject dischargedProduct = productPhysics.gameObject;
        dischargedProduct.SetActive(false);
        dischargedProduct.name = $"DischargedProduct_{route}_{++spawnSequence:000}";
        dischargedProduct.transform.SetPositionAndRotation(
            releasePoint.position,
            processProduct.rotation);
        SetWorldScale(dischargedProduct.transform, processProduct.lossyScale);

        ApplyLayerRecursively(dischargedProduct.transform, dischargedLayer);
        if (!TryFindSafeReleasePosition(
                dischargedProduct.transform,
                releasePoint,
                dischargedLayer,
                out Vector3 releasePosition))
        {
            productPhysics.DeactivateForPool();
            inactiveProducts.Push(productPhysics);
            Debug.LogWarning(
                $"[DISCHARGED PRODUCT] No clear {route} release position was found; spawn skipped");
            return false;
        }

        dischargedProduct.transform.position = releasePosition;
        Physics.SyncTransforms();
        IgnoreProcessProductCollisions(processProduct, dischargedProduct.transform);
        productPhysics.Prepare(
            physicsActivationDelay,
            initialLinearVelocity,
            initialAngularVelocity,
            productMass);
        dischargedProduct.SetActive(true);
        activeProducts.Enqueue(productPhysics);

        Debug.Log($"[DISCHARGED PRODUCT] Spawned {route} product ({activeProducts.Count}/{maxAccumulatedProducts})");
        return true;
    }

    private bool IsRouteEnabled(DischargeRoute route)
    {
        return route == DischargeRoute.Ok ? enableOkPhysics : enableNgPhysics;
    }

    private DischargedProductPhysics GetAvailableProduct(Transform processProduct)
    {
        RemoveDestroyedProducts();
        while (inactiveProducts.Count > 0)
        {
            DischargedProductPhysics inactiveProduct = inactiveProducts.Pop();
            if (inactiveProduct != null)
            {
                return inactiveProduct;
            }
        }

        int capacity = Mathf.Max(1, maxAccumulatedProducts);
        if (activeProducts.Count < capacity)
        {
            return CreateProduct(processProduct);
        }

        DischargedProductPhysics reusableProduct = null;
        while (activeProducts.Count >= capacity)
        {
            DischargedProductPhysics oldest = activeProducts.Dequeue();
            if (oldest == null)
            {
                continue;
            }

            oldest.DeactivateForPool();
            if (reuseOldestProducts && reusableProduct == null)
            {
                reusableProduct = oldest;
            }
            else
            {
                Destroy(oldest.gameObject);
            }
        }

        return reusableProduct != null
            ? reusableProduct
            : CreateProduct(processProduct);
    }

    private DischargedProductPhysics CreateProduct(Transform processProduct)
    {
        GameObject source = accumulatedProductPrefab != null
            ? accumulatedProductPrefab
            : processProduct.gameObject;
        GameObject instance = Instantiate(
            source,
            processProduct.position,
            processProduct.rotation,
            accumulatedProductsRoot);
        instance.SetActive(false);

        DisableProcessComponents(instance);
        EnsurePhysicalCollider(instance);

        DischargedProductPhysics productPhysics =
            instance.GetComponent<DischargedProductPhysics>();
        if (productPhysics == null)
        {
            productPhysics = instance.AddComponent<DischargedProductPhysics>();
        }

        return productPhysics;
    }

    private bool TryFindSafeReleasePosition(
        Transform dischargedProduct,
        Transform releasePoint,
        int dischargedLayer,
        out Vector3 releasePosition)
    {
        BoxCollider productCollider =
            dischargedProduct.GetComponentInChildren<BoxCollider>(true);
        if (productCollider == null)
        {
            Debug.LogError("[DISCHARGED PRODUCT] A BoxCollider is required for safe release checks");
            releasePosition = releasePoint.position;
            return false;
        }

        Vector3 productSize = GetWorldBoxSize(productCollider);
        float safeHorizontalSpacing = Mathf.Max(
            Mathf.Max(0.01f, candidateSpacing),
            Mathf.Max(productSize.x, productSize.z) +
            Mathf.Max(0f, colliderClearance) * 2f);
        List<Vector2> gridOffsets = BuildGridOffsets(safeHorizontalSpacing);
        int attempts = Mathf.Max(1, maxPositionSearchAttempts);
        float productHeight = productSize.y;
        float verticalStep = productHeight + Mathf.Max(0.01f, additionalVerticalSpacing);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int gridIndex = attempt % gridOffsets.Count;
            int verticalLevel = attempt / gridOffsets.Count;
            Vector2 gridOffset = gridOffsets[gridIndex];
            Vector3 candidate = releasePoint.position +
                                releasePoint.right * gridOffset.x +
                                releasePoint.forward * gridOffset.y +
                                releasePoint.up * (verticalLevel * verticalStep);

            dischargedProduct.position = candidate;
            if (!OverlapsDischargedProduct(
                    dischargedProduct,
                    productCollider,
                    dischargedLayer))
            {
                releasePosition = candidate;
                return true;
            }
        }

        releasePosition = releasePoint.position;
        return false;
    }

    private List<Vector2> BuildGridOffsets(float spacing)
    {
        int xCount = Mathf.Max(1, Mathf.FloorToInt(releaseAreaSize.x / spacing) + 1);
        int zCount = Mathf.Max(1, Mathf.FloorToInt(releaseAreaSize.y / spacing) + 1);
        float xStart = -(xCount - 1) * spacing * 0.5f;
        float zStart = -(zCount - 1) * spacing * 0.5f;
        var offsets = new List<Vector2>();

        for (int z = 0; z < zCount; z++)
        {
            for (int x = 0; x < xCount; x++)
            {
                offsets.Add(new Vector2(
                    xStart + x * spacing,
                    zStart + z * spacing));
            }
        }

        offsets.Sort((left, right) =>
        {
            int distanceComparison = left.sqrMagnitude.CompareTo(right.sqrMagnitude);
            if (distanceComparison != 0)
            {
                return distanceComparison;
            }

            int zComparison = left.y.CompareTo(right.y);
            return zComparison != 0 ? zComparison : left.x.CompareTo(right.x);
        });
        return offsets;
    }

    private bool OverlapsDischargedProduct(
        Transform productRoot,
        BoxCollider productCollider,
        int dischargedLayer)
    {
        Vector3 center = productCollider.transform.TransformPoint(productCollider.center);
        Vector3 halfExtents = GetWorldBoxSize(productCollider) * 0.5f +
                              Vector3.one * Mathf.Max(0f, colliderClearance);
        int overlapCount = Physics.OverlapBoxNonAlloc(
            center,
            halfExtents,
            overlapBuffer,
            productCollider.transform.rotation,
            1 << dischargedLayer,
            QueryTriggerInteraction.Ignore);

        for (int index = 0; index < overlapCount; index++)
        {
            Collider overlap = overlapBuffer[index];
            overlapBuffer[index] = null;
            if (overlap != null && !overlap.transform.IsChildOf(productRoot))
            {
                return true;
            }
        }

        return false;
    }

    private static Vector3 GetWorldBoxSize(BoxCollider productCollider)
    {
        Vector3 scale = productCollider.transform.lossyScale;
        return Vector3.Scale(
            productCollider.size,
            new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
    }

    private static void DisableProcessComponents(GameObject instance)
    {
        foreach (MonoBehaviour behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour is DischargedProductPhysics)
            {
                continue;
            }

            behaviour.enabled = false;
        }
    }

    private static void EnsurePhysicalCollider(GameObject instance)
    {
        Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
        if (colliders.Length == 0)
        {
            colliders = new Collider[] { instance.AddComponent<BoxCollider>() };
        }

        foreach (Collider productCollider in colliders)
        {
            productCollider.enabled = true;
            productCollider.isTrigger = false;
        }
    }

    private static void ApplyLayerRecursively(Transform root, int layer)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            child.gameObject.layer = layer;
        }
    }

    private static void SetWorldScale(Transform target, Vector3 worldScale)
    {
        Vector3 parentScale = target.parent != null
            ? target.parent.lossyScale
            : Vector3.one;
        target.localScale = new Vector3(
            Mathf.Approximately(parentScale.x, 0f) ? worldScale.x : worldScale.x / parentScale.x,
            Mathf.Approximately(parentScale.y, 0f) ? worldScale.y : worldScale.y / parentScale.y,
            Mathf.Approximately(parentScale.z, 0f) ? worldScale.z : worldScale.z / parentScale.z);
    }

    private static void IgnoreProcessProductCollisions(
        Transform processProduct,
        Transform dischargedProduct)
    {
        Collider[] processColliders = processProduct.GetComponentsInChildren<Collider>(true);
        Collider[] dischargedColliders =
            dischargedProduct.GetComponentsInChildren<Collider>(true);

        foreach (Collider processCollider in processColliders)
        {
            foreach (Collider dischargedCollider in dischargedColliders)
            {
                Physics.IgnoreCollision(processCollider, dischargedCollider, true);
            }
        }
    }

    private void RemoveDestroyedProducts()
    {
        int count = activeProducts.Count;
        for (int index = 0; index < count; index++)
        {
            DischargedProductPhysics product = activeProducts.Dequeue();
            if (product != null)
            {
                activeProducts.Enqueue(product);
            }
        }
    }

    private void OnValidate()
    {
        maxAccumulatedProducts = Mathf.Max(1, maxAccumulatedProducts);
        releaseAreaSize.x = Mathf.Max(0f, releaseAreaSize.x);
        releaseAreaSize.y = Mathf.Max(0f, releaseAreaSize.y);
        candidateSpacing = Mathf.Max(0.01f, candidateSpacing);
        maxPositionSearchAttempts = Mathf.Max(1, maxPositionSearchAttempts);
        colliderClearance = Mathf.Max(0f, colliderClearance);
        additionalVerticalSpacing = Mathf.Max(0.01f, additionalVerticalSpacing);
        physicsActivationDelay = Mathf.Max(0f, physicsActivationDelay);
        productMass = Mathf.Max(0.0001f, productMass);
    }
}
