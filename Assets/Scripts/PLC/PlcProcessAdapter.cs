using UnityEngine;
using UnityEngine.InputSystem;

public sealed class PlcProcessAdapter : MonoBehaviour
{
    private const int ProductSensorBit = 1 << 0;
    private const int InspectionSensorBit = 1 << 1;
    private const int InspectionResultMask = 0x001C;
    private const int InspectionCompleteBit = 1 << 2;
    private const int InspectionOkBit = 1 << 3;
    private const int InspectionNgBit = 1 << 4;
    private const int DischargeCompleteBit = 1 << 5;
    private const int CylinderExtendedBit = 1 << 6;
    private const int CylinderRetractedBit = 1 << 7;

    private const int ConveyorCommandBit = 1 << 0;
    private const int InspectionRequestBit = 1 << 1;
    private const int OkDischargeCommandBit = 1 << 2;
    private const int NgExtendCommandBit = 1 << 3;
    private const int NgRetractCommandBit = 1 << 4;
    private const int CycleInputSignalMask = 0x00FE;

    private enum InspectionHandshakeState
    {
        WaitingForRequest,
        WaitingForResultInput,
        WritingResult,
        WaitingForPlcAck,
        ClearingResult
    }

    private enum DischargeState
    {
        WaitingForCommand,
        MovingToOk,
        WritingOkDischargeComplete,
        WaitingForOkAck,
        ClearingOkDischargeComplete,
        ExtendingNgCylinder,
        WritingCylinderExtended,
        WaitingForExtendAck,
        ClearingCylinderExtended,
        MovingToNg,
        WritingNgDischargeComplete,
        WaitingForRetractCommand,
        ClearingNgDischargeComplete,
        RetractingNgCylinder,
        WritingCylinderRetracted,
        WaitingForRetractAck,
        ClearingCylinderRetracted,
        Completed,
        Faulted
    }

    private enum CyclePreparationState
    {
        Inactive,
        PreparingNextCycle,
        WaitingForNextProductDetection,
        Faulted
    }

    [Header("Mode")]
    [SerializeField] private bool plcIntegrationMode;
    [SerializeField] private bool repeatProductionInPlcMode = true;

    [Header("PLC")]
    [SerializeField] private PlcConnectionTest plcConnection;

    [Header("Existing Process")]
    [SerializeField] private ProductProcessController processController;
    [SerializeField] private ProductMover productMover;
    [SerializeField] private CylinderController cylinderController;
    [SerializeField] private Transform product;
    [SerializeField] private Transform productStartPoint;
    [SerializeField] private Transform inspectionStopPoint;
    [SerializeField] private Transform okTargetPoint;
    [SerializeField] private Transform ngTargetPoint;

    [SerializeField, Min(0f)] private float positionTolerance = 0.05f;

    private bool appliedMode;
    private bool conveyorStateInitialized;
    private bool previousConveyorCommand;
    private bool productSensorWritePending;
    private bool productSensorPendingValue;
    private bool inspectionSensorWritePending;
    private bool inspectionSensorPendingValue;
    private bool inspectionReached;
    private InspectionHandshakeState inspectionHandshakeState;
    private DischargeState dischargeState;
    private CyclePreparationState cyclePreparationState;
    private int submittedResultBits;

    public bool PlcIntegrationMode => plcIntegrationMode;

    private void Start()
    {
        ApplyMode();
    }

    private void OnEnable()
    {
        productSensorWritePending = false;
        inspectionSensorWritePending = false;
        inspectionReached = false;
        inspectionHandshakeState = InspectionHandshakeState.WaitingForRequest;
        dischargeState = DischargeState.WaitingForCommand;
        cyclePreparationState = CyclePreparationState.Inactive;
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

        if (cyclePreparationState != CyclePreparationState.Inactive)
        {
            UpdateCyclePreparation();
            return;
        }

        if (plcConnection == null || productMover == null || product == null ||
            !plcConnection.Connected)
        {
            if (productMover != null)
            {
                productMover.Stop();
            }

            if (conveyorStateInitialized && previousConveyorCommand)
            {
                previousConveyorCommand = false;
                Debug.Log("[PLC PROCESS] Conveyor command OFF");
            }

            return;
        }

        // Level inputs are requested only when the observed position and current D0 bit differ.
        UpdateProductSensorLevel();
        UpdateConveyorCommand();
        UpdateInspectionSensorLevel();
        UpdateInspectionHandshake();
        UpdateDischargeStateMachine();
    }

