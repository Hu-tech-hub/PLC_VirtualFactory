using UnityEngine;
using UnityEngine.InputSystem;

public sealed class ProductProcessController : MonoBehaviour
{
    public enum ProcessState
    {
        Waiting,
        MovingToInspection,
        WaitingForInspectionResult,
        MovingToOK,
        ExtendingCylinder,
        MovingToNG,
        RetractingCylinder,
        Completed
    }

    [Header("Process Objects")]
    [SerializeField]
    private Transform product;

    [SerializeField]
    private Transform cylinderRod;

    [SerializeField]
    private ProductMover productMover;

    [SerializeField]
    private CylinderController cylinderController;

    [Header("Process Targets")]
    [SerializeField]
    private Transform inspectionStopPoint;

    [SerializeField]
    private Transform okTargetPoint;

    [SerializeField]
    private Transform ngTargetPoint;

    [SerializeField]
    private Transform cylinderRetractedPoint;

    [SerializeField]
    private Transform cylinderExtendedPoint;

    [SerializeField]
    private ProcessState currentState = ProcessState.Waiting;

    private bool resultSelected;
    private bool completionLogged;
    private bool plcIntegrationMode;

    public ProcessState CurrentState => currentState;
    public bool PlcIntegrationMode => plcIntegrationMode;

    public void SetPlcIntegrationMode(bool enabled)
    {
        plcIntegrationMode = enabled;
    }

    public void ResetForPlcSafety()
    {
        resultSelected = false;
        completionLogged = false;
        currentState = ProcessState.Waiting;
        if (productMover != null)
        {
            productMover.ResetMovementState();
        }
    }

    private void Update()
    {
        HandleInput();
        UpdateProcess();
    }

    private void HandleInput()
    {
        if (plcIntegrationMode)
        {
            return;
        }

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        if (keyboard.spaceKey.wasPressedThisFrame)
        {
            if (currentState == ProcessState.Waiting)
            {
                productMover.MoveTo(
                    inspectionStopPoint,
                    ProductMover.MovementAxis.X);
                currentState = ProcessState.MovingToInspection;
            }
            else if (currentState == ProcessState.MovingToInspection)
            {
                productMover.TogglePause();
            }
        }

        if (currentState != ProcessState.WaitingForInspectionResult ||
            resultSelected)
        {
            return;
        }

        if (keyboard.oKey.wasPressedThisFrame)
        {
            resultSelected = true;
            productMover.MoveTo(okTargetPoint, ProductMover.MovementAxis.X);
            currentState = ProcessState.MovingToOK;
        }
        else if (keyboard.nKey.wasPressedThisFrame)
        {
            resultSelected = true;
            cylinderController.BeginExtend();
            currentState = ProcessState.ExtendingCylinder;
        }
    }

    private void UpdateProcess()
    {
        switch (currentState)
        {
            case ProcessState.MovingToInspection:
                if (productMover.ConsumeTargetReached())
                {
                    currentState = ProcessState.WaitingForInspectionResult;
                    Debug.Log("Inspection Position Centered");
                }
                break;

            case ProcessState.MovingToOK:
                if (productMover.ConsumeTargetReached())
                {
                    CompleteProcess("OK Discharge Complete");
                }
                break;

            case ProcessState.ExtendingCylinder:
                if (cylinderController.IsExtended)
                {
                    productMover.MoveTo(
                        ngTargetPoint,
                        ProductMover.MovementAxis.Z);
                    currentState = ProcessState.MovingToNG;
                }
                break;

            case ProcessState.MovingToNG:
                if (productMover.ConsumeTargetReached())
                {
                    cylinderController.BeginRetract();
                    currentState = ProcessState.RetractingCylinder;
                }
                break;

            case ProcessState.RetractingCylinder:
                if (cylinderController.IsRetracted)
                {
                    CompleteProcess("NG Discharge Complete");
                }
                break;
        }
    }

    private void CompleteProcess(string message)
    {
        currentState = ProcessState.Completed;
        if (!completionLogged)
        {
            completionLogged = true;
            Debug.Log(message);
        }
    }
}
