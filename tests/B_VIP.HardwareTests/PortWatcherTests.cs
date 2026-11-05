using B_VIP.Hardware;
using Xunit;

namespace B_VIP.HardwareTests
{
    public sealed class PortClassifierTests
    {
        [Theory]
        [InlineData("Qualcomm HS-USB QDLoader 9008 (COM5)", PortCategory.QualcommEdl, "COM5")]
        [InlineData("Qualcomm HS-USB Diagnostics 9008", PortCategory.QualcommEdl, null)]
        [InlineData("MediaTek PreLoader USB VCOM (Android) (COM9)", PortCategory.MediaTekPreloader, "COM9")]
        [InlineData("MTK USB Port (COM11)", PortCategory.MediaTekVcom, "COM11")]
        [InlineData("MediaTek USB VCOM (COM7)", PortCategory.MediaTekVcom, "COM7")]
        [InlineData("SPRD U2S Diag (COM3)", PortCategory.SpreadtrumDiag, "COM3")]
        [InlineData("Unisoc USB Serial Device (COM4)", PortCategory.SpreadtrumDiag, "COM4")]
        [InlineData("SAMSUNG Mobile USB Composite Device", PortCategory.SamsungMtp, null)]
        [InlineData("SAMSUNG Mobile USB Modem #2 (COM6)", PortCategory.SamsungMtp, "COM6")]
        [InlineData("SM-A536B MTP Device", PortCategory.Mtp, null)]
        [InlineData("Android Composite ADB Interface", PortCategory.Adb, null)]
        [InlineData("SAMSUNG Android ADB Interface", PortCategory.Adb, null)]
        [InlineData("Android Bootloader Interface", PortCategory.Fastboot, null)]
        [InlineData("Communications Port (COM1)", PortCategory.Serial, "COM1")]
        public void Classify_maps_windows_device_names_to_tool_categories(string name, PortCategory expected, string? port)
        {
            DetectedPort device = PortClassifier.Classify("USB\\VID_0000&PID_0000\\TEST", name);

            Assert.Equal(expected, device.Category);
            Assert.Equal(port, device.PortName);
            Assert.Equal(name, device.FriendlyName);
        }

        [Theory]
        [InlineData("SAMSUNG Mobile USB Composite Device", "Samsung")]
        [InlineData("Xiaomi Redmi Note 12 (MTP)", "Xiaomi")]
        [InlineData("Infinix X683 MTP Device", "Infinix")]
        [InlineData("TECNO KJ5 MTP Device", "Tecno")]
        [InlineData("Qualcomm HS-USB QDLoader 9008 (COM5)", "Qualcomm")]
        [InlineData("MediaTek PreLoader USB VCOM (COM9)", "MediaTek")]
        [InlineData("SPRD U2S Diag (COM3)", "Unisoc")]
        public void Classify_reports_the_vendor_it_can_prove_from_the_device_name(string name, string vendor)
        {
            Assert.Equal(vendor, PortClassifier.Classify("USB\\VID_0000&PID_0000\\TEST", name).Vendor);
        }

        [Fact]
        public void Classify_leaves_the_vendor_empty_when_the_name_names_no_vendor()
        {
            DetectedPort device = PortClassifier.Classify("USB\\VID_0000&PID_0000\\TEST", "USB Serial Device (COM8)");

            Assert.Null(device.Vendor);
            Assert.Equal(PortCategory.Serial, device.Category);
        }

        [Theory]
        [InlineData("USB\\VID_18D1&PID_4EE7\\SERIAL", "Android Composite ADB Interface", null, true)]
        [InlineData("USBSTOR\\DISK&VEN_X&PROD_Y\\0001", "USB Mass Storage Device", null, true)]
        [InlineData("ACPI\\PNP0501\\1", "Communications Port (COM1)", null, true)]
        [InlineData("ACPI\\PNP0501\\1", "Communications Port", "usbser", true)]
        [InlineData("PCI\\VEN_8086&DEV_1234", "Ethernet Controller", null, false)]
        [InlineData("", "Communications Port (COM1)", null, false)]
        public void IsRelevant_keeps_usb_and_com_devices_and_drops_everything_else(
            string instanceId, string name, string? service, bool expected)
        {
            Assert.Equal(expected, PortClassifier.IsRelevant(instanceId, name, service));
        }

