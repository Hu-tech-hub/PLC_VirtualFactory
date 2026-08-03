using UnityEngine;
using UnityEngine.InputSystem;

public sealed class PlcProcessAdapter : MonoBehaviour
{
    private const int InspectionResultMask = 0x001C;
    private const int InspectionCompleteBit = 0x0004;
    private const int InspectionOkBit = 0x0008;
    private const int InspectionNgBit = 0x0010;

    private enum InspectionHandshakeState
    {
        WaitingForRequest,
        WaitingForResultInput,
        WritingResult,
        WaitingForPlcAck,
        ClearingResult
    }

    [Header("Mode")]
    [SerializeField] private bool plcIntegrationMode;

    [Header("PLC")]
    [SerializeField] private PlcConnectionTest plcConnection;

    [Header("Existing Process")]
    [SerializeField] private ProductProcessController processController;
    [SerializeField] private ProductMover productMover;
    [SerializeField] private Transform product;
    [SerializeField] private Transform productStartPoint;
    [SerializeField] private Transform inspectionStopPoint;

    [SerializeField, Min(0f)] private float positionTolerance = 0.05f;

    private bool appliedMode;
    private bool conveyorStateInitialized;
    private bool previousConveyorCommand;
    private bool productSensorSent;
    private bool inspectionSensorSent;
    private bool inspectionWritePending;
    private bool inspectionReached;
    private InspectionHandshakeState inspectionHandshakeState;
    private int submittedResultBits;

    public bool PlcIntegrationMode => plcIntegrationMode;

    private void Start()
    {
        ApplyMode();
    }

    private void OnEnable()
    {
        inspectionSensorSent = false;
        inspectionWritePending = false;
        inspectionReached = false;
        inspectionHandshakeState = InspectionHandshakeState.WaitingForRequest;
        submittedResultBits = 0;

        if (plcConnection != null)
        {
            plcConnection.D0WriteCompleted -= OnD0WriteCompleted;
            plcConnection.D0WriteCompleted += OnD0WriteCompleted;
        }
    }

    private void Update()
    {
        if (appliedMode != plcIntegrationMode)
        {
            ApplyMode();
        }

        if (!plcIntegrationMode)
        {
            return;
        }

        if (plcConnection == null || !plcConnection.Connected)
        {
            productMover.Stop();
            if (conveyorStateInitialized && previousConveyorCommand)
            {
                previousConveyorCommand = false;
                Debug.Log("[PLC PROCESS] Conveyor command OFF");
            }

            return;
        }

        // Sensor methods remain pending while D0Ready is false and are retried every frame.
        UpdateProductSensor();
        UpdateConveyorCommand();
        UpdateInspectionSensor();
        UpdateInspectionHandshake();
    }

    private void ApplyMode()
    {
        appliedMode = plcIntegrationMode;
        processController.SetPlcIntegrationMode(plcIntegrationMode);
        productMover.Stop();

        if (!plcIntegrationMode)
        {
            conveyorStateInitialized = false;
            previousConveyorCommand = false;
        }

        inspectionHandshakeState = InspectionHandshakeState.WaitingForRequest;
        submittedResultBits = 0;
    }

    private void UpdateProductSensor()
    {
        if (productSensorSent)
        {
            return;
        }

        float distance = Vector3.Distance(product.position, productStartPoint.position);
        if (distance > positionTolerance || !plcConnection.D0Ready)
        {
            return;
        }

        Debug.Log($"[PLC INPUT] Product start detected: distance={distance:F4}, threshold={positionTolerance:F4}");
        if (plcConnection.SetD0Bit(0, true))
        {
            productSensorSent = true;
            Debug.Log("[PLC PROCESS] Product sensor ON");
        }
        else if (plcConnection.D0Ready && (plcConnection.D0Word & 0x0001) != 0)
        {
            productSensorSent = true;
        }
    }

    private void UpdateConveyorCommand()
    {
        bool conveyorCommand = (plcConnection.D100Value & 0x0001) != 0;
        if (!conveyorStateInitialized)
        {
            conveyorStateInitialized = true;
            previousConveyorCommand = conveyorCommand;
            if (conveyorCommand)
            {
                Debug.Log("[PLC PROCESS] Conveyor command ON");
                StartProductMovement();
            }

            return;
        }

        if (conveyorCommand == previousConveyorCommand)
        {
            if (conveyorCommand && !inspectionReached && !productMover.IsMoving)
            {
                StartProductMovement();
            }

            return;
        }

        previousConveyorCommand = conveyorCommand;
        if (conveyorCommand)
        {
            Debug.Log("[PLC PROCESS] Conveyor command ON");
            StartProductMovement();
        }
        else
        {
            productMover.Stop();
            Debug.Log("[PLC PROCESS] Conveyor command OFF");
        }
    }

    private void StartProductMovement()
    {
        if (!inspectionReached)
        {
            productMover.MoveTo(
                inspectionStopPoint,
                ProductMover.MovementAxis.X);
        }
    }

    private void UpdateInspectionSensor()
    {
        if (inspectionSensorSent)
        {
            return;
        }

        if (!plcConnection.D0Ready)
        {
            return;
        }

        float distance = Vector3.Distance(product.position, inspectionStopPoint.position);
        float threshold = productMover.StopTolerance;
        bool reachedEvent = productMover.ConsumeTargetReached();
        if (!inspectionReached && !reachedEvent && distance > threshold)
        {
            return;
        }

        if (!inspectionReached)
        {
            inspectionReached = true;
            productMover.Stop();
            Debug.Log($"[PLC INPUT] Inspection check: distance={distance:F4}, threshold={threshold:F4}");
            Debug.Log("[PLC INPUT] Inspection position detected");
        }

        TrySendInspectionSensor();
    }

