using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ActUtlType64Lib;

internal static class PlcMxBridge
{
    private const string MutexName = @"Local\PLC_VirtualFactory_PlcMxBridge";

    private static ActUtlType64Class plc;
    private static bool isOpen;
    private static Form messageLoop;
    private static Mutex singleInstanceMutex;
    private static int acceptingCommands = 1;
    private static int shutdownStarted;

    [STAThread]
    private static int Main()
    {
        WriteDiagnostic("Main entered");

        bool createdNew;
        singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
        if (!createdNew)
        {
            WriteResponse("OPEN 0x800700AA");
            singleInstanceMutex.Dispose();
            return 1;
        }

        int exitCode = 0;

        try
        {
            Console.Out.Flush();
            messageLoop = new Form
            {
                ShowInTaskbar = false,
                WindowState = FormWindowState.Minimized
            };
            WriteDiagnostic("Form created");

            messageLoop.Shown += delegate
            {
                WriteDiagnostic("Message loop started");
                messageLoop.BeginInvoke(new Action(delegate
                {
                    int openResult = Open();
                    WriteResponse("OPEN 0x{0:X8}", openResult);

                    if (openResult != 0)
                    {
                        exitCode = 1;
                        ExitAfterOpenFailure();
                        return;
                    }

                    Thread inputThread = new Thread(ReadCommands)
                    {
                        IsBackground = true,
                        Name = "PlcMxBridge-Input"
                    };
                    inputThread.Start();
                }));
            };

            Application.Run(messageLoop);
        }
        catch (Exception exception)
        {
            exitCode = 3;
            WriteResponse("ERROR OPEN 0x{0:X8}", exception.HResult);
        }
        finally
        {
            EnsureReleasedAfterRun();
            if (messageLoop != null)
            {
                messageLoop.Dispose();
            }

            if (singleInstanceMutex != null)
            {
                singleInstanceMutex.ReleaseMutex();
                WriteDiagnostic("Mutex released");
                singleInstanceMutex.Dispose();
                singleInstanceMutex = null;
            }
        }

        return exitCode;
    }

    private static int Open()
    {
        WriteDiagnostic("Creating ActUtlType64");

        try
        {
            plc = new ActUtlType64Class
            {
                ActLogicalStationNumber = 1
            };

            WriteDiagnostic("Calling Open");
            int result = plc.Open();
            WriteDiagnostic("Open returned: 0x{0:X8}", result);
            isOpen = result == 0;
            return result;
        }
        catch (Exception exception)
        {
            WriteDiagnostic("Open returned: 0x{0:X8}", exception.HResult);
            return exception.HResult;
        }
    }

    private static void ExitAfterOpenFailure()
    {
        if (Interlocked.CompareExchange(ref shutdownStarted, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref acceptingCommands, 0);
        isOpen = false;
        if (plc != null)
        {
            Marshal.FinalReleaseComObject(plc);
            plc = null;
        }

        Application.ExitThread();
    }

    private static void ReadCommands()
    {
        try
        {
            string command;
            while ((command = Console.ReadLine()) != null)
            {
                string capturedCommand = command;
                bool isCloseCommand = string.Equals(
                    capturedCommand.Trim(),
                    "CLOSE",
                    StringComparison.OrdinalIgnoreCase);

                if (Volatile.Read(ref acceptingCommands) == 0 && !isCloseCommand)
                {
                    break;
                }

                if (!TryBeginInvoke(new Action(delegate { ExecuteCommand(capturedCommand); })))
                {
                    break;
                }

                if (isCloseCommand)
                {
                    Interlocked.Exchange(ref acceptingCommands, 0);
                    return;
                }
            }

            RequestShutdownFromInput();
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref shutdownStarted) == 0)
            {
                WriteResponse("ERROR IPC 0x{0:X8}", exception.HResult);
            }

            RequestShutdownFromInput();
        }
    }

    private static void ExecuteCommand(string commandLine)
    {
        string[] parts = commandLine.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        string command = parts[0].ToUpperInvariant();

        if (command == "CLOSE" && parts.Length == 1)
        {
            ShutdownOnStaThread();
            return;
        }

        if (Volatile.Read(ref acceptingCommands) == 0)
        {
            return;
        }

        if (command == "READ" && parts.Length == 2)
        {
            ReadDevice(parts[1]);
            return;
        }

        if (command == "WRITE" && parts.Length == 3)
        {
            int value;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                WriteResponse("WRITE {0} 0x80070057", parts[1]);
                return;
            }

            WriteDevice(parts[1], value);
            return;
        }

        WriteResponse("ERROR COMMAND 0x80070057");
    }

    private static void ReadDevice(string device)
    {
        if (!isOpen || plc == null)
        {
            WriteResponse("READ {0} 0xF4001001 0", device);
            return;
        }

        try
        {
            int value;
            int result = plc.GetDevice(device, out value);
            WriteResponse("READ {0} 0x{1:X8} {2}", device, result, value);
        }
        catch (Exception exception)
        {
            WriteResponse("READ {0} 0x{1:X8} 0", device, exception.HResult);
        }
    }

    private static void WriteDevice(string device, int value)
    {
        if (!isOpen || plc == null)
        {
            WriteResponse("WRITE {0} 0xF4001001", device);
            return;
        }

        try
        {
            int result = plc.SetDevice(device, value);
            WriteResponse("WRITE {0} 0x{1:X8}", device, result);
        }
        catch (Exception exception)
        {
            WriteResponse("WRITE {0} 0x{1:X8}", device, exception.HResult);
        }
    }

    private static void ShutdownOnStaThread()
    {
        if (Interlocked.CompareExchange(ref shutdownStarted, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref acceptingCommands, 0);
        int closeResult = 0;

        if (plc != null && isOpen)
        {
            try
            {
                closeResult = plc.Close();
            }
            catch (Exception exception)
            {
                closeResult = exception.HResult;
            }
        }

        isOpen = false;
        WriteResponse("CLOSE 0x{0:X8}", closeResult);
        Console.Out.Flush();

        if (plc != null)
        {
            Marshal.FinalReleaseComObject(plc);
            plc = null;
        }

        Application.ExitThread();
    }

    private static void RequestShutdownFromInput()
    {
        Interlocked.Exchange(ref acceptingCommands, 0);
        TryBeginInvoke(new Action(ShutdownOnStaThread));
    }

    private static bool TryBeginInvoke(Action action)
    {
        try
        {
            if (messageLoop == null ||
                messageLoop.IsDisposed ||
                !messageLoop.IsHandleCreated)
            {
                return false;
            }

            messageLoop.BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void EnsureReleasedAfterRun()
    {
        if (Interlocked.CompareExchange(ref shutdownStarted, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref acceptingCommands, 0);
        if (plc != null)
        {
            if (isOpen)
            {
                try
                {
                    plc.Close();
                }
                catch
                {
                }
            }

            isOpen = false;
            Marshal.FinalReleaseComObject(plc);
            plc = null;
        }
    }

    private static void WriteResponse(string format, params object[] arguments)
    {
        Console.WriteLine(format, arguments);
        Console.Out.Flush();
    }

    private static void WriteDiagnostic(string format, params object[] arguments)
    {
        Console.WriteLine("[BRIDGE] " + format, arguments);
        Console.Out.Flush();
    }
}
