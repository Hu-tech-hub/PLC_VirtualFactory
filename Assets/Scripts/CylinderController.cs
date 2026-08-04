using UnityEngine;

public sealed class CylinderController : MonoBehaviour
{
    private enum CylinderState
    {
        Idle,
        Extending,
        Extended,
        Retracting,
        Retracted
    }

    [SerializeField]
    private Transform cylinderRod;

    [SerializeField]
    private Transform cylinderRetractedPoint;

    [SerializeField]
    private Transform cylinderExtendedPoint;

    [SerializeField, Min(0f)]
    private float extendSpeed = 1.5f;

    [SerializeField, Min(0f)]
    private float retractSpeed = 2f;

    private CylinderState state = CylinderState.Idle;
    private Vector3 retractedLocalPosition;
    private Vector3 retractedLocalScale;
    private Vector3 extensionDirection;
    private Vector3 fixedBodyEnd;
    private float meshLength;
    private float retractedLength;
    private float extendedLength;
    private float currentLength;

    public bool IsExtended => state == CylinderState.Extended;
    public bool IsRetracted => state == CylinderState.Retracted;

    private void Awake()
    {
        retractedLocalPosition = cylinderRod.localPosition;
        retractedLocalScale = cylinderRod.localScale;

        MeshFilter meshFilter = cylinderRod.GetComponent<MeshFilter>();
        meshLength = meshFilter.sharedMesh.bounds.size.y;

        Vector3 rodLocalYAxis = cylinderRod.TransformDirection(Vector3.up).normalized;
        float directionSign = Mathf.Sign(Vector3.Dot(
            cylinderExtendedPoint.position - cylinderRetractedPoint.position,
            rodLocalYAxis));
        extensionDirection = rodLocalYAxis * directionSign;

        retractedLength = meshLength * Mathf.Abs(retractedLocalScale.y);
        currentLength = retractedLength;
        fixedBodyEnd = cylinderRod.position -
            extensionDirection * retractedLength * 0.5f;
        extendedLength = Vector3.Dot(
            cylinderExtendedPoint.position - fixedBodyEnd,
            extensionDirection);
        state = CylinderState.Retracted;
    }

    public void BeginExtend()
    {
        if (cylinderRod == null || cylinderExtendedPoint == null)
        {
            return;
        }

        state = CylinderState.Extending;
    }

    public void BeginRetract()
    {
        if (cylinderRod == null || cylinderRetractedPoint == null)
        {
            return;
        }

        state = CylinderState.Retracting;
    }

    private void Update()
    {
        if (state == CylinderState.Extending)
        {
            currentLength = Mathf.MoveTowards(
                currentLength,
                extendedLength,
                extendSpeed * Time.deltaTime);
            ApplyLength(currentLength);

            if (Mathf.Approximately(currentLength, extendedLength))
            {
                state = CylinderState.Extended;
            }
        }
        else if (state == CylinderState.Retracting)
        {
            currentLength = Mathf.MoveTowards(
                currentLength,
                retractedLength,
                retractSpeed * Time.deltaTime);
            ApplyLength(currentLength);

            if (Mathf.Approximately(currentLength, retractedLength))
            {
                cylinderRod.localPosition = retractedLocalPosition;
                cylinderRod.localScale = retractedLocalScale;
                state = CylinderState.Retracted;
            }
        }
    }

    private void ApplyLength(float length)
    {
        Vector3 scale = retractedLocalScale;
        scale.y = Mathf.Sign(retractedLocalScale.y) * length / meshLength;
        cylinderRod.localScale = scale;

        Vector3 centerPosition =
            fixedBodyEnd + extensionDirection * length * 0.5f;
        cylinderRod.position = centerPosition;
    }
}