    private void TrySendInspectionSensor()
    {
        if (inspectionSensorSent || inspectionWritePending)
        {
            return;
        }

        if ((plcConnection.D0Word & 0x0002) != 0)
        {
            inspectionSensorSent = true;
            return;
        }

        if (plcConnection.SetD0Bit(1, true))
        {
            inspectionWritePending = true;
        }
    }

    private void OnD0WriteCompleted(int result, int value)
    {
        if (inspectionWritePending && (value & 0x0002) != 0)
        {
            inspectionWritePending = false;
            if (result == 0)
            {
                inspectionSensorSent = true;
            }
        }

        if (inspectionHandshakeState == InspectionHandshakeState.WritingResult &&
            (value & InspectionResultMask) == submittedResultBits)
        {
            if (result == 0)
            {
                inspectionHandshakeState = InspectionHandshakeState.WaitingForPlcAck;
                Debug.Log("[PLC INSPECTION] Result D0 write succeeded");
            }
            else
            {
                inspectionHandshakeState = InspectionHandshakeState.WaitingForResultInput;
                submittedResultBits = 0;
                Debug.Log($"[PLC INSPECTION] Result D0 write failed: 0x{result:X8}");
            }

            return;
        }

        if (inspectionHandshakeState == InspectionHandshakeState.ClearingResult &&
            (value & InspectionResultMask) == 0)
        {
            if (result == 0)
            {
                Debug.Log("[PLC INSPECTION] Result bits cleared");
                CompleteInspectionHandshakeCycle();
            }
            else
            {
                inspectionHandshakeState = InspectionHandshakeState.WaitingForPlcAck;
                Debug.Log($"[PLC INSPECTION] Result clear failed: 0x{result:X8}");
            }
        }
    }

    private void UpdateInspectionHandshake()
    {
        bool inspectionRequest = (plcConnection.D100Value & 0x0002) != 0;

        switch (inspectionHandshakeState)
        {
            case InspectionHandshakeState.WaitingForRequest:
                if (inspectionRequest &&
                    inspectionReached &&
                    inspectionSensorSent &&
                    plcConnection.D0Ready &&
                    !productMover.IsMoving)
                {
                    inspectionHandshakeState = InspectionHandshakeState.WaitingForResultInput;
                    Debug.Log("[PLC INSPECTION] Request ON detected");
                }
                break;

            case InspectionHandshakeState.WaitingForResultInput:
                if (!inspectionRequest)
                {
                    inspectionHandshakeState = InspectionHandshakeState.WaitingForRequest;
                    return;
                }

                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    return;
                }

                if (keyboard.oKey.wasPressedThisFrame)
                {
                    SubmitInspectionResult(true);
                }
                else if (keyboard.nKey.wasPressedThisFrame)
                {
                    SubmitInspectionResult(false);
                }
                break;

            case InspectionHandshakeState.WaitingForPlcAck:
                if (!inspectionRequest)
                {
                    Debug.Log("[PLC INSPECTION] D100.1 OFF ACK detected");
                    BeginClearInspectionResult();
                }
                break;
        }
    }

    private void SubmitInspectionResult(bool isOk)
    {
        submittedResultBits = InspectionCompleteBit |
                              (isOk ? InspectionOkBit : InspectionNgBit);
        inspectionHandshakeState = InspectionHandshakeState.WritingResult;
        Debug.Log(isOk
            ? "[PLC INSPECTION] OK selected"
            : "[PLC INSPECTION] NG selected");

        if (!plcConnection.SetD0MaskedBits(InspectionResultMask, submittedResultBits))
        {
            inspectionHandshakeState = InspectionHandshakeState.WaitingForResultInput;
            submittedResultBits = 0;
            Debug.Log("[PLC INSPECTION] Result D0 write failed: request rejected");
        }
    }

    private void BeginClearInspectionResult()
    {
        inspectionHandshakeState = InspectionHandshakeState.ClearingResult;
        if (plcConnection.SetD0MaskedBits(InspectionResultMask, 0))
        {
            return;
        }

        if ((plcConnection.D0Word & InspectionResultMask) == 0)
        {
            Debug.Log("[PLC INSPECTION] Result bits cleared");
            CompleteInspectionHandshakeCycle();
        }
        else
        {
            inspectionHandshakeState = InspectionHandshakeState.WaitingForPlcAck;
            Debug.Log("[PLC INSPECTION] Result clear failed: request rejected");
        }
    }

    private void CompleteInspectionHandshakeCycle()
    {
        inspectionHandshakeState = InspectionHandshakeState.WaitingForRequest;
        submittedResultBits = 0;
        Debug.Log("[PLC INSPECTION] Ready for next inspection cycle");
    }

    private void OnDisable()
    {
        if (plcConnection != null)
        {
            plcConnection.D0WriteCompleted -= OnD0WriteCompleted;
        }

        if (plcIntegrationMode && productMover != null)
        {
            productMover.Stop();
        }

        if (processController != null)
        {
            processController.SetPlcIntegrationMode(false);
        }
    }
}
