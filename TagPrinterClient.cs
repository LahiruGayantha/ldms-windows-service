using System.IO.Ports;
using Microsoft.Extensions.Options;

namespace LdmsOutletCameraHelper;

public record PrintResult(bool Success, string Message)
{
    public static PrintResult Ok(string message) => new(true, message);
    public static PrintResult Failed(string message) => new(false, message);
}

// Serial-port tag/label printing, lifted from ldms-windows-app's TagPrinter.cs so a
// print job can be triggered over local HTTP instead of requiring the WinForms process
// to own the COM port directly. Protocol is unchanged: a framed command (STX + tag +
// 2-digit qty + ETX) preceded by an ENQ/ACK handshake. A SemaphoreSlim serializes
// concurrent requests - a physical port can only run one job at a time, and printing is
// human-triggered (clicking Save), not high-frequency, so a simple lock is enough
// (a durable retry queue was considered and deliberately not used here).
public class TagPrinterClient(IOptionsMonitor<CameraHelperOptions> options, ILogger<TagPrinterClient> logger)
{
    private readonly SemaphoreSlim _printLock = new(1, 1);

    public async Task<PrintResult> PrintAsync(string tag, int tagCount, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return PrintResult.Failed("No tag was supplied.");
        }

        PrinterOptions printer = options.CurrentValue.Printer;
        if (!printer.Enabled)
        {
            return PrintResult.Failed("Printing is disabled on this outlet.");
        }

        if (string.IsNullOrWhiteSpace(printer.PortName) || !SerialPort.GetPortNames().Contains(printer.PortName))
        {
            return PrintResult.Failed("Please check the tagging machine port name in this outlet's configuration.");
        }

        await _printLock.WaitAsync(cancellationToken);
        try
        {
            using var serialPort = new SerialPort
            {
                PortName = printer.PortName,
                BaudRate = 2400,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadTimeout = 250,
                WriteTimeout = 250
            };

            try
            {
                serialPort.Open();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to open serial port {PortName} for tag printing.", printer.PortName);
                return PrintResult.Failed("Issue found in serial port.");
            }

            try
            {
                PrintResult holdingResult = WaitForCtsRelease(serialPort);
                if (!holdingResult.Success)
                {
                    return holdingResult;
                }

                PrintResult readyResult = WaitForMachineReady(serialPort);
                if (!readyResult.Success)
                {
                    return readyResult;
                }

                WriteTag(serialPort, tag, tagCount);
                LogPrint(tag);
                return PrintResult.Ok($"Sent {tagCount} * {tag} to printer.");
            }
            finally
            {
                if (serialPort.IsOpen)
                {
                    serialPort.Close();
                }
            }
        }
        finally
        {
            _printLock.Release();
        }
    }

    public async Task<PrintResult> PrintGuestLaundryTagAsync(PrintGuestLaundryTagRequest request, CancellationToken cancellationToken)
    {
        const int MaximumPiecesPerTagJob = 99;

        if (string.IsNullOrWhiteSpace(request.TagNumber)
            || string.IsNullOrWhiteSpace(request.HotelCode)
            || string.IsNullOrWhiteSpace(request.IncomingNumber)
            || string.IsNullOrWhiteSpace(request.QrPayload))
        {
            return PrintResult.Failed("The guest laundry tag details are incomplete.");
        }

        if (request.TotalPcs < 1 || request.TotalPcs > MaximumPiecesPerTagJob)
        {
            return PrintResult.Failed($"Piece count must be between 1 and {MaximumPiecesPerTagJob}.");
        }

        PrintResult result = await PrintAsync(request.TagNumber, request.TotalPcs, cancellationToken);
        if (result.Success)
        {
            LogGuestLaundryTag(request);
        }

        return result;
    }

    private void LogGuestLaundryTag(PrintGuestLaundryTagRequest request)
    {
        logger.LogInformation(
            "Guest laundry tag {TagNumber} printed for {IncomingNumber} ({HotelCode}), {TotalPcs} pcs, process {ProcessType}, delivery {DeliveryType}.",
            request.TagNumber,
            request.IncomingNumber,
            request.HotelCode,
            request.TotalPcs,
            request.ProcessType,
            request.DeliveryType);
    }

    private static PrintResult WaitForCtsRelease(SerialPort serialPort)
    {
        int holdingRetries = 0;
        while (serialPort.CtsHolding)
        {
            if (holdingRetries == 3)
            {
                return PrintResult.Failed("Machine is busy.");
            }

            holdingRetries++;
            Thread.Sleep(250);
        }

        return PrintResult.Ok("Port available.");
    }

    private PrintResult WaitForMachineReady(SerialPort serialPort)
    {
        serialPort.DiscardInBuffer();
        serialPort.DiscardOutBuffer();
        string enquiry = string.Format("{0}", '\x06');
        int retries = 0;
        while (true)
        {
            try
            {
                serialPort.Write(enquiry);
                int response = serialPort.ReadByte();
                if (response == 0x06)
                {
                    return PrintResult.Ok("Machine ready.");
                }

                if (response == 0x15)
                {
                    retries++;
                    continue;
                }

                if (retries == 10)
                {
                    return PrintResult.Failed("Machine is busy.");
                }

                retries++;
            }
            catch (TimeoutException)
            {
                return PrintResult.Failed("Machine is not responding.");
            }
        }
    }

    private static void WriteTag(SerialPort serialPort, string tag, int tagCount)
    {
        serialPort.DiscardInBuffer();
        string text = string.Format("{0}" + tag + "{1,2:00}{2}", '\x02', tagCount, '\x03');
        serialPort.Write(text);
        Thread.Sleep(500);
        serialPort.DiscardOutBuffer();
    }

    private static void LogPrint(string tag)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "dataFile.csv");
            File.AppendAllText(path, $"{tag},\"{DateTime.Now}\"{Environment.NewLine}");
        }
        catch
        {
            // Best-effort local audit log only - never fail a print over this.
        }
    }
}
