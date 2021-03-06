//
// Copyright (c) 2010-2018 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Input;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.USB;
using Antmicro.Renode.Core.USB.HID;

namespace Antmicro.Renode.Peripherals.USB
{
    public class USBMouse : IUSBDevice, IRelativePositionPointerInput
    {
        public USBMouse(Machine machine)
        {
            this.machine = machine;

            USBCore = new USBDeviceCore(this)
                .WithConfiguration(configure: c =>
                    c.WithInterface(new Core.USB.HID.Interface(this, 0,
                        subClassCode: (byte)Core.USB.HID.SubclassCode.BootInterfaceSubclass,
                        protocol: (byte)Core.USB.HID.Protocol.Mouse,
                        reportDescriptor: new Core.USB.HID.ReportDescriptor(ReportHidDescriptor)),
                        configure: i =>
                            i.WithEndpoint(
                                Direction.DeviceToHost,
                                EndpointTransferType.Interrupt,
                                maximumPacketSize: 0x4,
                                interval: 0xa,
                                createdEndpoint: out endpoint)));
        }

        public void Reset()
        {
            buttonState = 0;
            USBCore.Reset();
        }

        public void MoveBy(int x, int y)
        {
            using(var p = endpoint.PreparePacket())
            {
                p.Add((byte)buttonState);
                p.Add((byte)x.Clamp(-127, 127));
                p.Add((byte)y.Clamp(-127, 127));
                p.Add(0);
            }
        }

        public void Press(MouseButton button = MouseButton.Left)
        {
            buttonState = button;
            SendButtonState();
        }

        public void Release(MouseButton button = MouseButton.Left)
        {
            buttonState = 0;
            SendButtonState();
        }

        private void SendButtonState()
        {
            using(var p = endpoint.PreparePacket())
            {
                p.Add((byte)buttonState);
                p.Add(0);
                p.Add(0);
                p.Add(0);
            }
        }

        public USBDeviceCore USBCore { get; }

        private MouseButton buttonState;

        private USBEndpoint endpoint;

        private readonly Machine machine;

        private readonly byte[] ReportHidDescriptor = new byte[]
        {
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x09, 0x01,
            0xA1, 0x00, 0x05, 0x09, 0x19, 0x01, 0x29, 0x03,
            0x15, 0x00, 0x25, 0x01, 0x95, 0x08, 0x75, 0x01,
            0x81, 0x02, 0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
            0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08,
            0x95, 0x03, 0x81, 0x06, 0xC0, 0xC0
        };
    }
}

