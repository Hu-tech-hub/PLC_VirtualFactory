using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ActUtlType64Lib;

internal static class PlcMxBridge
{
    [STAThread]
    private static int Main()
    {
        int exitCode = 3;

        using (Form messageLoop = new Form())
        {
            messageLoop.ShowInTaskbar = false;
            messageLoop.WindowState = FormWindowState.Minimized;
            messageLoop.Load += delegate
            {
                exitCode = RunTest();
                messageLoop.BeginInvoke(new Action(messageLoop.Close));
            };
            Application.Run(messageLoop);
        }

        return exitCode;
    }

    private static int RunTest()
    {
        ActUtlType64Class plc = null;
        bool isOpen = false;

        try
        {
            plc = new ActUtlType64Class
            {
                ActLogicalStationNumber = 1
            };

            int openResult = plc.Open();
            if (openResult != 0)
            {
                Console.WriteLine("OPEN=0x{0:X8}", openResult);
                return 1;
            }

            isOpen = true;
            int value;
            int readResult = plc.GetDevice("D100", out value);

            Console.WriteLine(
                "OPEN=0x{0:X8};READ=0x{1:X8};VALUE={2}",
                openResult,
                readResult,
                value);
            return readResult == 0 ? 0 : 2;
        }
        catch (Exception exception)
        {
            Console.WriteLine("EXCEPTION=0x{0:X8};MESSAGE={1}", exception.HResult, exception.Message);
            return 3;
        }
        finally
        {
            if (isOpen && plc != null)
            {
                try { plc.Close(); }
                catch { }
            }

            if (plc != null)
            {
                Marshal.FinalReleaseComObject(plc);
            }
        }
    }
}