        [Fact]
        public void ExtractPortName_reads_the_number_from_parentheses_only()
        {
            Assert.Equal("COM12", PortClassifier.ExtractPortName("Something (COM12)"));
            Assert.Equal("COM3", PortClassifier.ExtractPortName("something (com3)"));
            Assert.Null(PortClassifier.ExtractPortName("COM12 without parentheses"));
            Assert.Null(PortClassifier.ExtractPortName(null));
        }

        [Fact]
        public void Summary_includes_the_port_and_the_category_label()
        {
            DetectedPort device = PortClassifier.Classify("USB\\VID_0000&PID_0000\\TEST",
                "Qualcomm HS-USB QDLoader 9008 (COM5)");

            Assert.Contains("Qualcomm EDL 9008", device.Summary);
            Assert.Contains("COM5", device.Summary);
        }
    }

    public sealed class PortWatcherChangeDetectionTests
    {
        private static DetectedPort Port(string name) => PortClassifier.Classify($"USB\\{name}", name);

        [Fact]
        public void First_refresh_reports_every_connected_device_as_arriving()
        {
            DetectedPort adb = Port("Android Composite ADB Interface");
            using PortWatcherService watcher = new(() => new[] { adb });
            List<PortChangeEventArgs> events = new();
            watcher.DeviceConnectionChanged += (_, e) => events.Add(e);

            PortSnapshot snapshot = watcher.Refresh();

            Assert.Single(snapshot.Ports);
            PortChangeEventArgs change = Assert.Single(events);
            Assert.Single(change.Added);
            Assert.Empty(change.Removed);
            Assert.Equal(PortCategory.Adb, change.Added[0].Category);
        }

        [Fact]
        public void A_new_device_is_reported_once_and_the_snapshot_is_complete()
        {
            List<DetectedPort> devices = new() { Port("Android Composite ADB Interface") };
            using PortWatcherService watcher = new(() => devices.ToList());
            List<PortChangeEventArgs> events = new();
            watcher.DeviceConnectionChanged += (_, e) => events.Add(e);

            watcher.Refresh();
            devices.Add(Port("Qualcomm HS-USB QDLoader 9008 (COM5)"));
            PortSnapshot second = watcher.Refresh();

            Assert.Equal(2, second.Count);
            Assert.Equal(2, events.Count);
            Assert.Single(events[1].Added);
            Assert.Empty(events[1].Removed);
            Assert.Equal(PortCategory.QualcommEdl, events[1].Added[0].Category);
        }

        [Fact]
        public void A_removed_device_is_reported_and_an_unchanged_scan_raises_nothing()
        {
            List<DetectedPort> devices = new() { Port("Android Composite ADB Interface"), Port("MTK USB Port (COM11)") };
            using PortWatcherService watcher = new(() => devices.ToList());
            List<PortChangeEventArgs> events = new();
            watcher.DeviceConnectionChanged += (_, e) => events.Add(e);

            watcher.Refresh();
            devices.RemoveAt(1);
            PortSnapshot afterRemoval = watcher.Refresh();

            Assert.Single(afterRemoval.Ports);
            Assert.Equal(2, events.Count);
            Assert.Empty(events[1].Added);
            Assert.Single(events[1].Removed);
            Assert.Equal(PortCategory.MediaTekVcom, events[1].Removed[0].Category);

            watcher.Refresh();
            Assert.Equal(2, events.Count);
        }

        [Fact]
        public void Snapshot_orders_devices_by_category_and_exposes_per_category_counts()
        {
            using PortWatcherService watcher = new(() => new[]
            {
                Port("Communications Port (COM1)"),
                Port("Android Composite ADB Interface"),
                Port("Qualcomm HS-USB QDLoader 9008 (COM5)")
            });

            PortSnapshot snapshot = watcher.Refresh();

            Assert.Equal(new[] { PortCategory.Adb, PortCategory.QualcommEdl, PortCategory.Serial },
                snapshot.Ports.Select(p => p.Category).ToArray());
            Assert.Equal(1, snapshot.CountOf(PortCategory.Adb));
            Assert.Equal(0, snapshot.CountOf(PortCategory.Fastboot));
        }

        [Fact]
        public void A_failing_enumerator_is_reported_as_a_warning_instead_of_throwing()
        {
            using PortWatcherService watcher = new(() => throw new InvalidOperationException("WMI is offline"));

            PortSnapshot snapshot = watcher.Refresh();

            Assert.Empty(snapshot.Ports);
        }
    }
}