    private void ApplyMode()
    {
        appliedMode = plcIntegrationMode;
        if (processController != null)
        {
            processController.SetPlcIntegrationMode(plcIntegrationMode);
        }

        if (productMover != null)
        {
            productMover.Stop();
        }

        if (!plcIntegrationMode)
        {
            conveyorStateInitialized = false;
            previousConveyorCommand = false;
        }

        inspectionHandshakeState = InspectionHandshakeState.WaitingForRequest;
        dischargeState = DischargeState.WaitingForCommand;
        cyclePreparationState = CyclePreparationState.Inactive;
        submittedResultBits = 0;
    }

    private void UpdateProductSensorLevel()
    {
        if (productStartPoint == null)
        {
            return;
        }

        bool productAtStart = Vector3.Distance(product.position, productStartPoint.position) <=
                              positionTolerance;
        UpdateLevelInput(
            ProductSensorBit,
            productAtStart,
            ref productSensorWritePending,
            ref productSensorPendingValue,
            "Product sensor");
    }

    private void UpdateInspectionSensorLevel()
    {
        if (inspectionStopPoint == null)
        {
            return;
        }

        float distance = Vector3.Distance(product.position, inspectionStopPoint.position);
        float threshold = Mathf.Max(positionTolerance, productMover.StopTolerance);

        if (!inspectionReached)
        {
            bool reachedEvent = productMover.ConsumeTargetReached();
            if (reachedEvent || distance <= threshold)
            {
                inspectionReached = true;
                productMover.Stop();
                Debug.Log($"[PLC INPUT] Inspection check: distance={distance:F4}, threshold={threshold:F4}");
                Debug.Log("[PLC INPUT] Inspection position detected");
            }
        }

        bool productAtInspection = distance <= threshold;
        UpdateLevelInput(
            InspectionSensorBit,
            productAtInspection,
            ref inspectionSensorWritePending,
            ref inspectionSensorPendingValue,
            "Inspection sensor");
    }

    private void UpdateLevelInput(
        int bit,
        bool desiredValue,
        ref bool writePending,
        ref bool pendingValue,
        string label)
    {
        if (!plcConnection.D0Ready)
        {
            return;
        }

        bool currentValue = (plcConnection.D0Word & bit) != 0;
        if (writePending)
        {
            if (pendingValue == desiredValue)
            {
                return;
            }

            if (plcConnection.SetD0MaskedBits(bit, desiredValue ? bit : 0))
            {
                pendingValue = desiredValue;
            }
            else if (currentValue == desiredValue)
            {
                writePending = false;
            }

            return;
        }

        if (currentValue == desiredValue)
        {
            return;
        }

        if (plcConnection.SetD0MaskedBits(bit, desiredValue ? bit : 0))
        {
            writePending = true;
            pendingValue = desiredValue;
            return;
        }

        Debug.LogWarning($"[PLC INPUT] {label} write request was rejected");
    }

