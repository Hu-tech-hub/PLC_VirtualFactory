using UnityEngine;

[RequireComponent(typeof(Rigidbody), typeof(Collider))]
public sealed class ProductMover : MonoBehaviour
{
    public enum MovementAxis
    {
        X,
        Z
    }

    [SerializeField, Min(0f)]
    private float moveSpeed = 2f;

    [SerializeField, Min(0f)]
    private float stopTolerance = 0.02f;

    private Rigidbody productRigidbody;
    private MovementAxis movementAxis;
    private Vector3 targetPosition;
    private float targetCoordinate;
    private bool isMoving;
    private bool isPaused;
    private bool hasReachedTarget;

    public bool IsMoving => isMoving;
    public bool IsPaused => isPaused;
    public bool HasReachedTarget => hasReachedTarget;
    public float StopTolerance => stopTolerance;

    private void Awake()
    {
        productRigidbody = GetComponent<Rigidbody>();
        productRigidbody.isKinematic = true;
        productRigidbody.useGravity = false;
    }

    public void MoveTo(Transform target, MovementAxis axis)
    {
        if (target == null)
        {
            return;
        }

        movementAxis = axis;
        targetPosition = target.position;
        targetCoordinate = axis == MovementAxis.X
            ? targetPosition.x
            : targetPosition.z;
        hasReachedTarget = false;
        isPaused = false;
        isMoving = true;
    }

    public void TogglePause()
    {
        if (isMoving)
        {
            isPaused = !isPaused;
        }
    }

    public void Stop()
    {
        isMoving = false;
        isPaused = false;
    }

    public void ResetMovementState()
    {
        isMoving = false;
        isPaused = false;
        hasReachedTarget = false;

        if (productRigidbody == null)
        {
            productRigidbody = GetComponent<Rigidbody>();
        }

        productRigidbody.linearVelocity = Vector3.zero;
        productRigidbody.angularVelocity = Vector3.zero;
        targetPosition = productRigidbody.position;
        targetCoordinate = movementAxis == MovementAxis.X
            ? targetPosition.x
            : targetPosition.z;
    }

    public bool ConsumeTargetReached()
    {
        if (!hasReachedTarget)
        {
            return false;
        }

        hasReachedTarget = false;
        return true;
    }

    private void FixedUpdate()
    {
        if (!isMoving || isPaused)
        {
            return;
        }

        Vector3 currentPosition = productRigidbody.position;
        float currentCoordinate = movementAxis == MovementAxis.X
            ? currentPosition.x
            : currentPosition.z;

        float nextCoordinate = Mathf.MoveTowards(
            currentCoordinate,
            targetCoordinate,
            moveSpeed * Time.fixedDeltaTime);

        Vector3 nextPosition = currentPosition;
        if (movementAxis == MovementAxis.X)
        {
            nextPosition.x = nextCoordinate;
        }
        else
        {
            nextPosition.z = nextCoordinate;
        }

        bool reached = Mathf.Abs(nextCoordinate - targetCoordinate) <= stopTolerance;
        if (reached)
        {
            if (movementAxis == MovementAxis.X)
            {
                nextPosition.x = targetCoordinate;
            }
            else
            {
                nextPosition.x = targetPosition.x;
                nextPosition.y = targetPosition.y;
                nextPosition.z = targetPosition.z;
            }
        }

        productRigidbody.MovePosition(nextPosition);

        if (reached)
        {
            isMoving = false;
            isPaused = false;
            hasReachedTarget = true;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (isMoving &&
            movementAxis == MovementAxis.X &&
            other.gameObject.name == "InspectionPosition")
        {
            targetCoordinate = other.transform.position.x;
        }
    }
}
