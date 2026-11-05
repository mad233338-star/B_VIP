using System.Runtime.Versioning;
using B_VIP.Core.Operations;
using B_VIP.Hardware;

namespace B_VIP.Modules
{
    /// <summary>
    /// Composition root for the module layer: builds the real tool locator, adb client, device
    /// detector, diagnostics engine and the executor registry that routes every operation type.
    /// </summary>
    public static class ModuleServices
    {
        public static ToolLocatorOptions ToolLocatorOptions { get; set; } = new();

        public static IProcessRunner CreateRunner() => new ProcessRunner();

        public static IToolLocator CreateToolLocator(IProcessRunner? runner = null)
            => new ToolLocator(ToolLocatorOptions, runner ?? CreateRunner());

        public static IAdbClient CreateAdbClient(IToolLocator? tools = null, IProcessRunner? runner = null,
            IProcessOutputSink? outputSink = null)
        {
            IProcessRunner process = runner ?? CreateRunner();
            return new AdbClient(tools ?? CreateToolLocator(process), process, outputSink);
        }

        public static IDeviceDetector CreateDetector(IAdbClient? adb = null, IToolLocator? tools = null)
        {
            IToolLocator locator = tools ?? CreateToolLocator();
            return new DeviceDetector(adb ?? CreateAdbClient(locator), locator);
        }

        public static IDeviceDiagnostics CreateDiagnostics(IAdbClient? adb = null)
            => new DeviceDiagnostics(adb ?? CreateAdbClient());

        [SupportedOSPlatform("windows")]
        public static OperationExecutorRegistry CreateRegistry(
            IAdbClient? adb = null,
            IToolLocator? tools = null,
            IProcessRunner? runner = null)
        {
            IProcessRunner process = runner ?? CreateRunner();
            IToolLocator locator = tools ?? CreateToolLocator(process);
            IAdbClient client = adb ?? CreateAdbClient(locator, process);
            IDeviceDiagnostics diagnostics = new DeviceDiagnostics(client);

            // Samsung services: MTP identity (no debugging needed), browser dispatch over adb, and the
            // modem/test-mode FRP flow. One instance is shared so the browser service reuses the client.
            Samsung.MtpBrowserService browser = new(client);
            Samsung.SamsungExecutionService samsung = new(client, new MtpProtocolService(), browser);

            IOperationExecutor[] executors =
            [
                new AdbOperationExecutor(client, locator),
                new FastbootOperationExecutor(client, locator),
                new AndroidDiagnosticExecutor(diagnostics),
                new ToolProbeExecutor(locator),
                new SamsungModuleExecutor(client, samsung),
                new BrandModuleExecutor(BrandProfiles.Xiaomi, client, locator, process,
                    [OperationTypes.XiaomiReadInfo, OperationTypes.XiaomiResetFrp]),
                new BrandModuleExecutor(BrandProfiles.Infinix, client, locator, process,
                    [OperationTypes.InfinixReadInfo, OperationTypes.InfinixResetFrp, OperationTypes.InfinixFactoryFormat]),
                new BrandModuleExecutor(BrandProfiles.Tecno, client, locator, process,
                    [OperationTypes.TecnoReadInfo, OperationTypes.TecnoResetFrp, OperationTypes.TecnoWipe]),
                new BrandModuleExecutor(BrandProfiles.Spreadtrum, client, locator, process,
                    [OperationTypes.SpreadtrumReadInfo, OperationTypes.SpreadtrumResetFrp]),
                new Mtk.MtkModuleExecutor(client, locator),
                new Qualcomm.QualcommEdlExecutor(client, locator),
                // Unisoc: boot-ROM handshake, FDL loader upload and the safe partition routines.
                new Spreadtrum.SpdModuleExecutor(client, locator, process),
                new GoogleRecoveryExecutor(client)
            ];


            return new OperationExecutorRegistry(executors);
        }
    }
}