    private void UpdateConveyorCommand()
    {
        bool conveyorCommand = (plcConnection.D100Value & ConveyorCommandBit) != 0;
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
            if (!IsDischargeCycleActive())
            {
                productMover.Stop();
            }

            Debug.Log("[PLC PROCESS] Conveyor command OFF");
        }
    }

    private void StartProductMovement()
    {
        if (!inspectionReached && inspectionStopPoint != null)
        {
            productMover.MoveTo(inspectionStopPoint, ProductMover.MovementAxis.X);
        }
    }

    private void OnD0WriteCompleted(int result, int value)
    {
        CompleteLevelInputWrite(
            ProductSensorBit,
            result,
            value,
            ref productSensorWritePending,
            productSensorPendingValue,
            "Product sensor");
        CompleteLevelInputWrite(
            InspectionSensorBit,
            result,
            value,
            ref inspectionSensorWritePending,
            inspectionSensorPendingValue,
            "Inspection sensor");

        HandleInspectionWriteCompleted(result, value);
        HandleDischargeWriteCompleted(result, value);
    }

    private static void CompleteLevelInputWrite(
        int bit,
        int result,
        int value,
        ref bool writePending,
        bool pendingValue,
        string label)
    {
        if (!writePending || ((value & bit) != 0) != pendingValue)
        {
            return;
        }

        writePending = false;
        if (result != 0)
        {
            return;
        }

        Debug.Log($"[PLC PROCESS] {label} {(pendingValue ? "ON" : "OFF")}");
    }

    private void HandleInspectionWriteCompleted(int result, int value)
    {
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
        bool inspectionRequest = (plcConnection.D100Value & InspectionRequestBit) != 0;

        switch (inspectionHandshakeState)
        {
            case InspectionHandshakeState.WaitingForRequest:
                if (inspectionRequest &&
                    inspectionReached &&
                    (plcConnection.D0Word & InspectionSensorBit) != 0 &&
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

    private void UpdateDischargeStateMachine()
    {
        bool okCommand = (plcConnection.D100Value & OkDischargeCommandBit) != 0;
        bool extendCommand = (plcConnection.D100Value & NgExtendCommandBit) != 0;
        bool retractCommand = (plcConnection.D100Value & NgRetractCommandBit) != 0;

        if (dischargeState == DischargeState.Faulted ||
            dischargeState == DischargeState.Completed)
        {
            return;
        }

        if ((okCommand && extendCommand) || (extendCommand && retractCommand))
        {
            EnterDischargeFault(
                $"Conflicting commands: OK={okCommand}, Extend={extendCommand}, Retract={retractCommand}");
            return;
        }

        bool okCycle = IsOkCycleActive();
        bool ngCycle = IsNgCycleActive();
        if (okCycle && (extendCommand || retractCommand))
        {
            EnterDischargeFault("NG command became active during the OK cycle");
            return;
        }

        if (ngCycle && okCommand)
        {
            EnterDischargeFault("OK command became active during the NG cycle");
            return;
        }

        switch (dischargeState)
        {
            case DischargeState.WaitingForCommand:
                if (!plcConnection.D0Ready ||
                    inspectionHandshakeState != InspectionHandshakeState.WaitingForRequest)
                {
                    return;
                }

                if (retractCommand)
                {
                    EnterDischargeFault("Retract command became active before an NG extend cycle");
                }
                else if (okCommand)
                {
                    BeginOkDischarge();
                }
                else if (extendCommand)
                {
                    BeginNgExtend();
                }
                break;

            case DischargeState.MovingToOk:
                if (!okCommand)
                {
                    AbortForMissingCommand("OK command turned OFF before the OK target was reached");
                }
                else if (HasProductReached(okTargetPoint, ProductMover.MovementAxis.X))
                {
                    productMover.Stop();
                    Debug.Log("[PLC DISCHARGE] OK target reached");
                    RequestDischargeBit(
                        DischargeCompleteBit,
                        true,
                        DischargeState.WritingOkDischargeComplete);
                }
                break;

            case DischargeState.WaitingForOkAck:
                if (!okCommand)
                {
                    Debug.Log("[PLC DISCHARGE] OK command OFF ACK");
                    RequestDischargeBit(
                        DischargeCompleteBit,
                        false,
                        DischargeState.ClearingOkDischargeComplete);
                }
                break;

            case DischargeState.ExtendingNgCylinder:
                if (!extendCommand)
                {
                    AbortForMissingCommand("NG extend command turned OFF before extension completed");
                }
                else if (retractCommand)
                {
                    EnterDischargeFault("Retract command became active before extend ACK completed");
                }
                else if (cylinderController != null && cylinderController.IsExtended)
                {
                    Debug.Log("[PLC DISCHARGE] Cylinder extended");
                    RequestDischargeBit(
                        CylinderExtendedBit,
                        true,
                        DischargeState.WritingCylinderExtended);
                }
                break;

            case DischargeState.WaitingForExtendAck:
                if (retractCommand)
                {
                    EnterDischargeFault("Retract command became active before extend command OFF ACK");
                }
                else if (!extendCommand)
                {
                    Debug.Log("[PLC DISCHARGE] Extend command OFF ACK");
                    RequestDischargeBit(
                        CylinderExtendedBit,
                        false,
                        DischargeState.ClearingCylinderExtended);
                }
                break;

            case DischargeState.MovingToNg:
                if (extendCommand)
                {
                    EnterDischargeFault("NG extend command turned ON again while moving to NG");
                }
                else if (retractCommand)
                {
                    EnterDischargeFault("Retract command became active before the NG target was reached");
                }
                else if (HasProductReached(ngTargetPoint, ProductMover.MovementAxis.Z))
                {
                    productMover.Stop();
                    Debug.Log("[PLC DISCHARGE] NG target reached");
                    RequestDischargeBit(
                        DischargeCompleteBit,
                        true,
                        DischargeState.WritingNgDischargeComplete);
                }
                break;

            case DischargeState.WaitingForRetractCommand:
                if (extendCommand)
                {
                    EnterDischargeFault("NG extend command turned ON again before retract command");
                }
                else if (retractCommand)
                {
                    Debug.Log("[PLC DISCHARGE] Retract command ON");
                    RequestDischargeBit(
                        DischargeCompleteBit,
                        false,
                        DischargeState.ClearingNgDischargeComplete);
                }
                break;

            case DischargeState.RetractingNgCylinder:
                if (!retractCommand)
                {
                    AbortForMissingCommand("Retract command turned OFF before retraction completed");
                }
                else if (cylinderController != null && cylinderController.IsRetracted)
                {
                    Debug.Log("[PLC DISCHARGE] Cylinder retracted");
                    RequestDischargeBit(
                        CylinderRetractedBit,
                        true,
                        DischargeState.WritingCylinderRetracted);
                }
                break;

            case DischargeState.WaitingForRetractAck:
                if (!retractCommand)
                {
                    Debug.Log("[PLC DISCHARGE] Retract command OFF ACK");
                    RequestDischargeBit(
                        CylinderRetractedBit,
                        false,
                        DischargeState.ClearingCylinderRetracted);
                }
                break;
        }
    }

    private void BeginOkDischarge()
    {
        if (!ValidateDischargeReferences(false))
        {
            return;
        }

        if ((plcConnection.D0Word & DischargeCompleteBit) != 0)
        {
            EnterDischargeFault("D0.5 was already ON before the OK discharge started");
            return;
        }

        Debug.Log("[PLC DISCHARGE] OK command ON");
        dischargeState = DischargeState.MovingToOk;
        productMover.MoveTo(okTargetPoint, ProductMover.MovementAxis.X);
        Debug.Log("[PLC DISCHARGE] Moving product to OK");
    }

    private void BeginNgExtend()
    {
        if (!ValidateDischargeReferences(true))
        {
            return;
        }

        if ((plcConnection.D0Word &
             (DischargeCompleteBit | CylinderExtendedBit | CylinderRetractedBit)) != 0)
        {
            EnterDischargeFault("A discharge confirmation bit was already ON before the NG cycle started");
            return;
        }

        Debug.Log("[PLC DISCHARGE] NG extend command ON");
        dischargeState = DischargeState.ExtendingNgCylinder;
        cylinderController.BeginExtend();
    }

    private bool ValidateDischargeReferences(bool ngCycle)
    {
        if (productMover == null || product == null ||
            (ngCycle ? ngTargetPoint == null : okTargetPoint == null) ||
            (ngCycle && cylinderController == null))
        {
            EnterDischargeFault("Required existing scene reference is missing");
            return false;
        }

        return true;
    }

    private bool HasProductReached(Transform target, ProductMover.MovementAxis axis)
    {
        if (target == null)
        {
            return false;
        }

        bool reachedEvent = productMover.ConsumeTargetReached();
        float distance = axis == ProductMover.MovementAxis.X
            ? Mathf.Abs(product.position.x - target.position.x)
            : Mathf.Abs(product.position.z - target.position.z);
        float threshold = Mathf.Max(positionTolerance, productMover.StopTolerance);
        return reachedEvent || (!productMover.IsMoving && distance <= threshold);
    }

    private void RequestDischargeBit(int bit, bool enabled, DischargeState writingState)
    {
        bool currentValue = (plcConnection.D0Word & bit) != 0;
        if (currentValue == enabled)
        {
            if (enabled)
            {
                EnterDischargeFault($"D0 confirmation bit 0x{bit:X4} was already ON");
            }
            else
            {
                if (!plcConnection.D0Ready)
                {
                    EnterDischargeFault(
                        $"D0 confirmation bit 0x{bit:X4} cannot be cleared while D0 is not ready");
                    return;
                }

                dischargeState = writingState;
                HandleDischargeWriteCompleted(0, plcConnection.D0Word);
            }

            return;
        }

        dischargeState = writingState;
        if (!plcConnection.SetD0MaskedBits(bit, enabled ? bit : 0))
        {
            EnterDischargeFault($"D0 confirmation bit 0x{bit:X4} write request was rejected");
        }
    }

    private void HandleDischargeWriteCompleted(int result, int value)
    {
        int expectedBit;
        bool expectedValue;
        switch (dischargeState)
        {
            case DischargeState.WritingOkDischargeComplete:
            case DischargeState.WritingNgDischargeComplete:
                expectedBit = DischargeCompleteBit;
                expectedValue = true;
                break;
            case DischargeState.ClearingOkDischargeComplete:
            case DischargeState.ClearingNgDischargeComplete:
                expectedBit = DischargeCompleteBit;
                expectedValue = false;
                break;
            case DischargeState.WritingCylinderExtended:
                expectedBit = CylinderExtendedBit;
                expectedValue = true;
                break;
            case DischargeState.ClearingCylinderExtended:
                expectedBit = CylinderExtendedBit;
                expectedValue = false;
                break;
            case DischargeState.WritingCylinderRetracted:
                expectedBit = CylinderRetractedBit;
                expectedValue = true;
                break;
            case DischargeState.ClearingCylinderRetracted:
                expectedBit = CylinderRetractedBit;
                expectedValue = false;
                break;
            default:
                return;
        }

        if (((value & expectedBit) != 0) != expectedValue)
        {
            return;
        }

        if (result != 0)
        {
            EnterDischargeFault(
                $"D0 confirmation write failed: bit=0x{expectedBit:X4}, result=0x{result:X8}");
            return;
        }

        switch (dischargeState)
        {
            case DischargeState.WritingOkDischargeComplete:
                dischargeState = DischargeState.WaitingForOkAck;
                Debug.Log("[PLC DISCHARGE] Discharge complete ON");
                break;

            case DischargeState.ClearingOkDischargeComplete:
                dischargeState = DischargeState.Completed;
                Debug.Log("[PLC DISCHARGE] Discharge complete OFF");
                Debug.Log("[PLC CYCLE] OK cycle ACK completed");
                BeginNextCyclePreparation();
                break;

            case DischargeState.WritingCylinderExtended:
                dischargeState = DischargeState.WaitingForExtendAck;
                Debug.Log("[PLC DISCHARGE] Cylinder extended confirmation ON");
                break;

            case DischargeState.ClearingCylinderExtended:
                dischargeState = DischargeState.MovingToNg;
                productMover.MoveTo(ngTargetPoint, ProductMover.MovementAxis.Z);
                Debug.Log("[PLC DISCHARGE] Moving product to NG");
                break;

            case DischargeState.WritingNgDischargeComplete:
                dischargeState = DischargeState.WaitingForRetractCommand;
                Debug.Log("[PLC DISCHARGE] Discharge complete ON");
                break;

            case DischargeState.ClearingNgDischargeComplete:
                dischargeState = DischargeState.RetractingNgCylinder;
                Debug.Log("[PLC DISCHARGE] Discharge complete OFF");
                cylinderController.BeginRetract();
                break;

            case DischargeState.WritingCylinderRetracted:
                dischargeState = DischargeState.WaitingForRetractAck;
                Debug.Log("[PLC DISCHARGE] Cylinder retracted confirmation ON");
                break;

            case DischargeState.ClearingCylinderRetracted:
                dischargeState = DischargeState.Completed;
                Debug.Log("[PLC DISCHARGE] NG cycle completed");
                Debug.Log("[PLC CYCLE] NG cycle ACK completed");
                BeginNextCyclePreparation();
                break;
        }
    }

    private void BeginNextCyclePreparation()
    {
        if (!repeatProductionInPlcMode || !plcIntegrationMode)
        {
            return;
        }

        if (plcConnection == null || !plcConnection.Connected || !plcConnection.D0Ready)
        {
            FailCyclePreparation("[PLC CYCLE] Cannot restart: PLC disconnected");
            return;
        }

        cyclePreparationState = CyclePreparationState.PreparingNextCycle;
        productMover.ResetMovementState();
        Debug.Log("[PLC CYCLE] Preparing next cycle");
    }

    private void UpdateCyclePreparation()
    {
        if (cyclePreparationState == CyclePreparationState.Faulted)
        {
            return;
        }

        if (plcConnection == null || !plcConnection.Connected || !plcConnection.D0Ready)
        {
            FailCyclePreparation("[PLC CYCLE] Cannot restart: PLC disconnected");
            return;
        }

        if (cyclePreparationState == CyclePreparationState.WaitingForNextProductDetection)
        {
            UpdateProductSensorLevel();
            if (!productSensorWritePending &&
                (plcConnection.D0Word & ProductSensorBit) != 0)
            {
                cyclePreparationState = CyclePreparationState.Inactive;
                Debug.Log("[PLC CYCLE] Next cycle product sensor ON");
            }

            return;
        }

        if (cylinderController == null || !cylinderController.IsRetracted)
        {
            FailCyclePreparation("[PLC CYCLE] Cannot restart: cylinder is not retracted");
            return;
        }

        if (productSensorWritePending || inspectionSensorWritePending)
        {
            return;
        }

        if ((plcConnection.D0Word & CycleInputSignalMask) != 0 ||
            inspectionHandshakeState != InspectionHandshakeState.WaitingForRequest ||
            submittedResultBits != 0)
        {
            FailCyclePreparation("[PLC CYCLE] Cannot restart: PLC input signal remains ON");
            return;
        }

        if (productMover == null || product == null || productStartPoint == null ||
            productMover.transform != product)
        {
            FailCyclePreparation("[PLC CYCLE] Cannot restart: required scene reference is missing");
            return;
        }

        Debug.Log("[PLC CYCLE] PLC input signals verified clear");
        RestoreProductToStartPose();
        Debug.Log("[PLC CYCLE] Product returned to start point");

        ResetInternalCycleState();
        Debug.Log("[PLC CYCLE] Internal process state reset");

        cyclePreparationState = CyclePreparationState.WaitingForNextProductDetection;
        Debug.Log("[PLC CYCLE] Ready for next product detection");
    }

    private void RestoreProductToStartPose()
    {
        if (product.TryGetComponent(out Rigidbody productRigidbody))
        {
            productRigidbody.linearVelocity = Vector3.zero;
            productRigidbody.angularVelocity = Vector3.zero;
            productRigidbody.position = productStartPoint.position;
            productRigidbody.rotation = productStartPoint.rotation;
        }

        product.SetPositionAndRotation(productStartPoint.position, productStartPoint.rotation);
        Physics.SyncTransforms();
    }

    private void ResetInternalCycleState()
    {
        productSensorWritePending = false;
        productSensorPendingValue = false;
        inspectionSensorWritePending = false;
        inspectionSensorPendingValue = false;
        inspectionReached = false;
        inspectionHandshakeState = InspectionHandshakeState.WaitingForRequest;
        submittedResultBits = 0;
        dischargeState = DischargeState.WaitingForCommand;
        conveyorStateInitialized = false;
        previousConveyorCommand = false;
        productMover.ResetMovementState();
    }

    private void FailCyclePreparation(string message)
    {
        if (cyclePreparationState == CyclePreparationState.Faulted)
        {
            return;
        }

        cyclePreparationState = CyclePreparationState.Faulted;
        dischargeState = DischargeState.Faulted;
        if (productMover != null)
        {
            productMover.Stop();
        }

        Debug.LogError(message);
    }

    private bool IsDischargeCycleActive()
    {
        return dischargeState != DischargeState.WaitingForCommand &&
               dischargeState != DischargeState.Completed &&
               dischargeState != DischargeState.Faulted;
    }

    private bool IsOkCycleActive()
    {
        return dischargeState >= DischargeState.MovingToOk &&
               dischargeState <= DischargeState.ClearingOkDischargeComplete;
    }

    private bool IsNgCycleActive()
    {
        return dischargeState >= DischargeState.ExtendingNgCylinder &&
               dischargeState <= DischargeState.ClearingCylinderRetracted;
    }

    private void AbortForMissingCommand(string message)
    {
        if (dischargeState == DischargeState.Faulted)
        {
            return;
        }

        Debug.LogWarning($"[PLC DISCHARGE] {message}");
        dischargeState = DischargeState.Faulted;
        productMover.Stop();
    }

    private void EnterDischargeFault(string message)
    {
        if (dischargeState == DischargeState.Faulted)
        {
            return;
        }

        Debug.LogError($"[PLC DISCHARGE] {message}");
        dischargeState = DischargeState.Faulted;
        if (productMover != null)
        {
            productMover.Stop();
        }
    }

    private void OnDisable()
    {
        cyclePreparationState = CyclePreparationState.Inactive;

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
